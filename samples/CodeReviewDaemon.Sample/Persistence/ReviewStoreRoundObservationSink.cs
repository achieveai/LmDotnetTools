using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Persistence;

internal sealed class ReviewStoreRoundObservationSink : IRoundObservationSink
{
    private readonly ReviewStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    public ReviewStoreRoundObservationSink(ReviewStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task RecordAsync(
        long roundId,
        IReadOnlyList<RoundObservationDraft> observations,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_store.GetEngagementRound(roundId) is null)
            {
                throw new InvalidOperationException($"Engagement round {roundId} does not exist.");
            }

            var sequence = _store.ListRoundObservations(roundId).Select(item => item.Sequence).DefaultIfEmpty().Max();
            foreach (var draft in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sequence++;
                _store.AppendRoundObservation(
                    new RoundObservation(
                        $"observation-{roundId}-{sequence}-{Guid.NewGuid():N}",
                        roundId,
                        sequence,
                        draft.Kind,
                        draft.Summary,
                        _timeProvider.GetUtcNow()
                    ),
                    draft.Evidence
                );
            }
        }

        return Task.CompletedTask;
    }
}
