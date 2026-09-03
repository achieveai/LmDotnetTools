namespace TodoEval.Runner.Metrics;

/// <summary>Which way a gated metric is allowed to move.</summary>
internal enum GateDirection
{
    /// <summary>Lower is better: the actual must not exceed the threshold.</summary>
    AtMost = 0,

    /// <summary>Higher is better: the actual must not fall below the threshold.</summary>
    AtLeast = 1,
}

/// <summary>How a gate ended.</summary>
internal enum GateOutcome
{
    Passed = 0,
    Failed = 1,

    /// <summary>
    /// The gate had nothing to measure — an error rate over zero calls, a no-worse-than over a
    /// baseline that never recorded the signal, a token figure over a sweep that persisted no usage
    /// records. Reported as UNPROVEN, never as a pass: a criterion nobody could evaluate has not
    /// been met, and a rate over zero calls is undefined rather than 0%.
    /// </summary>
    NotMeasurable = 2,
}

/// <summary>One evaluated gate row.</summary>
internal sealed record GateResult
{
    public required string GateId { get; init; }
    public required string Description { get; init; }
    public required GateOutcome Outcome { get; init; }
    public required GateDirection Direction { get; init; }

    /// <summary>The baseline figure the threshold was derived from, or null for an absolute gate.</summary>
    public double? Baseline { get; init; }

    public double? Actual { get; init; }
    public double? Threshold { get; init; }

    /// <summary>
    /// A pass whose actual sits within <see cref="DeterministicGates.PassMarginFraction"/> of its own
    /// threshold. It is still a pass; it is forced into "Contrary evidence" so a reader is never told
    /// a metric improved when it merely failed to get measurably worse.
    /// </summary>
    public bool WithinMargin { get; init; }

    /// <summary>Why the gate could not be measured, present only for <see cref="GateOutcome.NotMeasurable"/>.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The success thresholds of metrics-spec.md, evaluated in code rather than read as prose.
/// </summary>
/// <remarks>
/// Two kinds of gate: baseline-derived ("no worse than the archived sweep", which is the rule) and
/// absolute (the two targets #621 states as numbers). Both are computed from the same two
/// <see cref="SweepAggregate"/>s the before/after table prints, so the verdict and the numbers under
/// it can never disagree.
/// </remarks>
internal static class DeterministicGates
{
    /// <summary>Absolute #621 target: fewer than 5% of add-note calls may fail.</summary>
    public const double AddNoteErrorRateCeiling = 0.05;

    /// <summary>Absolute #621 target: no retry storm at all.</summary>
    public const int RetryStormCeiling = 0;

    /// <summary>
    /// How close to its own threshold a pass may sit before it is reported as contrary evidence,
    /// as a fraction of the threshold (or of 1 when the threshold is smaller, so a near-zero
    /// threshold does not make every pass "within margin").
    /// </summary>
    public const double PassMarginFraction = 0.05;

    private const string RateOverNoCalls =
        "The tool was never called in one of the sweeps, so its error rate is undefined rather than 0%.";

    public static IReadOnlyList<GateResult> Evaluate(SweepAggregate baseline, SweepAggregate candidate)
    {
        List<GateResult> gates =
        [
            // --- baseline-derived: no worse than the archived sweep ---------------------------
            NoWorseThan(
                "task-tool-error-rate",
                "Share of board tool calls that failed, no worse than the baseline",
                baseline.TaskErrorRate,
                candidate.TaskErrorRate,
                measurable: baseline.TaskToolCalls > 0 && candidate.TaskToolCalls > 0,
                note: RateOverNoCalls
            ),
            NoWorseThan(
                "coordination-tool-error-rate",
                "Share of coordination calls refused, no worse than the baseline",
                baseline.CoordinationErrorRate,
                candidate.CoordinationErrorRate,
                measurable: baseline.CoordinationToolCalls > 0 && candidate.CoordinationToolCalls > 0,
                note: RateOverNoCalls
            ),
            NoWorseThan(
                "board-id-vanished",
                "Board rows minted, never deleted and later not found, no worse than the baseline",
                baseline.BoardIdVanished,
                candidate.BoardIdVanished,
                measurable: true
            ),
            AtLeastBaseline(
                "completion-rate",
                "Runs whose board matched the expectation, no worse than the baseline",
                baseline.CompletionRate,
                candidate.CompletionRate,
                measurable: baseline.CompletionMeasuredRuns > 0 && candidate.CompletionMeasuredRuns > 0,
                note: "No expected-board fixture was supplied, so completion was never judged."
            ),
            NoWorseThan(
                "average-turns",
                "Model calls per run, no worse than the baseline",
                baseline.AverageTurns,
                candidate.AverageTurns,
                measurable: baseline.Runs > 0 && candidate.Runs > 0
            ),
            NoWorseThan(
                "unknown-agent-waits",
                "Waits naming an agent the directory does not know, no worse than the baseline",
                baseline.UnknownAgentWaits,
                candidate.UnknownAgentWaits,
                measurable: baseline.WaitOutcomes > 0,
                note: "The baseline recorded no wait call at all, so there is no figure to be no worse than."
            ),
            // --- criterion 3: equivalent successful runs only ---------------------------------
            NoWorseThan(
                "tool-calls-per-successful-run",
                "Tool calls per valid completed run, no worse than the baseline",
                baseline.ToolCallsPerComparableRun,
                candidate.ToolCallsPerComparableRun,
                measurable: baseline.ComparableRuns > 0 && candidate.ComparableRuns > 0,
                note: "One of the sweeps has no valid completed run, so there are no equivalent runs to compare."
            ),
            NoWorseThan(
                "input-tokens-per-successful-run",
                "Input tokens per valid completed run, no worse than the baseline",
                baseline.InputTokensPerComparableRun,
                candidate.InputTokensPerComparableRun,
                measurable: baseline.ComparableUsageRecords > 0 && candidate.ComparableUsageRecords > 0,
                note: "No usage record was persisted with the comparable runs: the token figures are "
                    + "ABSENT data, not zero consumption."
            ),
            NoWorseThan(
                "tool-catalog-bytes-per-spawn",
                "Tool contract carried into each spawn, no worse than the baseline",
                baseline.ToolCatalogBytesPerSpawn,
                candidate.ToolCatalogBytesPerSpawn,
                measurable: baseline.SpawnTimings > 0 && candidate.SpawnTimings > 0,
                note: "No spawn timing was stamped, so the per-spawn tool contract is unmeasured, not zero."
            ),
            // --- absolute targets stated as numbers by #621 -----------------------------------
            Absolute(
                "add-note-error-rate",
                $"At most {AddNoteErrorRateCeiling:P0} of add-note calls may fail (#621 target)",
                candidate.AddNoteErrorRate,
                AddNoteErrorRateCeiling,
                measurable: candidate.AddNoteCalls > 0,
                note: RateOverNoCalls
            ),
            Absolute(
                "retry-storms",
                "No run of 3+ identical failing calls anywhere in the sweep (#621 target)",
                candidate.RetryStorms,
                RetryStormCeiling,
                measurable: true
            ),
            Absolute(
                "open-obligations",
                "No obligation left open when a run ended",
                candidate.OpenObligations,
                ceiling: 0,
                measurable: baseline.ObligationReportingResults > 0 && candidate.ObligationReportingResults > 0,
                note: OpenObligationsReport.NotYetEmittedNote
                    + " The gate is UNPROVEN rather than passed: the number NOT REPORTED cannot clear a ceiling."
            ),
        ];

        return gates;
    }

    /// <summary>A baseline-derived ceiling: the candidate may not exceed what the archive recorded.</summary>
    private static GateResult NoWorseThan(
        string gateId,
        string description,
        double baseline,
        double actual,
        bool measurable,
        string? note = null
    ) => Build(gateId, description, GateDirection.AtMost, baseline, actual, baseline, measurable, note);

    /// <summary>A baseline-derived floor: the candidate may not fall below what the archive recorded.</summary>
    private static GateResult AtLeastBaseline(
        string gateId,
        string description,
        double baseline,
        double actual,
        bool measurable,
        string? note = null
    ) => Build(gateId, description, GateDirection.AtLeast, baseline, actual, baseline, measurable, note);

    /// <summary>
    /// A stated target rather than a baseline-derived one. The baseline is deliberately NOT the
    /// threshold: a worse baseline must never raise a ceiling #621 set as a number.
    /// </summary>
    private static GateResult Absolute(
        string gateId,
        string description,
        double actual,
        double ceiling,
        bool measurable,
        string? note = null
    ) => Build(gateId, description, GateDirection.AtMost, baseline: null, actual, ceiling, measurable, note);

    private static GateResult Build(
        string gateId,
        string description,
        GateDirection direction,
        double? baseline,
        double actual,
        double threshold,
        bool measurable,
        string? note
    )
    {
        if (!measurable)
        {
            return new GateResult
            {
                GateId = gateId,
                Description = description,
                Outcome = GateOutcome.NotMeasurable,
                Direction = direction,
                Baseline = baseline,
                Note = note ?? "Nothing in either sweep carries this signal.",
            };
        }

        var passed = direction == GateDirection.AtMost ? actual <= threshold : actual >= threshold;
        return new GateResult
        {
            GateId = gateId,
            Description = description,
            Outcome = passed ? GateOutcome.Passed : GateOutcome.Failed,
            Direction = direction,
            Baseline = baseline,
            Actual = actual,
            Threshold = threshold,
            // Only a baseline-derived gate can be "passed by a hair": meeting a stated target is
            // meeting it, and a zero threshold has no proportional margin to sit inside.
            WithinMargin =
                passed
                && baseline is not null
                && threshold != 0
                && Math.Abs(actual - threshold) <= PassMarginFraction * Math.Abs(threshold),
        };
    }
}
