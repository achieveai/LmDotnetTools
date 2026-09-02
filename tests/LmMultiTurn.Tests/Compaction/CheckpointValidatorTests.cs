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
        ExpectRule(CheckpointValidator.Validate(Checkpoint() with { SchemaVersion = 2 }, Context()), "V1");
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
