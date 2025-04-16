namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Mocks;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Client that captures the request for testing assertions
/// </summary>
internal class CaptureAnthropicClient : IAnthropicClient
{
  // Store request properties explicitly to avoid any mutation issues
  private AnthropicThinking? _thinking;
  private string? _model;
  private List<AnthropicMessage>? _messages;
  
  public AnthropicRequest? CapturedRequest { get; private set; }
  
  // Expose thinking directly for testing
  public AnthropicThinking? CapturedThinking => _thinking;
  
  public CaptureAnthropicClient()
  {
    Console.WriteLine("Initializing CaptureAnthropicClient");
  }
  
  public Task<AnthropicResponse> CreateChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    try
    {
      Console.WriteLine("CaptureAnthropicClient.CreateChatCompletionsAsync called");
      Console.WriteLine($"Request details: Model={request.Model}, Stream={request.Stream}, MaxTokens={request.MaxTokens}");
      Console.WriteLine($"Messages count: {request.Messages?.Count ?? 0}");
      Console.WriteLine($"Thinking: {(request.Thinking != null ? $"BudgetTokens={request.Thinking.BudgetTokens}" : "null")}");
      
      if (request.Messages != null && request.Messages.Count > 0)
      {
        for (int i = 0; i < request.Messages.Count; i++)
        {
          var msg = request.Messages[i];
          Console.WriteLine($"Message[{i}]: Role={msg.Role}, Content items={msg.Content?.Count ?? 0}");
        }
      }
      
      // Store the complete request - create a deep copy to avoid issues with reference types
      CapturedRequest = request;
      
      // Also store key properties individually for testing
      _thinking = request.Thinking;
      _model = request.Model;
      _messages = request.Messages != null ? new List<AnthropicMessage>(request.Messages) : null;
      
      Console.WriteLine($"After storing properties:");
      Console.WriteLine($" - CapturedRequest: {(CapturedRequest != null ? "not null" : "null")}");
      Console.WriteLine($" - _model: {_model ?? "null"}");
      Console.WriteLine($" - _thinking: {(_thinking != null ? $"BudgetTokens={_thinking.BudgetTokens}" : "null")}");
      Console.WriteLine($" - _messages: {(_messages != null ? $"{_messages.Count} items" : "null")}");
      
      // Create a simple mock response
      var response = new AnthropicResponse
      {
        Id = "resp_test123",
        Type = "message",
        Role = "assistant",
        Model = request.Model ?? "claude-3-7-sonnet-20250219",
        Content = new List<AnthropicResponseContent>
        {
          new AnthropicResponseTextContent
          {
            Type = "text",
            Text = "This is a mock response for testing."
          }
        }
      };
      
      Console.WriteLine($"Created response with Id={response.Id}, Role={response.Role}");
      return Task.FromResult(response);
    }
    catch (Exception ex)
    {
      Console.WriteLine($"ERROR in CreateChatCompletionsAsync: {ex.Message}");
      Console.WriteLine($"Stack trace: {ex.StackTrace}");
      throw;
    }
  }
  
  public IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    try
    {
      Console.WriteLine("CaptureAnthropicClient.StreamingChatCompletionsAsync called");
      Console.WriteLine($"Request details: Model={request.Model}, Stream={request.Stream}, MaxTokens={request.MaxTokens}");
      Console.WriteLine($"Messages count: {request.Messages?.Count ?? 0}");
      
      // Store the complete request
      CapturedRequest = request;
      
      // Also store key properties individually for testing
      _thinking = request.Thinking;
      _model = request.Model;
      _messages = request.Messages != null ? new List<AnthropicMessage>(request.Messages) : null;
      
      Console.WriteLine($"After storing streaming request properties:");
      Console.WriteLine($" - CapturedRequest: {(CapturedRequest != null ? "not null" : "null")}");
      Console.WriteLine($" - _model: {_model ?? "null"}");
      Console.WriteLine($" - _messages: {(_messages != null ? $"{_messages.Count} items" : "null")}");
      
      return EmptyStreamAsync(cancellationToken);
    }
    catch (Exception ex)
    {
      Console.WriteLine($"ERROR in StreamingChatCompletionsAsync: {ex.Message}");
      Console.WriteLine($"Stack trace: {ex.StackTrace}");
      throw;
    }
  }
  
  private async IAsyncEnumerable<AnthropicStreamEvent> EmptyStreamAsync(
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    // Simple stream with just start/stop events
    yield return new AnthropicMessageStartEvent
    {
      Type = "message_start",
      Message = new AnthropicResponse
      {
        Id = "resp_test123",
        Type = "message",
        Role = "assistant"
      }
    };
    
    await Task.Delay(1, cancellationToken);
    
    yield return new AnthropicMessageDeltaEvent
    {
      Type = "message_delta",
      Delta = new AnthropicMessageDelta
      {
        StopReason = "end_turn"
      },
      Usage = new AnthropicUsage
      {
        InputTokens = 10,
        OutputTokens = 5
      }
    };
    
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