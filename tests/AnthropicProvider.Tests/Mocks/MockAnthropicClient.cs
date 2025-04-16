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
    yield return new AnthropicMessageStartEvent
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
    
    // Content block start event
    yield return new AnthropicContentBlockStartEvent
    {
      Type = "content_block_start",
      Index = 0,
      ContentBlock = new AnthropicResponseTextContent
      {
        Type = "text",
        Text = ""
      }
    };
    
    // Content block delta event
    yield return new AnthropicContentBlockDeltaEvent
    {
      Type = "content_block_delta",
      Index = 0,
      Delta = new AnthropicTextDelta
      {
        Type = "text_delta",
        Text = "Hello! I'm Claude, an AI assistant created by Anthropic."
      }
    };
    
    await Task.Delay(10, cancellationToken); // Simulate streaming delay
    
    // Content block stop event
    yield return new AnthropicContentBlockStopEvent
    {
      Type = "content_block_stop",
      Index = 0
    };
    
    // Message delta event with usage
    yield return new AnthropicMessageDeltaEvent
    {
      Type = "message_delta",
      Delta = new AnthropicMessageDelta
      {
        StopReason = "end_turn"
      },
      Usage = new AnthropicUsage
      {
        InputTokens = 50,
        OutputTokens = 25
      }
    };
    
    // Message stop event
    yield return new AnthropicMessageStopEvent
    {
      Type = "message_stop"
    };
  }
  
  public void Dispose()
  {
    // Nothing to dispose
  }
} 