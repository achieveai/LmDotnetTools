using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Interface for receiving notifications when tool call results become available.
///     This enables streaming of tool results as they complete, rather than waiting for all results.
/// </summary>
public interface IToolResultCallback
{
    /// <summary>
    ///     Called when a tool call result becomes available.
    /// </summary>
    /// <param name="toolCallId">The unique identifier of the tool call</param>
    /// <param name="result">The result of the tool call execution</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>A task representing the asynchronous notification operation</returns>
    Task OnToolResultAvailableAsync(
        string toolCallId,
        ToolCallResult result,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Called when a tool call starts execution.
    /// </summary>
    /// <param name="toolCallId">The unique identifier of the tool call</param>
    /// <param name="functionName">The name of the function being called</param>
    /// <param name="functionArgs">The arguments being passed to the function</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>A task representing the asynchronous notification operation</returns>
    Task OnToolCallStartedAsync(
        string toolCallId,
        string functionName,
        string functionArgs,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Called when a tool call encounters an error.
    /// </summary>
    /// <param name="toolCallId">The unique identifier of the tool call</param>
    /// <param name="functionName">The name of the function that failed</param>
    /// <param name="error">The error message or exception details</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>A task representing the asynchronous notification operation</returns>
    Task OnToolCallErrorAsync(
        string toolCallId,
        string functionName,
        string error,
        CancellationToken cancellationToken = default
    );
}
