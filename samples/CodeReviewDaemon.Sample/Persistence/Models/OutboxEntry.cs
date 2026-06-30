namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// Crash-safe transition states for a <c>review_outbox</c> row (plan §11):
/// <c>Pending → Sending|Leased → Sent|Posted|Collected</c>, plus the collect-only shortcut
/// <c>Pending → Collected</c> (the daemon deliberately recorded the artifact without ever attempting a
/// send). Transitions are applied with an optimistic conditional UPDATE so a process that crashes
/// mid-send leaves the row in its prior state for another worker to retry. Persisted as TEXT.
/// </summary>
internal enum OutboxStatus
{
    Pending,
    Sending,
    Leased,
    Sent,
    Posted,
    Collected,
}

/// <summary>
/// A pending or completed external side effect (post a review comment, push ReviewBot, …) guarded by
/// a versioned, provider-aware idempotency key (plan §11). The key is <c>UNIQUE</c>, so enqueuing the
/// same logical operation twice is a no-op. <see cref="BodyHash"/> is kept separate (audit only) and
/// is never part of the idempotency key. <see cref="ProviderResponseId"/> records the provider's id
/// for the artifact once the operation succeeds.
/// </summary>
internal sealed record OutboxEntry
{
    public long Id { get; init; }

    /// <summary>Hashed, versioned, provider-keyed idempotency key (<c>v1:{provider}:…</c>).</summary>
    public required string IdempotencyKey { get; init; }

    public required string Provider { get; init; }

    /// <summary>FK to the owning <c>review_run</c>.</summary>
    public required long ReviewRunId { get; init; }

    public required string Operation { get; init; }

    public required string ArtifactKind { get; init; }

    public required OutboxStatus Status { get; init; }

    /// <summary>Audit-only hash of the rendered body; deliberately NOT part of the idempotency key.</summary>
    public string? BodyHash { get; init; }

    /// <summary>Provider's id for the posted/created artifact, recorded on success.</summary>
    public string? ProviderResponseId { get; init; }
}
