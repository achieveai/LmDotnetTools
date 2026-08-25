using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Thread metadata and state for persistence.
/// Uses property bags for extensibility without schema changes.
/// </summary>
public sealed record ThreadMetadata
{
    /// <summary>
    /// The thread identifier.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    /// The current run ID (null if idle).
    /// </summary>
    public string? CurrentRunId { get; init; }

    /// <summary>
    /// The latest completed run ID.
    /// </summary>
    public string? LatestRunId { get; init; }

    /// <summary>
    /// Unix timestamp in milliseconds when metadata was last updated.
    /// </summary>
    public required long LastUpdated { get; init; }

    /// <summary>
    /// Session mappings: external provider session IDs to internal RunIds.
    /// For example, Claude SDK session_id -> RunId.
    /// Key format: "{provider}:{sessionId}" (e.g., "claude-sdk:sess_abc123")
    /// </summary>
    public IReadOnlyDictionary<string, string>? SessionMappings { get; init; }

    /// <summary>
    /// Extensible property bag for provider-specific or agent-specific data.
    /// </summary>
    public ImmutableDictionary<string, object>? Properties { get; init; }

    /// <summary>
    /// Owning tenant (P1 spec 8.3). Null means "legacy, unclaimed" - a row written before identity
    /// existed, or by a rolled-back build. The startup repair stamps those with the quarantine
    /// tenant, so no row is null while the process is serving requests.
    /// </summary>
    /// <remarks>
    /// A first-class property, deliberately NOT a <see cref="Properties"/> entry: the property bag
    /// is serialized into <c>metadata_json</c> and cannot be filtered in SQL, and the whole point
    /// of these four is that listing is a filter rather than a fetch-then-loop (spec 7.5).
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>
    /// Owning end user, <c>{tid}:{oid}</c>. Null for an app-owned or unclaimed conversation. Null
    /// never matches anything (spec 7.1 principle 4).
    /// </summary>
    public string? OwnerUserId { get; init; }

    /// <summary>
    /// Owning app id - the durable form of the pool's caller-credential freeze, which today exists
    /// only in memory and is lost on restart. Null for a conversation created through the UI.
    /// </summary>
    public string? OwnerAppId { get; init; }

    /// <summary>
    /// How widely the conversation is exposed. Null reads as <see cref="Visibility.Private"/>.
    /// Conversations are never published, so the domain here is Private or Shared.
    /// </summary>
    public Visibility? Visibility { get; init; }
}
