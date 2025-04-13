using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
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
  public async Task<IMessage> GenerateReplyAsync(
    IEnumerable<IMessage> messages,
    GenerateReplyOptions? options = null,
    CancellationToken cancellationToken = default)
  {
    var request = AnthropicRequest.FromMessages(messages, options);

    var response = await _client.CreateChatCompletionsAsync(
      request,
      cancellationToken);

    // Extract text content from the response
    string textContent = string.Empty;
    foreach (var content in response.Content)
    {
      if (content.Type == "text" && content.Text != null)
      {
        textContent += content.Text;
      }
    }

    // Create a text message with the content
    var message = new TextMessage
    {
      Text = textContent,
      Role = Role.Assistant,
      FromAgent = Name
    };
    
    // Note: In a full implementation, we would add usage information
    // to the message, but we're simplifying for now

    return message;
  }

  /// <inheritdoc/>
  public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
    IEnumerable<IMessage> messages,
    GenerateReplyOptions? options = null,
    CancellationToken cancellationToken = default)
  {
    var request = AnthropicRequest.FromMessages(messages, options)
      with { Stream = true };
    
    // Return the streaming response as an IAsyncEnumerable
    return await Task.FromResult(GenerateStreamingMessages(request, cancellationToken));
  }

  private async IAsyncEnumerable<IMessage> GenerateStreamingMessages(
    AnthropicRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    string fullText = string.Empty;
    string messageId = string.Empty;
    string modelId = string.Empty;
    int inputTokens = 0;
    int outputTokens = 0;

    await foreach (var streamEvent in _client.StreamingChatCompletionsAsync(request, cancellationToken))
    {
      // Handle different event types
      switch (streamEvent.Type)
      {
        case "message_start":
          if (streamEvent.Message != null)
          {
            messageId = streamEvent.Message.Id;
            modelId = streamEvent.Message.Model;
          }
          break;

        case "content_block_start":
          // Content block started, nothing to do yet
          break;

        case "content_block_delta":
          if (streamEvent.Delta?.Type == "text_delta" && streamEvent.Delta.Text != null)
          {
            fullText += streamEvent.Delta.Text;
            
            // Create a streaming update
            var message = new TextMessage
            {
              Text = fullText,
              Role = Role.Assistant,
              FromAgent = Name
            };
            
            // Note: In a full implementation, we would add usage information
            // to the message, but we're simplifying for now
            
            yield return message;
          }
          break;

        case "message_stop":
          if (streamEvent.Usage != null)
          {
            inputTokens = streamEvent.Usage.InputTokens;
            outputTokens = streamEvent.Usage.OutputTokens;
            
            // Final message with complete content
            var finalMessage = new TextMessage
            {
              Text = fullText,
              Role = Role.Assistant,
              FromAgent = Name
            };
            
            // Note: In a full implementation, we would add usage information
            // to the message, but we're simplifying for now
            
            yield return finalMessage;
          }
          break;
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
