using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

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
        RetryGovernor? retryGovernor = null)
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
    public async Task<ReviewRun> RunAsync(ReviewRun seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var run = _store.CreateOrGetReviewRun(seed);

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
                    "Review run {RunId} halted: PR {PrId} is {State}.", run.Id, run.PrId, run.PrLifecycleState);
                _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.Completed, run.PrLifecycleState);
                _progress?.Finished(
                    run, $"halted (PR {run.PrLifecycleState})", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                return run with { WorkflowStatus = WorkflowStatus.Completed };
            }

            // Retry governance: a run that failed a recent poll is backing off, and one that exhausted its
            // attempts is parked — either way, skip this poll's attempt (leaving it RetryPending) instead of
            // the old ~30s hot-loop. Restart clears the in-memory state, so a restart retries everything.
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
                        run, $"failed at {stage}", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
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
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
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
    /// Whether a stage's outcomes are accounted by the <see cref="RetryGovernor"/> at all. Deliberately tiny:
    /// the governor exists to park work that CANNOT self-heal by being retried on the poll interval, and
    /// widening it turns ordinary transients into abandoned reviews.
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
                if (payload?.ReviewText.TrimStart().StartsWith(
                        "No new findings",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    return "no new findings — nothing posted";
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Run {RunId}: could not classify delivery from review artifact {ArtifactId}.", run.Id, artifact.Id);
            }
        }

        var delivery = _store.GetOutboxForRun(run.Id)
            .LastOrDefault(entry => string.Equals(
                entry.Operation,
                ReviewPoster.PostReviewCommentOperation,
                StringComparison.Ordinal));

        return delivery?.Status switch
        {
            OutboxStatus.Posted when !string.IsNullOrWhiteSpace(delivery.ProviderResponseId) => "posted",
            OutboxStatus.Collected => "collect-only",
            _ => "completed without provider-visible post evidence",
        };
    }

    private static bool IsGovernedStage(ReviewStage stage) =>
        stage is ReviewStage.ContextReady or ReviewStage.Reviewed;

    /// <summary>
    /// Whether <paramref name="ex"/> is a failure the governor should charge against the run's budget. Any
    /// ContextReady failure qualifies (the stuck-slot hot-loop). At Reviewed only three do:
    /// <see cref="ReviewBarrierDeadlineException"/> — the sub-agent completion barrier spent the review's whole
    /// absolute deadline waiting on a tree that never settled, so the next round would wait exactly as long on
    /// exactly the same tree; <see cref="ReviewCheckpointCorruptException"/>, where the stage cannot read
    /// the checkpoint that says whether a hosted tree is already running, and re-reading it will keep failing;
    /// and <see cref="ReviewHostContractException"/>, where the review host cannot keep a message contract the
    /// turn depends on — an incompatibility that reproduces identically on every attempt, and whose attempts
    /// are not free (each one can leave another turn running on the host). All three are stuck reviews, not
    /// transients: they have to park eventually. A provider blip, a host 5xx or a blank synthesis stays
    /// outside the budget and keeps retrying.
    /// </summary>
    private static bool IsGovernedFailure(ReviewStage stage, Exception ex) => stage switch
    {
        ReviewStage.ContextReady => true,
        ReviewStage.Reviewed => ex
            is ReviewBarrierDeadlineException
                or ReviewCheckpointCorruptException
                or ReviewHostContractException,
        _ => false,
    };

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
