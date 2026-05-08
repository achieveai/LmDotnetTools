using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Message indicating the system has initialized (RunStarted).
///     Used to signal that a new run/session has started processing.
/// </summary>
public record SystemInitMessage : IMessage
{
    /// <summary>
    ///     The session ID from the initialization event
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    ///     The model being used
    /// </summary>
    public string? Model { get; init; }

    // IMessage implementation
    public Role Role => Role.System;
    public string? FromAgent => "claude-agent-sdk";
    public string? GenerationId => null;
    public ImmutableDictionary<string, object>? Metadata => null;
}
