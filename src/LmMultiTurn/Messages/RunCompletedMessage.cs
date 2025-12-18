using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Message published when a run completes.
/// </summary>
public record RunCompletedMessage : IMessage
{
    public required string CompletedRunId { get; init; }
    public bool WasForked { get; init; }
    public string? ForkedToRunId { get; init; }

    /// <summary>
    /// Indicates there are pending messages queued that haven't been assigned to a run yet.
    /// When true, workflows should NOT transition state - another run will follow to process
    /// the pending messages. Only transition when HasPendingMessages is false.
    /// </summary>
    public bool HasPendingMessages { get; init; }

    /// <summary>
    /// Number of pending message batches waiting to be processed.
    /// </summary>
    public int PendingMessageCount { get; init; }

    public string? FromAgent { get; init; }
    public Role Role => Role.System;
    public ImmutableDictionary<string, object>? Metadata { get; init; }
    public string? RunId => CompletedRunId;
    public string? ParentRunId { get; init; }
    public string? ThreadId { get; init; }
    public string? GenerationId { get; init; }
    public int? MessageOrderIdx { get; init; }
}
