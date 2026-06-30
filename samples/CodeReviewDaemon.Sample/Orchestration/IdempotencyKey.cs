namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Builds the versioned, provider-aware idempotency key that guards every external side effect (plan
/// §11). The key is the <c>UNIQUE</c> column on <c>review_outbox</c>, so two attempts to post the same
/// logical artifact collapse to one row — the first half of exactly-once posting. The leading
/// <c>v1:</c> lets the key shape evolve without colliding with keys minted by an older daemon.
/// <para>
/// Shape: <c>v1:{provider}:{org}:{project?}:{repo_stable_id}:{pr_id}:{operation}:{artifact_kind}:{artifact_subject}:{trigger_watermark}:{variant_id}</c>.
/// A null project emits an <i>empty</i> segment so the colon count stays fixed regardless of provider
/// (GitHub has no project layer; ADO does) — the segment positions never shift.
/// </para>
/// </summary>
internal static class IdempotencyKey
{
    /// <summary>Current key schema version. Bump only when the segment layout changes.</summary>
    public const string Version = "v1";

    /// <summary>
    /// Renders <paramref name="components"/> into the canonical key string. The human-identity parts
    /// (provider/org/project) are case-folded to match <see cref="Persistence.Models.RepoIdentity.NormalizedKey"/>
    /// so casing drift never mints a second key; the opaque provider id and SHAs are left verbatim.
    /// </summary>
    public static string Build(IdempotencyKeyComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var segments = new[]
        {
            Version,
            Fold(Require(components.Provider, nameof(components.Provider))),
            Fold(Require(components.OrgOrOwner, nameof(components.OrgOrOwner))),
            Fold(components.Project ?? string.Empty),
            Require(components.RepoStableId, nameof(components.RepoStableId)),
            Require(components.PrId, nameof(components.PrId)),
            Require(components.Operation, nameof(components.Operation)),
            Require(components.ArtifactKind, nameof(components.ArtifactKind)),
            Require(components.ArtifactSubject, nameof(components.ArtifactSubject)),
            Require(components.TriggerWatermark, nameof(components.TriggerWatermark)),
            Require(components.VariantId, nameof(components.VariantId)),
        };

        return string.Join(':', segments);
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Idempotency key component '{name}' must be non-empty.", nameof(value))
            : value.Contains(':', StringComparison.Ordinal)
                ? throw new ArgumentException(
                    $"Idempotency key component '{name}' must not contain ':' (it is the segment separator).",
                    nameof(value))
                : value;

    private static string Fold(string value) => value.ToLowerInvariant();
}

/// <summary>
/// The inputs that uniquely identify one external side effect. <see cref="ArtifactSubject"/> scopes the
/// key below the artifact <em>kind</em> (e.g. a specific finding id or file path) so two distinct
/// comments of the same kind on the same run get distinct keys; <see cref="VariantId"/> keeps the A/B
/// arms' side effects independent.
/// </summary>
internal sealed record IdempotencyKeyComponents(
    string Provider,
    string OrgOrOwner,
    string? Project,
    string RepoStableId,
    string PrId,
    string Operation,
    string ArtifactKind,
    string ArtifactSubject,
    string TriggerWatermark,
    string VariantId);
