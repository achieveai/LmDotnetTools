using AchieveAi.LmDotnetTools.LmEval.Corpus;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEval.Running;

/// <summary>
/// Looks up what the host spent on one corpus item's threads, in USD micro-units.
/// <para>
/// The runner <i>reads</i> cost; the harness never produces it. The host owns the agent and its
/// thread id, which is the join key into the usage ledger, so it is the only layer that can measure
/// this — and returning null is the honest answer when it has not.
/// </para>
/// </summary>
/// <param name="candidate">The item to price.</param>
/// <param name="cancellationToken">Cancellation.</param>
public delegate ValueTask<long?> EvalCostSource(
    Candidate candidate,
    CancellationToken cancellationToken
);

/// <summary>
/// Replays a frozen corpus through the judge harness and emits one <see cref="EvalRun"/>.
/// <para>
/// It owns no persistence: it returns a complete run and knows nothing about a database or a
/// schema. Storing the run is the host's job, exactly as storing a verdict is.
/// </para>
/// <para>
/// <b>Items are evaluated one at a time.</b> No parallelism knob is offered, because the value it
/// would buy is wall-clock and the cost it would carry is a starvation mode that only shows up
/// under load — and nothing this run reports is a time measurement, so there is no result to
/// protect by going faster.
/// </para>
/// </summary>
public sealed class EvalRunner
{
    private readonly EvaluatorConfig _config;
    private readonly JudgeGauntlet _gauntlet;
    private readonly ILogger<EvalRunner>? _logger;

    /// <summary>Builds a runner over a frozen evaluator configuration.</summary>
    /// <param name="config">
    /// The evaluator side of the run. The gauntlet is built <i>from</i> it rather than handed in
    /// beside it, so <see cref="EvaluatorConfig.Hash"/> necessarily names the thing that actually
    /// ran — a hash describing a configuration other than the one executing is worse than no hash,
    /// since every refusal built on it would then be checking the wrong fact.
    /// </param>
    /// <param name="logger">Optional diagnostics.</param>
    public EvalRunner(EvaluatorConfig config, ILogger<EvalRunner>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _gauntlet = config.BuildGauntlet();
        _logger = logger;
    }

    /// <summary>
    /// Replays one snapshot.
    /// </summary>
    /// <param name="runId">Stable identity for this run.</param>
    /// <param name="snapshot">The frozen corpus to replay.</param>
    /// <param name="rubric">The rubric to score against. Its task type must match the snapshot's.</param>
    /// <param name="reliability">The per-judge reliability snapshot; a judge absent from it weighs 1.0.</param>
    /// <param name="costSource">Optional lookup for host-recorded per-item cost.</param>
    /// <param name="cancellationToken">Cancellation. A caller's cancellation is never an item fault.</param>
    /// <exception cref="ArgumentException">The rubric's task type is not the snapshot's.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A reliability weight is NaN or outside [0,1]. This is a caller error about the whole run, so
    /// it leaves <c>RunAsync</c> rather than being recorded once per corpus item.
    /// </exception>
    public async Task<EvalRun> RunAsync(
        string runId,
        CorpusSnapshot snapshot,
        Rubric rubric,
        IReadOnlyDictionary<string, double> reliability,
        EvalCostSource? costSource,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(reliability);

        // The snapshot is a property of the RUN, not of any one corpus item, so it is checked here
        // rather than left to the gauntlet's own per-item guard. Left there, the first item threw,
        // the per-item catch below recorded it as a faulted CORPUS item, and the loop repeated that
        // for every remaining item -- so a misfitted refit came back as a run that scored nothing
        // and read as an unscoreable corpus, when what had been rejected was the configuration.
        AggregationContext.ValidateReliability(reliability, nameof(reliability));

        // And they must be the weights the frozen configuration NAMES. The hash covers the weights
        // by content, which is only worth anything if the run cannot then be executed under a
        // different set: a hash describing a configuration other than the one executing is worse
        // than no hash, since every refusal built on it is checking the wrong fact.
        if (!SameWeights(reliability, _config.ReliabilityWeights))
        {
            throw new ArgumentException(
                $"The reliability weights handed to this run are not the ones evaluator config "
                    + $"'{_config.Hash}' froze under snapshot "
                    + $"'{_config.ReliabilitySnapshotId}'. Running under undeclared weights would "
                    + "produce scores the config hash does not describe, so the refusal that stops "
                    + "a refit reading as a candidate regression would be checking the wrong fact.",
                nameof(reliability)
            );
        }

        if (!string.Equals(snapshot.TaskType, rubric.TaskType, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Corpus '{snapshot.CorpusId}' is task type '{snapshot.TaskType}' but the rubric "
                    + $"scores '{rubric.TaskType}'. A score is meaningful only relative to other "
                    + "scores of the same task type.",
                nameof(rubric)
            );
        }

        var items = new List<EvalItemResult>(snapshot.Size);

        foreach (var candidate in snapshot.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(
                await EvaluateAsync(candidate, rubric, reliability, costSource, cancellationToken)
                    .ConfigureAwait(false)
            );
        }

        return new EvalRun
        {
            RunId = runId,
            TaskType = snapshot.TaskType,
            CorpusId = snapshot.CorpusId,
            CorpusSnapshotHash = snapshot.SnapshotHash,
            EvaluatorConfigHash = _config.Hash,
            RubricId = rubric.RubricId,
            RubricVersion = rubric.RubricVersion,
            Items = items,
        };
    }

    private static bool SameWeights(
        IReadOnlyDictionary<string, double> run,
        IReadOnlyDictionary<string, double> declared
    ) =>
        run.Count == declared.Count
        && run.All(w =>
            declared.TryGetValue(w.Key, out var weight) && weight.Equals(w.Value)
        );

    private async Task<EvalItemResult> EvaluateAsync(
        Candidate candidate,
        Rubric rubric,
        IReadOnlyDictionary<string, double> reliability,
        EvalCostSource? costSource,
        CancellationToken cancellationToken
    )
    {
        Verdict verdict;
        try
        {
            verdict = await _gauntlet
                .RunAsync(candidate, rubric, reliability, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped the run. Recording it as a faulted item would put a hole in a run
            // nobody tried to complete, and the aggregates over that run would be read as real.
            throw;
        }
        catch (Exception ex)
        {
            // One bad record must not take out the batch. A corpus is host data of unknown quality
            // and a run over it is a long operation; losing every item's work to one item's fault
            // is an operational failure, not a measurement. The item keeps its place in the
            // denominator, which is what stops the loss from flattering the result.
            _logger?.LogWarning(
                ex,
                "Corpus item {CandidateId} faulted during evaluation; it is recorded as unscored and stays in the denominator.",
                candidate.CandidateId
            );

            return new EvalItemResult
            {
                CandidateId = candidate.CandidateId,
                Verdict = null,
                FaultReason = ex.GetType().Name,
                Exclusion = ScoreExclusion.Faulted,
                CostMicros = await PriceAsync(candidate, costSource, cancellationToken)
                    .ConfigureAwait(false),
            };
        }

        return new EvalItemResult
        {
            CandidateId = candidate.CandidateId,
            Verdict = verdict,
            Exclusion = Classify(verdict, candidate),
            CostMicros = await PriceAsync(candidate, costSource, cancellationToken)
                .ConfigureAwait(false),
        };
    }

    private static async ValueTask<long?> PriceAsync(
        Candidate candidate,
        EvalCostSource? costSource,
        CancellationToken cancellationToken
    ) =>
        costSource is null
            ? null
            : await costSource(candidate, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Why this item contributes no score, or <see cref="ScoreExclusion.None"/> when it does.
    /// <para>
    /// Ordered most-specific first, and the null-score check is <b>last</b> rather than first on
    /// purpose: it is a backstop for a verdict shape none of the named cases covers, so putting it
    /// ahead of them would relabel every gate rejection and every no-decision as an unclassifiable
    /// row and erase exactly the segmentation this field exists to provide.
    /// </para>
    /// </summary>
    private static ScoreExclusion Classify(Verdict verdict, Candidate candidate) =>
        verdict switch
        {
            { TieBreakRule: TieBreakRules.GateReject } => ScoreExclusion.GateRejected,
            { Outcome: VerdictOutcome.NoDecision } => ScoreExclusion.NoDecision,
            { Outcome: VerdictOutcome.Split } => ScoreExclusion.Straddled,
            { Degradation: not PanelDegradation.None } => ScoreExclusion.Degraded,
            _ when candidate.GeneratorFamily is null => ScoreExclusion.UnknownGeneratorFamily,
            { Score: null } => ScoreExclusion.NoDecision,
            _ => ScoreExclusion.None,
        };
}
