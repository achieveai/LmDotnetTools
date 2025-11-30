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

    public string? FromAgent { get; init; }
    public Role Role => Role.System;
    public ImmutableDictionary<string, object>? Metadata { get; init; }
    public string? RunId => CompletedRunId;
    public string? ParentRunId { get; init; }
    public string? ThreadId { get; init; }
    public string? GenerationId { get; init; }
    public int? MessageOrderIdx { get; init; }
}
