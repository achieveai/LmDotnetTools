using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Posts a review comment exactly once (plan §11). It combines two guards: the <c>review_outbox</c>
/// idempotency key (a duplicate logical post collapses to one row) and a provider-side comment scan
/// (the backstop for the window between "posted provider-side" and "outbox transition committed").
/// <para>
/// <b>Collect-only is the safe default.</b> A post happens only when the caller passes
/// <see cref="PostReviewRequest.LivePostingAuthorized"/> — an explicit operator action. Otherwise the
/// outbox row is recorded as <see cref="OutboxStatus.Collected"/> (deliberately un-posted) and no
/// external side effect occurs.
/// </para>
/// </summary>
internal sealed class ReviewPoster
{
    /// <summary>Outbox operation discriminator for a posted review comment.</summary>
    public const string PostReviewCommentOperation = "post-review-comment";

    private readonly IReviewCommentPublisher _publisher;
    private readonly ReviewStore _store;
    private readonly ILogger<ReviewPoster> _logger;

    public ReviewPoster(IReviewCommentPublisher publisher, ReviewStore store, ILogger<ReviewPoster> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Idempotently handles one review-comment post. Returns the disposition (posted, recorded as
    /// collect-only, or a replay no-op) plus the outbox row id and any provider response id.
    /// </summary>
    public async Task<PostOutcome> PostReviewAsync(PostReviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Body);

        var key = IdempotencyKey.Build(request.Key);

        // First guard: idempotent enqueue. A replay re-finds the existing row with its current status.
        var entry = _store.EnqueueOutbox(new OutboxEntry
        {
            IdempotencyKey = key,
            Provider = request.Key.Provider,
            ReviewRunId = request.ReviewRunId,
            Operation = PostReviewCommentOperation,
            ArtifactKind = request.Key.ArtifactKind,
            Status = OutboxStatus.Pending,
        });

        // Terminal replay — the side effect (or the deliberate decision not to act) already happened. NOTE the
        // activation semantics this encodes (see appsettings *_comment_posting + ADO-ONBOARDING.md): a run
        // recorded Collected while posting was off stays terminal, so turning EnableCommentPosting on later does
        // NOT retroactively post that already-collected head — enabling posting applies to FUTURE commits only
        // (a new head is a new run + a fresh outbox key that posts normally). Promoting/requeuing already-
        // collected artifacts is a deliberate, not-yet-built operation, not an accidental side effect of the flip.
        if (entry.Status is OutboxStatus.Posted or OutboxStatus.Collected)
        {
            _logger.LogInformation(
                "Outbox {OutboxId} for key {Key} is already {Status}; replay no-op.",
                entry.Id,
                key,
                entry.Status
            );
            return new PostOutcome(PostOutcomeKind.ReplayNoOp, entry.Id, entry.ProviderResponseId);
        }

        // Safe default: no live authorization → record as collect-only and never touch the provider.
        if (!request.LivePostingAuthorized)
        {
            _ = _store.TryTransitionOutbox(entry.Id, entry.Status, OutboxStatus.Collected);
            _logger.LogInformation(
                "Outbox {OutboxId} for key {Key} recorded collect-only (no live posting authorized).",
                entry.Id,
                key
            );
            return new PostOutcome(PostOutcomeKind.CollectedOnly, entry.Id, null);
        }

        // Second guard: backstop scan. If a prior attempt posted but crashed before committing the
        // transition, adopt the existing comment instead of posting a duplicate.
        var existing = await _publisher
            .FindPostedCommentAsync(request.Target, key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _ = _store.TryTransitionOutbox(entry.Id, entry.Status, OutboxStatus.Posted, existing.ProviderResponseId);
            _logger.LogInformation(
                "Outbox {OutboxId} for key {Key} already posted as {ResponseId} (found via backstop scan); not re-posting.",
                entry.Id,
                key,
                existing.ProviderResponseId
            );
            return new PostOutcome(PostOutcomeKind.AlreadyPostedBackstop, entry.Id, existing.ProviderResponseId);
        }

        // Hold the lease. Idempotent: a crashed prior attempt may already sit in Sending.
        if (entry.Status == OutboxStatus.Pending)
        {
            _ = _store.TryTransitionOutbox(entry.Id, OutboxStatus.Pending, OutboxStatus.Sending);
        }

        var posted = await _publisher
            .PostReviewCommentAsync(request.Target, key, request.Body, cancellationToken)
            .ConfigureAwait(false);

        _ = _store.TryTransitionOutbox(entry.Id, OutboxStatus.Sending, OutboxStatus.Posted, posted.ProviderResponseId);
        _logger.LogInformation(
            "Outbox {OutboxId} for key {Key} posted as {ResponseId}.",
            entry.Id,
            key,
            posted.ProviderResponseId
        );
        return new PostOutcome(PostOutcomeKind.Posted, entry.Id, posted.ProviderResponseId);
    }
}

/// <summary>
/// One review-comment post request. <see cref="LivePostingAuthorized"/> defaults to <c>false</c> so a
/// caller must <i>opt in</i> to live posting — collect-only is the default both here and in the policy.
/// </summary>
internal sealed record PostReviewRequest(
    long ReviewRunId,
    IdempotencyKeyComponents Key,
    ReviewCommentTarget Target,
    string Body,
    bool LivePostingAuthorized = false);

/// <summary>How a <see cref="ReviewPoster.PostReviewAsync"/> call resolved.</summary>
internal enum PostOutcomeKind
{
    /// <summary>No live authorization — recorded as collect-only; nothing posted.</summary>
    CollectedOnly,

    /// <summary>Posted to the provider during this invocation.</summary>
    Posted,

    /// <summary>A prior post was discovered via the backstop scan and adopted; not re-posted.</summary>
    AlreadyPostedBackstop,

    /// <summary>The outbox row was already terminal (posted or collected); nothing to do.</summary>
    ReplayNoOp,
}

/// <summary>The disposition of a post attempt plus the outbox row id and any provider response id.</summary>
internal sealed record PostOutcome(PostOutcomeKind Kind, long OutboxId, string? ProviderResponseId);
