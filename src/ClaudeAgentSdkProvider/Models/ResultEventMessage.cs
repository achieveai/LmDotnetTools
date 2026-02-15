using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Marker message indicating a turn is complete (ResultEvent received from CLI).
///     Used by SubscribeToMessagesAsync to signal end of a response cycle.
/// </summary>
public record ResultEventMessage : IMessage
{
    /// <summary>
    ///     Whether the result indicates an error
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    ///     The result text (if any)
    /// </summary>
    public string? Result { get; init; }

    // IMessage implementation
    public Role Role => Role.Assistant;
    public string? FromAgent => "claude-agent-sdk";
    public string? GenerationId => null;
    public ImmutableDictionary<string, object>? Metadata => null;
}
