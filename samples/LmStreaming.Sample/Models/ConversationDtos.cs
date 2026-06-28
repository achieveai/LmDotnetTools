namespace LmStreaming.Sample.Models;

/// <summary>
/// Summary of a conversation for listing purposes.
/// </summary>
public record ConversationSummary
{
    public required string ThreadId { get; init; }
    public required string Title { get; init; }
    public string? Preview { get; init; }
    public required long LastUpdated { get; init; }

    /// <summary>
    /// Provider id this thread is locked to. Set on first agent creation and persisted
    /// in <c>ThreadMetadata.Properties["provider"]</c>. Null for legacy threads predating
    /// the per-conversation provider feature.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Workspace id this thread is locked to. Set on first agent creation and persisted
    /// in <c>ThreadMetadata.Properties["workspace"]</c>. Null for legacy threads predating
    /// the per-conversation workspace feature.
    /// </summary>
    public string? Workspace { get; init; }
}

/// <summary>
/// In-memory run state for a conversation. Lets a reconnecting client decide whether to resume
/// the live stream: after switching conversations or refreshing, the backend run keeps running
/// (the agent is pooled), so a client returning to a conversation with <see cref="IsInProgress"/>
/// re-opens the WebSocket to resume the in-flight stream instead of showing a frozen partial.
/// </summary>
public record ConversationRunState
{
    public required string ThreadId { get; init; }
    public required bool IsInProgress { get; init; }
    public string? CurrentRunId { get; init; }
}

/// <summary>
/// DTO for updating conversation metadata (title, preview).
/// </summary>
public record ConversationMetadataUpdate
{
    public string? Title { get; init; }
    public string? Preview { get; init; }
}

/// <summary>
/// DTO for switching a conversation's chat mode.
/// </summary>
public record SwitchModeRequest
{
    public required string ModeId { get; init; }
}
