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
                    _retryGovernor?.RecordFailure(run.Id, ex.Message);
                    _logger.LogError(ex, "Review run {RunId} failed at stage {Stage}.", run.Id, stage);
                    _progress?.Finished(
                        run, $"failed at {stage}", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                    throw;
                }

                var workflowStatus = StageMachine.IsComplete(stage) ? WorkflowStatus.Completed : WorkflowStatus.Running;
                _store.UpdateReviewRunState(run.Id, stage, workflowStatus, run.PrLifecycleState);
                run = run with { Stage = stage, WorkflowStatus = workflowStatus };
            }

            _progress?.Finished(
                run,
                $"complete ({(string.Equals(run.Mode, "post", StringComparison.Ordinal) ? "posted" : "collect-only")})",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
            _retryGovernor?.RecordSuccess(run.Id);
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
