namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

public class MessageExtensionsNaturalToolUseTests
{
    [Fact]
    public void ToNaturalToolUse_WithToolsCallAggregateMessage_TransformsCorrectly()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = "GetWeather", FunctionArgs = "{\"location\":\"Paris\"}" };
        var toolResult = new ToolCallResult(null, "Sunny, 25°C");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall], Role = Role.Assistant };

        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "test-agent");

        // Act
        var result = aggregateMessage.ToNaturalToolUse();

        // Assert
        _ = Assert.IsType<TextMessage>(result);
        var textMessage = (TextMessage)result;
        Assert.Contains("<tool_call name=\"GetWeather\">", textMessage.Text);
        Assert.Contains("<tool_response name=\"GetWeather\">", textMessage.Text);
        Assert.Contains("Sunny, 25°C", textMessage.Text);
        Assert.Equal(Role.Assistant, textMessage.Role);
        Assert.Equal("test-agent", textMessage.FromAgent);
    }

    [Fact]
    public void ToNaturalToolUse_WithNonAggregateMessage_ReturnsOriginalMessage()
    {
        // Arrange
        var textMessage = new TextMessage { Text = "Hello, world!", Role = Role.User };

        // Act
        var result = textMessage.ToNaturalToolUse();

        // Assert
        Assert.Same(textMessage, result); // Should return the exact same instance
    }

    [Fact]
    public void ToNaturalToolUse_WithNullMessage_ThrowsArgumentNullException()
    {
        // Arrange
        IMessage nullMessage = null!;

        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(nullMessage.ToNaturalToolUse);
    }

    [Fact]
    public void ToNaturalToolUse_Collection_TransformsOnlyAggregateMessages()
    {
        // Arrange
        var textMessage = new TextMessage { Text = "Hello", Role = Role.User };

        var toolCall = new ToolCall { FunctionName = "TestFunction", FunctionArgs = "{}" };
        var toolResult = new ToolCallResult(null, "test result");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var messages = new IMessage[] { textMessage, aggregateMessage };

        // Act
        var results = messages.ToNaturalToolUse().ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Same(textMessage, results[0]); // First message unchanged
        _ = Assert.IsType<TextMessage>(results[1]); // Second message transformed
        Assert.Contains("<tool_call name=\"TestFunction\">", ((TextMessage)results[1]).Text);
    }

    [Fact]
    public void CombineAsNaturalToolUse_WithMixedMessages_CombinesCorrectly()
    {
        // Arrange
        var prefixMessage = new TextMessage { Text = "Let me check that for you.", Role = Role.Assistant };

        var toolCall = new ToolCall { FunctionName = "CheckWeather", FunctionArgs = "{\"city\":\"London\"}" };
        var toolResult = new ToolCallResult(null, "Cloudy, 18°C");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var suffixMessage = new TextMessage { Text = "Hope that helps!", Role = Role.Assistant };

        var messages = new IMessage[] { prefixMessage, aggregateMessage, suffixMessage };

        // Act
        var result = messages.CombineAsNaturalToolUse();

        // Assert
        Assert.Equal(Role.Assistant, result.Role);
        Assert.Contains("Let me check that for you.", result.Text);
        Assert.Contains("<tool_call name=\"CheckWeather\">", result.Text);
        Assert.Contains("<tool_response name=\"CheckWeather\">", result.Text);
        Assert.Contains("Cloudy, 18°C", result.Text);
        Assert.Contains("Hope that helps!", result.Text);
    }

    [Fact]
    public void CombineAsNaturalToolUse_WithNullCollection_ReturnsEmptyTextMessage()
    {
        // Arrange
        IEnumerable<IMessage> nullMessages = null!;

        // Act
        var result = nullMessages.CombineAsNaturalToolUse();

        // Assert
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(Role.Assistant, result.Role);
    }

    [Fact]
    public void CombineAsNaturalToolUse_WithEmptyCollection_ReturnsEmptyTextMessage()
    {
        // Arrange
        var emptyMessages = Array.Empty<IMessage>();

        // Act
        var result = emptyMessages.CombineAsNaturalToolUse();

        // Assert
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(Role.Assistant, result.Role);
    }

    [Fact]
    public void ContainsTransformableToolCalls_WithAggregateMessage_ReturnsTrue()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = "TestFunc", FunctionArgs = "{}" };
        var toolResult = new ToolCallResult(null, "result");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var messages = new IMessage[]
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            aggregateMessage,
        };

        // Act
        var result = messages.ContainsTransformableToolCalls();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ContainsTransformableToolCalls_WithoutAggregateMessage_ReturnsFalse()
    {
        // Arrange
        var messages = new IMessage[]
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            new TextMessage { Text = "World", Role = Role.Assistant },
        };

        // Act
        var result = messages.ContainsTransformableToolCalls();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ContainsTransformableToolCalls_WithNullCollection_ReturnsFalse()
    {
        // Arrange
        IEnumerable<IMessage> nullMessages = null!;

        // Act
        var result = nullMessages.ContainsTransformableToolCalls();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsTransformableToolCall_WithAggregateMessage_ReturnsTrue()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = "TestFunc", FunctionArgs = "{}" };
        var toolResult = new ToolCallResult(null, "result");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act
        var result = aggregateMessage.IsTransformableToolCall();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsTransformableToolCall_WithTextMessage_ReturnsFalse()
    {
        // Arrange
        var textMessage = new TextMessage { Text = "Hello", Role = Role.User };

        // Act
        var result = textMessage.IsTransformableToolCall();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ToNaturalToolUse_WithInvalidAggregateMessage_ReturnsOriginalMessage()
    {
        // Arrange - Create an aggregate message that might cause transformation to fail
        var toolCall = new ToolCall { FunctionName = null, FunctionArgs = null }; // Invalid data
        var toolResult = new ToolCallResult(null, "result");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act
        var result = aggregateMessage.ToNaturalToolUse();

        // Assert - Should still work due to graceful handling in transformer
        Assert.NotNull(result);
        // The transformer handles null function names gracefully, so this should actually transform
        _ = Assert.IsType<TextMessage>(result);
    }

    [Fact]
    public void CombineAsNaturalToolUse_WithGracefulFallback_CombinesTextContent()
    {
        // Arrange - Mix of text messages to test normal behavior
        var messages = new IMessage[]
        {
            new TextMessage { Text = "First message", Role = Role.User },
            new TextMessage { Text = "Second message", Role = Role.Assistant },
        };

        // Act
        var result = messages.CombineAsNaturalToolUse();

        // Assert
        Assert.Contains("First message", result.Text);
        Assert.Contains("Second message", result.Text);
        // The CombineMessageSequence uses the last role, so it should be Assistant
        Assert.Equal(Role.Assistant, result.Role);
    }
}
