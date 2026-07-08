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
    private readonly string? _throwForPrId;

    /// <param name="throwAtStage">Throw whenever this stage is executed (any run).</param>
    /// <param name="throwForPrId">
    /// When set, only runs whose <see cref="ReviewRun.PrId"/> equals this id throw (at
    /// <paramref name="throwAtStage"/> if given, else at the first stage) — used to prove one poison PR
    /// does not starve the rest of a poll cycle.
    /// </param>
    public RecordingStageExecutor(ReviewStage? throwAtStage = null, string? throwForPrId = null)
    {
        _throwAtStage = throwAtStage;
        _throwForPrId = throwForPrId;
    }

    /// <summary>The stages executed, in the order the orchestrator invoked them.</summary>
    public List<ReviewStage> ExecutedStages { get; } = [];

    public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
    {
        var prMatches = _throwForPrId is null || string.Equals(run.PrId, _throwForPrId, StringComparison.Ordinal);
        var stageMatches = _throwAtStage is null ? _throwForPrId is not null : _throwAtStage == stage;
        if (prMatches && stageMatches && (_throwAtStage is not null || _throwForPrId is not null))
        {
            throw new InvalidOperationException($"Simulated failure at stage {stage} for PR {run.PrId}.");
        }

        ExecutedStages.Add(stage);
        return Task.CompletedTask;
    }

    /// <summary>Records lease releases the orchestrator requested (this double leases no real slots, so it
    /// simply counts the calls). Lets a test assert the orchestrator's terminal <c>finally</c> ran.</summary>
    public int ReleaseCount { get; private set; }

    public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken)
    {
        ReleaseCount++;
        return Task.CompletedTask;
    }
}
