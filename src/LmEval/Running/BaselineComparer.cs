namespace AchieveAi.LmDotnetTools.LmEval.Running;

/// <summary>Why a comparison was refused. Never a warning — a refusal is not a pass and not a regression.</summary>
public enum ComparisonRefusal
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>The run and the baseline are of different task types.</summary>
    TaskTypeDiffers,

    /// <summary>The rubric identity differs.</summary>
    RubricIdDiffers,

    /// <summary>The rubric version differs. Scores from different versions are never pooled.</summary>
    RubricVersionDiffers,

    /// <summary>A different corpus snapshot. The two means are over different items.</summary>
    CorpusSnapshotDiffers,

    /// <summary>
    /// A score-affecting evaluator input moved — a judge model, a gate bound, the reliability
    /// snapshot. The candidate side may be identical and the scores will still differ.
    /// </summary>
    EvaluatorConfigDiffers,

    /// <summary>The run's coverage is below the floor the baseline imposes.</summary>
    CoverageBelowMinimum,
}

/// <summary>Which regression trigger fired.</summary>
[Flags]
public enum RegressionTrigger
{
    /// <summary>None fired.</summary>
    None = 0,

    /// <summary>Pass rate dropped past the margin, and the drop fell outside the bootstrap interval.</summary>
    PassRateDrop = 1,

    /// <summary>The tail collapsed while the mean held — the case a mean hides.</summary>
    TailCollapse = 2,

    /// <summary>
    /// The panel has stopped being able to judge. That invalidates the comparison rather than
    /// passing it.
    /// </summary>
    NoDecisionRise = 4,
}

/// <summary>The margins a regression is declared past. All are absolute, on their own scales.</summary>
public sealed record RegressionMargins
{
    /// <summary>How far the pass rate may drop before trigger 1 is considered. A fraction.</summary>
    public double PassRateMargin { get; init; } = 0.05;

    /// <summary>How far P10 may drop, on the rubric's scale, before the tail counts as collapsed.</summary>
    public double P10Margin { get; init; } = 0.5;

    /// <summary>
    /// How little the mean may move and still count as "holding", on the rubric's scale. A tail
    /// collapse is a P10 drop <i>while the mean holds</i>; a run whose mean fell too is a plain
    /// regression that trigger 1 already describes.
    /// </summary>
    public double MeanHoldTolerance { get; init; } = 0.25;

    /// <summary>How far the no-decision rate may rise. A fraction.</summary>
    public double NoDecisionRateMargin { get; init; } = 0.10;

    /// <summary>Bootstrap resamples behind the pass-rate confidence interval.</summary>
    public int BootstrapIterations { get; init; } = 2000;

    /// <summary>
    /// Seed for the bootstrap resampler. Fixed by default so a comparison is <b>reproducible</b>:
    /// a regression verdict that changed between two runs over identical inputs would be
    /// indistinguishable from a real one, which is the failure this whole pillar exists to prevent.
    /// </summary>
    public int BootstrapSeed { get; init; } = 20_320;
}

/// <summary>The delta-and-regression report, or the refusal that replaced it.</summary>
public sealed record EvalComparison
{
    /// <summary>The run compared.</summary>
    public required string RunId { get; init; }

    /// <summary>The baseline compared against.</summary>
    public required string BaselineId { get; init; }

    /// <summary>
    /// Why the comparison was refused, or <see cref="ComparisonRefusal.None"/>. When it is not
    /// None, every delta below is null: a refusal is recorded with the failing condition named, and
    /// it is not a regression and not a pass.
    /// </summary>
    public required ComparisonRefusal Refusal { get; init; }

    /// <summary>Human-readable statement of the failing condition. Null when nothing was refused.</summary>
    public string? RefusalDetail { get; init; }

    /// <summary>Run pass rate minus baseline pass rate. Null on a refusal.</summary>
    public double? PassRateDelta { get; init; }

    /// <summary>
    /// Run mean minus baseline mean. Null on a refusal. Both sides are conditional metrics, which
    /// is why <see cref="Coverage"/> and <see cref="BaselineCoverage"/> travel with them.
    /// </summary>
    public double? MeanScoreDelta { get; init; }

    /// <summary>Run P10 minus baseline P10. Null on a refusal.</summary>
    public double? P10ScoreDelta { get; init; }

    /// <summary>Run no-decision rate minus the baseline's. Null on a refusal.</summary>
    public double? NoDecisionRateDelta { get; init; }

    /// <summary>Lower bound of the bootstrap 95% interval on the pass-rate delta. Null on a refusal.</summary>
    public double? PassRateDeltaLower { get; init; }

    /// <summary>Upper bound of the bootstrap 95% interval on the pass-rate delta. Null on a refusal.</summary>
    public double? PassRateDeltaUpper { get; init; }

    /// <summary>The run's coverage — the qualifier its conditional metrics are only readable with.</summary>
    public double? Coverage { get; init; }

    /// <summary>The baseline's own coverage, frozen when it was recorded.</summary>
    public double? BaselineCoverage { get; init; }

    /// <summary>Which triggers fired. Never non-None on a refusal.</summary>
    public RegressionTrigger Triggers { get; init; }

    /// <summary>True when at least one trigger fired.</summary>
    public bool IsRegression => Triggers != RegressionTrigger.None;

    /// <summary>True when the comparison never ran.</summary>
    public bool IsRefused => Refusal != ComparisonRefusal.None;
}

/// <summary>
/// Compares a run against a named baseline, refusing rather than warning when the two are not
/// comparable.
/// <para>
/// A silent incomparable comparison is the most likely way this system produces a confident wrong
/// number, so each precondition is a hard refusal. The coverage bound and the no-decision trigger
/// catch the same failure at different ranges — the bound rejects a single thin run outright, the
/// trigger catches a coverage slide that stays inside it.
/// </para>
/// </summary>
public static class BaselineComparer
{
    /// <summary>Compares a run against a baseline.</summary>
    /// <param name="run">The candidate run.</param>
    /// <param name="baseline">The frozen baseline.</param>
    /// <param name="margins">The margins a regression is declared past.</param>
    public static EvalComparison Compare(
        EvalRun run,
        EvalBaseline baseline,
        RegressionMargins? margins = null
    )
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(baseline);
        margins ??= new RegressionMargins();

        if (Refuse(run, baseline) is { } refusal)
        {
            return refusal;
        }

        var (lower, upper) = BootstrapPassRateDeltaInterval(run, baseline, margins);
        var passRateDelta = run.PassRate - baseline.PassRate;
        var meanDelta = run.MeanScore!.Value - baseline.MeanScore;
        var p10Delta = run.P10Score!.Value - baseline.P10Score;
        var noDecisionDelta = run.NoDecisionRate - baseline.NoDecisionRate;

        return new EvalComparison
        {
            RunId = run.RunId,
            BaselineId = baseline.BaselineId,
            Refusal = ComparisonRefusal.None,
            PassRateDelta = passRateDelta,
            MeanScoreDelta = meanDelta,
            P10ScoreDelta = p10Delta,
            NoDecisionRateDelta = noDecisionDelta,
            PassRateDeltaLower = lower,
            PassRateDeltaUpper = upper,
            Coverage = run.Coverage,
            BaselineCoverage = (double)baseline.ScoredItems / baseline.CorpusSize,
            Triggers = Triggers(
                passRateDelta,
                meanDelta,
                p10Delta,
                noDecisionDelta,
                upper,
                margins
            ),
        };
    }

    private static EvalComparison? Refuse(EvalRun run, EvalBaseline baseline)
    {
        EvalComparison Refused(ComparisonRefusal reason, string detail) =>
            new()
            {
                RunId = run.RunId,
                BaselineId = baseline.BaselineId,
                Refusal = reason,
                RefusalDetail = detail,
                Coverage = run.Coverage,
                BaselineCoverage = (double)baseline.ScoredItems / baseline.CorpusSize,
            };

        if (!string.Equals(run.TaskType, baseline.TaskType, StringComparison.Ordinal))
        {
            return Refused(
                ComparisonRefusal.TaskTypeDiffers,
                $"run task type '{run.TaskType}' is not the baseline's '{baseline.TaskType}'; a "
                    + "score is meaningful only relative to other scores of the same task type"
            );
        }

        if (!string.Equals(run.RubricId, baseline.RubricId, StringComparison.Ordinal))
        {
            return Refused(
                ComparisonRefusal.RubricIdDiffers,
                $"run rubric '{run.RubricId}' is not the baseline's '{baseline.RubricId}'"
            );
        }

        if (!string.Equals(run.RubricVersion, baseline.RubricVersion, StringComparison.Ordinal))
        {
            return Refused(
                ComparisonRefusal.RubricVersionDiffers,
                $"run rubric version '{run.RubricVersion}' is not the baseline's "
                    + $"'{baseline.RubricVersion}'; scores from different rubric versions are never "
                    + "pooled"
            );
        }

        if (
            !string.Equals(
                run.CorpusSnapshotHash,
                baseline.CorpusSnapshotHash,
                StringComparison.Ordinal
            )
        )
        {
            return Refused(
                ComparisonRefusal.CorpusSnapshotDiffers,
                "run and baseline are over different corpus snapshots, so their means are over "
                    + "different items"
            );
        }

        if (
            !string.Equals(
                run.EvaluatorConfigHash,
                baseline.EvaluatorConfigHash,
                StringComparison.Ordinal
            )
        )
        {
            return Refused(
                ComparisonRefusal.EvaluatorConfigDiffers,
                "a score-affecting evaluator input moved — a judge model, a gate bound, or the "
                    + "reliability snapshot. The candidate side may be identical and the scores "
                    + "will still differ, so this must not read as a candidate regression"
            );
        }

        if (run.Coverage < baseline.MinCoverage)
        {
            return Refused(
                ComparisonRefusal.CoverageBelowMinimum,
                $"run coverage {run.Coverage:F4} is below the baseline's floor "
                    + $"{baseline.MinCoverage:F4}; the run is too thin to compare"
            );
        }

        if (run.MeanScore is null || run.P10Score is null)
        {
            return Refused(
                ComparisonRefusal.CoverageBelowMinimum,
                "the run scored no items at all, so it has no conditional mean or P10 to compare"
            );
        }

        return null;
    }

    private static RegressionTrigger Triggers(
        double passRateDelta,
        double meanDelta,
        double p10Delta,
        double noDecisionDelta,
        double passRateDeltaUpperBound,
        RegressionMargins margins
    )
    {
        var triggers = RegressionTrigger.None;

        // 1. A pass-rate drop past the margin AND outside the bootstrap 95% interval. Both, not
        //    either: a drop inside the interval is noise the resampling cannot distinguish from
        //    zero, and declaring it would make this report cry wolf on every run.
        if (passRateDelta < -margins.PassRateMargin && passRateDeltaUpperBound < 0)
        {
            triggers |= RegressionTrigger.PassRateDrop;
        }

        // 2. The tail collapsed while the mean held. The mean-holds conjunct is what makes this a
        //    distinct finding rather than a restatement of trigger 1.
        if (p10Delta < -margins.P10Margin && Math.Abs(meanDelta) <= margins.MeanHoldTolerance)
        {
            triggers |= RegressionTrigger.TailCollapse;
        }

        // 3. The panel has stopped being able to judge.
        if (noDecisionDelta > margins.NoDecisionRateMargin)
        {
            triggers |= RegressionTrigger.NoDecisionRise;
        }

        return triggers;
    }

    /// <summary>
    /// A bootstrap 95% interval on the pass-rate delta, resampling the run's per-item pass
    /// indicators. The baseline's own items are long gone — only its summary survives — so the
    /// baseline rate enters as a constant and the interval measures the run's sampling error alone.
    /// That is the honest reading of what a frozen baseline can support, and it is stated here so
    /// nobody later reads the interval as covering both sides.
    /// </summary>
    private static (double Lower, double Upper) BootstrapPassRateDeltaInterval(
        EvalRun run,
        EvalBaseline baseline,
        RegressionMargins margins
    )
    {
        var indicators = run
            .Items.Select(i =>
                i.IsScored && i.Verdict?.Outcome == VerdictOutcome.Pass ? 1.0 : 0.0
            )
            .ToArray();

        var random = new Random(margins.BootstrapSeed);
        var deltas = new double[margins.BootstrapIterations];

        for (var iteration = 0; iteration < margins.BootstrapIterations; iteration++)
        {
            var passes = 0.0;
            for (var draw = 0; draw < indicators.Length; draw++)
            {
                passes += indicators[random.Next(indicators.Length)];
            }

            deltas[iteration] = (passes / indicators.Length) - baseline.PassRate;
        }

        Array.Sort(deltas);
        var lowerIndex = (int)Math.Floor(0.025 * (deltas.Length - 1));
        var upperIndex = (int)Math.Ceiling(0.975 * (deltas.Length - 1));
        return (deltas[lowerIndex], deltas[upperIndex]);
    }
}
