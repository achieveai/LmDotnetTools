using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Drives one review run through the <see cref="StageMachine"/> serially, persisting progress after
/// every stage so a crash resumes from the first incomplete step rather than re-doing work. Creation
/// is idempotent (the §6 identity tuple), and a PR observed as no longer open short-circuits to
/// completion. The per-stage work is delegated to <see cref="IReviewStageExecutor"/>.
/// </summary>
internal sealed class PrOrchestrator
{
    private readonly ReviewStore _store;
    private readonly IReviewStageExecutor _executor;
    private readonly ILogger<PrOrchestrator> _logger;
    private readonly ReviewProgressReporter? _progress;
    private readonly RetryGovernor? _retryGovernor;

    public PrOrchestrator(
        ReviewStore store,
        IReviewStageExecutor executor,
        ILogger<PrOrchestrator> logger,
        ReviewProgressReporter? progress = null,
        RetryGovernor? retryGovernor = null
    )
    {
        _store = store;
        _executor = executor;
        _logger = logger;
        _progress = progress;
        _retryGovernor = retryGovernor;
    }

    /// <summary>
    /// Ensures the run exists, then executes the stages still outstanding for it. Returns the run in
    /// its final state for this invocation.
    /// </summary>
    public Task<ReviewRun> RunAsync(ReviewRun seed, CancellationToken cancellationToken) =>
        RunAsync(seed, admitParked: false, cancellationToken);

    /// <summary>
    /// The same drive as <see cref="RunAsync(ReviewRun, CancellationToken)"/>, except that a run the
    /// <see cref="RetryGovernor"/> is backing off or has parked is admitted anyway.
    /// <para>
    /// This is the entry <see cref="StrandedRunReconciler"/> uses, and it exists because the ordinary entry
    /// cannot serve it: the reconciler's whole job is to give a run that nothing else will reach another
    /// attempt, and a parked run is precisely such a run. Through <see cref="RunAsync(ReviewRun,
    /// CancellationToken)"/> the governor refused it before any stage ran, so the resume did nothing, the row
    /// was never written, and the next pass found it stranded exactly as before — a permanent loop that also
    /// spent one of the pass's resume slots each time. Deciding to spend another attempt is the caller's, and
    /// the split keeps that decision explicit instead of quietly weakening park for the poll path too.
    /// </para>
    /// </summary>
    public Task<ReviewRun> ReconcileAsync(ReviewRun seed, CancellationToken cancellationToken) =>
        RunAsync(seed, admitParked: true, cancellationToken);

    private async Task<ReviewRun> RunAsync(ReviewRun seed, bool admitParked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var run = _store.CreateOrGetReviewRun(seed);

        // Against the RESOLVED id, not the seed's: creation is idempotent on the §6 identity tuple, so the row
        // that actually gets worked — and therefore the id the governor is holding a park against — can be an
        // existing one rather than the seed.
        if (admitParked)
        {
            _retryGovernor?.Reset(run.Id);
        }

        try
        {
            // The seed carries the freshest observed PR lifecycle; reconcile the persisted run with it.
            if (seed.PrLifecycleState != run.PrLifecycleState)
            {
                _store.UpdateReviewRunState(run.Id, run.Stage, run.WorkflowStatus, seed.PrLifecycleState);
                run = run with { PrLifecycleState = seed.PrLifecycleState };
            }

            if (StageMachine.IsComplete(run.Stage))
            {
                return run;
            }

            // Everything below is real work for this run — announce it once. The steady-state no-op poll
            // (a completed run) returns above, so finished PRs don't re-announce every cycle.
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _progress?.Picked(run, DescribePickReason(run));

            if (run.PrLifecycleState != PrLifecycleState.Open)
            {
                // PR merged/closed/abandoned — stop working it without marking the run as failed.
                _logger.LogInformation(
                    "Review run {RunId} halted: PR {PrId} is {State}.",
                    run.Id,
                    run.PrId,
                    run.PrLifecycleState
                );
                _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.Completed, run.PrLifecycleState);
                _progress?.Finished(
                    run,
                    $"halted (PR {run.PrLifecycleState})",
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
                );
                return run with { WorkflowStatus = WorkflowStatus.Completed };
            }

            // Retry governance: a run that failed a recent poll is backing off, and one that exhausted its
            // attempts is parked — either way, skip this poll's attempt (leaving it RetryPending) instead of
            // the old ~30s hot-loop. Restart clears the in-memory state, so a restart retries everything, and
            // ReconcileAsync above has already cleared this run's state when a caller decided to spend an
            // attempt on it — so by here the answer is only ever about the poll path.
            if (_retryGovernor is not null && !_retryGovernor.ShouldAttempt(run.Id))
            {
                return run;
            }

            foreach (var stage in StageMachine.RemainingStages(run.Stage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                _progress?.StageStarting(run, stage);
                try
                {
                    await _executor.ExecuteStageAsync(stage, run, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.RetryPending, run.PrLifecycleState);
                    // The RetryGovernor bounds the ContextReady hot-loop (the stuck-slot case it exists for) and
                    // exactly one Reviewed failure: a review whose sub-agent completion barrier ran out the
                    // stage's shared deadline. Every OTHER failure at a later stage (Reviewed/Judged/Posted) is a
                    // different, usually self-healing problem — e.g. a Posted-stage lock the next lease's
                    // clean-on-entry clears — so it must NOT consume the budget or park recoverable work.
                    if (IsGovernedFailure(stage, ex))
                    {
                        _retryGovernor?.RecordFailure(run.Id, ex.Message);
                    }
                    _logger.LogError(ex, "Review run {RunId} failed at stage {Stage}.", run.Id, stage);
                    _progress?.Finished(
                        run,
                        $"failed at {stage}",
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
                    );
                    throw;
                }

                // A governed stage that cleared its cause → forget any accumulated retry state so a later
                // re-review (or a resume past that stage) starts fresh. Reviewed is included for the same reason
                // it is governed at all: without it, a run that survived the barrier this round would still be
                // refused by a governor holding its earlier barrier failures, and could never finish the stages
                // AFTER Reviewed. Stages outside the governor's scope neither record nor clear.
                if (IsGovernedStage(stage))
                {
                    _retryGovernor?.RecordSuccess(run.Id);
                }

                var workflowStatus = StageMachine.IsComplete(stage) ? WorkflowStatus.Completed : WorkflowStatus.Running;
                _store.UpdateReviewRunState(run.Id, stage, workflowStatus, run.PrLifecycleState);
                run = run with { Stage = stage, WorkflowStatus = workflowStatus };
            }

            _progress?.Finished(
                run,
                $"complete ({ClassifyDeliveryOutcome(run)})",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
            );
            return run;
        }
        finally
        {
            // Guarantee a pooled review slot is returned on EVERY terminal outcome of this run — normal
            // completion (where the Posted stage already returned it, so this is a no-op), the PR-not-open
            // short-circuit, and the failure→RetryPending rethrow — so a run that never reaches Posted can
            // never leak pool capacity. Uses CancellationToken.None so a cancelled run still returns its slot.
            await _executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Whether a stage CLEARS the run's accumulated retry state when it succeeds. Every executed stage does,
    /// and it has to: a stage whose failures can charge the budget (see <see cref="IsGovernedFailure"/>, which
    /// now charges a slot-preparation failure wherever prep re-ran) must also be able to un-charge it, or one
    /// persistent-then-recovered prep would follow the run to a park it no longer deserves.
    /// <para>
    /// The narrow judgement lives in <see cref="IsGovernedFailure"/>, not here. That is the one that decides
    /// what a stuck run IS, and widening THAT is what turns ordinary transients into abandoned reviews.
    /// </para>
    /// </summary>
    internal string ClassifyDeliveryOutcome(ReviewRun run)
    {
        if (!string.Equals(run.Mode, "post", StringComparison.Ordinal))
        {
            return "collect-only";
        }

        if (_store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind) is { } artifact)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<ReviewArtifactPayload>(artifact.Payload);
                if (
                    payload?.ReviewText.TrimStart().StartsWith("No new findings", StringComparison.OrdinalIgnoreCase)
                    == true
                )
                {
                    return "no new findings — nothing posted";
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Run {RunId}: could not classify delivery from review artifact {ArtifactId}.",
                    run.Id,
                    artifact.Id
                );
            }
        }

        var delivery = _store
            .GetOutboxForRun(run.Id)
            .LastOrDefault(entry =>
                string.Equals(entry.Operation, ReviewPoster.PostReviewCommentOperation, StringComparison.Ordinal)
            );

        return delivery?.Status switch
        {
            OutboxStatus.Posted when !string.IsNullOrWhiteSpace(delivery.ProviderResponseId) => "posted",
            OutboxStatus.Collected => "collect-only",
            _ => "completed without provider-visible post evidence",
        };
    }

    private static bool IsGovernedStage(ReviewStage stage) =>
        stage is ReviewStage.ContextReady or ReviewStage.Reviewed or ReviewStage.Judged or ReviewStage.Posted;

    /// <summary>
    /// Whether <paramref name="ex"/> is a failure the governor should charge against the run's budget. Any
    /// ContextReady failure qualifies (the stuck-slot hot-loop). At Reviewed only four do:
    /// <see cref="ReviewBarrierDeadlineException"/> — the sub-agent completion barrier spent the review's whole
    /// absolute deadline waiting on a tree that never settled, so the next round would wait exactly as long on
    /// exactly the same tree; <see cref="ReviewCheckpointCorruptException"/>, where the stage cannot read
    /// the checkpoint that says whether a hosted tree is already running, and re-reading it will keep failing;
    /// <see cref="ReviewHostContractException"/>, where the review host cannot keep a message contract the
    /// turn depends on — an incompatibility that reproduces identically on every attempt, and whose attempts
    /// are not free (each one can leave another turn running on the host); and
    /// <see cref="SentinelUnauthorizedException"/>, where the review answered that nothing had changed on a PR
    /// holding no earlier review — a question answered from the STORE, so the next poll asks the same question
    /// of the same rows and refuses identically, having paid for a full fanned-out review to get there. All
    /// four are stuck reviews, not transients: they have to park eventually. A provider blip, a host 5xx or a
    /// blank synthesis stays outside the budget and keeps retrying.
    /// </summary>
    private static bool IsGovernedFailure(ReviewStage stage, Exception ex)
    {
        // Slot PREPARATION is governed wherever it runs, not only under the stage it usually runs under. The
        // slot lease lives in memory only, so a run that persisted Stage=ContextReady in an earlier process (a
        // restart, or a resume after RetryPending) arrives at Reviewed/Judged/Posted with no lease and
        // re-prepares a slot there. These are the same stuck-store conditions ContextReady already
        // parks — a store that will not clone, a tree that will not clean, a path that cannot be established
        // as contained — and none of them is made better by waiting one more poll interval. Tagged with a
        // later stage they used to escape the budget entirely and busy-loop forever (issue #218 item 7).
        // A cleanliness probe that will not answer joins them, and is the mildest of the four: nothing
        // re-clones or retires the slot for it, so it RETRIES by construction — which is exactly why it needs
        // the budget. A probe that loses its output on every attempt would otherwise busy-loop a stage that
        // can never make progress, and the transient case it exists for is retried and gone long before the
        // budget is reached.
        if (
            ex
            is SlotNeedsRecloneException
                or SlotCorruptException
                or SlotAddressUnusableException
                or SlotProbeUnansweredException
        )
        {
            return true;
        }

        return stage switch
        {
            ReviewStage.ContextReady => true,
            ReviewStage.Reviewed => ex
                is ReviewBarrierDeadlineException
                    or ReviewCheckpointCorruptException
                    or ReviewHostContractException
                    or SentinelUnauthorizedException,
            _ => false,
        };
    }

    /// <summary>Human-readable reason a PR was picked this cycle: a brand-new run is "new PR" (no prior
    /// review of this PR) or "new commit {sha}" (its head advanced past the last reviewed commit); an
    /// incomplete run being resumed after a restart/retry reports the stage it left off at.</summary>
    private string DescribePickReason(ReviewRun run)
    {
        if (run.Stage != ReviewStage.Discovered)
        {
            return $"resuming at {run.Stage}";
        }

        var prior = _store.GetPriorReviewSummary(run.RepoId, run.PrId, run.Id);
        if (prior.PrevHeadSha is null)
        {
            return "new PR";
        }

        if (!string.Equals(prior.PrevHeadSha, run.HeadSha, StringComparison.Ordinal))
        {
            var shortSha = run.HeadSha.Length >= 7 ? run.HeadSha[..7] : run.HeadSha;
            return $"new commit {shortSha}";
        }

        return "re-review";
    }
}
