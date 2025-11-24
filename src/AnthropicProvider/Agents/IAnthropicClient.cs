using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
/// <summary>
/// Interface for clients that interact with the Anthropic API.
/// </summary>
public interface IAnthropicClient : IDisposable
{
    /// <summary>
    /// Creates a chat completion using the Anthropic API.
    /// </summary>
    /// <param name="request">The request to send to the API.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The response from the API.</returns>
    Task<AnthropicResponse> CreateChatCompletionsAsync(
        AnthropicRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a streaming chat completion using the Anthropic API.
    /// </summary>
    /// <param name="request">The request to send to the API.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of events from the API.</returns>
    Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(
        AnthropicRequest request,
        CancellationToken cancellationToken = default
    );
}
