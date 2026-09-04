using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

internal sealed record EngagementCutoverSeedResult(bool Completed, int SeededEngagements);

/// <summary>
/// Idempotently initializes durable engagement state for PRs that the legacy review path already knows.
/// </summary>
internal sealed class EngagementCutoverSeeder
{
    private const string MarkerProvider = "review-engagement";
    private const string MarkerScope = "global-cutover-seed";
    private const int MarkerVersion = 1;
    private const string MarkerPayload = "{\"complete\":true}";
    private static readonly string[] LegacyGapReasons =
    [
        "legacy_prompt_unavailable",
        "legacy_transcript_unavailable",
        "legacy_judge_unavailable",
    ];

    private readonly ReviewStore _store;
    private readonly IReadOnlyDictionary<string, IPrProvider> _providers;
    private readonly TimeProvider _timeProvider;

    public EngagementCutoverSeeder(ReviewStore store, IEnumerable<IPrProvider> providers, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Provider, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool IsComplete => !_store.ReadCursor(MarkerProvider, MarkerScope, MarkerVersion).ShouldResync;

    public async Task<EngagementCutoverSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        if (IsComplete)
        {
            return new EngagementCutoverSeedResult(true, 0);
        }

        var seeded = 0;
        var reviewedPrs = await _store.ListReviewedPrsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reviewedPr in reviewedPrs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_providers.TryGetValue(reviewedPr.Provider, out var provider))
            {
                throw new InvalidOperationException(
                    $"No engagement provider is registered for '{reviewedPr.Provider}'."
                );
            }

            var repoId = _store.EnsureRepo(reviewedPr.Repo);
            var existingEngagement = _store.GetEngagement(reviewedPr.Provider, repoId, reviewedPr.PrId);
            var snapshot = await provider
                .GetEngagementSnapshotAsync(
                    reviewedPr.Repo,
                    reviewedPr.PrId,
                    after: null,
                    daemonReceiptIds: _store.GetPostedProviderReceiptIds(repoId, reviewedPr.PrId),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (snapshot.Lifecycle != PrLifecycleState.Open)
            {
                continue;
            }

            var latestRun = _store.GetLatestReviewedRun(repoId, reviewedPr.PrId);
            var lastCompletedAt = _store.GetLastPostedReviewAt(repoId, reviewedPr.PrId);
            var rootReceipt = SerializeRootReceipt(_store.GetLatestPostedReviewOutbox(repoId, reviewedPr.PrId));
            var demandActivity = snapshot
                .ExternalActivity.Where(activity => activity.CreatesDiscussionDemand)
                .Select(activity => activity.Watermark)
                .DefaultIfEmpty(snapshot.LatestObserved)
                .Max()!;
            var consumedActivity = snapshot.ExternalActivity.Any(activity => activity.CreatesDiscussionDemand)
                ? null
                : demandActivity;
            var engagement = _store.CreateOrGetEngagement(
                new PrEngagement(
                    0,
                    repoId,
                    reviewedPr.Provider,
                    reviewedPr.PrId,
                    snapshot.Lifecycle,
                    snapshot.HeadSha,
                    snapshot.BaseSha,
                    latestRun?.HeadSha,
                    demandActivity,
                    consumedActivity,
                    lastCompletedAt,
                    lastCompletedAt?.AddHours(1),
                    null,
                    null,
                    rootReceipt,
                    _timeProvider.GetUtcNow()
                )
            );
            _store.UpdateEngagementObservation(
                engagement.Id,
                snapshot.Lifecycle,
                snapshot.HeadSha,
                snapshot.BaseSha,
                demandActivity,
                _timeProvider.GetUtcNow()
            );
            _store.AdoptHistoricalEngagementEvidence(engagement.Id, latestRun?.HeadSha, lastCompletedAt, rootReceipt);
            engagement = _store.GetEngagement(engagement.Id)!;
            if (latestRun is not null)
            {
                var historicalRound = _store.SeedHistoricalCompletedCodeReviewRound(
                    engagement.Id,
                    latestRun.Id,
                    lastCompletedAt
                );
                EnsureLegacyGaps(reviewedPr, repoId, historicalRound.Id);
            }

            seeded++;
        }

        _store.SaveCursor(
            new OpaqueCursor
            {
                Provider = MarkerProvider,
                Scope = MarkerScope,
                CursorVersion = MarkerVersion,
                CursorPayload = MarkerPayload,
                HighWaterMark = _timeProvider.GetUtcNow().ToString("O"),
            }
        );
        return new EngagementCutoverSeedResult(true, seeded);
    }

    private void EnsureLegacyGaps(ReviewedPrRow reviewedPr, long repoId, long roundId)
    {
        foreach (var gapReason in LegacyGapReasons)
        {
            _ = _store.RecordLegacyAuditGap(
                roundId,
                $"legacy:{reviewedPr.Provider}:{repoId}:{reviewedPr.PrId}:{gapReason}",
                gapReason,
                _timeProvider.GetUtcNow()
            );
        }
    }

    private static string? SerializeRootReceipt(OutboxEntry? receipt) =>
        receipt?.ProviderResponseId is { } providerObjectId
            ? JsonSerializer.Serialize(
                new
                {
                    provider = receipt.Provider,
                    providerObjectId,
                    outboxId = receipt.Id,
                }
            )
            : null;
}
