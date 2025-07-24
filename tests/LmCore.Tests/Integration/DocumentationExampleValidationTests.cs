using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Integration;

/// <summary>
/// Validation tests that ensure all documentation examples work as specified
/// </summary>
public class DocumentationExampleValidationTests
{
    [Fact]
    public void DocumentationExample1_SingleMessageTransformation_WorksAsSpecified()
    {
        // This test validates the exact example from NaturalToolUse.md Example 1

        // Create a sample aggregate message (from documentation)
        var toolCall = new ToolCall("GetWeather", "{\"location\":\"Paris\",\"unit\":\"celsius\"}");
        var toolResult = new ToolCallResult(null, "Sunny, 25°C with clear skies");

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = ImmutableList.Create(toolCall),
            Role = Role.Assistant,
            GenerationId = "gen-123"
        };

        var toolResultMessage = new ToolsCallResultMessage
        {
            ToolCallResults = ImmutableList.Create(toolResult)
        };

        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "weather-agent");

        // Transform to natural format (from documentation)
        var naturalFormat = aggregateMessage.ToNaturalToolUse();

        // Validate the expected output format from documentation
        Assert.IsType<TextMessage>(naturalFormat);
        var textMessage = (TextMessage)naturalFormat;
        
        // The expected format from documentation:
        /*
        <tool_call name="GetWeather">
        {
          "location": "Paris",
          "unit": "celsius"
        }
        </tool_call>
        <tool_response name="GetWeather">
        Sunny, 25°C with clear skies
        </tool_response>
        */
        
        Assert.Contains("<tool_call name=\"GetWeather\">", textMessage.Text);
        Assert.Contains("\"location\": \"Paris\"", textMessage.Text);
        Assert.Contains("\"unit\": \"celsius\"", textMessage.Text);
        Assert.Contains("</tool_call>", textMessage.Text);
        Assert.Contains("<tool_response name=\"GetWeather\">", textMessage.Text);
        Assert.Contains("Sunny, 25°C with clear skies", textMessage.Text);
        Assert.Contains("</tool_response>", textMessage.Text);
    }

    [Fact]
    public void DocumentationExample2_MessageSequenceCombination_WorksAsSpecified()
    {
        // This test validates Example 2 from NaturalToolUse.md

        // Create the example from documentation
        var toolCall = new ToolCall("GetWeather", "{\"location\":\"Paris\",\"unit\":\"celsius\"}");
        var toolResult = new ToolCallResult(null, "Sunny, 25°C with clear skies");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall) };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = ImmutableList.Create(toolResult) };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        // Create a conversation sequence (from documentation)
        var messages = new IMessage[]
        {
            new TextMessage 
            { 
                Text = "I'll check the weather for you.", 
                Role = Role.Assistant 
            },
            aggregateMessage, // Tool call/response
            new TextMessage 
            { 
                Text = "Based on this forecast, it's a great day for outdoor activities!", 
                Role = Role.Assistant 
            }
        };

        // Combine into single natural format message (from documentation)
        var combined = messages.CombineAsNaturalToolUse();

        // Validate the expected output format from documentation
        Assert.Equal(Role.Assistant, combined.Role);
        
        // Expected output from documentation should contain all these parts:
        Assert.Contains("I'll check the weather for you.", combined.Text);
        Assert.Contains("<tool_call name=\"GetWeather\">", combined.Text);
        Assert.Contains("\"location\": \"Paris\"", combined.Text);
        Assert.Contains("\"unit\": \"celsius\"", combined.Text);
        Assert.Contains("</tool_call>", combined.Text);
        Assert.Contains("<tool_response name=\"GetWeather\">", combined.Text);
        Assert.Contains("Sunny, 25°C with clear skies", combined.Text);
        Assert.Contains("</tool_response>", combined.Text);
        Assert.Contains("Based on this forecast, it's a great day for outdoor activities!", combined.Text);
        
        // Verify proper ordering
        var firstTextIndex = combined.Text.IndexOf("I'll check the weather");
        var toolCallIndex = combined.Text.IndexOf("<tool_call");
        var toolResponseIndex = combined.Text.IndexOf("<tool_response");
        var lastTextIndex = combined.Text.IndexOf("Based on this forecast");
        
        Assert.True(firstTextIndex < toolCallIndex);
        Assert.True(toolCallIndex < toolResponseIndex);
        Assert.True(toolResponseIndex < lastTextIndex);
    }

    [Fact]
    public void DocumentationExample3_CollectionTransformation_WorksAsSpecified()
    {
        // This test validates Example 3 from NaturalToolUse.md

        var toolCall = new ToolCall("GetWeather", "{\"location\":\"Paris\"}");
        var toolResult = new ToolCallResult(null, "Sunny, 25°C");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall) };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = ImmutableList.Create(toolResult) };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var messageCollection = new IMessage[]
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            aggregateMessage,
            new TextMessage { Text = "Goodbye", Role = Role.Assistant }
        };

        // Transform only the aggregate messages, leave others unchanged (from documentation)
        var transformed = messageCollection.ToNaturalToolUse().ToList();

        // Result: 3 messages where only the middle one is transformed (from documentation)
        Assert.Equal(3, transformed.Count);
        
        // First message should be unchanged (same instance)
        Assert.Same(messageCollection[0], transformed[0]);
        
        // Second message should be transformed
        Assert.IsType<TextMessage>(transformed[1]);
        Assert.NotSame(messageCollection[1], transformed[1]); // Different instance
        Assert.Contains("<tool_call name=\"GetWeather\">", ((TextMessage)transformed[1]).Text);
        
        // Third message should be unchanged (same instance)
        Assert.Same(messageCollection[2], transformed[2]);
    }

    [Fact]
    public void DocumentationExample4_ConditionalTransformation_WorksAsSpecified()
    {
        // This test validates Example 4 from NaturalToolUse.md

        var toolCall = new ToolCall("TestFunction", "{}");
        var toolResult = new ToolCallResult(null, "result");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall) };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = ImmutableList.Create(toolResult) };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var regularMessage = new TextMessage { Text = "Hello", Role = Role.User };

        var messageCollection = new IMessage[] { regularMessage, aggregateMessage };

        // Check if transformation is applicable (from documentation)
        Assert.True(aggregateMessage.IsTransformableToolCall());
        Assert.False(regularMessage.IsTransformableToolCall());

        Assert.True(messageCollection.ContainsTransformableToolCalls());
        
        var textOnlyMessages = new IMessage[] 
        { 
            new TextMessage { Text = "Hello", Role = Role.User },
            new TextMessage { Text = "Hi there", Role = Role.Assistant }
        };
        Assert.False(textOnlyMessages.ContainsTransformableToolCalls());

        // Conditional processing based on transformability
        if (aggregateMessage.IsTransformableToolCall())
        {
            var natural = aggregateMessage.ToNaturalToolUse();
            Assert.IsType<TextMessage>(natural);
            Assert.Contains("<tool_call", ((TextMessage)natural).Text);
        }

        if (messageCollection.ContainsTransformableToolCalls())
        {
            var transformed = messageCollection.ToNaturalToolUse();
            Assert.NotNull(transformed);
        }
    }

    [Fact]
    public void DocumentationXmlFormatSpecification_SingleToolCall_MatchesExactly()
    {
        // This test validates the exact XML format from the documentation

        var toolCall = new ToolCall("GetWeather", "{\"location\":\"San Francisco, CA\",\"unit\":\"celsius\"}");
        var toolResult = new ToolCallResult(null, "Temperature is 22°C with partly cloudy skies and light winds from the west.");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall) };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = ImmutableList.Create(toolResult) };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

        // Expected format from documentation:
        /*
        <tool_call name="GetWeather">
        {
          "location": "San Francisco, CA",
          "unit": "celsius"
        }
        </tool_call>
        <tool_response name="GetWeather">
        Temperature is 22°C with partly cloudy skies and light winds from the west.
        </tool_response>
        */

        var content = result.Text;

        // Validate exact format elements
        Assert.Contains("<tool_call name=\"GetWeather\">", content);
        Assert.Contains("\"location\": \"San Francisco, CA\"", content); // Pretty-printed JSON
        Assert.Contains("\"unit\": \"celsius\"", content);
        Assert.Contains("</tool_call>", content);
        Assert.Contains("<tool_response name=\"GetWeather\">", content);
        Assert.Contains("Temperature is 22°C with partly cloudy skies and light winds from the west.", content);
        Assert.Contains("</tool_response>", content);

        // Verify structure
        var toolCallStart = content.IndexOf("<tool_call");
        var toolCallEnd = content.IndexOf("</tool_call>");
        var toolResponseStart = content.IndexOf("<tool_response");
        var toolResponseEnd = content.IndexOf("</tool_response>");

        Assert.True(toolCallStart < toolCallEnd);
        Assert.True(toolCallEnd < toolResponseStart);
        Assert.True(toolResponseStart < toolResponseEnd);
    }

    [Fact]
    public void DocumentationXmlFormatSpecification_MultipleToolCalls_WithSeparator()
    {
        // This test validates the multiple tool calls format with separator from documentation

        var toolCall1 = new ToolCall("GetWeather", "{\"location\":\"San Francisco, CA\",\"unit\":\"celsius\"}");
        var toolCall2 = new ToolCall("GetTime", "{\"timezone\":\"America/Los_Angeles\"}");
        
        var toolResult1 = new ToolCallResult(null, "Temperature is 22°C with partly cloudy skies.");
        var toolResult2 = new ToolCallResult(null, "{\"current_time\":\"2024-07-24T15:30:00-07:00\",\"timezone\":\"PDT\",\"formatted\":\"3:30 PM PDT\"}");

        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall1, toolCall2) };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = ImmutableList.Create(toolResult1, toolResult2) };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var result = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);
        var content = result.Text;

        // Multiple tool call/response pairs are separated by --- (from documentation)
        Assert.Contains("---", content);

        // Should contain both tool calls
        Assert.Contains("<tool_call name=\"GetWeather\">", content);
        Assert.Contains("<tool_call name=\"GetTime\">", content);
        Assert.Contains("<tool_response name=\"GetWeather\">", content);
        Assert.Contains("<tool_response name=\"GetTime\">", content);

        // JSON should be pretty-printed in tool calls
        Assert.Contains("\"location\": \"San Francisco, CA\"", content);
        Assert.Contains("\"timezone\": \"America/Los_Angeles\"", content);

        // Verify separator placement
        var firstToolResponseEnd = content.IndexOf("</tool_response>");
        var separatorIndex = content.IndexOf("---");
        var secondToolCallStart = content.IndexOf("<tool_call name=\"GetTime\">");

        Assert.True(firstToolResponseEnd < separatorIndex);
        Assert.True(separatorIndex < secondToolCallStart);
    }

    [Fact]
    public void DocumentationQuickStartExample_BasicUsage_WorksAsSpecified()
    {
        // This test validates the Quick Start basic usage examples from documentation

        var toolCall = new ToolCall("TestFunction", "{\"param\":\"value\"}");
        var toolResult = new ToolCallResult(null, "test result");
        var toolCallMessage = new ToolsCallMessage { ToolCalls = ImmutableList.Create(toolCall) };
        var toolResultMessage = new ToolsCallResultMessage { ToolCallResults = ImmutableList.Create(toolResult) };
        var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage);

        var messageCollection = new IMessage[] { aggregateMessage };
        var messageSequence = new IMessage[] 
        { 
            new TextMessage { Text = "Hello", Role = Role.Assistant },
            aggregateMessage 
        };

        // Transform a single aggregate message (from documentation Quick Start)
        var naturalFormat = aggregateMessage.ToNaturalToolUse();
        Assert.IsType<TextMessage>(naturalFormat);

        // Transform a collection of messages (from documentation Quick Start) 
        var transformedMessages = messageCollection.ToNaturalToolUse();
        Assert.NotNull(transformedMessages);

        // Combine a message sequence with natural formatting (from documentation Quick Start)
        var combined = messageSequence.CombineAsNaturalToolUse();
        Assert.IsType<TextMessage>(combined);
        Assert.Contains("Hello", combined.Text);
        Assert.Contains("<tool_call name=\"TestFunction\">", combined.Text);

        // Using the Core Transformer Directly (from documentation Quick Start)
        var naturalMessage = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);
        Assert.IsType<TextMessage>(naturalMessage);

        var combinedMessage = ToolsCallAggregateTransformer.CombineMessageSequence(messageSequence);
        Assert.IsType<TextMessage>(combinedMessage);
    }
}
