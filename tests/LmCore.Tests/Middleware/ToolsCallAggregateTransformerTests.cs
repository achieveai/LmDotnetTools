using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class ToolsCallAggregateTransformerTests
{
    [Fact]
    public void TransformToNaturalFormat_SingleToolCall_ProducesCorrectXmlFormat()
    {
        // Arrange
        var toolCall = new ToolCall
        {
            FunctionName = "GetWeather",
            FunctionArgs = "{\"location\":\"San Francisco\",\"unit\":\"celsius\"}",
        };
        var toolResult = new ToolCallResult(null, "Temperature is 22°C with partly cloudy skies");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "test-gen-123" };

        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "test-agent");

        // Act
        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(Role.Assistant, result.Role);
        Assert.Equal("test-agent", result.FromAgent);
        Assert.Equal("test-gen-123", result.GenerationId);

        var expectedContent = """
            <tool_call name="GetWeather">
            {
              "location": "San Francisco",
              "unit": "celsius"
            }
            </tool_call>
            <tool_response name="GetWeather">
            Temperature is 22°C with partly cloudy skies
            </tool_response>
            """;

        Assert.Equal(expectedContent, result.Text);
    }

    [Fact]
    public void TransformToNaturalFormat_MultipleToolCalls_UsesCorrectSeparator()
    {
        // Arrange
        var toolCall1 = new ToolCall { FunctionName = "GetWeather", FunctionArgs = "{\"location\":\"San Francisco\"}" };
        var toolCall2 = new ToolCall { FunctionName = "GetTime", FunctionArgs = "{\"timezone\":\"PST\"}" };
        var toolResult1 = new ToolCallResult(null, "Sunny, 25°C");
        var toolResult2 = new ToolCallResult(null, "3:45 PM");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall1, toolCall2) };

        var toolResultMessage = new ToolsCallResultMessage
        {
            ToolCallResults = ImmutableList.Create(toolResult1, toolResult2),
        };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act
        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert
        Assert.Contains("---", result.Text); // Should have separator between tool calls
        Assert.Contains("<tool_call name=\"GetWeather\">", result.Text);
        Assert.Contains("<tool_call name=\"GetTime\">", result.Text);
        Assert.Contains("<tool_response name=\"GetWeather\">", result.Text);
        Assert.Contains("<tool_response name=\"GetTime\">", result.Text);
    }

    [Fact]
    public void TransformToNaturalFormat_WithMetadata_PreservesMetadata()
    {
        // Arrange
        var metadata = ImmutableDictionary
            .Create<string, object>()
            .Add("test_key", "test_value")
            .Add("another_key", 42);

        var toolCall = new ToolCall { FunctionName = "TestFunction", FunctionArgs = "{}" };
        var toolResult = new ToolCallResult(null, "test result");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall], Metadata = metadata };

        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act
        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert
        Assert.NotNull(result.Metadata);
        Assert.Equal("test_value", result.Metadata["test_key"]);
        Assert.Equal(42, result.Metadata["another_key"]);
    }

    [Fact]
    public void TransformToNaturalFormat_InvalidJsonArgs_UsesOriginalText()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = "TestFunction", FunctionArgs = "invalid json {" };
        var toolResult = new ToolCallResult(null, "test result");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };

        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act
        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert
        Assert.Contains("invalid json {", result.Text); // Should use original text when JSON parsing fails
    }

    [Fact]
    public void CombineMessageSequence_TextAndAggregateAndText_CombinesCorrectly()
    {
        // Arrange
        var textMessage1 = new TextMessage
        {
            Text = "I'll help you with that. Let me check the weather.",
            Role = Role.Assistant,
        };

        var toolCall = new ToolCall { FunctionName = "GetWeather", FunctionArgs = "{\"location\":\"Boston\"}" };
        var toolResult = new ToolCallResult(null, "Rainy, 18°C");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };

        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var textMessage2 = new TextMessage
        {
            Text = "Based on the weather data, you should bring an umbrella!",
            Role = Role.Assistant,
        };

        var messageSequence = new IMessage[] { textMessage1, aggregateMessage, textMessage2 };

        // Act
        var result = ToolsCallAggregateTransformer.CombineMessageSequence(messageSequence);

        // Assert
        Assert.Equal(Role.Assistant, result.Role);
        Assert.Contains("I'll help you with that. Let me check the weather.", result.Text);
        Assert.Contains("<tool_call name=\"GetWeather\">", result.Text);
        Assert.Contains("<tool_response name=\"GetWeather\">", result.Text);
        Assert.Contains("Based on the weather data, you should bring an umbrella!", result.Text);
    }

    [Fact]
    public void CombineMessageSequence_EmptySequence_ReturnsEmptyTextMessage()
    {
        // Arrange
        var emptySequence = Array.Empty<IMessage>();

        // Act
        var result = ToolsCallAggregateTransformer.CombineMessageSequence(emptySequence);

        // Assert
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(Role.Assistant, result.Role);
    }

    [Fact]
    public void FormatToolCallAndResponse_SimpleCall_FormatsCorrectly()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = "CalculateSum", FunctionArgs = "{\"a\":5,\"b\":3}" };
        var toolResult = new ToolCallResult(null, "8");

        // Act
        var result = ToolsCallAggregateTransformer.FormatToolCallAndResponse(toolCall, toolResult);

        // Assert
        var expected = """
            <tool_call name="CalculateSum">
            {
              "a": 5,
              "b": 3
            }
            </tool_call>
            <tool_response name="CalculateSum">
            8
            </tool_response>
            """;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatToolCallAndResponse_JsonResponse_FormatsJsonCorrectly()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = "GetData", FunctionArgs = "{}" };
        var toolResult = new ToolCallResult(null, "{\"status\":\"success\",\"data\":[1,2,3]}");

        // Act
        var result = ToolsCallAggregateTransformer.FormatToolCallAndResponse(toolCall, toolResult);

        // Assert
        Assert.Contains("\"status\": \"success\"", result); // Should be pretty-printed JSON
        Assert.Contains("\"data\": [", result);
    }

    [Fact]
    public void TransformToNaturalFormat_NullArgument_ThrowsArgumentNullException()
    {
        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() => ToolsCallAggregateTransformer.TransformToNaturalFormat(null!));
    }

    [Fact]
    public void CombineMessageSequence_NullArgument_ThrowsArgumentNullException()
    {
        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() => ToolsCallAggregateTransformer.CombineMessageSequence(null!));
    }

    [Fact]
    public void TransformToNaturalFormat_EmptyFunctionName_UsesUnknownFunction()
    {
        // Arrange
        var toolCall = new ToolCall { FunctionName = null, FunctionArgs = "{}" };
        var toolResult = new ToolCallResult(null, "result");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = [toolCall] };

        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = [toolResult] };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Act
        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Assert
        Assert.Contains("UnknownFunction", result.Text);
    }
}
