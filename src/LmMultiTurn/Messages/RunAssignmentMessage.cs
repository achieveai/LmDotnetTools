using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Message published when a run assignment is created.
/// Allows subscribers to track when user input is assigned to a run.
/// </summary>
public record RunAssignmentMessage : IMessage
{
    public required RunAssignment Assignment { get; init; }

    public string? FromAgent { get; init; }
    public Role Role => Role.System;
    public ImmutableDictionary<string, object>? Metadata { get; init; }
    public string? RunId => Assignment.RunId;
    public string? ParentRunId => Assignment.ParentRunId;
    public string? ThreadId { get; init; }
    public string? GenerationId => Assignment.GenerationId;
    public int? MessageOrderIdx { get; init; }
}
