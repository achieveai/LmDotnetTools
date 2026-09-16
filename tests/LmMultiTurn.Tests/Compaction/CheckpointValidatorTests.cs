using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// One rejected fixture per validation rule V1–V9 (#683; spec 679 §3.4, §12.2), each a single mutation
/// of one valid checkpoint, so a rule that stops firing fails exactly its own fixture. V3 carries the
/// R5 mutation: a paraphrased human row must be rejected.
/// </summary>
public sealed class CheckpointValidatorTests
{
    private static readonly ThreadFixture Thread = new ThreadFixture()
        .Human("fix the flaky test")
        .ToolTurn(tool: "Write", args: """{"file_path":"src/a.cs","content":"x"}""")
        .ToolTurns(3);

    private static readonly long Boundary = Thread.LastSeq; // 9

    private static readonly TodoBoardSnapshot Board = new()
    {
        ThreadId = "t",
        Tasks =
        [
            new TodoTaskNode
            {
                Id = "1",
                Status = TodoTaskStatus.InProgress,
                Title = "fix it",
                SubTasks =
                [
                    new TodoTaskNode
                    {
                        Id = "1.1",
                        Status = TodoTaskStatus.NotStarted,
                        Title = "rerun",
                    },
                ],
            },
        ],
    };

    private static CheckpointValidationContext Context(
        IReadOnlyList<SequencedMessage>? rows = null,
        TodoBoardSnapshot? board = null,
        bool withBoard = true
    ) =>
        new(
            rows ?? Thread.Rows,
            withBoard ? board ?? Board : null,
            new HashSet<string>(StringComparer.Ordinal) { "agent-1" }
        );

    private static ContextManifest ValidManifest() =>
        new()
        {
            CurrentInstruction = [new QuotedItem { Seq = 1, Quote = "fix the flaky test" }],
            Instructions = [new QuotedItem { Seq = 1, Quote = "flaky test" }],
            Goals = ["green"],
            Decisions = [new QuotedItem { Seq = 1, Quote = "fix" }],
            Tasks =
            [
                new TaskRef
                {
                    Id = "1.1",
                    Title = "rerun",
                    Status = "NotStarted",
                },
            ],
            Artifacts = [new ArtifactRef { Path = "src/a.cs", OriginSeq = 2 }],
            Agents = [new AgentRef { AgentId = "agent-1", Status = "Completed" }],
            Index =
            [
                new IndexEntry
                {
                    FromSeq = 1,
                    ToSeq = Boundary,
                    RunId = "run-1",
                    Headline = "the fix",
                },
            ],
        };

    private static CompactionCheckpointMessage Checkpoint(ContextManifest? manifest = null, string? narrative = null) =>
        new()
        {
            CheckpointId = "cp-1",
            Boundary = new CheckpointBoundary { Seq = Boundary, MessageId = $"m{Boundary}" },
            Trigger = CompactionTrigger.Preemptive,
            Manifest = manifest ?? ValidManifest(),
            Narrative = narrative ?? "Wrote a.cs, then reran the tests.",
        };

    private static void ExpectRule(CheckpointValidationResult result, string rule)
    {
        result.IsValid.Should().BeFalse();
        result.Rule.Should().Be(rule);
        result.Reason.Should().Be($"validation_failed:{rule}");
        result.Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AValidCheckpoint_PassesEveryRule()
    {
        var result = CheckpointValidator.Validate(Checkpoint(), Context());

        result.IsValid.Should().BeTrue();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void V1_UnknownSchemaVersion_IsRejected()
    {
        ExpectRule(CheckpointValidator.Validate(Checkpoint() with { SchemaVersion = 3 }, Context()), "V1");
    }

    [Fact]
    public void V2_BoundaryIdThatDoesNotMatchTheRow_IsRejected()
    {
        var checkpoint = Checkpoint() with
        {
            Boundary = new CheckpointBoundary { Seq = Boundary, MessageId = "someone-else" },
        };

        ExpectRule(CheckpointValidator.Validate(checkpoint, Context()), "V2");
    }

    [Fact]
    public void V2_BoundaryPastTheLastRow_IsRejected()
    {
        var checkpoint = Checkpoint() with
        {
            Boundary = new CheckpointBoundary { Seq = Boundary + 1, MessageId = "m10" },
        };

        ExpectRule(CheckpointValidator.Validate(checkpoint, Context()), "V2");
    }

    [Fact]
    public void V2_RowsWithoutPersistedIds_CannotValidate()
    {
        var positional = SequencedHistory.FromSnapshot(Thread.Messages);

        ExpectRule(CheckpointValidator.Validate(Checkpoint(), Context(rows: positional)), "V2");
    }

    [Fact]
    public void V3_InstructionQuotePastTheBoundary_IsRejected()
    {
        var manifest = ValidManifest() with { Instructions = [new QuotedItem { Seq = Boundary + 5, Quote = "x" }] };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V3");
    }

    [Fact]
    public void V3_ParaphrasedInstruction_IsRejected_R5()
    {
        var manifest = ValidManifest() with
        {
            Instructions = [new QuotedItem { Seq = 1, Quote = "repair the unstable test" }],
        };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V3");
    }

    [Fact]
    public void V3_ParaphrasedDecision_IsRejected_R5()
    {
        var manifest = ValidManifest() with { Decisions = [new QuotedItem { Seq = 1, Quote = "Fix the flaky test" }] };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V3");
    }

    [Fact]
    public void V3_EmptyQuote_IsRejected()
    {
        var manifest = ValidManifest() with { Instructions = [new QuotedItem { Seq = 1, Quote = "" }] };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V3");
    }

    [Fact]
    public void V3_CurrentInstructionThatIsNotTheWholeRow_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            CurrentInstruction = [new QuotedItem { Seq = 1, Quote = "fix the flaky" }],
        };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V3");
    }

    /// <summary>A 40,000-char instruction and two tool turns: ten thousand tokens against a 3,000-token instruction budget.</summary>
    private static (
        ThreadFixture Thread,
        CompactionCheckpointMessage Checkpoint,
        IReadOnlyList<QuotedItem> Trim
    ) OversizedInstruction(string? instruction = null)
    {
        var thread = new ThreadFixture()
            .Human(instruction ?? ("spec:" + new string('s', 40_000) + ":end"))
            .ToolTurns(2);
        var options = new CheckpointValidationOptions();
        var trim = CurrentInstructionQuotes.Quote(
            CutSelector.CurrentInstructionRows(thread.Rows, thread.LastSeq),
            CurrentInstructionQuotes.Budget(options.CheckpointTokenCap),
            options.TextEstimator
        );
        var checkpoint = Checkpoint(
            ValidManifest() with
            {
                CurrentInstruction = trim,
                Instructions = [],
                Decisions = [],
                Artifacts = [],
                Tasks = [],
                Index =
                [
                    new IndexEntry
                    {
                        FromSeq = 1,
                        ToSeq = thread.LastSeq,
                        RunId = "run-1",
                        Headline = "h",
                    },
                ],
            }
        ) with
        {
            Boundary = new CheckpointBoundary { Seq = thread.LastSeq, MessageId = $"m{thread.LastSeq}" },
        };
        return (thread, checkpoint, trim);
    }

    [Fact]
    public void V3_ACurrentInstructionOverItsBudget_QuotedAsTheDeterministicTrim_Validates_AndFitsV9()
    {
        var (thread, checkpoint, trim) = OversizedInstruction();

        var result = CheckpointValidator.Validate(checkpoint, Context(rows: thread.Rows));

        result.IsValid.Should().BeTrue(result.Detail);
        trim.Should().ContainSingle().Which.Quote.Should().StartWith("spec:").And.EndWith(":end");
        trim[0].Quote.Should().Contain("chars omitted; full text: RecallConversation seq 1]");
    }

    [Fact]
    public void V3_ATamperedTrimOfTheCurrentInstruction_IsRejected()
    {
        var (thread, checkpoint, trim) = OversizedInstruction();
        var tampered = checkpoint with
        {
            Manifest = checkpoint.Manifest with
            {
                CurrentInstruction = [trim[0] with { Quote = trim[0].Quote.Replace("spec:", "spec;") }],
            },
        };

        ExpectRule(CheckpointValidator.Validate(tampered, Context(rows: thread.Rows)), "V3");
    }

    [Fact]
    public void V3_ATrimOfACurrentInstructionWithinItsBudget_IsRejected()
    {
        // The trim is accepted only where the shared function would produce it: a row that fits is quoted whole.
        var thread = new ThreadFixture().Human("spec:" + new string('s', 4_000)).ToolTurns(2);
        var row = thread.Rows[0];
        var forced = CurrentInstructionQuotes.Quote([row], budgetTokens: 200, CompactionTokenEstimate.EstimateText);
        var (_, checkpoint, _) = OversizedInstruction();
        var trimmed = checkpoint with
        {
            Boundary = new CheckpointBoundary { Seq = thread.LastSeq, MessageId = $"m{thread.LastSeq}" },
            Manifest = checkpoint.Manifest with { CurrentInstruction = forced },
        };

        forced[0].Quote.Should().NotBe(row.Text, "the fixture really is a trim");
        ExpectRule(CheckpointValidator.Validate(trimmed, Context(rows: thread.Rows)), "V3");
    }

    [Fact]
    public void V3_AStandingInstructionQuotedAsATrim_OfItsWholeRowOrOfASubstring_Validates()
    {
        // A carried instruction the envelope budget shrank: head and tail verbatim around a marker with the exact count.
        var (thread, checkpoint, _) = OversizedInstruction();
        var text = thread.Rows[0].Text!;
        var manifest = checkpoint.Manifest with
        {
            Instructions =
            [
                CurrentInstructionQuotes.Shrink(new QuotedItem { Seq = 1, Quote = text }, 1_000),
                CurrentInstructionQuotes.Shrink(new QuotedItem { Seq = 1, Quote = text[100..5_000] }, 800),
            ],
        };

        var result = CheckpointValidator.Validate(checkpoint with { Manifest = manifest }, Context(rows: thread.Rows));

        result.IsValid.Should().BeTrue(result.Detail);
        manifest
            .Instructions.Should()
            .OnlyContain(q => q.Quote.Contains("chars omitted; full text: RecallConversation seq 1]"));
    }

    [Theory]
    [InlineData("count+1")]
    [InlineData("count-1")]
    [InlineData("seq")]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("tail off by one")]
    [InlineData("two markers")]
    [InlineData("empty head")]
    [InlineData("empty tail")]
    [InlineData("nothing omitted")]
    public void V3_ATrimOfAStandingInstruction_ThatIsNotExact_IsRejected(string tamper)
    {
        // No run of the row repeats, and the quoted substring ends well before the row does: a head or tail off by even one
        // char matches nowhere, and the row's end cannot reject a wrong count on its own.
        static string Numbers(int from, int to) =>
            string.Concat(Enumerable.Range(from, to - from).Select(i => $"{i:D5}"));
        var (thread, checkpoint, _) = OversizedInstruction(
            "spec:" + Numbers(0, 4_000) + ":end" + Numbers(4_000, 8_000)
        );
        var row = thread.Rows[0].Text!;
        var text = row[..(row.IndexOf(":end", StringComparison.Ordinal) + 4)];
        var trim = CurrentInstructionQuotes.Shrink(new QuotedItem { Seq = 1, Quote = text }, 1_000);
        var marker = System.Text.RegularExpressions.Regex.Match(trim.Quote, @"\n\[… (\d+) chars[^\]]*\]\n");
        var (head, omitted, tail) = (
            trim.Quote[..marker.Index],
            long.Parse(marker.Groups[1].Value),
            trim.Quote[(marker.Index + marker.Length)..]
        );
        static string Marker(long n) => $"\n[… {n} chars omitted; full text: RecallConversation seq 1]\n";
        var quote = tamper switch
        {
            "count+1" => head + Marker(omitted + 1) + tail,
            "count-1" => head + Marker(omitted - 1) + tail,
            "seq" => trim.Quote.Replace("RecallConversation seq 1]", "RecallConversation seq 2]"),
            "head" => trim.Quote.Replace("spec:", "spec;"),
            "tail" => trim.Quote.Replace(":end", ";end"),
            "tail off by one" => head + Marker(omitted) + tail[1..],
            "two markers" => trim.Quote + trim.Quote[trim.Quote.IndexOf('\n')..],
            // Each of these three still names a real, exactly placed span of the row: only the strict form rejects them.
            "empty head" => Marker(text.Length - tail.Length) + tail,
            "empty tail" => head + Marker(text.Length - head.Length),
            _ => text[..head.Length] + Marker(0) + text[head.Length..(head.Length + 100)],
        };
        var manifest = checkpoint.Manifest with { Instructions = [trim with { Quote = quote }] };
        var untampered = CheckpointValidator.Validate(
            checkpoint with
            {
                Manifest = manifest with { Instructions = [trim] },
            },
            Context(rows: thread.Rows)
        );

        untampered.IsValid.Should().BeTrue($"the exact trim validates: {untampered.Detail}");
        quote.Should().NotBe(trim.Quote, "the fixture really is tampered");
        ExpectRule(
            CheckpointValidator.Validate(checkpoint with { Manifest = manifest }, Context(rows: thread.Rows)),
            "V3"
        );
    }

    [Fact]
    public void V3_CurrentInstructionOmitted_IsRejected()
    {
        var manifest = ValidManifest() with { CurrentInstruction = [] };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V3");
    }

    [Fact]
    public void V3_CurrentInstruction_MustListEveryHumanRowOfTheRun_InSeqOrder()
    {
        var thread = new ThreadFixture().Human("start").ToolTurns(2).Human("also this").ToolTurns(2);
        var boundary = thread.LastSeq;
        var manifest = ValidManifest() with
        {
            CurrentInstruction =
            [
                new QuotedItem { Seq = 6, Quote = "also this" },
                new QuotedItem { Seq = 1, Quote = "start" },
            ],
            Instructions = [],
            Decisions = [],
            Index =
            [
                new IndexEntry
                {
                    FromSeq = 1,
                    ToSeq = boundary,
                    RunId = "run-1",
                    Headline = "h",
                },
            ],
        };
        var checkpoint = Checkpoint(manifest) with
        {
            Boundary = new CheckpointBoundary { Seq = boundary, MessageId = $"m{boundary}" },
        };

        ExpectRule(CheckpointValidator.Validate(checkpoint, Context(rows: thread.Rows)), "V3");

        var ordered = checkpoint with
        {
            Manifest = manifest with
            {
                CurrentInstruction =
                [
                    new QuotedItem { Seq = 1, Quote = "start" },
                    new QuotedItem { Seq = 6, Quote = "also this" },
                ],
            },
        };
        CheckpointValidator.Validate(ordered, Context(rows: thread.Rows)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void V4_TaskIdNotOnTheBoard_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            Tasks =
            [
                new TaskRef
                {
                    Id = "9",
                    Title = "ghost",
                    Status = "NotStarted",
                },
            ],
        };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V4");
    }

    [Fact]
    public void V4_TaskIdWithoutAnyBoard_IsRejected_ButNullIdsAreFine()
    {
        ExpectRule(CheckpointValidator.Validate(Checkpoint(), Context(withBoard: false)), "V4");

        var modelExtracted = ValidManifest() with
        {
            Tasks =
            [
                new TaskRef
                {
                    Id = null,
                    Title = "rerun",
                    Status = "open",
                },
            ],
        };
        CheckpointValidator.Validate(Checkpoint(modelExtracted), Context(withBoard: false)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void V5_AgentNotInTheRoster_IsRejected()
    {
        var manifest = ValidManifest() with { Agents = [new AgentRef { AgentId = "agent-7", Status = "Running" }] };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V5");
    }

    [Fact]
    public void V6_IndexWithAGap_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            Index =
            [
                new IndexEntry
                {
                    FromSeq = 1,
                    ToSeq = 3,
                    RunId = "run-1",
                    Headline = "a",
                },
                new IndexEntry
                {
                    FromSeq = 5,
                    ToSeq = Boundary,
                    RunId = "run-1",
                    Headline = "b",
                },
            ],
        };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V6");
    }

    [Fact]
    public void V6_IndexShortOfTheBoundary_IsRejected()
    {
        var manifest = ValidManifest() with
        {
            Index =
            [
                new IndexEntry
                {
                    FromSeq = 1,
                    ToSeq = Boundary - 1,
                    RunId = "run-1",
                    Headline = "a",
                },
            ],
        };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V6");
    }

    [Fact]
    public void V6_EmptyIndex_IsRejected()
    {
        ExpectRule(CheckpointValidator.Validate(Checkpoint(ValidManifest() with { Index = [] }), Context()), "V6");
    }

    [Fact]
    public void V7_NarrativeOverTheCap_IsRejected()
    {
        var options = new CheckpointValidationOptions { NarrativeTokenCap = 4 };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(narrative: new string('n', 100)), Context(), options), "V7");
        CheckpointValidator.Validate(Checkpoint(narrative: "ok"), Context(), options).IsValid.Should().BeTrue();
    }

    [Fact]
    public void V8_RecoveryReportingBlockingItems_IsRejected()
    {
        var manifest = ValidManifest() with { Recovery = new RecoveryStateAtCut { ParkedWaits = 1 } };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(manifest), Context()), "V8");
    }

    [Fact]
    public void V9_EnvelopeOverTheCap_IsRejected()
    {
        var options = new CheckpointValidationOptions { CheckpointTokenCap = 20 };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(), Context(), options), "V9");
    }

    [Fact]
    public void V9_SizesTheEnvelopeTheProjectionDispatches_IncludingTheRecallHint()
    {
        var baseline = CompactionTokenEstimate.EstimateText(
            Checkpoint().RenderEnvelope(CheckpointRenderOptions.Default)
        );
        var options = new CheckpointValidationOptions
        {
            CheckpointTokenCap = baseline,
            Render = new CheckpointRenderOptions { RecallToolName = "RecallConversation" },
        };

        ExpectRule(CheckpointValidator.Validate(Checkpoint(), Context(), options), "V9");
        CheckpointValidator
            .Validate(Checkpoint(), Context(), options with { Render = CheckpointRenderOptions.Default })
            .IsValid.Should()
            .BeTrue();
    }
}
