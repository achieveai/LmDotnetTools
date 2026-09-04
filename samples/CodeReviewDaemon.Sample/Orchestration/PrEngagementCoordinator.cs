using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

internal enum EngagementDecisionKind
{
    None,
    Coalesced,
    AdmitCodeReview,
    AdmitDiscussion,
    AdmitMergedClose,
    CleanupTerminal,
}

internal sealed record EngagementDecision(EngagementDecisionKind Kind, long? RoundId, string ReasonCode);

internal interface IEngagementRoundExecutor
{
    EngagementRoundIntent Intent { get; }

    bool IsAvailable => true;

    Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken);
}

/// <summary>
/// Coalesces provider truth into one durable PR engagement and admits at most one eligible round.
/// </summary>
internal sealed class PrEngagementCoordinator
{
    private readonly ReviewStore _store;
    private readonly TimeProvider _timeProvider;

    public PrEngagementCoordinator(ReviewStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<EngagementDecision> ObserveAsync(
        long repoId,
        RepoIdentity repo,
        PullRequestDescriptor descriptor,
        ProviderEngagementSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateObservation(repoId, repo, descriptor, snapshot);

        var observedAt = _timeProvider.GetUtcNow();
        var initialActivity =
            snapshot.ExternalActivity.Count == 0 ? snapshot.LatestObserved : snapshot.ExternalActivity[0].Watermark;
        var engagement = _store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                repo.Provider,
                descriptor.PrId,
                snapshot.Lifecycle,
                snapshot.HeadSha,
                snapshot.BaseSha,
                null,
                initialActivity,
                null,
                null,
                null,
                null,
                null,
                null,
                observedAt
            )
        );
        var latestDemandActivity = snapshot
            .ExternalActivity.Where(activity => activity.CreatesDiscussionDemand)
            .Select(activity => activity.Watermark)
            .DefaultIfEmpty(engagement.LatestActivity)
            .Max()!;
        _store.UpdateEngagementObservation(
            engagement.Id,
            snapshot.Lifecycle,
            snapshot.HeadSha,
            snapshot.BaseSha,
            latestDemandActivity,
            observedAt
        );
        engagement = _store.GetEngagement(engagement.Id)!;

        if (snapshot.Lifecycle is PrLifecycleState.Closed or PrLifecycleState.Abandoned)
        {
            SupersedeActiveOrdinaryRound(engagement, observedAt);
            return Task.FromResult(
                new EngagementDecision(
                    EngagementDecisionKind.CleanupTerminal,
                    null,
                    snapshot.Lifecycle == PrLifecycleState.Closed ? "pr_closed" : "pr_abandoned"
                )
            );
        }

        if (snapshot.Lifecycle == PrLifecycleState.Merged)
        {
            var terminalClose = _store
                .ListEngagementRounds(engagement.Id)
                .LastOrDefault(round =>
                    round.Intent == EngagementRoundIntent.MergedClose
                    && round.Status is EngagementRoundStatus.Completed or EngagementRoundStatus.Parked
                );
            if (terminalClose is not null)
            {
                return Task.FromResult(
                    new EngagementDecision(
                        EngagementDecisionKind.None,
                        terminalClose.Id,
                        terminalClose.Status == EngagementRoundStatus.Completed
                            ? "merged_close_completed"
                            : "merged_close_blocked"
                    )
                );
            }

            if (engagement.ActiveRoundId is { } activeRoundId)
            {
                var active = _store.GetEngagementRound(activeRoundId);
                if (active?.Intent == EngagementRoundIntent.MergedClose)
                {
                    return Task.FromResult(
                        active.Status is EngagementRoundStatus.Pending or EngagementRoundStatus.RetryPending
                            ? new EngagementDecision(
                                EngagementDecisionKind.AdmitMergedClose,
                                active.Id,
                                active.Status == EngagementRoundStatus.RetryPending
                                    ? "retry_pending"
                                    : "merged_close_pending"
                            )
                            : new EngagementDecision(EngagementDecisionKind.None, active.Id, "merged_close_active")
                    );
                }

                SupersedeActiveOrdinaryRound(engagement, observedAt);
                engagement = _store.GetEngagement(engagement.Id)!;
            }

            return Task.FromResult(
                Admit(
                    engagement,
                    EngagementRoundIntent.MergedClose,
                    EngagementDecisionKind.AdmitMergedClose,
                    "merge_bypasses_cooldown"
                )
            );
        }

        if (engagement.ActiveRoundId is { } ordinaryRoundId)
        {
            var ordinaryRound = _store.GetEngagementRound(ordinaryRoundId);
            if (
                ordinaryRound is not null
                && (
                    !string.Equals(ordinaryRound.HeadSha, snapshot.HeadSha, StringComparison.Ordinal)
                    || !string.Equals(ordinaryRound.BaseSha, snapshot.BaseSha, StringComparison.Ordinal)
                )
            )
            {
                SupersedeActiveOrdinaryRound(engagement, observedAt);
                engagement = _store.GetEngagement(engagement.Id)!;
            }
            else if (ordinaryRound?.Status == EngagementRoundStatus.RetryPending)
            {
                return Task.FromResult(
                    new EngagementDecision(DecisionFor(ordinaryRound.Intent), ordinaryRound.Id, "retry_pending")
                );
            }
            else
            {
                return Task.FromResult(
                    new EngagementDecision(EngagementDecisionKind.None, ordinaryRoundId, "round_active")
                );
            }
        }

        var codeReviewDemand = !string.Equals(
            engagement.LastReviewedHeadSha,
            snapshot.HeadSha,
            StringComparison.Ordinal
        );
        if (
            codeReviewDemand
            && _store
                .ListEngagementRounds(engagement.Id)
                .Any(round =>
                    round.Intent == EngagementRoundIntent.CodeReview
                    && round.Status == EngagementRoundStatus.Parked
                    && string.Equals(round.HeadSha, snapshot.HeadSha, StringComparison.Ordinal)
                    && string.Equals(round.BaseSha, snapshot.BaseSha, StringComparison.Ordinal)
                )
        )
        {
            codeReviewDemand = false;
        }
        var discussionDemand = IsAfter(engagement.LatestActivity, engagement.ConsumedActivity);
        if (!codeReviewDemand && !discussionDemand)
        {
            return Task.FromResult(new EngagementDecision(EngagementDecisionKind.None, null, "no_unconsumed_demand"));
        }

        if (engagement.NextEligibleAt is { } eligibleAt && observedAt < eligibleAt)
        {
            return Task.FromResult(new EngagementDecision(EngagementDecisionKind.Coalesced, null, "cooldown_active"));
        }

        return Task.FromResult(
            codeReviewDemand
                ? Admit(
                    engagement,
                    EngagementRoundIntent.CodeReview,
                    EngagementDecisionKind.AdmitCodeReview,
                    "code_review_demand"
                )
                : Admit(
                    engagement,
                    EngagementRoundIntent.DiscussionFollowUp,
                    EngagementDecisionKind.AdmitDiscussion,
                    "discussion_demand"
                )
        );
    }

    public EngagementDecision SupersedeRound(long roundId, ProviderEngagementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var round = _store.GetEngagementRound(roundId);
        if (
            round is null
            || round.Status
                is not (
                    EngagementRoundStatus.Pending
                    or EngagementRoundStatus.Running
                    or EngagementRoundStatus.RetryPending
                )
        )
        {
            return new EngagementDecision(EngagementDecisionKind.None, roundId, "round_not_supersedable");
        }

        if (
            snapshot.Lifecycle == PrLifecycleState.Open
            && string.Equals(round.HeadSha, snapshot.HeadSha, StringComparison.Ordinal)
            && string.Equals(round.BaseSha, snapshot.BaseSha, StringComparison.Ordinal)
        )
        {
            return new EngagementDecision(EngagementDecisionKind.None, roundId, "round_still_current");
        }

        _ = _store.TryTransitionEngagementRound(
            round.Id,
            round.Status,
            EngagementRoundStatus.Superseded,
            _timeProvider.GetUtcNow()
        );
        return new EngagementDecision(EngagementDecisionKind.None, roundId, "round_superseded");
    }

    public Task<EngagementDecision> ReconcileAsync(
        long repoId,
        RepoIdentity repo,
        PullRequestDescriptor descriptor,
        ProviderEngagementSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateObservation(repoId, repo, descriptor, snapshot);

        var engagement = _store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                repo.Provider,
                descriptor.PrId,
                snapshot.Lifecycle,
                snapshot.HeadSha,
                snapshot.BaseSha,
                null,
                snapshot.LatestObserved,
                null,
                null,
                null,
                null,
                null,
                null,
                _timeProvider.GetUtcNow()
            )
        );
        if (engagement.ActiveRoundId is not { } roundId)
        {
            return ObserveAsync(repoId, repo, descriptor, snapshot, cancellationToken);
        }

        var round = _store.GetEngagementRound(roundId);
        if (round?.Status != EngagementRoundStatus.Running)
        {
            return Task.FromResult(
                new EngagementDecision(EngagementDecisionKind.None, roundId, "round_not_reconcilable")
            );
        }

        var now = _timeProvider.GetUtcNow();
        if (round.Intent == EngagementRoundIntent.MergedClose && snapshot.Lifecycle == PrLifecycleState.Merged)
        {
            _ = _store.TryTransitionEngagementRound(
                round.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.RetryPending,
                now
            );
            return Task.FromResult(
                new EngagementDecision(DecisionFor(round.Intent), round.Id, "running_round_retry_pending")
            );
        }

        if (
            snapshot.Lifecycle != PrLifecycleState.Open
            || !string.Equals(round.HeadSha, snapshot.HeadSha, StringComparison.Ordinal)
            || !string.Equals(round.BaseSha, snapshot.BaseSha, StringComparison.Ordinal)
        )
        {
            _ = _store.TryTransitionEngagementRound(
                round.Id,
                EngagementRoundStatus.Running,
                EngagementRoundStatus.Superseded,
                now
            );
            return ObserveAsync(repoId, repo, descriptor, snapshot, cancellationToken);
        }

        _ = _store.TryTransitionEngagementRound(
            round.Id,
            EngagementRoundStatus.Running,
            EngagementRoundStatus.RetryPending,
            now
        );
        return Task.FromResult(
            new EngagementDecision(DecisionFor(round.Intent), round.Id, "running_round_retry_pending")
        );
    }

    private EngagementDecision Admit(
        PrEngagement engagement,
        EngagementRoundIntent intent,
        EngagementDecisionKind decision,
        string reasonCode
    )
    {
        var round = _store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                intent,
                EngagementRoundStatus.Pending,
                engagement.LatestHeadSha,
                engagement.LatestBaseSha,
                engagement.ConsumedActivity,
                engagement.LatestActivity,
                engagement.LatestRoundId ?? 0,
                null,
                0,
                null,
                null,
                null,
                null,
                null
            )
        );
        return round is null
            ? new EngagementDecision(EngagementDecisionKind.None, null, "lease_contended")
            : new EngagementDecision(decision, round.Id, reasonCode);
    }

    private void SupersedeActiveOrdinaryRound(PrEngagement engagement, DateTimeOffset observedAt)
    {
        if (engagement.ActiveRoundId is not { } roundId)
        {
            return;
        }

        var round = _store.GetEngagementRound(roundId);
        if (round is null || round.Intent == EngagementRoundIntent.MergedClose)
        {
            return;
        }

        _ = _store.TryTransitionEngagementRound(round.Id, round.Status, EngagementRoundStatus.Superseded, observedAt);
    }

    private static EngagementDecisionKind DecisionFor(EngagementRoundIntent intent) =>
        intent switch
        {
            EngagementRoundIntent.CodeReview => EngagementDecisionKind.AdmitCodeReview,
            EngagementRoundIntent.DiscussionFollowUp => EngagementDecisionKind.AdmitDiscussion,
            EngagementRoundIntent.MergedClose => EngagementDecisionKind.AdmitMergedClose,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null),
        };

    private static bool IsAfter(ProviderActivityWatermark candidate, ProviderActivityWatermark? boundary) =>
        boundary is null || candidate.CompareTo(boundary) > 0;

    private static void ValidateObservation(
        long repoId,
        RepoIdentity repo,
        PullRequestDescriptor descriptor,
        ProviderEngagementSnapshot snapshot
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repoId);
        if (!string.Equals(repo.Provider, snapshot.LatestObserved.Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshot provider does not match the repository provider.", nameof(snapshot));
        }

        if (descriptor.LifecycleState != PrLifecycleState.Open && descriptor.LifecycleState != snapshot.Lifecycle)
        {
            throw new ArgumentException("Descriptor and frozen engagement snapshot disagree.", nameof(snapshot));
        }
    }
}
