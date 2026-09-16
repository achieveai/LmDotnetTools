using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The per-(variant, task) roll-up: what a strategy cost on a task and what the checker made of it.
/// Its one invariant is that invalid runs are counted but never averaged.
/// </summary>
public class CellSummaryTests
{
    private static RunMetrics Run(
        string variant,
        string task,
        bool valid = true,
        string? j1Outcome = null,
        double? j1Score = null,
        long? costMicros = null,
        int compactions = 0,
        long outputTokens = 0
    ) =>
        new()
        {
            RunKey = $"{variant}/{task}/m1/seed0",
            Model = "m1",
            SeedIndex = 0,
            Topic = "t",
            Status = valid ? RunOutcomes.Completed : RunOutcomes.TimedOut,
            Variant = variant,
            Task = task,
            Valid = valid,
            Compactions = compactions,
            J1 = j1Outcome is null ? null : new J1Result { Outcome = j1Outcome, Score = j1Score },
            Cost = RunCost.Absent with { CostMicros = costMicros, OutputTokens = outputTokens, Records = 1 },
        };

    [Fact]
    public void InvalidRunsAreCountedButNeverAveraged()
    {
        // The whole point: a timed-out run did less work, so averaging its cost in would make the
        // variant look cheap exactly when it is failing.
        var cells = CellSummary.Of([
            Run("compact-v0", "c1", costMicros: 1000, outputTokens: 100, compactions: 2),
            Run("compact-v0", "c1", costMicros: 3000, outputTokens: 300, compactions: 4),
            Run("compact-v0", "c1", valid: false, costMicros: 10, outputTokens: 1, compactions: 0),
        ]);

        var cell = cells.Should().ContainSingle().Subject;
        cell.Runs.Should().Be(3);
        cell.ValidRuns.Should().Be(2);
        cell.MeanCostMicros.Should().Be(2000);
        cell.MeanOutputTokens.Should().Be(200);
        cell.MeanCompactions.Should().Be(3);
    }

    [Fact]
    public void OneRowPerVariantAndTask_OrderedSoTwoSweepsLineUp()
    {
        var cells = CellSummary.Of([
            Run("off", "d1"),
            Run("compact-v0", "d1"),
            Run("compact-v0", "c1"),
            Run("off", "c1"),
        ]);

        cells.Select(c => $"{c.Variant}/{c.Task}").Should().Equal("compact-v0/c1", "compact-v0/d1", "off/c1", "off/d1");
    }

    [Fact]
    public void ScoreAndPassRateAreOverTheJUDGEDRunsOnly()
    {
        var cells = CellSummary.Of([
            Run("off", "c1", j1Outcome: "pass", j1Score: 1.0),
            Run("off", "c1", j1Outcome: "partial", j1Score: 0.5),
            Run("off", "c1", j1Outcome: J1Result.Unjudged),
            Run("off", "c1", valid: false, j1Outcome: "pass", j1Score: 1.0),
        ]);

        var cell = cells.Should().ContainSingle().Subject;
        cell.JudgedRuns.Should().Be(2, "the unjudged run and the invalid one are not verdicts");
        cell.MeanScore.Should().Be(0.75);
        cell.PassRate.Should().Be(0.5);
    }

    [Fact]
    public void ACellWithNothingMeasured_ReportsNullRatherThanZero()
    {
        // A zero here would read as "it ran and scored nothing", which is the one thing it must not.
        var cell = CellSummary.Of([Run("off", "c1", valid: false)]).Single();

        cell.ValidRuns.Should().Be(0);
        cell.MeanScore.Should().BeNull();
        cell.PassRate.Should().BeNull();
        cell.MeanCostMicros.Should().BeNull();
        cell.MeanCompactions.Should().BeNull();
    }

    [Fact]
    public void AnUnpricedRunIsExcludedFromTheCostMeanAndCounted()
    {
        var cell = CellSummary.Of([Run("off", "c1", costMicros: 500), Run("off", "c1", costMicros: null)]).Single();

        cell.MeanCostMicros.Should().Be(500);
        cell.RunsWithCost.Should().Be(1, "the denominator says how much of the cell carried a price");
    }

    [Fact]
    public void TheTablePrintsNotApplicableForAnEmptyCell()
    {
        var table = CellSummaryWriter.BuildTable(CellSummary.Of([Run("off", "c1", valid: false)]));

        table.Should().Contain("n/a");
        table.Should().Contain("0/1", "the reader still sees that the cell had a run");
    }
}
