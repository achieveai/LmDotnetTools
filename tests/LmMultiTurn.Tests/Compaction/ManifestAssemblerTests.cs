using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins which manifest sections the model may not decide (#683; spec 679 §3.3): the current
/// instruction, the board, the roster, the index spans and file-tool artifacts come from the rows and
/// the loop's state; the summary adds quotes, goals, headlines, outcomes and named artifacts; and a
/// second checkpoint merges the first's manifest field by field (§2.5).
/// </summary>
public sealed class ManifestAssemblerTests
{
    /// <summary>A cut exactly at <paramref name="seq"/>: no floor and no correction lookback, so the assembler is what is under test.</summary>
    private static CutDecision.Cut CutAt(ThreadFixture thread, long seq) =>
        CutSelector
            .Select(thread.Request(seq, ThreadFixture.Options(minTail: 0, lookback: 0)))
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;

    private static CheckpointSummary Summary(
        IReadOnlyList<QuotedItem>? instructions = null,
        IReadOnlyList<string>? goals = null,
        IReadOnlyList<TaskRef>? tasks = null,
        IReadOnlyList<ArtifactRef>? artifacts = null,
        IReadOnlyDictionary<string, string>? headlines = null,
        IReadOnlyDictionary<string, string>? outcomes = null
    ) =>
        new()
        {
            Instructions = instructions ?? [],
            Goals = goals ?? [],
            Tasks = tasks ?? [],
            Artifacts = artifacts ?? [],
            Headlines = headlines ?? new Dictionary<string, string>(StringComparer.Ordinal),
            AgentOutcomes = outcomes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Narrative = "n",
        };

    private static ContextManifest Assemble(
        ThreadFixture thread,
        CutDecision.Cut cut,
        CheckpointSummary? summary = null,
        ContextManifest? previous = null,
        long previousBoundary = 0,
        TodoBoardSnapshot? board = null,
        IReadOnlyList<AgentRef>? roster = null,
        ManifestAssemblerOptions? options = null
    ) =>
        ManifestAssembler.Assemble(
            thread.Rows,
            cut,
            previous,
            previousBoundary,
            summary ?? Summary(),
            board,
            roster ?? [],
            options
        );

    [Fact]
    public void Recovery_IsCopiedFromTheCut_NotRecomputedAsClean()
    {
        // Select() only ever returns a clean recovery, so the copy is pinned on a cut built by hand.
        var thread = new ThreadFixture().Human("go").ToolTurns(3);
        var recovery = new RecoveryStateAtCut
        {
            DeferredToolCalls = 2,
            ParkedWaits = 1,
            OwedContinuations = 3,
            InterruptedTurns = 1,
        };
        var cut = CutAt(thread, thread.LastSeq) with { Recovery = recovery };

        var manifest = Assemble(thread, cut);

        manifest.Recovery.Should().Be(recovery);
        manifest.Recovery.IsClean.Should().BeFalse();
    }

    [Fact]
    public void CurrentInstruction_IsTheCutsHumanRows_QuotedWhole()
    {
        var thread = new ThreadFixture().Human("fix the flaky test").ToolTurns(3).Human("and lint").ToolTurns(3);
        var cut = CutAt(thread, thread.LastSeq);

        var manifest = Assemble(thread, cut);

        manifest
            .CurrentInstruction.Should()
            .Equal(
                new QuotedItem { Seq = 1, Quote = "fix the flaky test" },
                new QuotedItem { Seq = 8, Quote = "and lint" }
            );
        manifest.Recovery.Should().Be(cut.Recovery);
    }

    [Fact]
    public void Tasks_ComeFromTheBoard_FlattenedWithoutRemovedOnes_NotFromTheModel()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(2);
        var board = new TodoBoardSnapshot
        {
            ThreadId = "t",
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = "parent",
                    SubTasks =
                    [
                        new TodoTaskNode
                        {
                            Id = "1.1",
                            Status = TodoTaskStatus.Completed,
                            Title = "child",
                        },
                        new TodoTaskNode
                        {
                            Id = "1.2",
                            Status = TodoTaskStatus.Removed,
                            Title = "gone",
                        },
                    ],
                },
                new TodoTaskNode
                {
                    Id = "2",
                    Status = TodoTaskStatus.NotStarted,
                    Title = "next",
                },
            ],
        };
        var modelTasks = new[]
        {
            new TaskRef
            {
                Id = "9",
                Title = "invented",
                Status = "open",
            },
        };

        var manifest = Assemble(thread, CutAt(thread, thread.LastSeq), Summary(tasks: modelTasks), board: board);

        manifest
            .Tasks.Select(t => (t.Id, t.Title, t.Status))
            .Should()
            .Equal(("1", "parent", "InProgress"), ("1.1", "child", "Completed"), ("2", "next", "NotStarted"));
    }

    [Fact]
    public void Tasks_FromTheModel_LoseTheirIds_WhenThereIsNoBoard()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(2);
        var modelTasks = new[]
        {
            new TaskRef
            {
                Id = "9",
                Title = "extracted",
                Status = "open",
            },
        };

        var manifest = Assemble(thread, CutAt(thread, thread.LastSeq), Summary(tasks: modelTasks), board: null);

        manifest
            .Tasks.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new TaskRef
                {
                    Id = null,
                    Title = "extracted",
                    Status = "open",
                }
            );
    }

    [Fact]
    public void Agents_AreTheRoster_WithOutcomesFromCompletionNotifies_ThenTheModel()
    {
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurns(1)
            .Notify(label: "agent-1 finished: tests green")
            .ToolTurns(1);
        var roster = new[]
        {
            new AgentRef
            {
                AgentId = "agent-1",
                Template = "coder",
                Task = "fix",
                Status = "Completed",
            },
            new AgentRef
            {
                AgentId = "agent-2",
                Template = "coder",
                Task = "lint",
                Status = "Running",
            },
            new AgentRef
            {
                AgentId = "agent-3",
                Template = "coder",
                Task = "docs",
                Status = "Completed",
            },
        };
        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent-1"] = "model says otherwise",
            ["agent-3"] = "wrote the docs",
        };

        var manifest = Assemble(thread, CutAt(thread, thread.LastSeq), Summary(outcomes: outcomes), roster: roster);

        manifest
            .Agents.Select(a => (a.AgentId, a.Status, a.Outcome))
            .Should()
            .Equal(
                ("agent-1", "Completed", "agent-1 finished: tests green"),
                ("agent-2", "Running", null),
                ("agent-3", "Completed", "wrote the docs")
            );
    }

    [Fact]
    public void Index_IsOneSpanPerRun_ContiguousFromOneToTheCut_WithModelHeadlines()
    {
        var thread = new ThreadFixture().Human("one").ToolTurns(2).Run("run-2").Human("two").ToolTurns(2);
        var headlines = new Dictionary<string, string>(StringComparer.Ordinal) { ["run-1"] = "did one" };
        var cut = CutAt(thread, thread.LastSeq);

        var manifest = Assemble(thread, cut, Summary(headlines: headlines));

        manifest
            .Index.Select(e => (e.FromSeq, e.ToSeq, e.RunId, e.Headline))
            .Should()
            .Equal((1L, 5L, "run-1", "did one"), (6L, cut.Seq, "run-2", "run-2: 5 rows"));
    }

    [Fact]
    public void Index_ChainsThePreviousEntries_AndCoversOnlyTheNewRange()
    {
        var thread = new ThreadFixture().Human("one").ToolTurns(2).Run("run-2").Human("two").ToolTurns(2);
        var previous = new ContextManifest
        {
            Index =
            [
                new IndexEntry
                {
                    FromSeq = 1,
                    ToSeq = 5,
                    RunId = "run-1",
                    Headline = "kept",
                },
            ],
            Goals = ["old goal"],
            Instructions = [new QuotedItem { Seq = 1, Quote = "one" }],
        };
        var cut = CutAt(thread, thread.LastSeq);

        var manifest = Assemble(
            thread,
            cut,
            Summary(goals: ["old goal", "new goal"], instructions: [new QuotedItem { Seq = 6, Quote = "two" }]),
            previous,
            previousBoundary: 5
        );

        manifest
            .Index.Select(e => (e.FromSeq, e.ToSeq, e.RunId))
            .Should()
            .Equal((1L, 5L, "run-1"), (6L, cut.Seq, "run-2"));
        manifest.Index[0].Headline.Should().Be("kept");
        manifest.Goals.Should().Equal("old goal", "new goal");
        manifest.Instructions.Select(q => q.Seq).Should().Equal(1, 6);
    }

    [Fact]
    public void Index_HasNoHole_WhenARowInTheRangeWasUnreadable()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(3);
        var withHole = thread.Rows.Where(r => r.Seq != 4).ToList(); // seq 4 could not be read
        var cut = CutSelector
            .Select(new CutRequest(withHole, 7, CutBlockingState.Clean, [], null, ThreadFixture.Options(minTail: 10)))
            .Should()
            .BeOfType<CutDecision.Cut>()
            .Subject;

        var manifest = ManifestAssembler.Assemble(withHole, cut, null, 0, Summary(), null, []);

        manifest
            .Index.Should()
            .ContainSingle()
            .Which.Should()
            .Match<IndexEntry>(e => e.FromSeq == 1 && e.ToSeq == cut.Seq);
    }

    [Fact]
    public void Index_CoalescesTheOldestPairs_PastTheCap()
    {
        var thread = new ThreadFixture()
            .Human("a")
            .Run("run-2")
            .Human("b")
            .Run("run-3")
            .Human("c")
            .Run("run-4")
            .Human("d");
        var cut = CutAt(thread, thread.LastSeq);

        var manifest = Assemble(thread, cut, options: new ManifestAssemblerOptions { MaxIndexEntries = 2 });

        manifest
            .Index.Select(e => (e.FromSeq, e.ToSeq, e.RunId))
            .Should()
            .Equal((1L, 3L, "run-1,run-2,run-3"), (4L, 4L, "run-4"));
    }

    [Fact]
    public void Artifacts_ComeFromFileToolCalls_UnionTheModel_DedupedByPath()
    {
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(tool: "Write", args: """{"file_path":"src/a.cs","content":"x"}""") // seq 2
            .ToolTurn(tool: "Bash", args: """{"command":"cat src/b.cs"}""") // seq 4
            .ToolTurn(tool: "edit", args: """{"path":"docs/c.md"}""") // seq 6
            .ToolTurn(tool: "Write", args: """{"file_path":"src/a.cs","content":"y"}"""); // seq 8
        var model = new[]
        {
            new ArtifactRef { Path = "src/a.cs", Hash = "abc" },
            new ArtifactRef { Path = "out/report.json", OriginSeq = 9 },
        };

        var manifest = Assemble(thread, CutAt(thread, thread.LastSeq), Summary(artifacts: model));

        manifest
            .Artifacts.Should()
            .Equal(
                new ArtifactRef
                {
                    Path = "src/a.cs",
                    Hash = "abc",
                    OriginSeq = 2,
                },
                new ArtifactRef { Path = "docs/c.md", OriginSeq = 6 },
                new ArtifactRef { Path = "out/report.json", OriginSeq = 9 }
            );
    }

    [Fact]
    public void Artifacts_OutsideTheNewRange_AreNotRescanned_ButThePreviousOnesAreKept()
    {
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(tool: "Write", args: """{"file_path":"old.cs"}""") // seq 2, before the previous boundary
            .ToolTurn(tool: "Write", args: """{"file_path":"new.cs"}"""); // seq 4
        var previous = new ContextManifest
        {
            Artifacts =
            [
                new ArtifactRef
                {
                    Path = "old.cs",
                    OriginSeq = 2,
                    Hash = "h",
                },
            ],
        };

        var manifest = Assemble(thread, CutAt(thread, thread.LastSeq), previous: previous, previousBoundary: 3);

        manifest
            .Artifacts.Should()
            .Equal(
                new ArtifactRef
                {
                    Path = "old.cs",
                    OriginSeq = 2,
                    Hash = "h",
                },
                new ArtifactRef { Path = "new.cs", OriginSeq = 4 }
            );
    }

    [Fact]
    public void Quotes_MergeWithThePrevious_DistinctAndOrderedBySeq()
    {
        var thread = new ThreadFixture()
            .Human("never push")
            .ToolTurns(1)
            .Human("approved: delete the flag")
            .ToolTurns(1);
        var previous = new ContextManifest
        {
            Instructions = [new QuotedItem { Seq = 1, Quote = "never push" }],
            Decisions = [new QuotedItem { Seq = 4, Quote = "approved" }],
        };
        var summary = new CheckpointSummary
        {
            Instructions =
            [
                new QuotedItem { Seq = 1, Quote = "never push" },
                new QuotedItem { Seq = 4, Quote = "delete the flag" },
            ],
            Decisions = [new QuotedItem { Seq = 1, Quote = "never" }],
            Narrative = "n",
        };

        var manifest = Assemble(thread, CutAt(thread, thread.LastSeq), summary, previous, previousBoundary: 3);

        manifest.Instructions.Select(q => (q.Seq, q.Quote)).Should().Equal((1L, "never push"), (4L, "delete the flag"));
        manifest.Decisions.Select(q => (q.Seq, q.Quote)).Should().Equal((1L, "never"), (4L, "approved"));
    }
}
