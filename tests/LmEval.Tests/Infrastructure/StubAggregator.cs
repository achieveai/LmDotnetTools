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
