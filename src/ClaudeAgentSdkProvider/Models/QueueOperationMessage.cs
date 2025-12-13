using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Message wrapping queue operation events for ClaudeAgentLoop to process.
///     Operations:
///     - enqueue: CLI received the user input (message submitted)
///     - dequeue: CLI accepted the input for processing (message assigned to run)
///     - remove: CLI removed the input because a new input arrived before dequeue
/// </summary>
public record QueueOperationMessage : IMessage
{
    /// <summary>
    ///     The operation type: "enqueue", "dequeue", or "remove"
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    ///     Timestamp of the operation
    /// </summary>
    public DateTime? Timestamp { get; init; }

    /// <summary>
    ///     Session identifier from the CLI
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    ///     Content messages parsed from content blocks (for enqueue and remove operations).
    ///     Contains TextMessage and/or ImageMessage instances.
    /// </summary>
    public List<IMessage>? ContentMessages { get; init; }

    // IMessage implementation
    public Role Role => Role.User;

    public string? FromAgent { get; init; }

    public string? GenerationId { get; init; }

    public ImmutableDictionary<string, object>? Metadata { get; init; }

    public string? ThreadId { get; init; }

    public string? RunId { get; init; }
}
