using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// The envelope budget's drop order (spec 679 §3.4) measured in characters, so which item goes is exact: the pipeline
/// tests prove the same order holds against the real renderer and V9.
/// </summary>
public sealed class EnvelopeBudgetTests
{
    private static long InstructionChars(ContextManifest manifest, string narrative) =>
        manifest.Instructions.Sum(q => (long)q.Quote.Length);

    [Fact]
    public void DropInstructions_AStandingQuoteOfACurrentInstructionRow_DropsFirst_AndIsNeverTheProtectedNewestUserInstruction()
    {
        // A model quote of the untrimmed current instruction repeats a row CurrentInstruction already carries whole. It
        // used to count as the newest user instruction, so the real earlier instruction dropped and the duplicate stayed.
        var earlier = "USER-OLD " + new string('o', 300);
        var current = "USER-CURRENT " + new string('c', 300);
        var manifest = new ContextManifest
        {
            CurrentInstruction = [new QuotedItem { Seq = 5, Quote = current }],
            Instructions = [new QuotedItem { Seq = 1, Quote = earlier }, new QuotedItem { Seq = 5, Quote = current }],
        };

        var (fitted, _, fit) = EnvelopeBudget.Fit(
            manifest,
            string.Empty,
            previous: null,
            InstructionChars,
            cap: 500,
            _ => InstructionRank.User
        );

        fit.Fits.Should().BeTrue();
        fitted.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = earlier });
        fitted.CurrentInstruction.Should().Equal(manifest.CurrentInstruction);
        fit.DroppedInstructionSeqs.Should().ContainKey(InstructionRank.User).WhoseValue.Should().Equal(5L);
    }

    [Fact]
    public void DropInstructions_AStandingQuoteOfACurrentInstructionRow_DropsBeforeALowerRankedDirective()
    {
        // The rank order drops directives before user instructions; a repeat of a CurrentInstruction row loses nothing, so
        // it goes ahead of both.
        var directive = "STEER " + new string('s', 300);
        var current = "USER-CURRENT " + new string('c', 300);
        var manifest = new ContextManifest
        {
            CurrentInstruction = [new QuotedItem { Seq = 5, Quote = current }],
            Instructions = [new QuotedItem { Seq = 2, Quote = directive }, new QuotedItem { Seq = 5, Quote = current }],
        };

        var (fitted, _, _) = EnvelopeBudget.Fit(
            manifest,
            string.Empty,
            previous: null,
            InstructionChars,
            cap: 500,
            seq => seq == 2 ? InstructionRank.Directive : InstructionRank.User
        );

        fitted.Instructions.Should().Equal(new QuotedItem { Seq = 2, Quote = directive });
    }

    private const long ItemCost = 10;
    private const long CountLineCost = 5;

    /// <summary>Every listed item costs the same and every section's count line another fixed cost, so K is exact.</summary>
    private static long SectionCost(ContextManifest manifest, string narrative) =>
        ((manifest.Tasks.Count + manifest.Agents.Count + manifest.Artifacts.Count) * ItemCost)
        + (
            new[] { manifest.Omitted?.Tasks, manifest.Omitted?.Agents, manifest.Omitted?.Artifacts }.Count(n => n > 0)
            * CountLineCost
        );

    private static TaskRef OpenTask(string id, string status = "NotStarted") =>
        new()
        {
            Id = id,
            Title = id,
            Status = status,
        };

    private static AgentRef LiveAgent(string id, string status = "running") => new() { AgentId = id, Status = status };

    private static ArtifactRef File(int n) => new() { Path = $"src/{n}.cs" };

    private static (ContextManifest Manifest, EnvelopeFit Fit) FitSections(
        ContextManifest manifest,
        long cap,
        Func<ContextManifest, string, long>? measure = null,
        long? hardCap = null
    )
    {
        var (fitted, _, fit) = EnvelopeBudget.Fit(
            manifest,
            string.Empty,
            previous: null,
            measure ?? SectionCost,
            cap,
            _ => InstructionRank.User,
            hardCap: hardCap
        );
        return (fitted, fit);
    }

    [Theory]
    [InlineData(210, 9, 100)] // the lists alone can fit: the largest K at or above the floor, narrative whole
    [InlineData(120, 3, 75)] // the floor is not enough: the narrative pays, the lists stay at the floor
    [InlineData(20, 0, 1)] // nothing else is left: the lists go below the floor
    public void ListFloor_TheListsComeDownToItBeforeTheNarrativePays_AndBelowItOnlyLast(
        long hardCap,
        int tasksKept,
        int narrativeChars
    )
    {
        const int floor = 3;
        var manifest = new ContextManifest
        {
            Tasks = [.. Enumerable.Range(1, 30).Select(n => OpenTask($"t{n}"))],
            Agents = [LiveAgent("agent-1")],
        };

        static long Cost(ContextManifest m, string narrative) => SectionCost(m, narrative) + narrative.Length;

        var (fitted, narrative, fit) = EnvelopeBudget.Fit(
            manifest,
            new string('n', 100),
            previous: null,
            Cost,
            cap: 10,
            _ => InstructionRank.User,
            hardCap: hardCap,
            listFloor: floor
        );

        fit.Fits.Should().BeTrue();
        fitted.Tasks.Should().HaveCount(tasksKept);
        narrative.Should().HaveLength(narrativeChars);
        fitted.Omitted!.Tasks.Should().Be(30 - tasksKept, "every task is listed or counted across both list rungs");
        fitted.Agents.Should().HaveCount(tasksKept > 0 ? 1 : 0, "a section no longer than the floor is left whole");
    }

    [Fact]
    public void KeepNewest_WhenKeepingAShortTaskIsSmallerThanCountingIt_KeepsItInsteadOfReportingNoFit()
    {
        // The count line goes when K reaches a section's length, so K=1 can fit where K=0 does not: a search that stops
        // at K=0 declared this envelope unfittable and V9 rejected it.
        var manifest = new ContextManifest
        {
            Tasks = [OpenTask("t1")],
            Artifacts = [.. Enumerable.Range(1, 20).Select(File)],
        };

        static long LongTaskCountLine(ContextManifest m, string narrative) =>
            ((m.Tasks.Count + m.Artifacts.Count) * ItemCost)
            + (m.Omitted?.Tasks > 0 ? 30 : 0)
            + (m.Omitted?.Artifacts > 0 ? 20 : 0);

        var (fitted, fit) = FitSections(manifest, cap: 40, LongTaskCountLine);

        fit.Fits.Should().BeTrue("K=1 costs 40, K=0 costs 50 and K=2 costs 50");
        fitted.Tasks.Should().Equal(manifest.Tasks);
        fitted.Artifacts.Should().Equal(File(20));
        fitted.Omitted.Should().Be(new ManifestOmissions { Artifacts = 19 });
    }

    [Fact]
    public void KeepNewest_WhenOnlyCountLinesFit_ListsNothing_AndCountsEveryItemExactly()
    {
        var manifest = new ContextManifest
        {
            Tasks = [OpenTask("t1"), OpenTask("t2"), OpenTask("t3")],
            Agents = [LiveAgent("agent-1"), LiveAgent("agent-2")],
            Artifacts = [File(1), File(2), File(3), File(4)],
        };

        var (fitted, fit) = FitSections(manifest, cap: 3 * CountLineCost);

        fit.Fits.Should().BeTrue();
        fitted.Tasks.Should().BeEmpty();
        fitted.Agents.Should().BeEmpty();
        fitted.Artifacts.Should().BeEmpty();
        fitted
            .Omitted.Should()
            .Be(
                new ManifestOmissions
                {
                    Tasks = 3,
                    Agents = 2,
                    Artifacts = 4,
                }
            );
    }

    [Fact]
    public void KeepNewest_KeepsTheExactNewestKOfEachSection_AndOneMoreWouldBeOverTheCap()
    {
        var tasks = Enumerable.Range(1, 6).Select(n => OpenTask($"t{n}")).ToList();
        var agents = Enumerable.Range(1, 6).Select(n => LiveAgent($"agent-{n}")).ToList();
        var artifacts = Enumerable.Range(1, 6).Select(File).ToList();
        var manifest = new ContextManifest
        {
            Tasks = tasks,
            Agents = agents,
            Artifacts = artifacts,
        };
        const long cap = 80; // K=2: 6 items and 3 count lines = 75. K=3: 9 items and 3 count lines = 105.

        var (fitted, fit) = FitSections(manifest, cap);

        fit.Fits.Should().BeTrue();
        fitted.Tasks.Should().Equal(tasks[^2..]);
        fitted.Agents.Should().Equal(agents[^2..]);
        fitted.Artifacts.Should().Equal(artifacts[^2..]);
        fitted
            .Omitted.Should()
            .Be(
                new ManifestOmissions
                {
                    Tasks = 4,
                    Agents = 4,
                    Artifacts = 4,
                }
            );
        SectionCost(
                fitted with
                {
                    Tasks = tasks[^3..],
                    Agents = agents[^3..],
                    Artifacts = artifacts[^3..],
                },
                string.Empty
            )
            .Should()
            .BeGreaterThan(cap, "K is the largest that fits");
    }

    [Fact]
    public void CompletedTasks_DropOnlyAsManyAsTheTargetNeeds_OldestFirst()
    {
        // A drop-everything search also passes a test where every completed task has to go.
        var tasks = Enumerable.Range(1, 5).Select(n => OpenTask($"t{n}", "Completed")).ToList();

        var (fitted, fit) = FitSections(new ContextManifest { Tasks = tasks }, cap: 30, hardCap: 100);

        fit.CompletedTasksDropped.Should().Be(2);
        fit.MeetsTarget.Should().BeTrue();
        fitted.Tasks.Should().Equal(tasks[^3..]);
    }

    [Theory]
    [InlineData(15, new[] { "agent-10" })]
    [InlineData(25, new[] { "agent-10", "agent-2" })]
    public void KeepNewest_KeepsTheNewestSpawnedAgents_ByNumber_NotByRosterOrTextOrder(long cap, string[] expected)
    {
        // The roster lists queued agents first, then running ones in hash order, and "agent-2" sorts after "agent-10" as
        // text.
        var manifest = new ContextManifest
        {
            Agents = [LiveAgent("agent-10", "queued"), LiveAgent("agent-1"), LiveAgent("agent-2")],
        };

        var (fitted, _) = FitSections(manifest, cap);

        fitted.Agents.Select(a => a.AgentId).Should().Equal(expected, "each section keeps its own order");
    }

    [Fact]
    public void FinishedAgents_DropOldestSpawnedFirst_ByNumber()
    {
        var manifest = new ContextManifest
        {
            Agents =
            [
                LiveAgent("agent-10", "completed"),
                LiveAgent("agent-2", "completed"),
                LiveAgent("agent-1", "completed"),
            ],
        };

        var (fitted, fit) = FitSections(manifest, cap: 20);

        fit.FinishedAgentsDropped.Should().Be(1);
        fitted.Agents.Select(a => a.AgentId).Should().Equal("agent-10", "agent-2");
    }

    [Theory]
    [InlineData(25, new[] { "t1", "t4" })]
    [InlineData(35, new[] { "t1", "t4", "t5" })]
    public void KeepNewest_KeepsInProgressTasksBeforeNotStartedOnes_ThenTheLatest_InBoardOrder(
        long cap,
        string[] expected
    )
    {
        var manifest = new ContextManifest
        {
            Tasks =
            [
                OpenTask("t1", "InProgress"),
                OpenTask("t2"),
                OpenTask("t3"),
                OpenTask("t4", "InProgress"),
                OpenTask("t5"),
            ],
        };

        var (fitted, _) = FitSections(manifest, cap);

        fitted.Tasks.Select(t => t.Id).Should().Equal(expected);
    }

    [Fact]
    public void KeepNewest_AddsWhatItLeavesOutToTheCountTheManifestInherited()
    {
        var manifest = new ContextManifest
        {
            Artifacts = [File(1), File(2), File(3), File(4)],
            Omitted = new ManifestOmissions { Artifacts = 7 },
        };

        var (fitted, _) = FitSections(manifest, cap: ItemCost + CountLineCost);

        fitted.Artifacts.Should().Equal(File(4));
        fitted.Omitted.Should().Be(new ManifestOmissions { Artifacts = 10 }, "7 inherited and 3 newly left out");
    }

    [Theory]
    [InlineData(null, 0, 5)] // from the model on a build with no board: nothing else holds them
    [InlineData("t", 3, 2)] // from the board: it still lists them
    public void CompletedTasks_DropOverTheTargetOnlyWhenTheBoardStillHoldsThem(string? idPrefix, int dropped, int kept)
    {
        var manifest = new ContextManifest
        {
            Tasks =
            [
                .. Enumerable
                    .Range(1, 5)
                    .Select(n => new TaskRef
                    {
                        Id = idPrefix is null ? null : $"{idPrefix}{n}",
                        Title = $"task {n}",
                        Status = "Completed",
                    }),
            ],
        };

        var (fitted, fit) = FitSections(manifest, cap: 20, hardCap: 100);

        fit.CompletedTasksDropped.Should().Be(dropped);
        fitted.Tasks.Should().HaveCount(kept);
        fitted.Omitted.Should().BeNull("within the hard cap nothing is counted");
    }

    [Theory]
    [InlineData(1_100, 0)]
    [InlineData(1_099, 1)]
    public void HardCapBoundary_AnEnvelopeExactlyAtItKeepsEverything_AndOneUnitOverShrinks(
        long hardCap,
        int narrativeCharsTrimmed
    )
    {
        var quotes = new[]
        {
            new QuotedItem { Seq = 1, Quote = new string('a', 500) },
            new QuotedItem { Seq = 2, Quote = new string('b', 500) },
        };
        var narrative = new string('n', 100);

        var (fitted, fittedNarrative, fit) = EnvelopeBudget.Fit(
            new ContextManifest { Instructions = quotes },
            narrative,
            previous: null,
            (m, n) => InstructionChars(m, n) + n.Length,
            cap: 500,
            _ => InstructionRank.User,
            hardCap: hardCap
        );

        fit.Fits.Should().BeTrue();
        fit.MeetsTarget.Should().BeFalse();
        fit.NarrativeCharsTrimmed.Should().Be(narrativeCharsTrimmed);
        fitted.Instructions.Should().Equal(quotes);
        (narrativeCharsTrimmed == 0 ? fittedNarrative == narrative : fittedNarrative != narrative).Should().BeTrue();
    }
}
