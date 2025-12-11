using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Interface for multi-turn conversational agents that support background processing,
/// subscription-based message streaming, and run management.
/// </summary>
/// <remarks>
/// Multi-turn agents can be built on different backends:
/// <list type="bullet">
/// <item><description>Raw LLM APIs with middleware pipeline (<see cref="MultiTurnAgentLoop"/>)</description></item>
/// <item><description>CLI-based agents like Claude Agent SDK (<see cref="ClaudeAgentLoop"/>)</description></item>
/// </list>
/// </remarks>
public interface IMultiTurnAgent : IAsyncDisposable
{
    /// <summary>
    /// The current run ID being processed, or null if idle.
    /// </summary>
    string? CurrentRunId { get; }

    /// <summary>
    /// The thread ID for this agent instance.
    /// </summary>
    string ThreadId { get; }

    /// <summary>
    /// Whether the agent is currently running its background loop.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Enqueue messages for processing. Returns immediately with a receipt (non-blocking).
    /// The actual run assignment is published to subscribers via RunAssignmentMessage
    /// when the implementation decides to start processing.
    /// </summary>
    /// <param name="messages">The messages to submit (user messages, possibly with images)</param>
    /// <param name="inputId">Client-provided correlation ID (optional) - echoed back in receipt and assignment</param>
    /// <param name="parentRunId">Parent run ID to fork from. If null, continues from latest run</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Receipt confirming the input was queued (does not wait for run assignment)</returns>
    ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a single run synchronously (foreground-style).
    /// Sends the user input, subscribes to messages, and yields all messages for this run until completion.
    /// This provides a simpler API for cases where you don't need the full background loop capabilities.
    /// </summary>
    /// <param name="userInput">The user input containing messages to process</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>AsyncEnumerable of all messages produced during this run</returns>
    IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribe to output messages from the agent.
    /// Each subscriber gets an independent stream of messages.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>AsyncEnumerable of messages produced by the agent</returns>
    IAsyncEnumerable<IMessage> SubscribeAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Start the background loop. Runs until cancellation or disposal.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Task that completes when the loop stops</returns>
    Task RunAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop the background loop gracefully.
    /// </summary>
    /// <param name="timeout">Timeout for graceful shutdown. Defaults to 30 seconds.</param>
    Task StopAsync(TimeSpan? timeout = null);
}
