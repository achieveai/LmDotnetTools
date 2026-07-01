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
}
