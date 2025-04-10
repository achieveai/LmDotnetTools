using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
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
          new GenerateReplyOptions());

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
    public async Task InvokeStreamingAsync_JoinTextMessages()
    {
        // Arrange
        string testString = "This is a test";
        // Default behavior is to not preserve update messages
        var middleware = new MessageUpdateJoinerMiddleware();
        var cancellationToken = CancellationToken.None;

        // Create updates from the test string
        var updateMessages = CreateTextUpdateMessages(SplitStringPreservingSpaces(testString));

        // Set up mock streaming agent to return our updates as an async enumerable
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        mockStreamingAgent
          .Setup(a => a.GenerateReplyStreamingAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()))
          .ReturnsAsync(updateMessages.ToAsyncEnumerable());

        // Create context with empty messages
        var context = new MiddlewareContext(
          new List<IMessage>(),
          new GenerateReplyOptions());

        // Act - Get the stream from the middleware
        var resultStream = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object, cancellationToken);

        // Manually collect all messages from the stream
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert - With current implementation of ProcessTextUpdate, no update messages should be emitted
        // since it just returns the original message and preserveUpdateMessages is false
        Assert.Single(results);

        Assert.Equal(testString, ((ICanGetText)results[0]).GetText());

        // Verify the streaming agent was called exactly once
        mockStreamingAgent.Verify(a => a.GenerateReplyStreamingAsync(
          It.IsAny<IEnumerable<IMessage>>(),
          It.IsAny<GenerateReplyOptions>(),
          It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeStreaminAsync_ValidateUsage()
    {
        // Arrange
        string testString = "This is a test";
        // Set preserveUpdateMessages to true to see all messages
        var middleware = new MessageUpdateJoinerMiddleware();
        var cancellationToken = CancellationToken.None;

        // Create updates from the test string
        var updateMessages = CreateTextUpdateMessages(
            new List<string> { "", "", "" }
            .Concat(SplitStringPreservingSpaces(testString))
            .Concat([""]));

        var usage = new Usage
        {
            PromptTokens = 10,
            CompletionTokens = 10,
            TotalTokens = 20,
            CompletionTokenDetails = null,
        };

        updateMessages[updateMessages.Count - 1] = new TextUpdateMessage
        {
            Text = "",
            Metadata = JsonSerializer.SerializeToNode(
                new Dictionary<string, object>
                {
                    ["Usage"] = usage,
                }) as JsonObject
        };

        // Set up mock streaming agent to return our updates as an async enumerable
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        mockStreamingAgent
          .Setup(a => a.GenerateReplyStreamingAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()))
          .ReturnsAsync(updateMessages.ToAsyncEnumerable());

        // Create context with empty messages
        var context = new MiddlewareContext(
          new List<IMessage>(),
          new GenerateReplyOptions());

        // Act - Get the stream from the middleware
        var resultStream = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object, cancellationToken);

        // Manually collect all messages from the stream
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert - With preserveUpdateMessages:true, we should get all the update messages
        Assert.Single(results);

        // Check that the final message contains the complete text
        var finalMessage = results[results.Count - 1];
        Assert.NotNull(finalMessage);
        Assert.Equal(testString, ((LmCore.Messages.ICanGetText)finalMessage).GetText());

        Assert.NotNull(finalMessage.Metadata);
        Assert.Null(finalMessage.Metadata["usage"]);
        Assert.NotNull(finalMessage.Metadata["Usage"]);
        Assert.Equal(usage, JsonSerializer.Deserialize<Usage>(finalMessage.Metadata!["Usage"]!.AsObject()));

        // Verify the streaming agent was called exactly once
        mockStreamingAgent.Verify(a => a.GenerateReplyStreamingAsync(
          It.IsAny<IEnumerable<IMessage>>(),
          It.IsAny<GenerateReplyOptions>(),
          It.IsAny<CancellationToken>()), Times.Once);
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
    private List<IMessage> CreateTextUpdateMessages(IEnumerable<string> parts)
    {
        var messages = new List<IMessage>();

        foreach (var part in parts)
        {
            messages.Add(new TextUpdateMessage
            {
                Text = part,
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
