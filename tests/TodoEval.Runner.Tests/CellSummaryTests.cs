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
        long outputTokens = 0,
        int records = 1,
        int? recordsWithCost = null
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
            Cost = RunCost.Absent with
            {
                CostMicros = costMicros,
                OutputTokens = outputTokens,
                Records = records,
                // Default to a fully priced run, so a test that says nothing about coverage gets the
                // ordinary case rather than a silently partial one.
                RecordsWithCost = recordsWithCost ?? (costMicros is null ? 0 : records),
            },
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
    public void TheOrderIsByName_EvenWhenCostWouldOrderThemDifferently()
    {
        // ADR 0020 decision 3: the table reports outcome then cost and never ranks arms by cost. The
        // test that pins it has to be the one that DISTINGUISHES the two orders -- with equal costs a
        // by-cost sort and a by-name sort agree, so it would pass either way. Here the cheapest arm
        // sorts last by name and the dearest sorts first, so a table ordered by any measured value
        // comes out reversed.
        var cells = CellSummary.Of([
            Run("off", "c1", costMicros: 9000),
            Run("compact-v0", "c1", costMicros: 1000),
            Run("mid-v1", "c1", costMicros: 5000),
        ]);

        cells.Select(c => c.Variant).Should().Equal("compact-v0", "mid-v1", "off");
        cells.Select(c => c.MeanCostMicros).Should().Equal([1000d, 5000d, 9000d]);

        var rows = CellSummaryWriter
            .BuildTable(cells)
            .Split('\n')
            .Where(l =>
                l.StartsWith("| ", StringComparison.Ordinal) && !l.Contains("variant", StringComparison.Ordinal)
            )
            .ToList();
        rows.Should().HaveCount(3);
        rows[0].Should().Contain("compact-v0");
        rows[2].Should().Contain("off");
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

    [Fact]
    public void ARunPricedInPartMakesTheCellsCostALowerBound()
    {
        // RunCost.CostMicros covers only RecordsWithCost. A run whose resolver priced 3 of its 10
        // records is already under the truth, and averaging it with a complete run used to produce a
        // figure that read as a measurement.
        var cell = CellSummary
            .Of([
                Run("off", "c1", costMicros: 1000, records: 10, recordsWithCost: 10),
                Run("off", "c1", costMicros: 500, records: 10, recordsWithCost: 3),
            ])
            .Single();

        cell.RunsWithCost.Should().Be(2);
        cell.PartlyPricedRuns.Should().Be(1);
        cell.MeanCostIsLowerBound.Should().BeTrue();
        cell.MeanCostMicros.Should().Be(750, "the mean is still reported — it is the bound that changes");
    }

    [Fact]
    public void ACellWhereEveryRunWasFullyPricedIsNotALowerBound()
    {
        // The non-vacuity check on the flag: it must be able to be false, or it says nothing.
        var cell = CellSummary
            .Of([
                Run("off", "c1", costMicros: 1000, records: 4, recordsWithCost: 4),
                Run("off", "c1", costMicros: 2000, records: 7, recordsWithCost: 7),
            ])
            .Single();

        cell.PartlyPricedRuns.Should().Be(0);
        cell.MeanCostIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public void AnUnpricedCellIsNotALowerBound_BecauseItIsNotAMeasurement()
    {
        var cell = CellSummary.Of([Run("off", "c1", costMicros: null)]).Single();

        cell.MeanCostMicros.Should().BeNull();
        cell.MeanCostIsLowerBound.Should().BeFalse("n/a is an absence, not a bound on anything");
    }

    [Fact]
    public void TheTableMarksALowerBoundCostAndSaysWhatIsMissing()
    {
        var table = CellSummaryWriter.BuildTable(
            CellSummary.Of([
                Run("off", "c1", costMicros: 1000, records: 10, recordsWithCost: 3),
                Run("off", "c1", costMicros: null),
            ])
        );

        table.Should().Contain(">=1000 (1 priced, 1 partly priced)");
    }

    [Fact]
    public void AnExcludedRunIsCoverage_NotALowerBound()
    {
        // The mean covers the priced runs; the unpriced ones are unmeasured, not zero, so the figure
        // bounds the cell in NEITHER direction. Marking it ">=" was a bound pointing the wrong way:
        // here two priced runs average 100 while the cell's true mean, if the unpriced pair really
        // cost nothing, is 50. The honest report is the denominator, which claims nothing about them.
        var cell = CellSummary
            .Of([
                Run("off", "c1", costMicros: 100, records: 4, recordsWithCost: 4),
                Run("off", "c1", costMicros: 100, records: 4, recordsWithCost: 4),
                Run("off", "c1", costMicros: null),
                Run("off", "c1", costMicros: null),
            ])
            .Single();

        cell.MeanCostMicros.Should().Be(100);
        cell.RunsWithCost.Should().Be(2);
        cell.ValidRuns.Should().Be(4);
        cell.PartlyPricedRuns.Should().Be(0);
        cell.MeanCostIsLowerBound.Should().BeFalse("an excluded run is not a floor under the cell");

        CellSummaryWriter.BuildTable([cell]).Should().Contain("| 100 (2 priced) |").And.NotContain(">=");
    }

    [Fact]
    public void AMixedCell_SeparatesWhatIsBoundedFromWhatIsMerelyUncovered()
    {
        // All three populations in one cell: complete, partial, absent. The ">=" answers "is the
        // number I can see under the truth of the runs behind it", the "(n priced)" answers "how much
        // of the cell is behind it". They are different questions and the row answers both.
        var cell = CellSummary
            .Of([
                Run("off", "c1", costMicros: 300, records: 5, recordsWithCost: 5),
                Run("off", "c1", costMicros: 100, records: 5, recordsWithCost: 2),
                Run("off", "c1", costMicros: null),
            ])
            .Single();

        cell.ValidRuns.Should().Be(3);
        cell.RunsWithCost.Should().Be(2);
        cell.PartlyPricedRuns.Should().Be(1);
        cell.MeanCostIsLowerBound.Should().BeTrue();

        CellSummaryWriter.BuildTable([cell]).Should().Contain(">=200 (2 priced, 1 partly priced)");
    }

    [Fact]
    public void TheTablePrintsACompleteCostWithNoQualifier()
    {
        var table = CellSummaryWriter.BuildTable(
            CellSummary.Of([Run("off", "c1", costMicros: 1000, records: 2, recordsWithCost: 2)])
        );

        table.Should().Contain("| 1000 |").And.NotContain(">=");
    }
}
