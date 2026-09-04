using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Adapts an admitted code-review round to the existing five-stage review orchestrator.</summary>
internal sealed class EngagementCodeReviewRoundExecutor : IEngagementRoundExecutor
{
    private readonly ReviewStore _store;
    private readonly PrOrchestrator _orchestrator;

    public EngagementCodeReviewRoundExecutor(ReviewStore store, PrOrchestrator orchestrator)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public EngagementRoundIntent Intent => EngagementRoundIntent.CodeReview;

    public async Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken)
    {
        if (round.Intent != Intent || round.ReviewRunId is not { } reviewRunId)
        {
            throw new ArgumentException(
                "A code-review executor requires a code-review round linked to a review run.",
                nameof(round)
            );
        }

        var run =
            _store.GetReviewRun(reviewRunId)
            ?? throw new InvalidOperationException($"Review run {reviewRunId} does not exist.");
        var completed = await _orchestrator.RunAsync(run, cancellationToken).ConfigureAwait(false);
        if (completed.ParkedAt is not null)
        {
            return EngagementRoundStatus.Parked;
        }

        return completed.WorkflowStatus == WorkflowStatus.Completed && StageMachine.IsComplete(completed.Stage)
            ? EngagementRoundStatus.Completed
            : EngagementRoundStatus.RetryPending;
    }
}
