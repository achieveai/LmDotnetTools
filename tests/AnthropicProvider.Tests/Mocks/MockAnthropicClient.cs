namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Mocks;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Simple mock client that returns predefined responses
/// </summary>
internal class MockAnthropicClient : IAnthropicClient
{
  public MockAnthropicClient()
  {
  }

  public Task<AnthropicResponse> CreateChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    var response = new AnthropicResponse
    {
      Id = "resp_mockdummyid123456789",
      Type = "message",
      Role = "assistant",
      Model = request.Model,
      StopReason = "end_turn",
      Content = new List<AnthropicResponseContent>()
    };
    
    // Add a text response
    response.Content.Add(new AnthropicResponseTextContent
    {
      Type = "text",
      Text = "Hello! I'm Claude, an AI assistant created by Anthropic. How can I help you today?"
    });
    
    return Task.FromResult(response);
  }
  
  public IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    return GetMockStreamEvents(request, cancellationToken);
  }
  
  private async IAsyncEnumerable<AnthropicStreamEvent> GetMockStreamEvents(AnthropicRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    // Message start event
    yield return new AnthropicStreamEvent
    {
      Type = "message_start",
      Message = new AnthropicResponse
      {
        Id = "resp_mockdummyid123456789",
        Type = "message",
        Role = "assistant",
        Model = request.Model ?? "claude-3-7-sonnet-20250219"
      }
    };
    
    // Normal text response events
    yield return new AnthropicStreamEvent
    {
      Type = "content_block_start",
      Index = 0,
      Delta = new AnthropicDelta { Type = "text" }
    };
    
    // Just return a simple message for testing
    yield return new AnthropicStreamEvent
    {
      Type = "content_block_delta",
      Index = 0,
      Delta = new AnthropicDelta
      {
        Type = "text_delta",
        Text = "Hello! I'm Claude, an AI assistant created by Anthropic."
      }
    };
    
    await Task.Delay(10, cancellationToken); // Simulate streaming delay
    
    // Message complete event - only include once
    yield return new AnthropicStreamEvent
    {
      Type = "message_stop",
      Usage = new AnthropicUsage
      {
        InputTokens = 50,
        OutputTokens = 25
      }
    };
  }
  
  public void Dispose()
  {
    // Nothing to dispose
  }
} 