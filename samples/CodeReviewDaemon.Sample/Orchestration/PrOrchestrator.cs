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

    public PrOrchestrator(ReviewStore store, IReviewStageExecutor executor, ILogger<PrOrchestrator> logger)
    {
        _store = store;
        _executor = executor;
        _logger = logger;
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

            if (run.PrLifecycleState != PrLifecycleState.Open)
            {
                // PR merged/closed/abandoned — stop working it without marking the run as failed.
                _logger.LogInformation(
                    "Review run {RunId} halted: PR {PrId} is {State}.", run.Id, run.PrId, run.PrLifecycleState);
                _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.Completed, run.PrLifecycleState);
                return run with { WorkflowStatus = WorkflowStatus.Completed };
            }

            foreach (var stage in StageMachine.RemainingStages(run.Stage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await _executor.ExecuteStageAsync(stage, run, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.RetryPending, run.PrLifecycleState);
                    _logger.LogError(ex, "Review run {RunId} failed at stage {Stage}.", run.Id, stage);
                    throw;
                }

                var workflowStatus = StageMachine.IsComplete(stage) ? WorkflowStatus.Completed : WorkflowStatus.Running;
                _store.UpdateReviewRunState(run.Id, stage, workflowStatus, run.PrLifecycleState);
                run = run with { Stage = stage, WorkflowStatus = workflowStatus };
            }

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
}
