using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Agents;

/// <summary>
///     Interface for agents that support streaming responses.
/// </summary>
public interface IStreamingAgent : IAgent
{
    /// <summary>
    ///     Generates a streaming reply to a sequence of messages.
    /// </summary>
    /// <param name="messages">The input messages to respond to.</param>
    /// <param name="options">Optional configuration for reply generation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of message updates.</returns>
    Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    );
}
