namespace AchieveAi.LmDotnetTools.LmEval.Aggregation;

/// <summary>
/// The default reduction: drop abstentions and low-confidence ballots, then decide on which side of
/// the rubric's pass threshold the survivors land.
/// <para>
/// Weighted, not unweighted, because weighting a judge by its fitted reliability measurably beats
/// pooling every judge equally. <b>No median is defined anywhere</b>: a median only earns its
/// robustness at three or more ballots, and this harness fixes the panel at two, where it is
/// identical to a mean. The straddle test, not a robust statistic, is what protects against a
/// single bad judge.
/// </para>
/// <para>
/// It is pure and synchronous by design. Escalating a straddle to the arbiter is
/// <see cref="JudgeGauntlet.RunAsync"/>'s job; this reducer only ever sees the arbiter's result or
/// fault as data that was already appended for it.
/// </para>
/// </summary>
public sealed class WeightedMeanAggregator : IBallotAggregator
{
    /// <inheritdoc />
    public string RuleId => "weighted-mean";

    /// <inheritdoc />
    public Verdict Aggregate(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates,
        IReadOnlyList<Ballot> ballots,
        AggregationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(ballots);
        ArgumentNullException.ThrowIfNull(context);

        // The weights normalise the mean below, so an out-of-range one is what would let Score
        // leave the rubric's scale or land on the opposite side of the threshold from the outcome
        // the ballots decided. Checked here rather than only at the harness boundary because this
        // is the code whose invariant it breaks, and this reducer is directly constructible.
        AggregationContext.ValidateReliability(context.Reliability, nameof(context));

        var arbiterId = context.Options.ArbiterJudge?.JudgeId;

        // 1. Partition. An abstention and a self-distrusted score are both recorded and both
        //    excluded — neither is a zero. An excluded ballot keeps a null AppliedWeight even if a
        //    judge invented one, because a judge cannot know its own weight.
        var counted = new List<Ballot>(ballots.Count);
        var excluded = new List<ExcludedBallot>();
        foreach (var ballot in ballots)
        {
            var exclusion = ExclusionReasonFor(ballot, context.Options.AbstainFloor);
            if (exclusion is null)
            {
                counted.Add(ballot with { AppliedWeight = WeightOf(ballot, context) });
            }
            else
            {
                excluded.Add(new ExcludedBallot(ballot with { AppliedWeight = null }, exclusion));
            }
        }

        var panelFaults = context.Faults.Where(f => !IsArbiter(f.JudgeId, arbiterId)).ToList();
        var arbiterFault = context.Faults.FirstOrDefault(f => IsArbiter(f.JudgeId, arbiterId));

        // 2. No survivor is NoDecision, never Fail: an unmeasured candidate and a bad candidate
        //    are different facts.
        if (counted.Count == 0)
        {
            return Build(
                candidate,
                rubric,
                gates,
                counted,
                excluded,
                VerdictOutcome.NoDecision,
                score: null,
                dispersion: null,
                tieBreakRule: TieBreakRules.NoDecision,
                degradation: panelFaults.Count > 0
                    ? PanelDegradation.PanelUnavailable
                    : PanelDegradation.None,
                degradationReason: FaultReason("judge-faulted", panelFaults)
            );
        }

        var arbiterBallot = counted.FirstOrDefault(b => IsArbiter(b.JudgeId, arbiterId));
        var panelBallots = counted.Where(b => !IsArbiter(b.JudgeId, arbiterId)).ToList();
        if (panelBallots.Count == 0)
        {
            // Only reachable if a host hands the reducer arbiter ballots alone. The straddle test
            // is about the panel, so with no panel ballots there is nothing to straddle.
            panelBallots = counted;
        }

        // 3. One counted ballot is a real verdict, provided it is labelled as one judge's read.
        if (counted.Count == 1)
        {
            return Build(
                candidate,
                rubric,
                gates,
                counted,
                excluded,
                OutcomeFor(counted[0].WeightedScore, rubric),
                score: counted[0].WeightedScore,
                dispersion: null,
                tieBreakRule: TieBreakRules.SingleJudge,
                degradation: PanelDegradation.SingleJudge,
                degradationReason: FaultReason("judge-faulted", panelFaults)
            );
        }

        var dispersion = Dispersion(counted);

        // 4. The straddle test: raw score inequality would fire on almost every candidate, so what
        //    matters is whether the panel landed on opposite sides of the pass threshold.
        if (!Straddles(panelBallots, rubric))
        {
            return Build(
                candidate,
                rubric,
                gates,
                counted,
                excluded,
                OutcomeFor(panelBallots[0].WeightedScore, rubric),
                score: WeightedMean(counted),
                dispersion: dispersion,
                tieBreakRule: TieBreakRules.Consensus,
                degradation: PanelDegradation.None,
                degradationReason: null
            );
        }

        // 5. A straddle the arbiter already decided. Its side wins and its score is recorded as-is,
        //    never blended, so the deciding voice stays visible in the number.
        if (arbiterBallot is not null)
        {
            return Build(
                candidate,
                rubric,
                gates,
                counted,
                excluded,
                OutcomeFor(arbiterBallot.WeightedScore, rubric),
                score: arbiterBallot.WeightedScore,
                dispersion: dispersion,
                tieBreakRule: TieBreakRules.Arbiter(arbiterBallot.JudgeId, arbiterBallot.ModelFamily),
                degradation: PanelDegradation.None,
                degradationReason: null
            );
        }

        // 6. An unresolved straddle. "We tried and could not" is ArbiterUnavailable; "we chose not
        //    to escalate" — no arbiter, or one in the generator's own family — is None. The rule
        //    string is the same for both on purpose: the degradation is the discriminator, and
        //    encoding it twice would let the two drift.
        //
        //    Faulting is not the only way an escalation fails. An arbiter that RAN and returned an
        //    abstention — or a ballot below the abstain floor — throws nothing and leaves no
        //    countable ballot, so discriminating on the fault alone recorded it as None. §2.12.6's
        //    two None arms are "no arbiter configured" and "arbiter in the generator's own family",
        //    told apart post-hoc from the arbiter's family; a row where the arbiter declined to
        //    decide satisfies neither, and post-hoc reconstruction reads it as the first — an
        //    escalation that happened and failed, recorded as one never attempted.
        var arbiterExcluded = excluded.FirstOrDefault(e =>
            IsArbiter(e.Ballot.JudgeId, arbiterId)
        );

        return Build(
            candidate,
            rubric,
            gates,
            counted,
            excluded,
            VerdictOutcome.Split,
            score: WeightedMean(counted),
            dispersion: dispersion,
            tieBreakRule: TieBreakRules.SplitUnresolved,
            degradation: arbiterFault is null && arbiterExcluded is null
                ? PanelDegradation.None
                : PanelDegradation.ArbiterUnavailable,
            degradationReason: arbiterFault is not null
                ? FaultReason("arbiter-faulted", [arbiterFault])
                : arbiterExcluded is not null
                    ? $"arbiter-excluded:{arbiterExcluded.ExclusionReason}"
                    : null
        );
    }

    private static bool IsArbiter(string judgeId, string? arbiterId) =>
        arbiterId is not null && string.Equals(judgeId, arbiterId, StringComparison.Ordinal);

    /// <summary>
    /// Why this ballot is not counted, or null when it is. The two channels stay separate: a judge
    /// that refused to score and a judge that scored but distrusts itself are different facts.
    /// </summary>
    private static string? ExclusionReasonFor(Ballot ballot, double abstainFloor) =>
        ballot.Abstained ? "abstained"
        : ballot.Confidence < abstainFloor ? "confidence-below-floor"
        : null;

    private static double WeightOf(Ballot ballot, AggregationContext context) =>
        context.Reliability.TryGetValue(ballot.JudgeId, out var weight) ? weight : 1.0;

    private static VerdictOutcome OutcomeFor(double score, Rubric rubric) =>
        score >= rubric.PassThreshold ? VerdictOutcome.Pass : VerdictOutcome.Fail;

    private static bool Straddles(IReadOnlyList<Ballot> panelBallots, Rubric rubric) =>
        panelBallots.Any(b => b.WeightedScore >= rubric.PassThreshold)
        && panelBallots.Any(b => b.WeightedScore < rubric.PassThreshold);

    private static double WeightedMean(IReadOnlyList<Ballot> counted)
    {
        var totalWeight = counted.Sum(b => b.AppliedWeight ?? 1.0);
        return totalWeight <= 0
            ? counted.Average(b => b.WeightedScore)
            : counted.Sum(b => (b.AppliedWeight ?? 1.0) * b.WeightedScore) / totalWeight;
    }

    /// <summary>
    /// Population standard deviation of the counted scores. Undefined — null, never 0.0 — below two
    /// ballots, because a lone judge is not a panel in perfect agreement.
    /// </summary>
    private static double? Dispersion(IReadOnlyList<Ballot> counted)
    {
        if (counted.Count < 2)
        {
            return null;
        }

        var mean = counted.Average(b => b.WeightedScore);
        return Math.Sqrt(counted.Average(b => Math.Pow(b.WeightedScore - mean, 2)));
    }

    /// <summary>
    /// Stable, non-sensitive degradation text naming the unreachable families. Same rail as
    /// <see cref="GateDecision.Reason"/>: the fault reasons feeding it are exception type names,
    /// never messages.
    /// </summary>
    private static string? FaultReason(string prefix, IReadOnlyList<JudgeFault> faults) =>
        faults.Count == 0
            ? null
            : string.Join(",", faults.Select(f => $"{prefix}:{f.ModelFamily}:{f.Reason}"));

    private static Verdict Build(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates,
        IReadOnlyList<Ballot> counted,
        IReadOnlyList<ExcludedBallot> excluded,
        VerdictOutcome outcome,
        double? score,
        double? dispersion,
        string tieBreakRule,
        PanelDegradation degradation,
        string? degradationReason
    ) =>
        new()
        {
            CandidateId = candidate.CandidateId,
            Outcome = outcome,
            Score = score,
            GateDecisions = gates,
            Ballots = counted,
            ExcludedBallots = excluded,
            Dispersion = dispersion,
            RubricId = rubric.RubricId,
            RubricVersion = rubric.RubricVersion,
            TieBreakRule = tieBreakRule,
            Degradation = degradation,
            DegradationReason = degradationReason,
        };
}
