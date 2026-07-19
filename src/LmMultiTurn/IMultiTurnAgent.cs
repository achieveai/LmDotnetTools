using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Outcome of an expected-run <see cref="IMultiTurnAgent.CancelCurrentRunAsync"/> request.
/// </summary>
public enum RunCancellationResult
{
    /// <summary>
    /// <c>expectedRunId</c> matched the run that was active at the time of the call; its
    /// run-local cancellation token was signalled. The run's terminal outcome (durable
    /// <c>Cancelled</c> status and a matching <c>RunCompletedMessage</c>) follows asynchronously
    /// once the run's own per-run wrapper observes the cancellation.
    /// </summary>
    Accepted,

    /// <summary>
    /// A run is currently active, but <c>expectedRunId</c> does not match it — e.g. a delayed
    /// Stop for a run that has already finished and been superseded by a newer one. No
    /// cancellation was signalled; the actually-active run is unaffected.
    /// </summary>
    StaleRun,

    /// <summary>No run is currently active for this agent, so there is nothing to cancel.</summary>
    NoActiveRun,
}

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
    /// Non-blocking variant of <see cref="SendAsync"/> for callers (e.g. an HTTP request handler)
    /// that cannot block on channel backpressure and need a queue-full outcome they can map to a
    /// distinct response (e.g. HTTP 503) rather than waiting. When the implementation persists a
    /// run ledger, durably records the input as accepted before attempting to enqueue it, and
    /// rolls that record back if the queue turns out to be full — so a caller polling status by
    /// <paramref name="inputId"/> never observes "accepted" for an input that was in fact rejected.
    /// </summary>
    /// <param name="messages">The messages to submit (user messages, possibly with images)</param>
    /// <param name="inputId">Client-provided correlation ID (optional) - echoed back in receipt and assignment</param>
    /// <param name="parentRunId">Parent run ID to fork from. If null, continues from latest run</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The receipt if accepted and enqueued, or null if the input queue is full.</returns>
    ValueTask<SendReceipt?> TrySendAsync(
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

    /// <summary>
    /// Requests cancellation of the currently active run, IFF <paramref name="expectedRunId"/>
    /// still matches it. This is an expected-run "Stop": a caller (e.g. a UI Stop button or a
    /// parent delegating a child sub-agent's Stop) names the run it observed as current, so a
    /// delayed/stale request against a run that has since finished and been superseded by a
    /// newer one is rejected rather than cancelling the wrong run.
    /// </summary>
    /// <remarks>
    /// Cancels only that run's own cancellation token — linked to, but independently
    /// cancellable from, the background loop's lifetime token — so the loop itself keeps running
    /// and remains able to accept and process subsequent input. Applies uniformly to a primary
    /// agent and to a sub-agent owned by a <c>SubAgentManager</c>, since both are
    /// <see cref="IMultiTurnAgent"/> implementations.
    /// </remarks>
    /// <param name="expectedRunId">The run id the caller last observed as current.</param>
    /// <param name="ct">Cancellation token for the request itself (not the run being cancelled).</param>
    /// <returns>
    /// <see cref="RunCancellationResult.Accepted"/> if <paramref name="expectedRunId"/> matched
    /// the active run and cancellation was signalled;
    /// <see cref="RunCancellationResult.StaleRun"/> if a different run is active;
    /// <see cref="RunCancellationResult.NoActiveRun"/> if no run is active.
    /// </returns>
    Task<RunCancellationResult> CancelCurrentRunAsync(string expectedRunId, CancellationToken ct = default);
}
