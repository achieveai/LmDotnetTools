using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// The pipeline: deterministic gate, then a family-disjoint judge panel, then one reduction — and,
/// on a genuine straddle only, one arbiter call and a second reduction.
/// <para>
/// <b>The escalation boundary is here, not in the aggregator.</b> A reduction over ballots is a
/// pure synchronous fold and cannot await an <see cref="IJudge"/>, so the one asynchronous call the
/// tie-break rule requires is made by the gauntlet, exactly as gate execution and panel fan-out
/// are. The aggregator is therefore invoked at most twice per candidate and is pure both times.
/// </para>
/// <para>
/// The gauntlet owns no persistence and no orchestration: it returns a complete
/// <see cref="Verdict"/> and knows nothing about a database, a schema, or model routing. Storing a
/// verdict, measuring its cost and sequencing generate-evaluate-escalate are all the host's job.
/// </para>
/// </summary>
public sealed class JudgeGauntlet
{
    private readonly IReadOnlyList<IGate> _gates;
    private readonly IReadOnlyList<IJudge> _judges;
    private readonly IBallotAggregator _aggregator;
    private readonly HarnessOptions _options;
    private readonly ILogger<JudgeGauntlet>? _logger;

    /// <summary>
    /// Validates the configuration once, here, rather than per candidate: "two judges of distinct
    /// families" is an invariant of the configuration, whereas "how many are eligible for this
    /// candidate" is a per-candidate fact that legitimately varies.
    /// </summary>
    /// <param name="gates">Deterministic gates, run in this order. The first reject short-circuits.</param>
    /// <param name="judges">One or two judges. Two must be of distinct model families.</param>
    /// <param name="aggregator">The reduction from ballots to a verdict.</param>
    /// <param name="options">The abstain floor, the dispersion alarm and the optional arbiter.</param>
    /// <param name="logger">Optional diagnostics.</param>
    /// <exception cref="ArgumentException">The judge configuration is invalid.</exception>
    public JudgeGauntlet(
        IReadOnlyList<IGate> gates,
        IReadOnlyList<IJudge> judges,
        IBallotAggregator aggregator,
        HarnessOptions options,
        ILogger<JudgeGauntlet>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(judges);
        JudgePanel.ValidateConfiguration(judges);

        _gates = gates;
        _judges = judges;
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// Judges one candidate and returns a complete verdict.
    /// </summary>
    /// <param name="candidate">The candidate to judge.</param>
    /// <param name="rubric">The rubric to judge it against. Its task type must match the candidate's.</param>
    /// <param name="reliability">
    /// The per-judge reliability snapshot for this (task type, rubric version). Every weight is in
    /// [0,1] (§2.9); a judge absent from the map weighs 1.0, so an uncalibrated harness is usable
    /// on day one.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A reliability weight is NaN or outside [0,1].
    /// </exception>
    /// <param name="cancellationToken">Cancellation. A caller's cancellation is never a judge fault.</param>
    public async Task<Verdict> RunAsync(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyDictionary<string, double> reliability,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(reliability);

        // Same predicate the reducer enforces, applied before the panel is billed: a misfitted
        // weight is a property of the run, so paying for two judge calls per candidate across a
        // whole corpus before the reduction rejects it would be pure waste.
        AggregationContext.ValidateReliability(reliability, nameof(reliability));
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(candidate.TaskType, rubric.TaskType, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Candidate task type '{candidate.TaskType}' does not match rubric task type "
                    + $"'{rubric.TaskType}'. A score is meaningful only relative to other scores of the "
                    + "same task type.",
                nameof(rubric)
            );
        }

        // 1. Gates. The first reject short-circuits with no ballots and a null score, so this path
        //    costs nothing at all — which is the entire reason gates run first.
        var (gateDecisions, rejection) = await RunGatesAsync(candidate, cancellationToken)
            .ConfigureAwait(false);
        if (rejection is not null)
        {
            _logger?.LogInformation(
                "Candidate {CandidateId} rejected by gate {GateId}: {Reason}. No judge ran.",
                candidate.CandidateId,
                rejection.GateId,
                rejection.Reason
            );
            return GateRejectedVerdict(candidate, rubric, gateDecisions);
        }

        // 2. Eligibility. A pure filter over model families; it probes no provider.
        var composition = JudgePanel.Compose(_judges, candidate, _options);
        if (composition is PanelComposition.Unavailable unavailable)
        {
            return PanelUnavailableVerdict(candidate, rubric, gateDecisions, unavailable.Reason);
        }

        IReadOnlyList<IJudge> eligible = composition switch
        {
            PanelComposition.Full full => [full.First, full.Second],
            PanelComposition.Degraded degraded => [degraded.Only],
            _ => [],
        };

        // 3. Fan out. A judge that faults becomes a fault rather than propagating, so one provider
        //    outage degrades the verdict instead of losing it.
        var context = new JudgeContext { Reference = candidate.Reference };
        var results = await Task.WhenAll(
                eligible.Select(j => InvokeAsync(j, candidate, rubric, context, cancellationToken))
            )
            .ConfigureAwait(false);

        var ballots = results.Where(r => r.Ballot is not null).Select(r => r.Ballot!).ToList();
        var faults = results.Where(r => r.Fault is not null).Select(r => r.Fault!).ToList();

        // 4. Reduce.
        var verdict = _aggregator.Aggregate(
            candidate,
            rubric,
            gateDecisions,
            ballots,
            new AggregationContext
            {
                Options = _options,
                Reliability = reliability,
                Faults = faults,
            }
        );

        // 5. Escalate, at most once, and only when the tie-break rule's own condition holds.
        if (ShouldEscalate(verdict, candidate))
        {
            var arbiter = _options.ArbiterJudge!;
            var outcome = await InvokeAsync(arbiter, candidate, rubric, context, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Ballot is not null)
            {
                ballots = [.. ballots, outcome.Ballot];
            }
            else
            {
                faults = [.. faults, outcome.Fault!];
            }

            verdict = _aggregator.Aggregate(
                candidate,
                rubric,
                gateDecisions,
                ballots,
                new AggregationContext
                {
                    Options = _options,
                    Reliability = reliability,
                    Faults = faults,
                }
            );
        }

        return WithCompositionReason(verdict, composition);
    }

    /// <summary>
    /// The arbiter condition, stated once: an arbiter is configured AND its family is not the
    /// generator's. Configuration alone is not enough — an arbiter from the generator's own family
    /// would manufacture the self-preference the panel exists to detect.
    /// </summary>
    private bool ShouldEscalate(Verdict verdict, Candidate candidate) =>
        verdict.Outcome == VerdictOutcome.Split
        && verdict.Degradation == PanelDegradation.None
        && _options.ArbiterJudge is { } arbiter
        && !JudgePanel.FamilyComparer.Equals(arbiter.ModelFamily, candidate.GeneratorFamily);

    /// <summary>
    /// Fills in the composition's reason only when the aggregator left the field null. The
    /// aggregator writes the fault-derived reason because it is the layer holding the faults; the
    /// gauntlet writes the eligibility-derived one because it is the layer holding the composition.
    /// Neither overwrites the other.
    /// </summary>
    private static Verdict WithCompositionReason(Verdict verdict, PanelComposition composition) =>
        verdict is { Degradation: not PanelDegradation.None, DegradationReason: null }
        && composition is PanelComposition.Degraded degraded
            ? verdict with
            {
                DegradationReason = degraded.Reason,
            }
            : verdict;

    private async Task<(IReadOnlyList<GateDecision> Decisions, GateDecision? Rejection)> RunGatesAsync(
        Candidate candidate,
        CancellationToken cancellationToken
    )
    {
        var decisions = new List<GateDecision>();
        foreach (var gate in _gates)
        {
            if (gate.AppliesTo.Count > 0 && !gate.AppliesTo.Contains(candidate.TaskType))
            {
                continue;
            }

            var decision = await EvaluateGateAsync(gate, candidate, cancellationToken)
                .ConfigureAwait(false);
            decisions.Add(decision);

            if (decision.Outcome == GateOutcome.Reject)
            {
                return (decisions, decision);
            }
        }

        return (decisions, null);
    }

    /// <summary>
    /// One gate evaluation, containing any fault into an
    /// <see cref="GateOutcome.Inconclusive"/> decision — the same containment
    /// <see cref="InvokeAsync"/> gives the judge path, and for the same reason: a gate that throws
    /// would otherwise take the whole candidate with it, leaving no verdict and no gate record.
    /// <para>
    /// This is also the only way <see cref="GateOutcome.Inconclusive"/> becomes reachable in
    /// practice. Its own doc names the intended trigger as "a tool missing, a checkout absent", and
    /// those surface from a host gate as <c>IOException</c> / <c>FileNotFoundException</c> /
    /// <c>Win32Exception</c>, never as a returned decision.
    /// </para>
    /// <para>
    /// The reason is the exception's TYPE, never its message: it reaches persistence and is held to
    /// the same stable, non-sensitive rail as every other <see cref="GateDecision.Reason"/>.
    /// </para>
    /// </summary>
    private async ValueTask<GateDecision> EvaluateGateAsync(
        IGate gate,
        Candidate candidate,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await gate.EvaluateAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped the run. That is not a gate failure, and recording it as one would
            // put a gate record on a run nobody tried to complete.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Gate {GateId} faulted on candidate {CandidateId}; it is recorded as inconclusive and the remaining gates still run.",
                gate.GateId,
                candidate.CandidateId
            );
            return GateDecision.Inconclusive(gate.GateId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// One judge invocation, returning either a ballot or a fault. The fault reason is the
    /// exception's TYPE, never its message: a reason string reaches persistence and is held to the
    /// same stable, non-sensitive rail as <see cref="GateDecision.Reason"/>.
    /// </summary>
    private async Task<(Ballot? Ballot, JudgeFault? Fault)> InvokeAsync(
        IJudge judge,
        Candidate candidate,
        Rubric rubric,
        JudgeContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var ballot = await judge
                .JudgeAsync(candidate, rubric, context, cancellationToken)
                .ConfigureAwait(false);
            return (ballot, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped the run. That is not a provider outage, and reporting it as one
            // would record a PanelUnavailable verdict for a run nobody tried to complete.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Judge {JudgeId} ({ModelFamily}) faulted on candidate {CandidateId}; the verdict degrades.",
                judge.JudgeId,
                judge.ModelFamily,
                candidate.CandidateId
            );
            return (null, new JudgeFault(judge.JudgeId, judge.ModelFamily, ex.GetType().Name));
        }
    }

    private static Verdict GateRejectedVerdict(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates
    ) =>
        new()
        {
            CandidateId = candidate.CandidateId,
            Outcome = VerdictOutcome.Fail,
            Score = null,
            GateDecisions = gates,
            Ballots = [],
            ExcludedBallots = [],
            Dispersion = null,
            RubricId = rubric.RubricId,
            RubricVersion = rubric.RubricVersion,
            TieBreakRule = TieBreakRules.GateReject,
            Degradation = PanelDegradation.None,
        };

    private static Verdict PanelUnavailableVerdict(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyList<GateDecision> gates,
        string reason
    ) =>
        new()
        {
            CandidateId = candidate.CandidateId,
            Outcome = VerdictOutcome.NoDecision,
            Score = null,
            GateDecisions = gates,
            Ballots = [],
            ExcludedBallots = [],
            Dispersion = null,
            RubricId = rubric.RubricId,
            RubricVersion = rubric.RubricVersion,
            TieBreakRule = TieBreakRules.NoDecision,
            Degradation = PanelDegradation.PanelUnavailable,
            DegradationReason = reason,
        };
}
