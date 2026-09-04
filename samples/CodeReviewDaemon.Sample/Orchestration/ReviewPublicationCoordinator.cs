using System.Security.Cryptography;
using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using PersistedReviewAction = CodeReviewDaemon.Sample.Persistence.Models.ReviewAction;
using PersistedReviewActionKind = CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind;

namespace CodeReviewDaemon.Sample.Orchestration;

internal sealed class ReviewPublicationCoordinator : IReviewPublicationOperations
{
    private const int MaxBodyLength = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<ReviewActionStatus> TerminalStatuses =
    [
        ReviewActionStatus.CollectedOnly,
        ReviewActionStatus.Accepted,
        ReviewActionStatus.Rejected,
    ];

    private readonly ReviewStore _store;
    private readonly IReadOnlyDictionary<string, IPrProvider> _providers;
    private readonly IReadOnlyDictionary<string, IReviewCommentPublisher> _publishers;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lock _discussionGateLock = new();
    private readonly Dictionary<(long RoundId, string ActionId), DiscussionPublicationGate> _discussionGates = [];

    public ReviewPublicationCoordinator(
        ReviewStore store,
        IEnumerable<IPrProvider> providers,
        IEnumerable<IReviewCommentPublisher> publishers,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(publishers);
        _providers = providers.ToDictionary(provider => provider.Provider, StringComparer.OrdinalIgnoreCase);
        _publishers = publishers.ToDictionary(publisher => publisher.Provider, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public Task<PublicationOutcome> CreateRootSummaryAsync(
        RootSummaryRequest request,
        CancellationToken cancellationToken
    ) =>
        PublishFlatAsync(
            request.Scope,
            PersistedReviewActionKind.CreateRootSummary,
            request.Body,
            requiresRoot: false,
            createsRoot: true,
            cancellationToken
        );

    public Task<PublicationOutcome> AppendSummaryDeltaAsync(
        SummaryDeltaRequest request,
        CancellationToken cancellationToken
    ) =>
        PublishFlatAsync(
            request.Scope,
            PersistedReviewActionKind.AppendSummaryDelta,
            request.Body,
            requiresRoot: true,
            createsRoot: false,
            cancellationToken
        );

    public Task<PublicationOutcome> SubmitInlineFindingsAsync(
        InlineFindingsRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return PublishInlineFindingsAsync(request, cancellationToken);
    }

    public Task<PublicationOutcome> PostClarificationQuestionAsync(
        ClarificationQuestionRequest request,
        CancellationToken cancellationToken
    ) =>
        PublishFlatAsync(
            request.Scope,
            PersistedReviewActionKind.PostClarificationQuestion,
            request.Body,
            requiresRoot: false,
            createsRoot: false,
            cancellationToken,
            request.ProviderTargetId
        );

    public Task<PublicationOutcome> ReplyToDiscussionAsync(
        DiscussionReplyRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return PublishDiscussionReplyAsync(request, cancellationToken);
    }

    public Task<PublicationOutcome> FinalizeRoundAsync(
        FinalizeRoundRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateScope(request.Scope);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payloadHash = HashPayload(new { request.NoOp });
        var existing = _store.GetReviewAction(request.Scope.RoundId, request.Scope.ActionId);
        if (existing is not null)
        {
            var replay = ReplayOrConflict(existing, PersistedReviewActionKind.FinalizeRound, payloadHash);
            if (replay.Status != ReviewActionStatus.Planned)
            {
                return Task.FromResult(replay);
            }
        }

        var prior = _store.ListReviewActions(request.Scope.RoundId);
        if (
            prior.Any(action =>
                action.Kind == PersistedReviewActionKind.FinalizeRound
                && !string.Equals(action.ActionId, request.Scope.ActionId, StringComparison.Ordinal)
            )
        )
        {
            return Task.FromResult(Rejected(request.Scope.ActionId, "round_already_finalized"));
        }

        if (
            prior.Any(action =>
                !string.Equals(action.ActionId, request.Scope.ActionId, StringComparison.Ordinal)
                && !TerminalStatuses.Contains(action.Status)
            )
        )
        {
            return Task.FromResult(Rejected(request.Scope.ActionId, "actions_not_terminal"));
        }

        var now = _timeProvider.GetUtcNow();
        PersistedReviewAction action;
        try
        {
            action =
                existing
                ?? _store.AddOrGetReviewAction(
                    NewAction(request.Scope, PersistedReviewActionKind.FinalizeRound, payloadHash, now),
                    request.Scope.Sources
                );
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(Rejected(request.Scope.ActionId, "action_id_conflict"));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(Rejected(request.Scope.ActionId, "invalid_source_reference"));
        }

        if (action.Status != ReviewActionStatus.Planned)
        {
            return Task.FromResult(FromAction(action));
        }

        var localReceipt = JsonSerializer.Serialize(new { local = true, noOp = request.NoOp }, JsonOptions);
        _ = _store.TryTransitionReviewAction(
            request.Scope.RoundId,
            request.Scope.ActionId,
            ReviewActionStatus.Planned,
            ReviewActionStatus.Accepted,
            localReceipt,
            null,
            now
        );
        return Task.FromResult(FromAction(_store.GetReviewAction(request.Scope.RoundId, request.Scope.ActionId)!));
    }

    private async Task<PublicationOutcome> PublishFlatAsync(
        PublicationScope scope,
        PersistedReviewActionKind kind,
        string body,
        bool requiresRoot,
        bool createsRoot,
        CancellationToken cancellationToken,
        string? providerTargetId = null
    )
    {
        var validation = ValidateScope(scope);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxBodyLength)
        {
            return Rejected(scope.ActionId, "invalid_body");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var round = _store.GetEngagementRound(scope.RoundId)!;
        var engagement = _store.GetEngagement(round.PrEngagementId)!;
        var payloadHash = HashPayload(new { body, providerTargetId });
        var targetJson = providerTargetId is null
            ? null
            : JsonSerializer.Serialize(new { providerTargetId }, JsonOptions);
        var existing = _store.GetReviewAction(scope.RoundId, scope.ActionId);
        if (existing is not null)
        {
            var replay = ReplayOrConflict(existing, kind, payloadHash, targetJson);
            if (
                createsRoot
                && replay.Status == ReviewActionStatus.Accepted
                && string.IsNullOrWhiteSpace(engagement.RootSummaryReceiptJson)
                && existing.ProviderReceiptJson is { } acceptedReceipt
            )
            {
                _ = _store.TrySetRootSummaryReceipt(engagement.Id, acceptedReceipt);
            }

            var resumable =
                replay.Status == ReviewActionStatus.Planned
                || (replay.Status == ReviewActionStatus.CollectedOnly && scope.LivePostingAuthorized)
                || (
                    replay.Status == ReviewActionStatus.Sending
                    && round.Intent == EngagementRoundIntent.DiscussionFollowUp
                );
            if (!resumable)
            {
                return replay;
            }
        }

        var rootExists = !string.IsNullOrWhiteSpace(_store.GetEngagement(engagement.Id)!.RootSummaryReceiptJson);
        if (createsRoot && rootExists)
        {
            return Rejected(scope.ActionId, "root_summary_already_exists");
        }

        if (
            createsRoot
            && _store
                .ListReviewActions(scope.RoundId)
                .Any(action =>
                    action.Kind == PersistedReviewActionKind.CreateRootSummary
                    && !string.Equals(action.ActionId, scope.ActionId, StringComparison.Ordinal)
                )
        )
        {
            return Rejected(scope.ActionId, "root_summary_already_planned");
        }

        if (requiresRoot && !rootExists)
        {
            return Rejected(scope.ActionId, "root_summary_not_accepted");
        }

        var now = _timeProvider.GetUtcNow();
        PersistedReviewAction action;
        try
        {
            action =
                existing
                ?? _store.AddOrGetReviewAction(NewAction(scope, kind, payloadHash, now, targetJson), scope.Sources);
        }
        catch (InvalidOperationException)
        {
            return Rejected(scope.ActionId, "action_id_conflict");
        }
        catch (ArgumentException)
        {
            return Rejected(scope.ActionId, "invalid_source_reference");
        }

        if (!scope.LivePostingAuthorized)
        {
            if (action.Status == ReviewActionStatus.Planned)
            {
                _ = _store.TryTransitionReviewAction(
                    scope.RoundId,
                    scope.ActionId,
                    ReviewActionStatus.Planned,
                    ReviewActionStatus.CollectedOnly,
                    null,
                    null,
                    now
                );
            }

            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        if (action.Status == ReviewActionStatus.CollectedOnly)
        {
            _ = _store.TryReopenCollectedReviewAction(scope.RoundId, scope.ActionId, now);
            action = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
        }

        var liveRejection = await ValidateLiveStateAsync(scope, cancellationToken).ConfigureAwait(false);
        if (liveRejection is not null)
        {
            PersistRejection(action, liveRejection.RejectionCode!, now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var repo = _store.GetRepo(scope.RepoId)!;
        var publisherProvider = RepoIdentity.ToPublisherNamespace(repo.Provider);
        if (!_publishers.TryGetValue(publisherProvider, out var publisher))
        {
            PersistRejection(action, "publisher_unavailable", now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var key = BuildPublicationKey(scope, repo, kind);
        var target = new ReviewCommentTarget(repo, scope.PrId);
        string? providerResponseId;
        long? outboxId;
        var expected = action.Status;
        if (round.ReviewRunId is { } reviewRunId)
        {
            var poster = new ReviewPoster(publisher, _store, _loggerFactory.CreateLogger<ReviewPoster>());
            var post = await poster
                .PostReviewAsync(
                    new PostReviewRequest(reviewRunId, key, target, body, LivePostingAuthorized: true),
                    cancellationToken
                )
                .ConfigureAwait(false);
            providerResponseId = post.ProviderResponseId;
            outboxId = post.OutboxId;
        }
        else if (round.Intent == EngagementRoundIntent.DiscussionFollowUp)
        {
            var gate = AcquireDiscussionGate(scope.RoundId, scope.ActionId);
            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    action = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
                    if (TerminalStatuses.Contains(action.Status))
                    {
                        return FromAction(action);
                    }

                    expected = action.Status;
                    var idempotencyKey = IdempotencyKey.Build(key);
                    var existingComment = await publisher
                        .FindPostedCommentAsync(target, idempotencyKey, cancellationToken)
                        .ConfigureAwait(false);
                    if (existingComment is not null)
                    {
                        providerResponseId = existingComment.ProviderResponseId;
                    }
                    else
                    {
                        if (
                            action.Status == ReviewActionStatus.Planned
                            && !_store.TryTransitionReviewAction(
                                scope.RoundId,
                                scope.ActionId,
                                ReviewActionStatus.Planned,
                                ReviewActionStatus.Sending,
                                null,
                                null,
                                now
                            )
                        )
                        {
                            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                        }

                        expected = ReviewActionStatus.Sending;
                        var posted = await publisher
                            .PostReviewCommentAsync(target, idempotencyKey, body, cancellationToken)
                            .ConfigureAwait(false);
                        providerResponseId = posted.ProviderResponseId;
                    }

                    outboxId = null;

                    if (providerResponseId is null)
                    {
                        return Rejected(scope.ActionId, "provider_receipt_missing");
                    }

                    var discussionReceipt = JsonSerializer.Serialize(
                        new
                        {
                            provider = scope.Provider,
                            providerCommentId = providerResponseId,
                            outboxId,
                            acceptedAt = now,
                        },
                        JsonOptions
                    );
                    if (expected is ReviewActionStatus.Planned or ReviewActionStatus.Sending)
                    {
                        _ = _store.TryTransitionReviewAction(
                            scope.RoundId,
                            scope.ActionId,
                            expected,
                            ReviewActionStatus.Accepted,
                            discussionReceipt,
                            null,
                            now
                        );
                    }

                    return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                }
                finally
                {
                    gate.Semaphore.Release();
                }
            }
            finally
            {
                ReleaseDiscussionGate(scope.RoundId, scope.ActionId, gate);
            }
        }
        else
        {
            PersistRejection(action, "review_run_unavailable", now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        if (providerResponseId is null)
        {
            return Rejected(scope.ActionId, "provider_receipt_missing");
        }

        var receipt = JsonSerializer.Serialize(
            new
            {
                provider = scope.Provider,
                providerCommentId = providerResponseId,
                outboxId,
                acceptedAt = now,
            },
            JsonOptions
        );
        if (expected is ReviewActionStatus.Planned or ReviewActionStatus.Sending)
        {
            _ = _store.TryTransitionReviewAction(
                scope.RoundId,
                scope.ActionId,
                expected,
                ReviewActionStatus.Accepted,
                receipt,
                null,
                now
            );
        }

        var persistedAction = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
        if (
            createsRoot
            && (
                persistedAction.ProviderReceiptJson is not { } persistedReceipt
                || !_store.TrySetRootSummaryReceipt(engagement.Id, persistedReceipt)
            )
        )
        {
            return Rejected(scope.ActionId, "root_summary_conflict");
        }

        return FromAction(persistedAction);
    }

    private async Task<PublicationOutcome> PublishInlineFindingsAsync(
        InlineFindingsRequest request,
        CancellationToken cancellationToken
    )
    {
        var scope = request.Scope;
        var validation = ValidateScope(scope);
        if (validation is not null)
        {
            return validation;
        }

        if (!TryBuildGitHubInlineComments(request.Findings, out var comments))
        {
            return Rejected(scope.ActionId, "invalid_anchor");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payloadHash = HashPayload(request.Findings);
        var existing = _store.GetReviewAction(scope.RoundId, scope.ActionId);
        if (existing is not null)
        {
            var replay = ReplayOrConflict(existing, PersistedReviewActionKind.SubmitInlineFindings, payloadHash);
            var resumable =
                replay.Status == ReviewActionStatus.Planned
                || (replay.Status == ReviewActionStatus.CollectedOnly && scope.LivePostingAuthorized)
                || replay.Status == ReviewActionStatus.Sending;
            if (!resumable)
            {
                return replay;
            }
        }

        var now = _timeProvider.GetUtcNow();
        PersistedReviewAction action;
        try
        {
            action =
                existing
                ?? _store.AddOrGetReviewAction(
                    NewAction(scope, PersistedReviewActionKind.SubmitInlineFindings, payloadHash, now),
                    scope.Sources
                );
        }
        catch (InvalidOperationException)
        {
            return Rejected(scope.ActionId, "action_id_conflict");
        }
        catch (ArgumentException)
        {
            return Rejected(scope.ActionId, "invalid_source_reference");
        }

        if (!scope.LivePostingAuthorized)
        {
            if (action.Status == ReviewActionStatus.Planned)
            {
                _ = _store.TryTransitionReviewAction(
                    scope.RoundId,
                    scope.ActionId,
                    ReviewActionStatus.Planned,
                    ReviewActionStatus.CollectedOnly,
                    null,
                    null,
                    now
                );
            }

            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        if (action.Status == ReviewActionStatus.CollectedOnly)
        {
            _ = _store.TryReopenCollectedReviewAction(scope.RoundId, scope.ActionId, now);
        }

        var liveRejection = await ValidateLiveStateAsync(scope, cancellationToken).ConfigureAwait(false);
        if (liveRejection is not null)
        {
            PersistRejection(_store.GetReviewAction(scope.RoundId, scope.ActionId)!, liveRejection.RejectionCode!, now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var repo = _store.GetRepo(scope.RepoId)!;
        var providerName = RepoIdentity.ToPublisherNamespace(repo.Provider);
        if (
            !string.Equals(providerName, "github", StringComparison.OrdinalIgnoreCase)
            || !_providers.TryGetValue(providerName, out var provider)
            || !_publishers.TryGetValue(providerName, out var publisher)
        )
        {
            PersistRejection(
                _store.GetReviewAction(scope.RoundId, scope.ActionId)!,
                "provider_native_inline_not_available",
                now
            );
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var anchors = await provider
            .GetInlineAnchorSnapshotAsync(repo, scope.PrId, scope.ExpectedHeadSha, cancellationToken)
            .ConfigureAwait(false);
        if (
            !string.Equals(anchors.HeadSha, scope.ExpectedHeadSha, StringComparison.Ordinal)
            || !comments.All(comment => IsAuthorizedAnchor(comment.Span, anchors))
        )
        {
            PersistRejection(_store.GetReviewAction(scope.RoundId, scope.ActionId)!, "invalid_anchor", now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var gate = AcquireDiscussionGate(scope.RoundId, scope.ActionId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                action = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
                if (TerminalStatuses.Contains(action.Status))
                {
                    return FromAction(action);
                }

                var idempotencyKey = IdempotencyKey.Build(
                    BuildPublicationKey(scope, repo, PersistedReviewActionKind.SubmitInlineFindings)
                );
                var target = new ReviewCommentTarget(repo, scope.PrId);
                var posted = await publisher
                    .FindPostedCommentAsync(target, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (posted is null)
                {
                    liveRejection = await ValidateLiveStateAsync(scope, cancellationToken).ConfigureAwait(false);
                    if (liveRejection is not null)
                    {
                        PersistRejection(action, liveRejection.RejectionCode!, _timeProvider.GetUtcNow());
                        return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                    }

                    if (
                        action.Status == ReviewActionStatus.Planned
                        && !_store.TryTransitionReviewAction(
                            scope.RoundId,
                            scope.ActionId,
                            ReviewActionStatus.Planned,
                            ReviewActionStatus.Sending,
                            null,
                            null,
                            now
                        )
                    )
                    {
                        return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                    }

                    action = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
                    posted = await publisher
                        .SubmitInlineReviewAsync(
                            target,
                            idempotencyKey,
                            new GitHubInlineReviewRequest(scope.ExpectedHeadSha, comments),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                var receipt = SerializeReceipt(scope.Provider, posted, now);
                if (action.Status is ReviewActionStatus.Planned or ReviewActionStatus.Sending)
                {
                    _ = _store.TryTransitionReviewAction(
                        scope.RoundId,
                        scope.ActionId,
                        action.Status,
                        ReviewActionStatus.Accepted,
                        receipt,
                        null,
                        now
                    );
                }

                return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseDiscussionGate(scope.RoundId, scope.ActionId, gate);
        }
    }

    private static bool TryBuildGitHubInlineComments(
        IReadOnlyList<InlineFindingRequest>? findings,
        out IReadOnlyList<GitHubInlineReviewComment> comments
    )
    {
        comments = [];
        if (findings is null || findings.Count == 0)
        {
            return false;
        }

        var built = new List<GitHubInlineReviewComment>(findings.Count);
        foreach (var finding in findings)
        {
            if (
                string.IsNullOrWhiteSpace(finding.Path)
                || finding.Side is not ("LEFT" or "RIGHT")
                || finding.EndLine <= 0
                || finding.StartLine is <= 0
                || finding.StartLine > finding.EndLine
                || string.IsNullOrWhiteSpace(finding.Body)
                || finding.Body.Length > MaxBodyLength
            )
            {
                return false;
            }

            built.Add(
                new GitHubInlineReviewComment(
                    new ProviderCommentSpan(finding.Path, finding.Side, finding.StartLine, finding.EndLine),
                    finding.Body
                )
            );
        }

        comments = built;
        return true;
    }

    private static bool IsAuthorizedAnchor(ProviderCommentSpan span, ProviderInlineAnchorSnapshot snapshot)
    {
        var files = snapshot
            .Files.Where(file => string.Equals(file.Path, span.Path, StringComparison.Ordinal))
            .ToArray();
        if (files.Length != 1)
        {
            return false;
        }

        var ranges = span.Side == "RIGHT" ? files[0].RightRanges : files[0].LeftRanges;
        var start = span.StartLine ?? span.EndLine;
        return ranges.Any(range => range.Contains(start) && range.Contains(span.EndLine));
    }

    private async Task<PublicationOutcome> PublishDiscussionReplyAsync(
        DiscussionReplyRequest request,
        CancellationToken cancellationToken
    )
    {
        var scope = request.Scope;
        var validation = ValidateScope(scope);
        if (validation is not null)
        {
            return validation;
        }

        if (
            string.IsNullOrWhiteSpace(request.Body)
            || request.Body.Length > MaxBodyLength
            || string.IsNullOrWhiteSpace(request.ProviderTargetId)
        )
        {
            return Rejected(scope.ActionId, "invalid_reply");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payloadHash = HashPayload(new { request.Body, request.ProviderTargetId });
        var targetJson = JsonSerializer.Serialize(new { request.ProviderTargetId }, JsonOptions);
        var existing = _store.GetReviewAction(scope.RoundId, scope.ActionId);
        if (existing is not null)
        {
            var replay = ReplayOrConflict(
                existing,
                PersistedReviewActionKind.ReplyToDiscussion,
                payloadHash,
                targetJson
            );
            var resumable =
                replay.Status == ReviewActionStatus.Planned
                || (replay.Status == ReviewActionStatus.CollectedOnly && scope.LivePostingAuthorized)
                || replay.Status == ReviewActionStatus.Sending;
            if (!resumable)
            {
                return replay;
            }
        }

        var now = _timeProvider.GetUtcNow();
        PersistedReviewAction action;
        try
        {
            action =
                existing
                ?? _store.AddOrGetReviewAction(
                    NewAction(scope, PersistedReviewActionKind.ReplyToDiscussion, payloadHash, now, targetJson),
                    scope.Sources
                );
        }
        catch (InvalidOperationException)
        {
            return Rejected(scope.ActionId, "action_id_conflict");
        }
        catch (ArgumentException)
        {
            return Rejected(scope.ActionId, "invalid_source_reference");
        }

        if (!scope.LivePostingAuthorized)
        {
            if (action.Status == ReviewActionStatus.Planned)
            {
                _ = _store.TryTransitionReviewAction(
                    scope.RoundId,
                    scope.ActionId,
                    ReviewActionStatus.Planned,
                    ReviewActionStatus.CollectedOnly,
                    null,
                    null,
                    now
                );
            }

            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        if (action.Status == ReviewActionStatus.CollectedOnly)
        {
            _ = _store.TryReopenCollectedReviewAction(scope.RoundId, scope.ActionId, now);
        }

        var liveRejection = await ValidateLiveStateAsync(scope, cancellationToken).ConfigureAwait(false);
        if (liveRejection is not null)
        {
            PersistRejection(_store.GetReviewAction(scope.RoundId, scope.ActionId)!, liveRejection.RejectionCode!, now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var repo = _store.GetRepo(scope.RepoId)!;
        var providerName = RepoIdentity.ToPublisherNamespace(repo.Provider);
        if (
            !_providers.TryGetValue(providerName, out var provider)
            || !_publishers.TryGetValue(providerName, out var publisher)
        )
        {
            PersistRejection(_store.GetReviewAction(scope.RoundId, scope.ActionId)!, "publisher_unavailable", now);
            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
        }

        var gate = AcquireDiscussionGate(scope.RoundId, scope.ActionId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                action = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
                if (TerminalStatuses.Contains(action.Status))
                {
                    return FromAction(action);
                }

                var idempotencyKey = IdempotencyKey.Build(
                    BuildPublicationKey(scope, repo, PersistedReviewActionKind.ReplyToDiscussion)
                );
                var commentTarget = new ReviewCommentTarget(repo, scope.PrId);
                var posted = await publisher
                    .FindPostedCommentAsync(commentTarget, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (posted is not null && !IsAuthorizedProviderTarget(action, request.ProviderTargetId))
                {
                    PersistRejection(action, "target_not_authorized", now);
                    return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                }

                if (posted is null)
                {
                    var snapshot = await provider
                        .GetEngagementSnapshotAsync(
                            repo,
                            scope.PrId,
                            null,
                            new HashSet<string>(StringComparer.Ordinal),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    var target = snapshot
                        .ExternalActivity.Concat(snapshot.AncestorActivity)
                        .Where(activity =>
                            string.Equals(activity.ProviderObjectId, request.ProviderTargetId, StringComparison.Ordinal)
                        )
                        .DistinctBy(activity => activity.ProviderObjectId, StringComparer.Ordinal)
                        .ToArray();
                    if (target.Length == 0)
                    {
                        PersistRejection(action, "target_not_found", now);
                        return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                    }

                    if (!IsAuthorizedProviderTarget(action, request.ProviderTargetId))
                    {
                        PersistRejection(action, "target_not_authorized", now);
                        return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                    }

                    if (target.Length != 1 || !IsReplyable(target[0]))
                    {
                        PersistRejection(action, "target_not_replyable", now);
                        return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                    }

                    if (action.Status == ReviewActionStatus.Planned)
                    {
                        if (
                            !_store.TryTransitionReviewAction(
                                scope.RoundId,
                                scope.ActionId,
                                ReviewActionStatus.Planned,
                                ReviewActionStatus.Sending,
                                null,
                                null,
                                now
                            )
                        )
                        {
                            return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
                        }

                        action = _store.GetReviewAction(scope.RoundId, scope.ActionId)!;
                    }

                    posted = await PublishNativeReplyAsync(
                            publisher,
                            commentTarget,
                            idempotencyKey,
                            target[0],
                            request.Body,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                var receipt = SerializeReceipt(scope.Provider, posted, now);
                var expected = action.Status;
                if (expected is ReviewActionStatus.Planned or ReviewActionStatus.Sending)
                {
                    _ = _store.TryTransitionReviewAction(
                        scope.RoundId,
                        scope.ActionId,
                        expected,
                        ReviewActionStatus.Accepted,
                        receipt,
                        null,
                        now
                    );
                }

                return FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseDiscussionGate(scope.RoundId, scope.ActionId, gate);
        }
    }

    private bool IsAuthorizedProviderTarget(PersistedReviewAction action, string providerTargetId) =>
        _store
            .ListReviewActionSources(action.EngagementRoundId, action.ActionId)
            .Any(source =>
            {
                var record = _store.GetAuditRecord(source.SourceRecordId);
                return record is not null
                    && record.EngagementRoundId == action.EngagementRoundId
                    && string.Equals(record.ContentSha256, source.ContentSha256, StringComparison.Ordinal)
                    && string.Equals(
                        record.GenerationId,
                        $"provider-object:{providerTargetId}",
                        StringComparison.Ordinal
                    );
            });

    private static bool IsReplyable(ProviderDiscussionRef target) =>
        target.Kind == ProviderDiscussionKind.Comment
        && !string.IsNullOrWhiteSpace(target.CommentId)
        && (
            target.ProviderObjectId.StartsWith("review-comment:", StringComparison.Ordinal)
            || target.ProviderObjectId.StartsWith("issue-comment:", StringComparison.Ordinal)
            || (
                target.ProviderObjectId.StartsWith("thread:", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(target.ThreadId)
                && TryParseAdoThreadContext(target.IterationContext, out _)
            )
        );

    private static Task<PostedComment> PublishNativeReplyAsync(
        IReviewCommentPublisher publisher,
        ReviewCommentTarget target,
        string idempotencyKey,
        ProviderDiscussionRef discussion,
        string body,
        CancellationToken cancellationToken
    )
    {
        if (discussion.ProviderObjectId.StartsWith("review-comment:", StringComparison.Ordinal))
        {
            return publisher.ReplyToInlineReviewCommentAsync(
                target,
                idempotencyKey,
                new GitHubInlineReplyRequest(discussion.ThreadId, discussion.CommentId, discussion.Permalink, body),
                cancellationToken
            );
        }

        if (discussion.ProviderObjectId.StartsWith("issue-comment:", StringComparison.Ordinal))
        {
            return publisher.PostFlatConversationCommentAsync(
                target,
                idempotencyKey,
                new GitHubFlatConversationComment(body, discussion.Permalink),
                cancellationToken
            );
        }

        _ = TryParseAdoThreadContext(discussion.IterationContext, out var iterationContext);
        return publisher.ReplyToThreadAsync(
            target,
            idempotencyKey,
            new AdoThreadReplyRequest(
                discussion.ThreadId,
                discussion.CommentId,
                discussion.Status,
                ToSpan(discussion),
                iterationContext,
                body
            ),
            cancellationToken
        );
    }

    private static ProviderCommentSpan? ToSpan(ProviderDiscussionRef discussion) =>
        string.IsNullOrWhiteSpace(discussion.Path)
        || string.IsNullOrWhiteSpace(discussion.Side)
        || discussion.EndLine is null
            ? null
            : new ProviderCommentSpan(discussion.Path, discussion.Side, discussion.StartLine, discussion.EndLine.Value);

    private static bool TryParseAdoThreadContext(string? contextJson, out AdoPullRequestThreadContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return true;
        }

        try
        {
            context = JsonSerializer.Deserialize<AdoPullRequestThreadContext>(contextJson, JsonOptions);
            return context is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SerializeReceipt(string provider, PostedComment posted, DateTimeOffset acceptedAt) =>
        JsonSerializer.Serialize(
            new
            {
                provider,
                providerResponseId = posted.ProviderResponseId,
                providerReviewId = posted.ReviewId,
                providerThreadId = posted.ThreadId,
                providerCommentId = posted.CommentId ?? posted.ProviderResponseId,
                providerParentCommentId = posted.ParentCommentId,
                providerPermalink = posted.Permalink,
                status = posted.Status,
                span = posted.Span,
                iterationContext = posted.IterationContext,
                relationshipDegraded = posted.RelationshipDegraded,
                acceptedAt,
            },
            JsonOptions
        );

    private Task<PublicationOutcome> RejectUnsupportedRichActionAsync<TRequest>(
        PublicationScope scope,
        PersistedReviewActionKind kind,
        TRequest request,
        string rejectionCode,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateScope(scope);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        var payloadHash = HashPayload(request!);
        var existing = _store.GetReviewAction(scope.RoundId, scope.ActionId);
        if (existing is not null)
        {
            var replay = ReplayOrConflict(existing, kind, payloadHash);
            if (replay.Status != ReviewActionStatus.Planned)
            {
                return Task.FromResult(replay);
            }
        }

        var now = _timeProvider.GetUtcNow();
        try
        {
            var action =
                existing ?? _store.AddOrGetReviewAction(NewAction(scope, kind, payloadHash, now), scope.Sources);
            PersistRejection(action, rejectionCode, now);
            return Task.FromResult(FromAction(_store.GetReviewAction(scope.RoundId, scope.ActionId)!));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(Rejected(scope.ActionId, "action_id_conflict"));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(Rejected(scope.ActionId, "invalid_source_reference"));
        }
    }

    private PublicationOutcome? ValidateScope(PublicationScope? scope)
    {
        if (scope is null || scope.RoundId <= 0 || string.IsNullOrWhiteSpace(scope.ActionId))
        {
            return Rejected(scope?.ActionId ?? string.Empty, "invalid_scope");
        }

        var round = _store.GetEngagementRound(scope.RoundId);
        if (round is null)
        {
            return Rejected(scope.ActionId, "round_not_found");
        }

        if (round.Status != EngagementRoundStatus.Running)
        {
            return Rejected(scope.ActionId, "round_not_running");
        }

        var engagement = _store.GetEngagement(round.PrEngagementId)!;
        if (
            !string.Equals(
                scope.Provider,
                RepoIdentity.ToPublisherNamespace(engagement.Provider),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return Rejected(scope.ActionId, "scope_provider_mismatch");
        }

        if (scope.RepoId != engagement.RepoId)
        {
            return Rejected(scope.ActionId, "scope_repository_mismatch");
        }

        if (!string.Equals(scope.PrId, engagement.PrId, StringComparison.Ordinal))
        {
            return Rejected(scope.ActionId, "scope_pr_mismatch");
        }

        if (!string.Equals(scope.ExpectedHeadSha, round.HeadSha, StringComparison.Ordinal))
        {
            return Rejected(scope.ActionId, "stale_head");
        }

        return null;
    }

    private async Task<PublicationOutcome?> ValidateLiveStateAsync(
        PublicationScope scope,
        CancellationToken cancellationToken
    )
    {
        var repo = _store.GetRepo(scope.RepoId)!;
        var providerName = RepoIdentity.ToPublisherNamespace(repo.Provider);
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return Rejected(scope.ActionId, "provider_unavailable");
        }

        var lifecycle = await provider.GetPrStateAsync(repo, scope.PrId, cancellationToken).ConfigureAwait(false);
        if (lifecycle != PrLifecycle.Open)
        {
            return Rejected(scope.ActionId, "pr_not_open");
        }

        var currentHead = await provider
            .GetCurrentHeadShaAsync(repo, scope.PrId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentHead, scope.ExpectedHeadSha, StringComparison.Ordinal))
        {
            return Rejected(scope.ActionId, "stale_head");
        }

        return null;
    }

    private IdempotencyKeyComponents BuildPublicationKey(
        PublicationScope scope,
        RepoIdentity repo,
        PersistedReviewActionKind kind
    ) =>
        new(
            scope.Provider,
            repo.OrgOrOwner,
            repo.Project,
            repo.RepoStableId ?? repo.NormalizedKey,
            scope.PrId,
            "typed-review-action",
            kind.ToString(),
            $"{scope.RoundId}-{scope.ActionId}",
            scope.ExpectedHeadSha,
            "primary"
        );

    private static PersistedReviewAction NewAction(
        PublicationScope scope,
        PersistedReviewActionKind kind,
        string payloadHash,
        DateTimeOffset now,
        string? targetJson = null
    ) =>
        new(
            scope.RoundId,
            scope.ActionId,
            kind,
            ReviewActionStatus.Planned,
            payloadHash,
            targetJson,
            null,
            null,
            now,
            now
        );

    private DiscussionPublicationGate AcquireDiscussionGate(long roundId, string actionId)
    {
        lock (_discussionGateLock)
        {
            var key = (roundId, actionId);
            if (!_discussionGates.TryGetValue(key, out var gate))
            {
                gate = new DiscussionPublicationGate();
                _discussionGates.Add(key, gate);
            }

            gate.ReferenceCount++;
            return gate;
        }
    }

    private void ReleaseDiscussionGate(long roundId, string actionId, DiscussionPublicationGate gate)
    {
        lock (_discussionGateLock)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0)
            {
                _discussionGates.Remove((roundId, actionId));
                gate.Semaphore.Dispose();
            }
        }
    }

    private void PersistRejection(PersistedReviewAction action, string code, DateTimeOffset now)
    {
        if (action.Status is not (ReviewActionStatus.Planned or ReviewActionStatus.Sending))
        {
            return;
        }

        _ = _store.TryTransitionReviewAction(
            action.EngagementRoundId,
            action.ActionId,
            action.Status,
            ReviewActionStatus.Rejected,
            null,
            JsonSerializer.Serialize(new { code }, JsonOptions),
            now
        );
    }

    private static PublicationOutcome ReplayOrConflict(
        PersistedReviewAction existing,
        PersistedReviewActionKind kind,
        string payloadHash,
        string? targetJson = null
    ) =>
        existing.Kind == kind
        && string.Equals(existing.PayloadSha256, payloadHash, StringComparison.Ordinal)
        && string.Equals(existing.ProviderTargetJson, targetJson, StringComparison.Ordinal)
            ? FromAction(existing)
            : Rejected(existing.ActionId, "action_id_conflict");

    private static PublicationOutcome FromAction(PersistedReviewAction action)
    {
        string? providerReviewId = null;
        string? providerThreadId = null;
        string? providerCommentId = null;
        string? providerPermalink = null;
        var relationshipDegraded = false;
        DateTimeOffset? acceptedAt = null;
        if (action.ProviderReceiptJson is { } receipt)
        {
            using var document = JsonDocument.Parse(receipt);
            var root = document.RootElement;
            providerReviewId = StringProperty(root, "providerReviewId");
            providerThreadId = StringProperty(root, "providerThreadId");
            providerCommentId = StringProperty(root, "providerCommentId");
            providerPermalink = StringProperty(root, "providerPermalink");
            if (
                root.TryGetProperty("relationshipDegraded", out var degraded)
                && degraded.ValueKind is JsonValueKind.True or JsonValueKind.False
            )
            {
                relationshipDegraded = degraded.GetBoolean();
            }

            if (root.TryGetProperty("acceptedAt", out var accepted) && accepted.TryGetDateTimeOffset(out var at))
            {
                acceptedAt = at;
            }
        }

        string? rejectionCode = null;
        if (action.RejectionJson is { } rejection)
        {
            using var document = JsonDocument.Parse(rejection);
            if (document.RootElement.TryGetProperty("code", out var code))
            {
                rejectionCode = code.GetString();
            }
        }

        return new PublicationOutcome(
            action.Status,
            action.ActionId,
            providerReviewId,
            providerThreadId,
            providerCommentId,
            providerPermalink,
            relationshipDegraded,
            acceptedAt,
            rejectionCode
        );
    }

    private static string? StringProperty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PublicationOutcome Rejected(string actionId, string code) =>
        new(ReviewActionStatus.Rejected, actionId, null, null, null, null, false, null, code);

    private static string HashPayload<T>(T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class DiscussionPublicationGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }
}
