using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Claims and executes admitted engagement rounds under the shared durable retry/parking contract.</summary>
internal sealed class EngagementRoundRunner
{
    private readonly ReviewStore _store;
    private readonly IReadOnlyDictionary<EngagementRoundIntent, IEngagementRoundExecutor> _executors;
    private readonly CodeReviewDaemonOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EngagementRoundRunner> _logger;

    public EngagementRoundRunner(
        ReviewStore store,
        IEnumerable<IEngagementRoundExecutor> executors,
        CodeReviewDaemonOptions options,
        TimeProvider timeProvider,
        ILogger<EngagementRoundRunner> logger
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executors = (executors ?? throw new ArgumentNullException(nameof(executors))).ToDictionary(executor =>
            executor.Intent
        );
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(EngagementDecision decision, ReviewRun? seed, CancellationToken cancellationToken)
    {
        if (decision.RoundId is not { } roundId)
        {
            return;
        }

        var round = _store.GetEngagementRound(roundId);
        if (round is null)
        {
            return;
        }

        if (!_executors.TryGetValue(round.Intent, out var executor))
        {
            if (
                round.Status is EngagementRoundStatus.Pending or EngagementRoundStatus.RetryPending
                && _store.TryTransitionEngagementRound(
                    round.Id,
                    round.Status,
                    EngagementRoundStatus.Superseded,
                    _timeProvider.GetUtcNow()
                )
            )
            {
                _logger.LogWarning(
                    "No round executor is registered for admitted {Intent} work; round {RoundId} was superseded.",
                    round.Intent,
                    round.Id
                );
            }

            return;
        }

        if (!executor.IsAvailable)
        {
            return;
        }

        var runnableStatus = round.Status is EngagementRoundStatus.Pending or EngagementRoundStatus.RetryPending
            ? round.Status
            : (EngagementRoundStatus?)null;
        if (
            runnableStatus is null
            || !_store.TryTransitionEngagementRound(
                round.Id,
                runnableStatus.Value,
                EngagementRoundStatus.Running,
                _timeProvider.GetUtcNow()
            )
        )
        {
            return;
        }

        try
        {
            if (round.Intent == EngagementRoundIntent.CodeReview)
            {
                if (seed is null)
                {
                    throw new InvalidOperationException("A code-review round requires a review-run seed.");
                }

                _ = _store.CreateOrGetReviewRun(seed with { EngagementRoundId = round.Id });
            }

            var terminal = await executor.ExecuteAsync(_store.GetEngagementRound(round.Id)!, cancellationToken);
            if (
                terminal
                is not (
                    EngagementRoundStatus.Completed
                    or EngagementRoundStatus.RetryPending
                    or EngagementRoundStatus.Superseded
                    or EngagementRoundStatus.Parked
                )
            )
            {
                throw new InvalidOperationException(
                    $"The {round.Intent} executor returned non-terminal status {terminal}."
                );
            }

            if (terminal == EngagementRoundStatus.RetryPending)
            {
                var failures = _store.IncrementEngagementRoundFailureCount(round.Id);
                if (failures >= _options.MaxDurableRetryAttempts)
                {
                    if (
                        !_store.TryParkEngagementRound(
                            round.Id,
                            EngagementRoundStatus.Running,
                            _timeProvider.GetUtcNow(),
                            "executor_retry_budget_exhausted"
                        )
                    )
                    {
                        throw new InvalidOperationException(
                            $"Engagement round {round.Id} could not transition from Running to Parked."
                        );
                    }

                    return;
                }
            }

            if (
                !_store.TryTransitionEngagementRound(
                    round.Id,
                    EngagementRoundStatus.Running,
                    terminal,
                    _timeProvider.GetUtcNow()
                )
            )
            {
                throw new InvalidOperationException(
                    $"Engagement round {round.Id} could not transition from Running to {terminal}."
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var failures = _store.IncrementEngagementRoundFailureCount(round.Id);
            if (failures >= _options.MaxDurableRetryAttempts)
            {
                _ = _store.TryParkEngagementRound(
                    round.Id,
                    EngagementRoundStatus.Running,
                    _timeProvider.GetUtcNow(),
                    "executor_failure_budget_exhausted"
                );
            }
            else
            {
                _ = _store.TryTransitionEngagementRound(
                    round.Id,
                    EngagementRoundStatus.Running,
                    EngagementRoundStatus.RetryPending,
                    _timeProvider.GetUtcNow()
                );
            }

            throw;
        }
    }
}
