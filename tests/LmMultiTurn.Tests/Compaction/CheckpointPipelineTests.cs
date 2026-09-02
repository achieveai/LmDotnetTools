using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the checkpoint pipeline over the #680 state machine on every store flavour (#683; spec 679
/// §3.2–§3.5, §12.2): a valid checkpoint appends its row and activates; an invalid one is rejected
/// with its typed reason and the view is unchanged (last-known-good); a paraphrasing summarizer fails
/// V3 (the R5 mutation); a row appended during the summary rejects with <c>stale_watermark</c>; rows
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

    private static CheckpointPipeline Pipeline(ICheckpointSummarizer summarizer) =>
        new(summarizer, new CheckpointPipelineOptions { Estimator = ThreadFixture.RowTokens }, new FixedClock(T0));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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
            .StartWith("<context-checkpoint version=\"1\" id=\"cp-1\" covers_seq=\"1-7\"");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Run_ParaphrasingSummarizer_IsRejectedByV3_AndNothingChanges(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var rows = await RowsAsync(store);
        var cut = CutAt(rows, ThreadFixture.TurnEnd(3));

        var result = await Pipeline(Summarizer(_ => GoodSummary("repair the unstable test")))
            .RunAsync(store, Request(rows, cut));

        result.Outcome.Should().Be(CheckpointOutcome.Rejected);
        result.Reason.Should().Be("validation_failed:V3");
        result.Validation!.Rule.Should().Be("V3");
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

        var second = await Pipeline(Summarizer(_ => GoodSummary("something the human never said")))
            .RunAsync(store, Request(rows, cut, "cp-2", first.Checkpoint));

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
}
