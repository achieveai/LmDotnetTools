using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var bodyHash = request.RequireConfirmedOutcome ? PayloadHash(request) : null;

        // First guard: idempotent enqueue. A replay re-finds the existing row with its current status.
        var entry = _store.EnqueueOutbox(
            new OutboxEntry
            {
                IdempotencyKey = key,
                Provider = request.Key.Provider,
                ReviewRunId = request.ReviewRunId,
                // From the KEY, not the constant. The key already discriminates one logical post from
                // another, and a second caller with its own operation (the permanent-park notice) would
                // otherwise land a row labelled as a posted REVIEW — which is exactly what
                // PrOrchestrator.ClassifyDeliveryOutcome reads as proof the review reached the PR. The
                // review path passes PostReviewCommentOperation here, so nothing about it changes.
                Operation = request.Key.Operation,
                ArtifactKind = request.Key.ArtifactKind,
                Status = OutboxStatus.Pending,
                BodyHash = bodyHash,
            }
        );

        if (request.RequireConfirmedOutcome && !string.Equals(entry.BodyHash, bodyHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An existing publication action cannot change its target or body.");
        }
        if (request.RequireConfirmedOutcome && request.Key.ArtifactKind == "workflow-publication")
        {
            var kind = IntentKind(key);
            if (_store.TryGetLatestArtifact(request.ReviewRunId, kind) is null)
            {
                // This is evidence for the existing receipt, not a second action journal. Save the exact
                // target before a provider send so recovery never needs a model to recreate its arguments.
                _store.AddArtifact(
                    new ReviewArtifact
                    {
                        ReviewRunId = request.ReviewRunId,
                        ArtifactKind = kind,
                        ArtifactSchemaVersion = 1,
                        Provider = request.Key.Provider,
                        Payload = JsonSerializer.Serialize(request),
                    }
                );
            }
        }

        // Terminal replay — the side effect (or the deliberate decision not to act) already happened. Posted is
        // terminal unconditionally (the comment exists). Collected is terminal only for another UNAUTHORIZED
        // replay: it records "we chose not to act", and that choice is the caller's to revise. Once a later
        // attempt IS authorized to post live, treating the Collected row as terminal would silently swallow the
        // delivery forever — the run would complete having posted nothing while its outbox claimed closure.
        var terminal =
            entry.Status is OutboxStatus.Posted
            || (entry.Status is OutboxStatus.Collected && !request.LivePostingAuthorized);
        if (terminal)
        {
            if (
                request.RequireConfirmedOutcome
                && entry.Status == OutboxStatus.Posted
                && string.IsNullOrWhiteSpace(entry.ProviderResponseId)
            )
            {
                throw new ReviewPublicationUncertainException("The posted receipt has no provider response identity.");
            }
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
            await VerifyCurrentPrAsync(request, cancellationToken).ConfigureAwait(false);
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
            var existingId = request.RequireConfirmedOutcome
                ? existing.ProviderCommentId ?? existing.ProviderResponseId
                : existing.ProviderResponseId;
            _ = _store.TryTransitionOutbox(entry.Id, entry.Status, OutboxStatus.Posted, existingId);
            RequireReceipt(request, entry.Id, existingId);
            _logger.LogInformation(
                "Outbox {OutboxId} for key {Key} already posted as {ResponseId} (found via backstop scan); not re-posting.",
                entry.Id,
                key,
                existing.ProviderResponseId
            );
            return new PostOutcome(PostOutcomeKind.AlreadyPostedBackstop, entry.Id, existingId);
        }

        if (request.RequireConfirmedOutcome && entry.Status is not (OutboxStatus.Pending or OutboxStatus.Collected))
        {
            throw new ReviewPublicationUncertainException(
                "An earlier send has no confirmed outcome; publication was not repeated."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.IsStillAuthorized?.Invoke() == false)
            throw new InvalidOperationException("Publication authorization expired before sending.");

        // Hold the lease. Idempotent: a crashed prior attempt may already sit in Sending, and an authorized
        // retry of a previously collect-only run reopens its Collected row from here.
        var claimed = false;
        if (entry.Status is OutboxStatus.Pending or OutboxStatus.Collected)
        {
            claimed = _store.TryTransitionOutbox(entry.Id, entry.Status, OutboxStatus.Sending);
            if (request.RequireConfirmedOutcome && !claimed)
            {
                throw new ReviewPublicationUncertainException("Another attempt owns this publication action.");
            }
        }

        // The admission check may have happened before the agent chose this action or before the dedupe scan.
        // Re-read after taking the lease and immediately before the provider write. If the PR changed in that
        // window, release only a lease claimed by this invocation; no uncertain send was attempted.
        try
        {
            await VerifyCurrentPrAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (claimed)
            {
                _ = _store.TryTransitionOutbox(entry.Id, OutboxStatus.Sending, entry.Status);
            }
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.IsStillAuthorized?.Invoke() == false)
            throw new InvalidOperationException("Publication authorization expired before sending.");
        var posted = await _publisher
            .PostReviewCommentAsync(request.Target, key, request.Body, cancellationToken)
            .ConfigureAwait(false);

        var postedId = request.RequireConfirmedOutcome
            ? posted.ProviderCommentId ?? posted.ProviderResponseId
            : posted.ProviderResponseId;
        _ = _store.TryTransitionOutbox(entry.Id, OutboxStatus.Sending, OutboxStatus.Posted, postedId);
        RequireReceipt(request, entry.Id, postedId);
        _logger.LogInformation(
            "Outbox {OutboxId} for key {Key} posted as {ResponseId}.",
            entry.Id,
            key,
            posted.ProviderResponseId
        );
        return new PostOutcome(PostOutcomeKind.Posted, entry.Id, postedId);
    }

    /// <summary>Adopts provider-visible evidence for uncertain workflow sends. Never sends or renews authorization.</summary>
    public async Task ReconcilePublicationsAsync(
        long runId,
        RepoIdentity expectedRepo,
        CancellationToken cancellationToken
    )
    {
        var run = _store.GetReviewRun(runId) ?? throw new InvalidOperationException("Publication run is missing.");
        foreach (
            var entry in _store
                .GetOutboxForRun(runId)
                .Where(entry => entry.ArtifactKind == "workflow-publication" && entry.Status == OutboxStatus.Sending)
        )
        {
            var artifact = _store.TryGetLatestArtifact(runId, IntentKind(entry.IdempotencyKey));
            if (artifact is null)
                continue; // Older unknown sends have no reconstructable trusted target.
            var request = JsonSerializer.Deserialize<PostReviewRequest>(artifact.Payload);
            if (
                artifact.ArtifactSchemaVersion != 1
                || request is null
                || request.Target?.Repo is null
                || request.Key is null
                || !request.RequireConfirmedOutcome
                || request.ReviewRunId != runId
                || request.Target.Repo.NormalizedKey != expectedRepo.NormalizedKey
                || request.Target.Repo.RepoStableId != expectedRepo.RepoStableId
                || request.Target.PrId != run.PrId
                || request.Key.HeadSha != run.HeadSha
                || request.Key.VariantId != run.VariantId
                || request.Key.ArtifactKind != entry.ArtifactKind
                || request.Key.Operation != entry.Operation
                || IdempotencyKey.Build(request.Key) != entry.IdempotencyKey
                || PayloadHash(request) != entry.BodyHash
                || artifact.Provider != entry.Provider
                || entry.Provider != _publisher.Provider
                || entry.Provider != RepoIdentity.ToPublisherNamespace(expectedRepo.Provider)
            )
                throw new InvalidOperationException(
                    "Publication intent does not match its durable receipt and run scope."
                );
            var existing = await _publisher
                .FindPostedCommentAsync(request.Target, entry.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                continue;
            var responseId = existing.ProviderCommentId ?? existing.ProviderResponseId;
            if (string.IsNullOrWhiteSpace(responseId))
                throw new ReviewPublicationUncertainException("Provider proof has no comment identity.");
            _ = _store.TryTransitionOutbox(entry.Id, OutboxStatus.Sending, OutboxStatus.Posted, responseId);
            RequireReceipt(request, entry.Id, responseId);
        }
    }

    private static string IntentKind(string key) =>
        "workflow-publication-intent:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string PayloadHash(PostReviewRequest request) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.Target, request.Body })))
        );

    private void RequireReceipt(PostReviewRequest request, long outboxId, string responseId)
    {
        if (!request.RequireConfirmedOutcome)
        {
            return;
        }
        var receipt = _store.GetOutbox(outboxId);
        if (
            string.IsNullOrWhiteSpace(responseId)
            || receipt?.Status != OutboxStatus.Posted
            || !string.Equals(receipt.ProviderResponseId, responseId, StringComparison.Ordinal)
        )
        {
            throw new ReviewPublicationUncertainException(
                "The provider result could not be confirmed in the receipt store."
            );
        }
    }

    private static async Task VerifyCurrentPrAsync(PostReviewRequest request, CancellationToken cancellationToken)
    {
        if (request.VerifyCurrentPr is not null)
        {
            await request.VerifyCurrentPr(cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class ReviewPublicationUncertainException(string message) : InvalidOperationException(message);

/// <summary>
/// One review-comment post request. <see cref="LivePostingAuthorized"/> defaults to <c>false</c> so a
/// caller must <i>opt in</i> to live posting — collect-only is the default both here and in the policy.
/// </summary>
internal sealed record PostReviewRequest(
    long ReviewRunId,
    IdempotencyKeyComponents Key,
    ReviewCommentTarget Target,
    string Body,
    bool LivePostingAuthorized = false,
    bool RequireConfirmedOutcome = false,
    [property: JsonIgnore] Func<bool>? IsStillAuthorized = null,
    [property: JsonIgnore] Func<CancellationToken, Task>? VerifyCurrentPr = null
);

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
