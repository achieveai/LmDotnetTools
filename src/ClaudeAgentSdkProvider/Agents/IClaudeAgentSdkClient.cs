using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;

/// <summary>
///     Client interface for interacting with claude-agent-sdk CLI
///     Manages long-lived Node.js process for continuous agent interaction
/// </summary>
public interface IClaudeAgentSdkClient : IAsyncDisposable, IDisposable
{
    /// <summary>
    ///     Whether the underlying process is currently running
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Information about the current session
    /// </summary>
    SessionInfo? CurrentSession { get; }

    /// <summary>
    ///     The last request used to start the client. Can be used for restart.
    /// </summary>
    ClaudeAgentSdkRequest? LastRequest { get; }

    /// <summary>
    ///     Start the long-lived Node.js process with initial configuration
    /// </summary>
    /// <param name="request">Initial request configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Send messages to the running agent and receive streaming responses
    ///     The process maintains session state, so no need to pass sessionId repeatedly
    /// </summary>
    /// <param name="messages">Messages to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of response messages</returns>
    IAsyncEnumerable<IMessage> SendMessagesAsync(
        IEnumerable<IMessage> messages,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Send /exit command to gracefully terminate the interactive session.
    ///     Only applicable in Interactive mode.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if exit command was sent successfully</returns>
    Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Initiates graceful shutdown of the underlying process.
    ///     Sends /exit command first, then closes stdin, waits for exit, then force kills if needed.
    /// </summary>
    /// <param name="timeout">Timeout for graceful shutdown. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">Cancellation token to abort shutdown early</param>
    Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
