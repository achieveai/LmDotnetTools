namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>The panel's decision over N ballots.</summary>
public enum VerdictOutcome
{
    /// <summary>The counted ballots put the candidate at or above the rubric's pass threshold.</summary>
    Pass,

    /// <summary>The counted ballots put the candidate below the threshold, or a gate rejected it.</summary>
    Fail,

    /// <summary>The panel ran and the judges genuinely disagreed across the threshold.</summary>
    Split,

    /// <summary>The panel could not be run, or no ballot survived the abstain filter. NOT a fail.</summary>
    NoDecision,
}

/// <summary>
/// What the panel actually ended up with. Non-null so a reader who ignores it cannot mistake a
/// one-judge verdict for a full-panel one.
/// </summary>
public enum PanelDegradation
{
    /// <summary>A full panel produced the verdict.</summary>
    None,

    /// <summary>Exactly one ballot was counted. Dispersion is null, not zero.</summary>
    SingleJudge,

    /// <summary>No eligible judge returned a countable ballot.</summary>
    PanelUnavailable,

    /// <summary>A straddle escalated to the arbiter and the arbiter could not be reached.</summary>
    ArbiterUnavailable,
}

/// <summary>A ballot that was cast but excluded from the tally, with why. Never silently dropped.</summary>
/// <param name="Ballot">The excluded ballot, with a null <see cref="LmEval.Ballot.AppliedWeight"/>.</param>
/// <param name="ExclusionReason">Stable, non-sensitive text naming why it was excluded.</param>
public sealed record ExcludedBallot(Ballot Ballot, string ExclusionReason);

/// <summary>A judge that faulted rather than returning a ballot, and why.</summary>
/// <param name="JudgeId">Which judge faulted.</param>
/// <param name="ModelFamily">Its model family, so degradation can name the unreachable family.</param>
/// <param name="Reason">Stable, non-sensitive text — an exception type name, never its message.</param>
public sealed record JudgeFault(string JudgeId, string ModelFamily, string Reason);

/// <summary>The aggregated decision over N ballots.</summary>
public sealed record Verdict
{
    /// <summary>The candidate this verdict is about.</summary>
    public required string CandidateId { get; init; }

    /// <summary>What the panel decided.</summary>
    public required VerdictOutcome Outcome { get; init; }

    /// <summary>
    /// Aggregated score on the rubric's scale. Null whenever there are <b>no counted ballots</b> —
    /// a gate rejection (which short-circuits before any judge runs) and a
    /// <see cref="VerdictOutcome.NoDecision"/> alike. A gate-rejected candidate has
    /// <see cref="VerdictOutcome.Fail"/> with a null score; the two carry different information and
    /// neither implies a numeric score.
    /// </summary>
    public double? Score { get; init; }

    /// <summary>Every gate decision made, in registration order, including inconclusive ones.</summary>
    public required IReadOnlyList<GateDecision> GateDecisions { get; init; }

    /// <summary>The counted ballots, each carrying a non-null applied weight.</summary>
    public required IReadOnlyList<Ballot> Ballots { get; init; }

    /// <summary>Ballots cast but excluded, each with why. Never silently dropped.</summary>
    public required IReadOnlyList<ExcludedBallot> ExcludedBallots { get; init; }

    /// <summary>
    /// Disagreement among counted ballots. High dispersion is a review-this-by-hand signal.
    /// <b>Null</b> whenever dispersion is undefined rather than zero: a single counted ballot, a
    /// gate short-circuit with no ballots, or a <see cref="VerdictOutcome.NoDecision"/>. Null is
    /// not 0.0 — a lone judge is not a panel in perfect agreement.
    /// </summary>
    public double? Dispersion { get; init; }

    /// <summary>The rubric this verdict was produced under.</summary>
    public required string RubricId { get; init; }

    /// <summary>Its exact version. Verdicts across versions are never pooled.</summary>
    public required string RubricVersion { get; init; }

    /// <summary>The rule that produced the outcome, recorded verbatim.</summary>
    public required string TieBreakRule { get; init; }

    /// <summary>What the panel ended up with.</summary>
    public required PanelDegradation Degradation { get; init; }

    /// <summary>
    /// Names the unreachable family when <see cref="Degradation"/> is not
    /// <see cref="PanelDegradation.None"/>. Stable, non-sensitive text only — the same rail as
    /// <see cref="GateDecision.Reason"/>.
    /// </summary>
    public string? DegradationReason { get; init; }
}

/// <summary>
/// Everything the reduction step needs beyond the ballots themselves. Passed in rather than
/// captured at construction so a verdict records the exact weights it was computed from.
/// </summary>
public sealed record AggregationContext
{
    /// <summary>The harness options in force, including the abstain floor and the arbiter's identity.</summary>
    public required HarnessOptions Options { get; init; }

    /// <summary>
    /// Reliability snapshot keyed by <see cref="IJudge.JudgeId"/> for this (task type, rubric
    /// version). A judge absent from the map weighs 1.0.
    /// </summary>
    public required IReadOnlyDictionary<string, double> Reliability { get; init; }

    /// <summary>
    /// Judges that faulted rather than returning a ballot, and why — this is how degradation is
    /// classified.
    /// </summary>
    public required IReadOnlyList<JudgeFault> Faults { get; init; }
}

/// <summary>A pure, synchronous reduction from ballots to a verdict.</summary>
public interface IBallotAggregator
{
    /// <summary>Stable identity of the reduction rule, for the experiment record.</summary>
    string RuleId { get; }

    /// <summary>
    /// Reduces the ballots to a verdict. Must be pure: it may not await, and in particular may not
    /// call an <see cref="IJudge"/>. Escalation to the arbiter is
    /// <see cref="JudgeGauntlet.RunAsync"/>'s job.
    /// </summary>
    Verdict Aggregate(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates,
        IReadOnlyList<Ballot> ballots,
        AggregationContext context
    );
}
