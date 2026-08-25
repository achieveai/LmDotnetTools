namespace AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

/// <summary>
/// An <see cref="IBallotAggregator"/> double returning a fixed outcome and degradation.
/// <para>
/// The reducer is an INJECTED seam on <see cref="JudgeGauntlet"/>, so a host reducer can legally
/// hand the gauntlet combinations <see cref="Aggregation.WeightedMeanAggregator"/> never produces.
/// That is the only way to reach the gauntlet's "a degraded panel does not escalate" guard, and
/// without this double the guard has no test that breaks when it is removed.
/// </para>
/// </summary>
internal sealed class StubAggregator(VerdictOutcome outcome, PanelDegradation degradation)
    : IBallotAggregator
{
    public string RuleId => "stub";

    public Verdict Aggregate(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates,
        IReadOnlyList<Ballot> ballots,
        AggregationContext context
    ) =>
        new()
        {
            CandidateId = candidate.CandidateId,
            Outcome = outcome,
            Score = ballots.Count > 0 ? ballots[0].WeightedScore : null,
            GateDecisions = gates,
            Ballots = ballots,
            ExcludedBallots = [],
            Dispersion = null,
            RubricId = rubric.RubricId,
            RubricVersion = rubric.RubricVersion,
            TieBreakRule = "stub",
            Degradation = degradation,
        };
}

/// <summary>
/// An <see cref="IBallotAggregator"/> double that throws for one named candidate.
/// <para>
/// The reducer is the remaining per-candidate seam that can still fault out of
/// <see cref="JudgeGauntlet.RunAsync"/>: gate faults and judge faults are both contained into the
/// verdict by design, so neither can reach the runner's own per-item isolation any more. A host
/// reducer genuinely can throw, which is what makes this the honest way to test that isolation.
/// </para>
/// </summary>
internal sealed class ThrowingAggregator(string throwOnCandidateId, IBallotAggregator inner)
    : IBallotAggregator
{
    public string RuleId => inner.RuleId;

    public Verdict Aggregate(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates,
        IReadOnlyList<Ballot> ballots,
        AggregationContext context
    ) =>
        string.Equals(candidate.CandidateId, throwOnCandidateId, StringComparison.Ordinal)
            ? throw new InvalidOperationException("reducer blew up")
            : inner.Aggregate(candidate, rubric, gates, ballots, context);
}
