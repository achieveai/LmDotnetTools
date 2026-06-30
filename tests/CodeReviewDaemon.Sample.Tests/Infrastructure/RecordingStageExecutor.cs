using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="IReviewStageExecutor"/> that records the stages it was asked to execute
/// (in order) and can be configured to throw at a chosen stage to exercise the orchestrator's failure
/// and resume behavior. It performs no real work — the orchestrator's sequencing/persistence is the
/// unit under test.
/// </summary>
internal sealed class RecordingStageExecutor : IReviewStageExecutor
{
    private readonly ReviewStage? _throwAtStage;

    public RecordingStageExecutor(ReviewStage? throwAtStage = null)
    {
        _throwAtStage = throwAtStage;
    }

    /// <summary>The stages executed, in the order the orchestrator invoked them.</summary>
    public List<ReviewStage> ExecutedStages { get; } = [];

    public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
    {
        if (_throwAtStage == stage)
        {
            throw new InvalidOperationException($"Simulated failure at stage {stage}.");
        }

        ExecutedStages.Add(stage);
        return Task.CompletedTask;
    }
}
