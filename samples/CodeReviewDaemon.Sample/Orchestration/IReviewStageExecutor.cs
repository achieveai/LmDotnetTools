using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Performs the work of one review stage (fetch context in the sandbox, run the review agent, judge,
/// post). The orchestrator owns sequencing and persistence; the executor owns the per-stage action.
/// This is the single seam through which P3/P4 plug the real sandbox + agent work in, and through
/// which tests drive the orchestrator deterministically.
/// </summary>
internal interface IReviewStageExecutor
{
    Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Returns any pooled review slot the executor leased for <paramref name="runId"/> and forgets it,
    /// idempotently. Called by the orchestrator in a <c>finally</c> on every terminal outcome of a run
    /// (normal completion, the PR-not-open short-circuit, and the failure→RetryPending rethrow) so a run
    /// that never reaches the Posted stage cannot leak pool capacity. A no-op when no slot is held (the
    /// diff-only path, or a run whose Posted stage already returned it).
    /// </summary>
    Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken);
}
