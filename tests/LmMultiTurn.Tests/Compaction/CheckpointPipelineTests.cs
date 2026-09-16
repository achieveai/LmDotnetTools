using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the checkpoint pipeline over the #680 state machine on every store flavour (#683; spec 679
/// §3.2–§3.5, §12.2): a valid checkpoint appends its row and activates; an invalid one is rejected
/// with its typed reason and the view is unchanged (last-known-good); a model quote that is not verbatim is
/// dropped, while a current instruction that is not the rows fails V3 (the R5 mutation); a row appended during
/// the summary rejects with <c>stale_watermark</c>; rows
/// behind the store skip before anything is prepared; a second checkpoint chains the first; and the
/// summary pass's usage and latency are attributed on the checkpoint.
/// </summary>
public sealed class CheckpointPipelineTests : IAsyncLifetime
{
    private const string Thread = "thread-pipeline";
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    private sealed class ScriptedSummarizer(Func<CheckpointSummaryRequest, Task<CheckpointSummaryResponse>> script)
        : ICheckpointSummarizer
    {
        public List<CheckpointSummaryRequest> Requests { get; } = [];

        public Task<CheckpointSummaryResponse> SummarizeAsync(
            CheckpointSummaryRequest request,
            CancellationToken ct = default
        )
        {
            Requests.Add(request);
            return script(request);
        }
    }

    private static UsageMessage SummaryUsage() =>
        new()
        {
            Usage = new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 20,
                TotalTokens = 120,
            },
        };

    private static CheckpointSummaryResponse GoodSummary(string instructionQuote = "flaky") =>
        new(
            new CheckpointSummary
            {
                Instructions = [new QuotedItem { Seq = 1, Quote = instructionQuote }],
                Goals = ["green"],
                Headlines = new Dictionary<string, string>(StringComparer.Ordinal) { ["run-1"] = "the fix" },
                Narrative = "Wrote a.cs and ran the tests.",
            },
            SummaryUsage()
        );

    private static ScriptedSummarizer Summarizer(Func<CheckpointSummaryRequest, CheckpointSummaryResponse> script) =>
        new(r => Task.FromResult(script(r)));

    private static CheckpointPipeline Pipeline(
        ICheckpointSummarizer summarizer,
        ILogger? logger = null,
        CheckpointValidationOptions? validation = null,
        ManifestAssemblerOptions? assembler = null
    ) =>
        new(
            summarizer,
            new CheckpointPipelineOptions
            {
                Estimator = ThreadFixture.RowTokens,
                Logger = logger ?? NullLogger.Instance,
                Validation = validation ?? new CheckpointValidationOptions(),
                Assembler = assembler ?? new ManifestAssemblerOptions(),
            },
            new FixedClock(T0)
        );

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(
            LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> Properties,
            Exception? Exception
        )> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            Entries.Add(
                (
                    logLevel,
                    formatter(state, exception),
                    (state as IEnumerable<KeyValuePair<string, object?>> ?? []).ToDictionary(p => p.Key, p => p.Value),
                    exception
                )
            );
    }

    /// <summary>The cut with a current instruction that is not what the rows say: only V3 can catch it.</summary>
    private static CutDecision.Cut Tampered(CutDecision.Cut cut) => cut with { CurrentInstruction = [] };

    /// <summary>Human, a Write call, then four tool turns: eleven rows, persisted under run-1.</summary>
    private static async Task SeedAsync(IConversationStore store)
    {
        var thread = new ThreadFixture()
            .Human("fix the flaky test")
            .ToolTurn(tool: "Write", args: """{"file_path":"src/a.cs","content":"x"}""")
            .ToolTurns(4);
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, "run-1")
        );
    }

    private static Task AppendTurnsAsync(IConversationStore store, int count) =>
        store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(
                new ThreadFixture().ToolTurns(count).Messages,
                Thread,
                "run-1"
            )
        );

    private static async Task<IReadOnlyList<SequencedMessage>> RowsAsync(IConversationStore store) =>
        SequencedHistory.FromPersisted(await store.LoadMessagesAsync(Thread));

    private static CutDecision.Cut CutAt(IReadOnlyList<SequencedMessage> rows, long seq, long? activeBoundary = null) =>
        CutSelector
            .Select(
                new CutRequest(
                    rows,
                    seq,
                    CutBlockingState.Clean,
                    [],
                    activeBoundary,
                    ThreadFixture.Options(minTail: 10)
                )
            )
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;

    private static CheckpointBuildRequest Request(
        IReadOnlyList<SequencedMessage> rows,
        CutDecision.Cut cut,
        string checkpointId = "cp-1",
        CompactionCheckpointMessage? previous = null
    ) =>
        new()
        {
            ThreadId = Thread,
            RunId = "run-1",
            CheckpointId = checkpointId,
            Rows = rows,
            Cut = cut,
            Previous = previous,
        };

    private static async Task<CompactionCheckpointMessage?> CheckpointRowAsync(
        IConversationStore store,
        string checkpointId
    ) =>
        (await RowsAsync(store))
            .Select(r => r.Message)
            .OfType<CompactionCheckpointMessage>()
            .SingleOrDefault(c => c.CheckpointId == checkpointId);

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_ValidCheckpoint_AppendsTheRow_ActivatesIt_AndTheViewHidesTheCoveredRows(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var cut = CutAt(rows, ThreadFixture.TurnEnd(3));
        var summarizer = Summarizer(_ => GoodSummary());

        var result = await Pipeline(summarizer).RunAsync(store, Request(rows, cut));

        result.Outcome.Should().Be(CheckpointOutcome.Activated);
        result.Reason.Should().BeNull();
        result.RowSeq.Should().Be(12);
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(12);

        var state = await CompactionStateProjection.LoadAsync(store, Thread);
        state!.ActiveCheckpointId.Should().Be("cp-1");
        state.ActiveBoundarySeq.Should().Be(cut.Seq);
        state.Find("cp-1")!.RowSeq.Should().Be(12);

        var checkpoint = result.Checkpoint!;
        checkpoint.Boundary.Should().Be(new CheckpointBoundary { Seq = 7, MessageId = rows[6].MessageId! });
        checkpoint.Manifest.CurrentInstruction.Should().Equal(new QuotedItem { Seq = 1, Quote = "fix the flaky test" });
        checkpoint
            .Manifest.Artifacts.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ArtifactRef { Path = "src/a.cs", OriginSeq = 2 });
        checkpoint.Manifest.Index.Should().ContainSingle().Which.Headline.Should().Be("the fix");
        checkpoint.CreatedAtUtc.Should().Be(T0);
        checkpoint.Stats.RowsCovered.Should().Be(7);
        checkpoint.Stats.EstimatedTokensBefore.Should().Be(7 * ThreadFixture.TokensPerRow);
        checkpoint.Stats.EstimatedTokensAfter.Should().BePositive();

        // Usage and latency of the summary pass are attributed on the checkpoint (§3.2).
        result.Usage!.GenerationId.Should().Be("cp-1:summary");
        result.Usage.ThreadId.Should().Be(Thread);
        result.Usage.RunId.Should().Be("run-1");
        checkpoint.Stats.SummaryUsageAttemptId.Should().Be($"{Thread}:cp-1:summary");
        checkpoint.Stats.SummaryLatencyMs.Should().BeGreaterThanOrEqualTo(0);
        summarizer.Requests.Should().ContainSingle().Which.Rows.Select(r => r.Seq).Should().Equal(1, 2, 3, 4, 5, 6, 7);

        // Replaying the store: the row decodes, and the view is system + envelope + rows 8..11.
        var persisted = await CheckpointRowAsync(store, "cp-1");
        persisted.Should().NotBeNull();
        MessagePersistenceConverter
            .ToPersistedMessage(persisted!, Thread, "run-1")
            .MessageJson.Should()
            .Be(
                MessagePersistenceConverter.ToPersistedMessage(checkpoint, Thread, "run-1").MessageJson,
                "the row round-trips the checkpoint"
            );
        var view = AgentContextProjection.Default.Build("sys", await RowsAsync(store), persisted);
        view.Should().HaveCount(1 + 1 + 4).And.NotContain(m => m is CompactionCheckpointMessage);
        view[1]
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .StartWith("<context-checkpoint version=\"2\" id=\"cp-1\" covers_seq=\"1-7\"");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_ACurrentInstructionThatIsNotTheRows_IsRejectedByV3_LoggedWithoutRowText_AndNothingChanges(
        string kind
    )
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var cut = Tampered(CutAt(rows, ThreadFixture.TurnEnd(3)));
        var logger = new ListLogger();

        var result = await Pipeline(Summarizer(_ => GoodSummary()), logger).RunAsync(store, Request(rows, cut));

        result.Outcome.Should().Be(CheckpointOutcome.Rejected);
        result.Reason.Should().Be("validation_failed:V3");
        result.Validation!.Rule.Should().Be("V3");
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Properties.Should().Contain("ThreadId", Thread).And.Contain("RunId", "run-1");
        warning.Properties.Should().Contain("CheckpointId", "cp-1").And.Contain("ValidationRule", "V3");
        warning.Properties["ValidationDetail"].Should().Be(result.Validation.Detail);
        warning.Message.Should().NotContain("fix the flaky test", "a validation detail names seqs, never row text");
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(11, "no row is appended for a rejected checkpoint");
        var state = await CompactionStateProjection.LoadAsync(store, Thread);
        state!.ActiveCheckpointId.Should().BeNull();
        state
            .Find("cp-1")
            .Should()
            .Match<CheckpointEntry>(e => e.Status == CheckpointStatus.Rejected && e.Reason == "validation_failed:V3");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_ModelQuotesThatAreNotVerbatim_AreDroppedBeforeValidation_AndTheVerbatimOnesKept(string kind)
    {
        // A real model quotes tool calls (rows with no text), adds the prompt's "user: " prefix or reflows
        // whitespace. One such quote used to reject the whole checkpoint.
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var cut = CutAt(rows, ThreadFixture.TurnEnd(3));
        var logger = new ListLogger();
        var summarizer = Summarizer(_ => new CheckpointSummaryResponse(
            new CheckpointSummary
            {
                Instructions =
                [
                    new QuotedItem { Seq = 1, Quote = "flaky" },
                    new QuotedItem { Seq = 1, Quote = "user: fix the flaky test" },
                    new QuotedItem { Seq = 9, Quote = "flaky" },
                ],
                Decisions =
                [
                    new QuotedItem { Seq = 2, Quote = "src/a.cs" },
                    new QuotedItem { Seq = 1, Quote = "fix the flaky" },
                    new QuotedItem { Seq = 40, Quote = "fix" },
                ],
                Narrative = "Wrote a.cs.",
            },
            null
        ));

        var result = await Pipeline(summarizer, logger).RunAsync(store, Request(rows, cut));

        result.Outcome.Should().Be(CheckpointOutcome.Activated, result.Validation?.Detail);
        var manifest = result.Checkpoint!.Manifest;
        manifest.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = "flaky" });
        manifest.Decisions.Should().Equal(new QuotedItem { Seq = 1, Quote = "fix the flaky" });
        var dropped = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Subject;
        dropped.Properties.Should().Contain("DroppedQuotes", 4).And.Contain("CheckpointId", "cp-1");
        dropped.Properties["DroppedSeqs"].Should().Be("1, 2, 9, 40");
        dropped.Message.Should().NotContain("user: fix").And.NotContain("src/a.cs", "quote text is never logged");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_RejectedSecondCheckpoint_LeavesTheFirstActive_AsLastKnownGood(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var first = await Pipeline(Summarizer(_ => GoodSummary()))
            .RunAsync(store, Request(await RowsAsync(store), CutAt(await RowsAsync(store), ThreadFixture.TurnEnd(3))));
        first.Outcome.Should().Be(CheckpointOutcome.Activated);
        await AppendTurnsAsync(store, 2); // rows 13..16
        var rows = await RowsAsync(store);
        var cut = CutAt(rows, 14, activeBoundary: 7);

        var second = await Pipeline(Summarizer(_ => GoodSummary()))
            .RunAsync(store, Request(rows, Tampered(cut), "cp-2", first.Checkpoint));

        second.Outcome.Should().Be(CheckpointOutcome.Rejected);
        second.Reason.Should().Be("validation_failed:V3");
        var state = await CompactionStateProjection.LoadAsync(store, Thread);
        state!.ActiveCheckpointId.Should().Be("cp-1");
        state.LastKnownGoodCheckpointId.Should().Be("cp-1");
        (await CheckpointRowAsync(store, "cp-2")).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_ThrowingSummarizer_IsRejectedWithSummaryCallFailed(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var summarizer = new ScriptedSummarizer(_ => throw new HttpRequestException("provider down"));

        var result = await Pipeline(summarizer).RunAsync(store, Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))));

        result.Outcome.Should().Be(CheckpointOutcome.Rejected);
        result.Reason.Should().Be(CompactionReasons.SummaryCallFailed);
        result.Checkpoint.Should().BeNull();
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(11);
        (await CompactionStateProjection.LoadAsync(store, Thread))!
            .Find("cp-1")
            .Should()
            .Match<CheckpointEntry>(e =>
                e.Status == CheckpointStatus.Rejected && e.Reason == CompactionReasons.SummaryCallFailed
            );
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_WithSummaryFallback_AFailedSummaryCall_ActivatesADeterministicCheckpoint_ThatValidates_AndIsMarked(
        string kind
    )
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var summarizer = new ScriptedSummarizer(_ => throw new HttpRequestException("provider down"));

        var result = await Pipeline(summarizer)
            .RunAsync(store, Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))) with { SummaryFallback = true });

        result.Outcome.Should().Be(CheckpointOutcome.Activated, result.Validation?.Detail);
        result.Reason.Should().BeNull();
        result.Usage.Should().BeNull("no summary was used");
        var checkpoint = result.Checkpoint!;
        checkpoint.Narrative.Should().Be(CheckpointPipeline.FallbackNarrative);
        checkpoint.Stats.SummaryFallback.Should().Be(CompactionReasons.SummaryCallFailed);
        checkpoint.Stats.SummaryUsageAttemptId.Should().BeNull();
        var manifest = checkpoint.Manifest;
        manifest.CurrentInstruction.Should().Equal(new QuotedItem { Seq = 1, Quote = "fix the flaky test" });
        manifest.Artifacts.Should().ContainSingle().Which.Path.Should().Be("src/a.cs");
        manifest.Index.Should().ContainSingle().Which.ToSeq.Should().Be(7);
        manifest.Instructions.Should().BeEmpty();
        manifest.Goals.Should().BeEmpty();
        manifest.Decisions.Should().BeEmpty();

        // The stored row validates unchanged, and the state history says it is a fallback.
        var persisted = await CheckpointRowAsync(store, "cp-1");
        persisted!.Stats.SummaryFallback.Should().Be(CompactionReasons.SummaryCallFailed);
        var verdict = CheckpointValidator.Validate(
            persisted,
            new CheckpointValidationContext(await RowsAsync(store), null, [])
        );
        verdict.IsValid.Should().BeTrue(verdict.Detail);
        var state = await CompactionStateProjection.LoadAsync(store, Thread);
        state!.ActiveCheckpointId.Should().Be("cp-1");
        state
            .Find("cp-1")
            .Should()
            .Match<CheckpointEntry>(e =>
                e.Status == CheckpointStatus.Active && e.SummaryFallback == CompactionReasons.SummaryCallFailed
            );
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_AFallbackOnAChain_KeepsThePreviousNarrative_ThenTheFallbackLine_AndValidates(string kind)
    {
        // The previous narrative is the only summary of everything before the old boundary.
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var first = await Pipeline(Summarizer(_ => GoodSummary()))
            .RunAsync(store, Request(await RowsAsync(store), CutAt(await RowsAsync(store), ThreadFixture.TurnEnd(3))));
        first.Outcome.Should().Be(CheckpointOutcome.Activated);
        await AppendTurnsAsync(store, 2); // rows 13..16
        var rows = await RowsAsync(store);
        var failing = new ScriptedSummarizer(_ => throw new HttpRequestException("provider down"));

        var second = await Pipeline(failing)
            .RunAsync(
                store,
                Request(rows, CutAt(rows, 14, activeBoundary: 7), "cp-2", first.Checkpoint) with
                {
                    SummaryFallback = true,
                }
            );

        second.Outcome.Should().Be(CheckpointOutcome.Activated, second.Validation?.Detail);
        second
            .Checkpoint!.Narrative.Should()
            .Be("Wrote a.cs and ran the tests.\n\n" + CheckpointPipeline.FallbackNarrative);
        second.Checkpoint.Manifest.Goals.Should().Equal("green");
        second.Checkpoint.Manifest.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = "flaky" });
    }

    [Fact]
    public async Task Build_AFallbackWhosePreviousNarrativeWouldBreakTheCap_KeepsItsEnd_AndTheWholeFallbackLine()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var previousNarrative =
            "START-OF-PREVIOUS " + string.Concat(Enumerable.Repeat("then more work; ", 40)) + "END-OF-PREVIOUS";
        var first = await Pipeline(
                Summarizer(_ =>
                    GoodSummary() with
                    {
                        Summary = GoodSummary().Summary with { Narrative = previousNarrative },
                    }
                )
            )
            .RunAsync(store, Request(await RowsAsync(store), CutAt(await RowsAsync(store), ThreadFixture.TurnEnd(3))));
        first.Outcome.Should().Be(CheckpointOutcome.Activated);
        await AppendTurnsAsync(store, 2);
        var rows = await RowsAsync(store);
        var cap = CompactionTokenEstimate.EstimateText(CheckpointPipeline.FallbackNarrative) + 20;
        var failing = new ScriptedSummarizer(_ => throw new HttpRequestException("provider down"));

        var build = await Pipeline(failing, validation: new CheckpointValidationOptions { NarrativeTokenCap = cap })
            .BuildAsync(
                Request(rows, CutAt(rows, 14, activeBoundary: 7), "cp-2", first.Checkpoint) with
                {
                    SummaryFallback = true,
                }
            );

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        var narrative = build.Checkpoint!.Narrative;
        CompactionTokenEstimate.EstimateText(narrative).Should().BeLessThanOrEqualTo(cap);
        narrative.Should().EndWith("END-OF-PREVIOUS\n\n" + CheckpointPipeline.FallbackNarrative);
        narrative.Should().NotContain("START-OF-PREVIOUS", "the oldest part is trimmed first");
        narrative
            .Length.Should()
            .BeGreaterThan(CheckpointPipeline.FallbackNarrative.Length + 40, "the room left is used");
    }

    [Fact]
    public void FallbackNarrative_OnAChainOfFallbacks_CarriesTheLastRealNarrative_AndTheFallbackLineOnce()
    {
        var once = CheckpointPipeline.FallbackNarrativeAfter(
            "Wrote a.cs.",
            2_000,
            CompactionTokenEstimate.EstimateText
        );
        var twice = CheckpointPipeline.FallbackNarrativeAfter(once, 2_000, CompactionTokenEstimate.EstimateText);

        twice.Should().Be("Wrote a.cs.\n\n" + CheckpointPipeline.FallbackNarrative).And.Be(once);
        CheckpointPipeline
            .FallbackNarrativeAfter(CheckpointPipeline.FallbackNarrative, 2_000, CompactionTokenEstimate.EstimateText)
            .Should()
            .Be(CheckpointPipeline.FallbackNarrative);
    }

    [Fact]
    public async Task Build_ACurrentInstructionOverHalfTheEnvelopeCap_IsQuotedTrimmed_SoSummarisedAndFallbackCheckpointsFitV9()
    {
        // V9 sizes the whole rendered envelope. Quoted whole, this 2,000-token instruction broke a 1,000-token cap on every
        // checkpoint, the fallback included; trimmed to half the cap, both validate.
        var store = _harness.Open("memory");
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(
                new ThreadFixture().Human(new string('p', 8_000)).ToolTurns(4).Messages,
                Thread,
                "run-1"
            )
        );
        var rows = await RowsAsync(store);
        var request = Request(rows, CutAt(rows, ThreadFixture.TurnEnd(2))) with { SummaryFallback = true };
        var capped = new CheckpointValidationOptions { CheckpointTokenCap = 1_000 };

        var summarised = await Pipeline(Summarizer(_ => GoodSummary("ppp")), validation: capped)
            .BuildAsync(request with { SummaryFallback = false });
        var fallback = await Pipeline(
                new ScriptedSummarizer(_ => throw new HttpRequestException("provider down")),
                validation: capped
            )
            .BuildAsync(request);

        summarised.IsValid.Should().BeTrue(summarised.Reason);
        fallback.IsValid.Should().BeTrue(fallback.Reason);
        fallback.Checkpoint!.Stats.SummaryFallback.Should().Be(CompactionReasons.SummaryCallFailed);
        foreach (var checkpoint in new[] { summarised.Checkpoint!, fallback.Checkpoint })
        {
            checkpoint
                .Manifest.CurrentInstruction.Should()
                .ContainSingle()
                .Which.Quote.Should()
                .Contain("chars omitted; full text: RecallConversation seq 1]");
        }
    }

    [Fact]
    public async Task Build_AModelQuoteOfTheCurrentInstructionRow_IsDropped_SoAPastedInstructionDoesNotBreakV9()
    {
        // The prompt asks the model to quote standing instructions and shows human rows whole, so it quotes a pasted
        // 40k-char prompt into Instructions: trimmed as the current instruction, the duplicate alone broke the envelope.
        var store = _harness.Open("memory");
        var pasted = "spec:" + new string('s', 40_000) + ":end";
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(
                new ThreadFixture().Human(pasted).ToolTurns(4).Messages,
                Thread,
                "run-1"
            )
        );
        var rows = await RowsAsync(store);
        var logger = new ListLogger();
        var summarizer = Summarizer(_ => new CheckpointSummaryResponse(
            new CheckpointSummary
            {
                Instructions = [new QuotedItem { Seq = 1, Quote = pasted[..30_000] }],
                Narrative = "Read the spec.",
            },
            SummaryUsage()
        ));

        var build = await Pipeline(summarizer, logger).BuildAsync(Request(rows, CutAt(rows, ThreadFixture.TurnEnd(2))));

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        build.Checkpoint!.Manifest.Instructions.Should().BeEmpty("seq 1 is already quoted as the current instruction");
        build
            .Checkpoint.Manifest.CurrentInstruction.Should()
            .ContainSingle()
            .Which.Quote.Should()
            .Contain("full text: RecallConversation seq 1]");
        var dropped = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Subject;
        dropped.Properties.Should().Contain("DroppedQuotes", 1);
        dropped.Properties["DroppedSeqs"].Should().Be("1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_OnAChain_KeepsTheHumanInstructionsOfEarlierRuns_WhetherOrNotTheSummaryQuotesThem(
        bool fallback
    )
    {
        // cp1 quoted run A's instruction only as its current instruction. Once run C is the current run, that quote and
        // run B's instruction (covered, never quoted by a summary that failed or skipped it) must still reach the model.
        var store = _harness.Open("memory");
        await AppendRunAsync(
            store,
            "run-A",
            new ThreadFixture().Run("run-A").Human("do NOT modify tests/").ToolTurns(3)
        );
        var first = await Pipeline(Summarizer(_ => new CheckpointSummaryResponse(new() { Narrative = "Ran A." }, null)))
            .RunAsync(store, Request(await RowsAsync(store), CutAt(await RowsAsync(store), ThreadFixture.TurnEnd(2))));
        first.Outcome.Should().Be(CheckpointOutcome.Activated, first.Reason);
        first.Checkpoint!.Manifest.CurrentInstruction.Should().ContainSingle().Which.Seq.Should().Be(1);
        await AppendRunAsync(store, "run-B", new ThreadFixture().Run("run-B").Human("use net8").ToolTurns(2)); // 9..13
        await AppendRunAsync(store, "run-C", new ThreadFixture().Run("run-C").Human("now lint").ToolTurns(3)); // 14..20
        var rows = await RowsAsync(store);
        var summarizer = fallback
            ? new ScriptedSummarizer(_ => throw new HttpRequestException("provider down"))
            : Summarizer(_ => new CheckpointSummaryResponse(new() { Narrative = "Ran B, then C." }, null));

        var second = await Pipeline(summarizer)
            .BuildAsync(
                Request(rows, CutAt(rows, 17, activeBoundary: 5), "cp-2", first.Checkpoint) with
                {
                    SummaryFallback = fallback,
                }
            );

        second.IsValid.Should().BeTrue(second.Validation?.Detail);
        second.Checkpoint!.Stats.SummaryFallback.Should().Be(fallback ? CompactionReasons.SummaryCallFailed : null);
        var manifest = second.Checkpoint.Manifest;
        manifest.CurrentInstruction.Should().Equal(new QuotedItem { Seq = 14, Quote = "now lint" });
        manifest
            .Instructions.Should()
            .Equal(
                new QuotedItem { Seq = 1, Quote = "do NOT modify tests/" },
                new QuotedItem { Seq = 9, Quote = "use net8" }
            );
    }

    [Fact]
    public async Task Build_ALongChainOnASmallWindow_KeepsEverySummarisedAndFallbackEnvelopeWithinV9_AndItsHumanInstructions()
    {
        // A 20k usable window caps the envelope at 3,000 tokens. Carried sections used to grow without bound: past 60
        // index entries coalescing concatenated headlines and run ids, and quotes, goals and artifacts piled up, until
        // every build failed V9 (the fallback included) and every turn was refused.
        const int runs = 36;
        var capped = new CheckpointValidationOptions { CheckpointTokenCap = 3_000 };
        var logger = new ListLogger();
        var thread = new ThreadFixture();
        var instructions = new Dictionary<long, string>();
        CompactionCheckpointMessage? previous = null;
        CheckpointBuildResult? fallback = null;
        for (var run = 1; run <= runs; run++)
        {
            var runId = $"run-{run}";
            var instruction =
                run % 12 == 0
                    ? $"PASTED-{run}:" + new string('p', 7_000) + $":END-{run}"
                    : $"run {run}: do NOT modify tests/ and keep module-{run} stable";
            _ = thread
                .Run(runId)
                .Human(instruction)
                .ToolTurn(tool: "Write", args: $$"""{"file_path":"src/module-{{run}}/{{new string('d', 90)}}.cs"}""")
                .ToolTurns(2);
            instructions[thread.LastSeq - 6] = instruction;
            var rows = thread.Rows;
            var cut = CutSelector
                .Select(
                    new CutRequest(
                        rows,
                        thread.LastSeq,
                        CutBlockingState.Clean,
                        [],
                        previous?.Boundary.Seq,
                        ThreadFixture.Options(minTail: 10)
                    )
                )
                .Should()
                .BeOfType<CutDecision.Cut>()
                .Subject;
            var humanSeq = thread.LastSeq - 6;
            var summarizer = Summarizer(_ => new CheckpointSummaryResponse(
                new CheckpointSummary
                {
                    Instructions = [new QuotedItem { Seq = humanSeq, Quote = instruction[..40] }],
                    Decisions = [new QuotedItem { Seq = humanSeq, Quote = instruction[..30] }],
                    Goals = [$"goal {run}: " + new string('g', 200)],
                    Headlines = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [runId] = $"headline {run}: " + new string('h', 150),
                    },
                    Narrative = $"narrative {run}: " + new string('n', 3_000),
                },
                null
            ));
            var request = new CheckpointBuildRequest
            {
                ThreadId = Thread,
                RunId = runId,
                CheckpointId = $"cp-{run}",
                Rows = rows,
                Cut = cut,
                Previous = previous,
            };

            var build = await Pipeline(summarizer, logger, capped).BuildAsync(request);
            fallback = await Pipeline(
                    new ScriptedSummarizer(_ => throw new HttpRequestException("provider down")),
                    logger,
                    capped
                )
                .BuildAsync(request with { SummaryFallback = true });

            build.IsValid.Should().BeTrue($"summarised build {run}: {build.Validation?.Detail}");
            fallback.IsValid.Should().BeTrue($"fallback build {run}: {fallback.Validation?.Detail}");
            previous = build.Checkpoint!;
            _ = thread.Checkpoint(previous);
        }

        foreach (var checkpoint in new[] { previous!, fallback!.Checkpoint! })
        {
            var manifest = checkpoint.Manifest;
            manifest.Index.Should().HaveCountLessThanOrEqualTo(60);
            manifest.Index[0].RunId.Should().Contain("more", "the chain went past the index cap");
            manifest.Index.Should().OnlyContain(e => e.Headline.Length <= ManifestAssembler.MaxCoalescedHeadlineChars);
            var quoted = manifest
                .CurrentInstruction.Concat(manifest.Instructions)
                .GroupBy(q => q.Seq)
                .ToDictionary(g => g.Key, g => g.First().Quote);
            foreach (var (seq, text) in instructions.Where(i => i.Key <= checkpoint.Boundary.Seq))
            {
                quoted.Should().ContainKey(seq, "a human instruction is never silently lost");
                if (quoted[seq] != text)
                {
                    quoted[seq].Should().Contain($"full text: RecallConversation seq {seq}]");
                }
            }
        }

        logger
            .Entries.Should()
            .Contain(e => e.Level == LogLevel.Information && e.Properties.ContainsKey("IndexEntriesCoalesced"));
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(450, true)]
    public async Task Build_WhenTrimmedInstructionsStillBreakTheCap_DropsOldestDirectivesThenUserInstructions_NeverTheNewestUserOne(
        long cap,
        bool usersMustDrop
    )
    {
        // Nine carried instructions at the 400-char trim floor overflow the envelope. The last resort drops the agent
        // directives oldest first; only once every directive is gone do user instructions drop, oldest first; the newest
        // user instruction always stays. Whatever drops stays inside an index span, so RecallConversation still reads it.
        var thread = new ThreadFixture()
            .Human("USER-OLD-1 " + new string('u', 900))
            .ToolTurns(1)
            .Human("USER-OLD-2 " + new string('w', 900))
            .ToolTurns(1);
        for (var steer = 1; steer <= 6; steer++)
        {
            _ = thread.AgentSteer($"STEER-{steer} " + new string('s', 900)).ToolTurns(1);
        }

        _ = thread.Human("USER-NEW " + new string('v', 900)).ToolTurns(1).Run("run-2").Human("now finish").ToolTurns(3);
        var cut = CutSelector
            .Select(
                new CutRequest(
                    thread.Rows,
                    thread.LastSeq - 2,
                    CutBlockingState.Clean,
                    [],
                    null,
                    ThreadFixture.Options(minTail: 10)
                )
            )
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;
        var logger = new ListLogger();

        var build = await Pipeline(
                new ScriptedSummarizer(_ => throw new HttpRequestException("provider down")),
                logger,
                new CheckpointValidationOptions { CheckpointTokenCap = cap }
            )
            .BuildAsync(Request(thread.Rows, cut) with { SummaryFallback = true });

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        var manifest = build.Checkpoint!.Manifest;
        bool Kept(string tag) => manifest.Instructions.Any(q => q.Quote.Contains(tag + " ", StringComparison.Ordinal));
        Kept("USER-NEW").Should().BeTrue("the newest user instruction is never dropped");
        manifest
            .Instructions.Should()
            .OnlyContain(q => q.Quote.Contains("full text: RecallConversation seq"), "every kept one is at its trim");
        var steers = Enumerable.Range(1, 6).Select(s => Kept($"STEER-{s}")).ToList();
        steers.Should().BeInAscendingOrder("directives drop oldest first").And.Contain(false);
        if (usersMustDrop)
        {
            steers.Should().NotContain(true, "a user instruction drops only after every directive");
            Kept("USER-OLD-1").Should().BeFalse("past the directives, the oldest user instruction drops");
            if (!Kept("USER-OLD-2"))
            {
                Kept("USER-OLD-1").Should().BeFalse("user instructions drop oldest first");
            }
        }
        else
        {
            Kept("USER-OLD-1").Should().BeTrue("user instructions survive while a directive can still drop");
            Kept("USER-OLD-2").Should().BeTrue("user instructions survive while a directive can still drop");
            manifest
                .Index.Should()
                .HaveCountGreaterThan(1, "instructions drop before the index coalesces to one entry");
        }

        var droppedSeqs = thread
            .Rows.Where(r => r.IsHumanRow && r.Seq <= cut.Seq && manifest.Instructions.All(q => q.Seq != r.Seq))
            .Where(r => manifest.CurrentInstruction.All(q => q.Seq != r.Seq))
            .Select(r => r.Seq)
            .ToList();
        droppedSeqs.Should().NotBeEmpty();
        droppedSeqs
            .Should()
            .OnlyContain(
                seq => manifest.Index.Any(e => e.FromSeq <= seq && seq <= e.ToSeq),
                "a dropped instruction stays recallable through an index span"
            );

        var warning = logger
            .Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Properties.ContainsKey("DroppedDirectiveSeqs"))
            .Subject;
        ((int)warning.Properties["InstructionsDropped"]!).Should().Be(droppedSeqs.Count);
        ((int)warning.Properties["DroppedDirectiveCount"]!).Should().Be(steers.Count(s => !s));
        ((int)warning.Properties["DroppedUserCount"]!).Should().Be(usersMustDrop ? droppedSeqs.Count - 6 : 0);
        warning.Message.Should().NotContain("STEER-").And.NotContain("USER-", "counts and seqs only, never text");
    }

    [Fact]
    public async Task Build_ASummaryNarrativeOverTheV7Cap_StillFailsV7_EvenWhenTrimmingItWouldFitTheEnvelope()
    {
        // The budget pass exists for V9 alone. A narrative over its own cap says the summary model misbehaved, which starts
        // backoff; trimming it to fit the envelope would turn that V7 failure into a pass.
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var logger = new ListLogger();
        var narrative = new string('n', 20_000);
        var summarizer = Summarizer(_ => new CheckpointSummaryResponse(
            new CheckpointSummary { Goals = ["green"], Narrative = narrative },
            SummaryUsage()
        ));

        var build = await Pipeline(summarizer, logger, new CheckpointValidationOptions { CheckpointTokenCap = 3_000 })
            .BuildAsync(Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))));

        build.Validation!.Rule.Should().Be("V7");
        build.Checkpoint!.Narrative.Should().Be(narrative, "the budget pass never touches a narrative that fails V7");
        logger.Entries.Should().NotContain(e => e.Properties.ContainsKey("NarrativeCharsTrimmed"));
    }

    [Fact]
    public async Task Build_WhenOnlyTheNewestUserInstructionCouldStillShrink_KeepsIt_AndReportsTheOverflowAsV9()
    {
        // This build's own sections (a trimmed pasted current instruction and three file artifacts) leave no room for the
        // newest user instruction even at its trim. It is kept anyway: the checkpoint fails V9 and the warning says why.
        var thread = new ThreadFixture()
            .Human("USER-NEW " + new string('v', 900))
            .ToolTurns(1)
            .Run("run-2")
            .Human("PASTED " + new string('p', 20_000));
        for (var file = 1; file <= 3; file++)
        {
            _ = thread.ToolTurn(tool: "Write", args: $$"""{"file_path":"src/{{file}}/{{new string('f', 400)}}.cs"}""");
        }

        _ = thread.ToolTurns(2);
        var cut = CutSelector
            .Select(
                new CutRequest(
                    thread.Rows,
                    thread.LastSeq - 2,
                    CutBlockingState.Clean,
                    [],
                    null,
                    ThreadFixture.Options(10)
                )
            )
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;
        var logger = new ListLogger();

        var build = await Pipeline(
                new ScriptedSummarizer(_ => throw new HttpRequestException("provider down")),
                logger,
                new CheckpointValidationOptions { CheckpointTokenCap = 1_000 }
            )
            .BuildAsync(Request(thread.Rows, cut) with { SummaryFallback = true });

        build.Validation!.Rule.Should().Be("V9");
        build.Checkpoint!.Manifest.Instructions.Should().ContainSingle().Which.Quote.Should().StartWith("USER-NEW");
        logger
            .Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Properties.ContainsKey("SectionTokens"))
            .Which.Message.Should()
            .Contain("CurrentInstruction")
            .And.NotContain("USER-NEW");
    }

    /// <summary>
    ///     A Workspace Agent fan-out: <paramref name="agents" /> spawns (an <c>Agent</c> call carrying the task) whose
    ///     completion notifications each carry a long result. Returns the
    ///     roster and, by agent id, the seqs of its spawn call and its completion Notify row.
    /// </summary>
    private static (IReadOnlyList<AgentRef> Roster, IReadOnlyDictionary<string, (long Spawn, long Notify)> Seqs) FanOut(
        ThreadFixture thread,
        int agents,
        int taskChars,
        int resultChars
    )
    {
        var roster = new List<AgentRef>();
        var seqs = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        for (var n = 1; n <= agents; n++)
        {
            var id = $"agent-{n}";
            var task = $"TASK-{n} " + new string('t', taskChars);
            var result = $"RESULT-{n}-HEAD " + new string('r', resultChars) + $" RESULT-{n}-TAIL";
            _ = thread.ToolTurn(tool: "Agent", args: $$"""{"subagent_type":"reviewer","prompt":"{{task}}"}""");
            var spawn = thread.LastSeq - 1;
            _ = thread.Notify(
                label: "reviewer",
                detail: $"<sub-agent name=\"reviewer\" template=\"reviewer\" id=\"{id}\">\n[Completed] Task: {task}\nResult: {result}\n</sub-agent>",
                sourceToolCallId: id
            );
            seqs[id] = (spawn, thread.LastSeq);
            roster.Add(
                new AgentRef
                {
                    AgentId = id,
                    Template = "reviewer",
                    Task = task,
                    Status = "completed",
                }
            );
        }

        return (roster, seqs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_ASixAgentFanOutWithLongResults_BoundsEachOutcomeAroundAMarkerForItsNotifyRow_AndFitsV9(
        bool fallback
    )
    {
        // Each outcome used to be the whole <sub-agent> block, uncapped: six ~5k-char results broke a 20k window's
        // 3,000-token envelope on every summarised and fallback build, and the fit check refused the turn.
        var thread = new ThreadFixture().Human("review the six modules in parallel").ToolTurns(1);
        var (roster, seqs) = FanOut(thread, agents: 6, taskChars: 2_000, resultChars: 5_000);
        _ = thread.ToolTurns(3);
        var cut = CutSelector
            .Select(
                new CutRequest(
                    thread.Rows,
                    thread.LastSeq - 2,
                    CutBlockingState.Clean,
                    [],
                    null,
                    ThreadFixture.Options(minTail: 10)
                )
            )
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;
        ICheckpointSummarizer summarizer = fallback
            ? new ScriptedSummarizer(_ => throw new HttpRequestException("provider down"))
            : Summarizer(_ => new CheckpointSummaryResponse(new() { Narrative = "Six reviews ran." }, null));

        var build = await Pipeline(
                summarizer,
                validation: new CheckpointValidationOptions { CheckpointTokenCap = 3_000 }
            )
            .BuildAsync(Request(thread.Rows, cut) with { Roster = roster, SummaryFallback = fallback });

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        build.Checkpoint!.Stats.SummaryFallback.Should().Be(fallback ? CompactionReasons.SummaryCallFailed : null);
        var bySeq = thread.Rows.ToDictionary(r => r.Seq);
        build.Checkpoint.Manifest.Agents.Should().HaveCount(6);
        foreach (var agent in build.Checkpoint.Manifest.Agents)
        {
            var notify = seqs[agent.AgentId].Notify;
            agent.Outcome.Should().NotContain("<sub-agent", "the outcome is the result, not the whole block");
            agent
                .Outcome.Should()
                .StartWith($"RESULT-{agent.AgentId[6..]}-HEAD")
                .And.EndWith($"RESULT-{agent.AgentId[6..]}-TAIL")
                .And.Contain($"full text: RecallConversation seq {notify}]");
            CurrentInstructionQuotes
                .IsTrimOf(agent.Outcome!, notify, bySeq[notify].Text!)
                .Should()
                .BeTrue("the Notify row RecallConversation reads holds the omitted span exactly");
            agent.Task.Should().Contain($"full text: RecallConversation seq {notify}]");
            CurrentInstructionQuotes.IsTrimOf(agent.Task!, notify, bySeq[notify].Text!).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Build_WhenBoundedAgentsStillBreakTheCap_TheirTasksAndOutcomesShrinkTowardTheMarker_BeforeAnyInstructionIsTrimmed()
    {
        // Ten bounded agents alone overflow a 1,000-token envelope. Everything an agent line loses is verbatim in a row the
        // marker names, so agents shrink before the narrative or a user's instruction does. A running agent has no
        // completion row: its task's marker names the spawn call.
        var standing = "USER-OLD keep module-7 stable " + new string('w', 870);
        var thread = new ThreadFixture().Human(standing).ToolTurns(1).Run("run-2").Human("now fan out");
        var (completed, seqs) = FanOut(thread, agents: 10, taskChars: 2_000, resultChars: 5_000);
        var runningTask = "TASK-RUNNING " + new string('q', 2_000);
        _ = thread.ToolTurn(tool: "Agent", args: $$"""{"subagent_type":"reviewer","prompt":"{{runningTask}}"}""");
        var runningSpawn = thread.LastSeq - 1;
        _ = thread.ToolTurns(3);
        var roster = completed
            .Append(
                new AgentRef
                {
                    AgentId = "agent-11",
                    Template = "reviewer",
                    Task = runningTask,
                    Status = "running",
                }
            )
            .ToList();
        var cut = CutSelector
            .Select(
                new CutRequest(
                    thread.Rows,
                    thread.LastSeq - 2,
                    CutBlockingState.Clean,
                    [],
                    null,
                    ThreadFixture.Options(minTail: 10)
                )
            )
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;
        var logger = new ListLogger();

        var build = await Pipeline(
                new ScriptedSummarizer(_ => throw new HttpRequestException("provider down")),
                logger,
                new CheckpointValidationOptions { CheckpointTokenCap = 1_000 }
            )
            .BuildAsync(Request(thread.Rows, cut) with { Roster = roster, SummaryFallback = true });

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        var manifest = build.Checkpoint!.Manifest;
        manifest.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = standing });
        var bySeq = thread.Rows.ToDictionary(r => r.Seq);
        foreach (var agent in manifest.Agents.Take(10))
        {
            var notify = seqs[agent.AgentId].Notify;
            CurrentInstructionQuotes.IsTrimOf(agent.Outcome!, notify, bySeq[notify].Text!).Should().BeTrue();
        }

        manifest
            .Agents.Take(10)
            .Should()
            .OnlyContain(
                a => a.Outcome!.Length < ManifestAssembler.MaxAgentOutcomeChars,
                "the budget shrank them past the assembly bound"
            );
        var running = manifest.Agents[10];
        running.Outcome.Should().BeNull();
        running.Task.Should().Contain($"full text: RecallConversation seq {runningSpawn}]");
        bySeq[runningSpawn]
            .Message.Should()
            .BeOfType<ToolCallMessage>()
            .Which.FunctionArgs.Should()
            .Contain(runningTask);
        var shrank = logger
            .Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Information && e.Properties.ContainsKey("AgentsTrimmed"))
            .Subject;
        ((int)shrank.Properties["AgentsTrimmed"]!).Should().BeGreaterThan(0);
        shrank.Properties["InstructionsTrimmed"].Should().Be(0);
        shrank.Properties["NarrativeCharsTrimmed"].Should().Be(0);
        shrank.Message.Should().NotContain("RESULT-").And.NotContain("TASK-", "counts only, never text");
    }

    [Fact]
    public async Task Build_AStandingInstructionWhoseTextHoldsAMarkerForItsOwnSeq_ShrinksToAnExactTrim_AndValidates()
    {
        // Whole at assembly, the row is shrunk by the budget. Its own text holds a marker naming seq 1, so the budget read it
        // as already trimmed, wrote a count that is not the row's, and V3 failed the checkpoint. The text never repeats
        // with a short period, so a count off by a few chars cannot still line the tail up.
        static string Distinct(int length, int salt) =>
            string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + (((i * 7) + salt) % 23))));
        var row =
            "USER-OLD "
            + Distinct(1_140, 0)
            + "\n[… 50 chars omitted; full text: RecallConversation seq 1]\n"
            + Distinct(700, 5);
        var thread = new ThreadFixture()
            .Human(row)
            .ToolTurns(1)
            .Run("run-2")
            .Human("PASTED " + new string('p', 20_000))
            .ToolTurn(tool: "Write", args: $$"""{"file_path":"src/{{new string('f', 400)}}.cs"}""")
            .ToolTurns(2);
        var cut = CutSelector
            .Select(
                new CutRequest(
                    thread.Rows,
                    thread.LastSeq - 2,
                    CutBlockingState.Clean,
                    [],
                    null,
                    ThreadFixture.Options(minTail: 10)
                )
            )
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;

        var build = await Pipeline(
                new ScriptedSummarizer(_ => throw new HttpRequestException("provider down")),
                validation: new CheckpointValidationOptions { CheckpointTokenCap = 1_000 }
            )
            .BuildAsync(Request(thread.Rows, cut) with { SummaryFallback = true });

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        var quote = build.Checkpoint!.Manifest.Instructions.Should().ContainSingle().Subject;
        quote.Seq.Should().Be(1);
        quote.Quote.Should().NotBe(row, "the budget had to shrink it");
        CurrentInstructionQuotes.IsTrimOf(quote.Quote, 1, row).Should().BeTrue();
    }

    private static async Task AppendRunAsync(IConversationStore store, string runId, ThreadFixture thread) =>
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, runId)
        );

    [Fact]
    public async Task Build_WithSummaryFallback_ASummaryThatFailsValidation_IsReplacedByTheFallback_NamingTheRule()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var logger = new ListLogger();
        var summarizer = Summarizer(_ => new CheckpointSummaryResponse(
            new CheckpointSummary { Goals = ["green"], Narrative = new string('n', 20_000) },
            SummaryUsage()
        ));

        var build = await Pipeline(summarizer, logger)
            .BuildAsync(Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))) with { SummaryFallback = true });

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        build.Checkpoint!.Stats.SummaryFallback.Should().Be("validation_failed:V7");
        build.Checkpoint.Narrative.Should().Be(CheckpointPipeline.FallbackNarrative);
        build.Checkpoint.Manifest.Goals.Should().BeEmpty("no model section of a failed summary is kept");
        logger
            .Entries.Should()
            .Contain(e => e.Level == LogLevel.Warning && Equals(e.Properties["ValidationRule"], "V7"));
    }

    [Fact]
    public async Task Build_WithSummaryFallback_AndAFailureThisPassAlreadySaw_NeverCallsTheModel()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var summarizer = Summarizer(_ => GoodSummary());

        var build = await Pipeline(summarizer)
            .BuildAsync(
                Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))) with
                {
                    SummaryFallback = true,
                    KnownSummaryFailure = "validation_failed:V3",
                }
            );

        build.IsValid.Should().BeTrue(build.Validation?.Detail);
        build.Checkpoint!.Stats.SummaryFallback.Should().Be("validation_failed:V3");
        summarizer.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_SummaryCallThatFailsOnce_IsRetried_AndTheSummarizerGetsTheBounds(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var calls = 0;
        var summarizer = new ScriptedSummarizer(_ =>
            ++calls == 1 ? throw new HttpRequestException("provider blip") : Task.FromResult(GoodSummary())
        );
        var pipeline = new CheckpointPipeline(
            summarizer,
            new CheckpointPipelineOptions { Estimator = ThreadFixture.RowTokens, SummaryAttempts = 2 },
            new FixedClock(T0)
        );

        var result = await pipeline.RunAsync(
            store,
            Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))) with
            {
                SummaryMaxOutputTokens = 1_234,
                SummaryRowCharCap = 500,
                SummaryPromptCharBudget = 9_000,
            }
        );

        result.Outcome.Should().Be(CheckpointOutcome.Activated, result.Reason);
        summarizer.Requests.Should().HaveCount(2, "one retry after the failed call");
        summarizer
            .Requests[1]
            .Should()
            .Match<CheckpointSummaryRequest>(r =>
                r.MaxOutputTokens == 1_234 && r.RowCharCap == 500 && r.PromptCharBudget == 9_000
            );
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_SummaryCallThatFails_LogsTheFailureTypeAndStatus_NeverTheProviderBody(string kind)
    {
        // HttpRetryHelper puts the provider's response body in the message, and a body can quote the rows being
        // summarised (#774 F-004). The log keeps the category only.
        const string secret = "SECRET-CONVERSATION-TEXT";
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var summarizer = new ScriptedSummarizer(_ =>
            throw new HttpRequestException(
                $"HTTP request failed with status BadRequest. Response body: {{\"error\":\"{secret}\"}}",
                new InvalidOperationException(secret),
                System.Net.HttpStatusCode.BadRequest
            )
        );
        var logger = new ListLogger();
        var pipeline = new CheckpointPipeline(
            summarizer,
            new CheckpointPipelineOptions
            {
                Estimator = ThreadFixture.RowTokens,
                SummaryAttempts = 2,
                Logger = logger,
            },
            new FixedClock(T0)
        );

        var result = await pipeline.RunAsync(store, Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))));

        result.Reason.Should().Be(CompactionReasons.SummaryCallFailed);
        var failures = logger.Entries.Where(e => e.Properties.ContainsKey("Attempt")).ToList();
        failures.Should().HaveCount(2, "one warning per failed attempt");
        failures
            .Should()
            .OnlyContain(e =>
                Equals(e.Properties["ExceptionType"], nameof(HttpRequestException))
                && Equals(e.Properties["HttpStatus"], 400)
            );
        logger
            .Entries.Should()
            .NotContain(
                e =>
                    e.Message.Contains(secret, StringComparison.Ordinal)
                    || e.Properties.Values.Any(v =>
                        v != null && v.ToString()!.Contains(secret, StringComparison.Ordinal)
                    )
                    || (e.Exception != null && e.Exception.ToString().Contains(secret, StringComparison.Ordinal)),
                "no log line carries the provider's body"
            );
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_SummaryCallThatNeverAnswers_TimesOutOnEachAttempt_AndIsRejectedWithSummaryCallFailed(
        string kind
    )
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        // Ignores its token: only the pipeline's own bound can end the wait.
        var summarizer = new ScriptedSummarizer(_ => new TaskCompletionSource<CheckpointSummaryResponse>().Task);
        var pipeline = new CheckpointPipeline(
            summarizer,
            new CheckpointPipelineOptions
            {
                Estimator = ThreadFixture.RowTokens,
                SummaryAttempts = 2,
                SummaryTimeout = TimeSpan.FromMilliseconds(50),
            },
            new FixedClock(T0)
        );

        var result = await pipeline.RunAsync(store, Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))));

        result.Outcome.Should().Be(CheckpointOutcome.Rejected);
        result.Reason.Should().Be(CompactionReasons.SummaryCallFailed);
        summarizer.Requests.Should().HaveCount(2);
        (await CompactionStateProjection.LoadAsync(store, Thread))!
            .Find("cp-1")!
            .Status.Should()
            .Be(CheckpointStatus.Rejected);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_CancelledDuringTheSummary_AbandonsThePreparedEntry_AndRethrows(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        using var cts = new CancellationTokenSource();
        var summarizer = new ScriptedSummarizer(_ =>
        {
            cts.Cancel();
            return new TaskCompletionSource<CheckpointSummaryResponse>().Task;
        });
        var pipeline = new CheckpointPipeline(
            summarizer,
            new CheckpointPipelineOptions
            {
                Estimator = ThreadFixture.RowTokens,
                SummaryTimeout = TimeSpan.FromSeconds(30),
            },
            new FixedClock(T0)
        );

        var run = () => pipeline.RunAsync(store, Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        var entry = (await CompactionStateProjection.LoadAsync(store, Thread))!.Find("cp-1")!;
        entry.Status.Should().Be(CheckpointStatus.Rejected, "no Prepared entry is left looking in flight");
        entry.Reason.Should().Be(CheckpointReasons.Abandoned);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_RowAppendedDuringTheSummary_IsRejectedStaleWatermark_AndNoRowIsWritten(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var other = _harness.Reopen(kind);
        var summarizer = new ScriptedSummarizer(async _ =>
        {
            await AppendTurnsAsync(other, 1); // someone else appends while we summarize
            return GoodSummary();
        });

        var result = await Pipeline(summarizer).RunAsync(store, Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))));

        result.Outcome.Should().Be(CheckpointOutcome.Rejected);
        result.Reason.Should().Be(CheckpointReasons.StaleWatermark);
        (await CheckpointRowAsync(store, "cp-1")).Should().BeNull();
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(13, "only the other writer's rows landed");
        (await CompactionStateProjection.LoadAsync(store, Thread))!.ActiveCheckpointId.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_RowsBehindTheStore_SkipWithWatermarkDrift_BeforeAnythingIsPrepared(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var stale = rows.Take(rows.Count - 1).ToList();
        var summarizer = Summarizer(_ => GoodSummary());

        var result = await Pipeline(summarizer).RunAsync(store, Request(stale, CutAt(stale, ThreadFixture.TurnEnd(3))));

        result.Outcome.Should().Be(CheckpointOutcome.Skipped);
        result.Reason.Should().Be(CompactionReasons.WatermarkDrift);
        summarizer.Requests.Should().BeEmpty();
        var state = await CompactionStateProjection.LoadAsync(store, Thread);
        (state?.History ?? []).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_SecondCheckpoint_ChainsTheFirst_AndSupersedesIt(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var first = await Pipeline(Summarizer(_ => GoodSummary()))
            .RunAsync(store, Request(await RowsAsync(store), CutAt(await RowsAsync(store), ThreadFixture.TurnEnd(3))));
        first.Outcome.Should().Be(CheckpointOutcome.Activated);
        await AppendTurnsAsync(store, 2); // rows 13..16
        var rows = await RowsAsync(store);
        var cut = CutAt(rows, 14, activeBoundary: 7);
        var summarizer = Summarizer(_ => new CheckpointSummaryResponse(
            new CheckpointSummary
            {
                Headlines = new Dictionary<string, string>(StringComparer.Ordinal) { ["run-1"] = "more turns" },
                Narrative = "Then two more turns.",
            },
            null
        ));

        var second = await Pipeline(summarizer).RunAsync(store, Request(rows, cut, "cp-2", first.Checkpoint));

        second.Outcome.Should().Be(CheckpointOutcome.Activated);
        second.RowSeq.Should().Be(17);
        var checkpoint = second.Checkpoint!;
        checkpoint.SupersedesCheckpointId.Should().Be("cp-1");
        checkpoint.Manifest.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = "flaky" });
        checkpoint.Manifest.Goals.Should().Equal("green");
        checkpoint
            .Manifest.Index.Select(e => (e.FromSeq, e.ToSeq, e.Headline))
            .Should()
            .Equal((1L, 7L, "the fix"), (8L, 14L, "more turns"));
        checkpoint.Stats.SummaryUsageAttemptId.Should().BeNull("this summarizer made no model call");
        summarizer
            .Requests.Should()
            .ContainSingle()
            .Which.Should()
            .Match<CheckpointSummaryRequest>(r =>
                r.PreviousManifest == first.Checkpoint!.Manifest && r.PreviousNarrative == first.Checkpoint.Narrative
            );
        summarizer.Requests[0].Rows.Select(r => r.Seq).Should().Equal(8, 9, 10, 11, 12, 13, 14);

        var state = await CompactionStateProjection.LoadAsync(store, Thread);
        state!.ActiveCheckpointId.Should().Be("cp-2");
        state.Find("cp-1")!.Status.Should().Be(CheckpointStatus.Superseded);

        var view = AgentContextProjection.Default.Build(
            "sys",
            await RowsAsync(store),
            await CheckpointRowAsync(store, "cp-2")
        );
        view.Should()
            .HaveCount(1 + 1 + 2, "rows 15 and 16 remain")
            .And.NotContain(m => m is CompactionCheckpointMessage);
    }

    [Fact]
    public async Task Build_ProducesAValidatedCheckpoint_WithoutTouchingTheStore()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var rows = await RowsAsync(store);

        var build = await Pipeline(Summarizer(_ => GoodSummary()))
            .BuildAsync(Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))));

        build.IsValid.Should().BeTrue();
        build.Reason.Should().BeNull();
        build.Checkpoint!.Trigger.Should().Be(CompactionTrigger.Preemptive);
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(11);
        (await CompactionStateProjection.LoadAsync(store, Thread)).Should().BeNull();
    }

    [Fact]
    public async Task Build_WithABoardAndRoster_CopiesThem_AndValidatesAgainstThem()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var board = new TodoBoardSnapshot
        {
            ThreadId = Thread,
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = "fix",
                },
            ],
        };
        var roster = new[]
        {
            new AgentRef
            {
                AgentId = "agent-1",
                Status = "Running",
                Task = "lint",
            },
        };
        var request = Request(rows, CutAt(rows, ThreadFixture.TurnEnd(3))) with { Board = board, Roster = roster };

        var build = await Pipeline(Summarizer(_ => GoodSummary())).BuildAsync(request);

        build.IsValid.Should().BeTrue();
        build
            .Checkpoint!.Manifest.Tasks.Should()
            .Equal(
                new TaskRef
                {
                    Id = "1",
                    Title = "fix",
                    Status = "InProgress",
                }
            );
        build.Checkpoint.Manifest.Agents.Should().ContainSingle().Which.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task Build_WithTheRc3CheckOn_PinsTheOpenExchange_AndStillValidates()
    {
        var store = _harness.Open("memory");
        var thread = new ThreadFixture()
            .Human("fix the flaky test")
            .Agent(AgentMessageType.Question, "which db?")
            .ToolTurns(4);
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, "run-1")
        );
        var rows = await RowsAsync(store);

        var build = await Pipeline(
                Summarizer(_ => GoodSummary()),
                validation: new CheckpointValidationOptions { OpenExchanges = true },
                assembler: new ManifestAssemblerOptions { OpenExchanges = true }
            )
            .BuildAsync(Request(rows, CutAt(rows, 6)));

        build.IsValid.Should().BeTrue();
        build.Checkpoint!.Manifest.OpenExchanges.Should().ContainSingle().Which.MessageId.Should().Be("msg-2");
    }
}
