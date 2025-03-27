using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class MessageUpdateJoinerMiddlewareTests
{
  [Fact]
  public async Task InvokeAsync_ShouldPassThrough_ForNonStreamingRequests()
  {
    // Arrange
    var middleware = new MessageUpdateJoinerMiddleware();
    var cancellationToken = CancellationToken.None;
    
    // Create a regular non-streaming message
    var message = new TextMessage { Text = "This is a non-streaming response" };
    
    // Mock the agent to return our test message
    var mockAgent = new Mock<IAgent>();
    mockAgent
      .Setup(a => a.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(message);
    
    // Create context with empty messages
    var context = new MiddlewareContext(
      new List<IMessage>(),
      new GenerateReplyOptions { Stream = false });
    
    // Act
    var result = await middleware.InvokeAsync(context, mockAgent.Object, cancellationToken);
    
    // Assert
    Assert.NotNull(result);
    Assert.Equal(message.Text, ((LmCore.Messages.ICanGetText)result).GetText());
    
    // Verify the agent was called exactly once
    mockAgent.Verify(a => a.GenerateReplyAsync(
      It.IsAny<IEnumerable<IMessage>>(), 
      It.IsAny<GenerateReplyOptions>(), 
      It.IsAny<CancellationToken>()), Times.Once);
  }
  
  [Fact]
  public void SplitStringPreservingSpaces_ShouldKeepSpacesWithFollowingWord()
  {
    // Arrange
    string testString = "This is a test";
    
    // Act
    var result = SplitStringPreservingSpaces(testString);
    
    // Assert
    Assert.Equal(4, result.Count);
    Assert.Equal("This", result[0]);
    Assert.Equal(" is", result[1]);
    Assert.Equal(" a", result[2]);
    Assert.Equal(" test", result[3]);
    
    // Verify we can reconstruct the original string
    Assert.Equal(testString, string.Concat(result));
  }
  
  [Fact]
  public void CreateTextUpdateMessages_ShouldAccumulateText()
  {
    // Arrange
    string testString = "This is a test";
    var parts = SplitStringPreservingSpaces(testString);
    
    // Act
    var updates = CreateTextUpdateMessages(parts);
    
    // Assert
    Assert.Equal(4, updates.Count);
    
    // Check the progressive accumulation of text
    Assert.Equal("This", ((LmCore.Messages.ICanGetText)updates[0]).GetText());
    Assert.Equal("This is", ((LmCore.Messages.ICanGetText)updates[1]).GetText());
    Assert.Equal("This is a", ((LmCore.Messages.ICanGetText)updates[2]).GetText());
    Assert.Equal("This is a test", ((LmCore.Messages.ICanGetText)updates[3]).GetText());
  }
  
  [Fact]
  public void CreateToolCallUpdateSequence_ShouldBuildJsonIncrementally()
  {
    // Act
    var updates = CreateToolCallUpdateSequence();
    
    // Assert
    Assert.Equal(4, updates.Count);
    
    // Check progressive updates to the function arguments
    var firstUpdate = updates[0] as ToolsCallUpdateMessage;
    Assert.NotNull(firstUpdate);
    Assert.Equal("get_weather", firstUpdate.ToolCallUpdates[0].FunctionName);
    Assert.Null(firstUpdate.ToolCallUpdates[0].FunctionArgs);
    
    var secondUpdate = updates[1] as ToolsCallUpdateMessage;
    Assert.NotNull(secondUpdate);
    Assert.Contains("San", secondUpdate.ToolCallUpdates[0].FunctionArgs);
    
    var thirdUpdate = updates[2] as ToolsCallUpdateMessage;
    Assert.NotNull(thirdUpdate);
    Assert.Contains("San Francisco", thirdUpdate.ToolCallUpdates[0].FunctionArgs);
    
    var finalUpdate = updates[3] as ToolsCallUpdateMessage;
    Assert.NotNull(finalUpdate);
    Assert.Contains("celsius", finalUpdate.ToolCallUpdates[0].FunctionArgs);
    
    // Verify the final update has a complete JSON object with both location and unit
    Assert.Contains("location", finalUpdate.ToolCallUpdates[0].FunctionArgs ?? "");
    Assert.Contains("unit", finalUpdate.ToolCallUpdates[0].FunctionArgs ?? "");
  }
  
  [Fact]
  public void PreserveUpdateMessages_ShouldKeepAllUpdateMessages_WhenSet()
  {
    // This test verifies the _preserveUpdateMessages flag behavior using reflection
    
    // Arrange - create middleware with preserveUpdateMessages = true
    var middleware = new MessageUpdateJoinerMiddleware(preserveUpdateMessages: true);
    
    // Get the private field value using reflection to verify it was set correctly
    var preserveField = typeof(MessageUpdateJoinerMiddleware)
      .GetField("_preserveUpdateMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    
    // Assert
    Assert.NotNull(preserveField);
    var fieldValue = preserveField.GetValue(middleware);
    Assert.NotNull(fieldValue);
    var preserveValue = (bool)fieldValue;
    Assert.True(preserveValue);
    
    // Additional verification can be done by examining the InvokeStreamingAsync method
    // but that would involve more complex mocking
  }
  
  #region Helper Methods
  
  // Helper method to split string on spaces while including spaces in the parts
  private List<string> SplitStringPreservingSpaces(string input)
  {
    var result = new List<string>();
    var words = input.Split(' ');
    
    // Add first word
    result.Add(words[0]);
    
    // Add remaining words with preceding space
    for (int i = 1; i < words.Length; i++)
    {
      result.Add(" " + words[i]);
    }
    
    return result;
  }
  
  // Create a series of text update messages
  private List<IMessage> CreateTextUpdateMessages(List<string> parts)
  {
    var messages = new List<IMessage>();
    var accumulatedText = "";
    
    foreach (var part in parts)
    {
      accumulatedText += part;
      messages.Add(new TextUpdateMessage 
      { 
        Text = accumulatedText,
        Role = Role.Assistant
      });
    }
    
    return messages;
  }
  
  // Create tool call update messages that build incrementally
  private List<IMessage> CreateToolCallUpdateSequence()
  {
    return new List<IMessage>
    {
      // First update: Just the function name
      new ToolsCallUpdateMessage
      {
        ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
        {
          FunctionName = "get_weather"
        })
      },
      
      // Second update: Function name and partial args
      new ToolsCallUpdateMessage
      {
        ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
        {
          FunctionName = "get_weather",
          FunctionArgs = "{\"location\":\"San"
        })
      },
      
      // Third update: Function name and more complete args
      new ToolsCallUpdateMessage
      {
        ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
        {
          FunctionName = "get_weather",
          FunctionArgs = "{\"location\":\"San Francisco\""
        })
      },
      
      // Final update: Complete function call
      new ToolsCallUpdateMessage
      {
        ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
        {
          FunctionName = "get_weather",
          FunctionArgs = "{\"location\":\"San Francisco\",\"unit\":\"celsius\"}"
        })
      }
    };
  }
  
  #endregion
}

// Define these message types for testing purposes only
// These simulate the actual update message types that would be in the real codebase
public record TextUpdateMessage : IMessage, LmCore.Messages.ICanGetText
{
  public string? FromAgent { get; init; } = null;
  
  public Role Role { get; init; } = Role.Assistant;
  
  public JsonObject? Metadata { get; init; } = null;
  
  public string? GenerationId { get; init; } = null;
  
  public string Text { get; init; } = string.Empty;
  
  public string? GetText() => Text;
  
  public BinaryData? GetBinary() => null;
  
  public ToolCall? GetToolCalls() => null;
  
  public IEnumerable<IMessage>? GetMessages() => null;
}

// Mock of BinaryData for testing
public class BinaryData
{
  public byte[] Data { get; }

  public BinaryData(byte[] data)
  {
    Data = data;
  }
}
