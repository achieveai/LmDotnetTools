using System.Collections.Immutable;

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
}
