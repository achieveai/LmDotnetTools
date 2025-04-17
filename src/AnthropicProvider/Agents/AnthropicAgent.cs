using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Agents;

/// <summary>
/// Agent that interacts with the Anthropic Claude API.
/// </summary>
public class AnthropicAgent : IStreamingAgent, IDisposable
{
    private readonly IAnthropicClient _client;
    private bool _disposed = false;

    /// <summary>
    /// Gets the name of the agent.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicAgent"/> class.
    /// </summary>
    /// <param name="name">The name of the agent.</param>
    /// <param name="client">The client to use for API calls.</param>
    public AnthropicAgent(string name, IAnthropicClient client)
    {
        Name = name;
        _client = client;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
      IEnumerable<IMessage> messages,
      GenerateReplyOptions? options = null,
      CancellationToken cancellationToken = default)
    {
        var request = AnthropicRequest.FromMessages(messages, options);

        var response = await _client.CreateChatCompletionsAsync(
          request,
          cancellationToken);

        // Convert to messages using the Models namespace extension
        return Models.AnthropicExtensions.ToMessages(response, Name);
    }

    /// <inheritdoc/>
    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
      IEnumerable<IMessage> messages,
      GenerateReplyOptions? options = null,
      CancellationToken cancellationToken = default)
    {
        var request = AnthropicRequest.FromMessages(messages, options)
          with
        { Stream = true };

        // Return the streaming response as an IAsyncEnumerable
        return await Task.FromResult(GenerateStreamingMessages(request, cancellationToken));
    }

    private async IAsyncEnumerable<IMessage> GenerateStreamingMessages(
      AnthropicRequest request,
      [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create a parser to track state across events
        var parser = new AnthropicStreamParser();

        await foreach (var streamEvent in _client.StreamingChatCompletionsAsync(request, cancellationToken))
        {
            // Process the event directly without serialization/deserialization
            var messages = parser.ProcessStreamEvent(streamEvent);
            foreach (var message in messages)
            {
                // Set the agent name for all messages
                if (message is TextMessage textMessage)
                {
                    // yield return textMessage with { FromAgent = Name };
                }
                else if (message is TextUpdateMessage textUpdateMessage)
                {
                    yield return textUpdateMessage with { FromAgent = Name };
                }
                else if (message is ToolsCallUpdateMessage toolsCallMessage)
                {
                    yield return toolsCallMessage with { FromAgent = Name };
                }
                else if (message is ToolsCallMessage)
                {
                }
                else
                {
                    yield return message;
                }
            }
        }
    }

    /// <summary>
    /// Disposes the client.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the client.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_client is IDisposable disposableClient)
                {
                    disposableClient.Dispose();
                }
            }

            _disposed = true;
        }
    }
}
