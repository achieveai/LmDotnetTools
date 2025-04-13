namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Mocks;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Mock client that returns tool use responses
/// </summary>
internal class ToolResponseMockClient : IAnthropicClient
{
  public AnthropicRequest? LastRequest { get; private set; }
  
  public Task<AnthropicResponse> CreateChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    LastRequest = request;
    
    // Create a response with text content only, but include "tool_use" in the text
    // to simulate tool usage without requiring changes to AnthropicContent
    var response = new AnthropicResponse
    {
      Id = "msg_01EzEovKotLrrvB3JQN7voWh",
      Type = "message",
      Role = "assistant",
      Model = "claude-3-7-sonnet-20250219",
      StopReason = "tool_use",
      Content = new List<AnthropicContent>
      {
        new AnthropicContent
        {
          Type = "text",
          Text = "I'll help you list the files in the root directory. Let me do this for you by using the list_directory function. (tool_use: python_mcp-list_directory)"
        }
      },
      Usage = new AnthropicUsage
      {
        InputTokens = 1503,
        OutputTokens = 82
      }
    };
    
    return Task.FromResult(response);
  }
  
  public IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    throw new NotImplementedException("Streaming not implemented for this mock client");
  }
  
  public void Dispose()
  {
    // Nothing to dispose
  }
} 