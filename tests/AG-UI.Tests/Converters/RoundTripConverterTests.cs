using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LmCoreToolCall = AchieveAi.LmDotnetTools.LmCore.Messages.ToolCall;

namespace AchieveAi.LmDotnetTools.AgUi.Tests.Converters;

/// <summary>
///     Tests to verify that converting LmCore -> AG-UI -> LmCore preserves data integrity
/// </summary>
public class RoundTripConverterTests
{
    private readonly AgUiToLmCoreConverter _agUiToLm;
    private readonly LmCoreToAgUiConverter _lmToAgUi;

    public RoundTripConverterTests()
    {
        _lmToAgUi = new LmCoreToAgUiConverter(NullLogger<LmCoreToAgUiConverter>.Instance);
        _agUiToLm = new AgUiToLmCoreConverter(NullLogger<AgUiToLmCoreConverter>.Instance);
    }

    [Fact]
    public void RoundTrip_TextMessage_PreservesData()
    {
        // Arrange
        var original = new TextMessage
        {
            Text = "Hello, this is a test message",
            Role = Role.User,
            GenerationId = "gen-round-1",
            FromAgent = "TestAgent",
        };

        // Act - LmCore -> AG-UI
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        _ = agUiMessages.Should().HaveCount(1);

        // Act - AG-UI -> LmCore
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        _ = converted.Should().BeOfType<TextMessage>();
        var roundTrip = (TextMessage)converted;

        _ = roundTrip.Text.Should().Be(original.Text);
        _ = roundTrip.Role.Should().Be(original.Role);
        _ = roundTrip.GenerationId.Should().Be(original.GenerationId);
        _ = roundTrip.FromAgent.Should().Be(original.FromAgent);
    }

    [Fact]
    public void RoundTrip_ToolsCallMessage_PreservesStructure()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "get_weather",
            FunctionArgs = """{"location": "Paris", "units": "celsius"}""",
            ToolCallId = "call-round-1",
        };

        var original = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-tools-round",
            FromAgent = "WeatherAgent",
            ToolCalls = [toolCall],
        };

        // Act - LmCore -> AG-UI
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        _ = agUiMessages.Should().HaveCount(1);

        // Act - AG-UI -> LmCore
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        _ = converted.Should().BeOfType<ToolsCallMessage>();
        var roundTrip = (ToolsCallMessage)converted;

        _ = roundTrip.Role.Should().Be(original.Role);
        _ = roundTrip.GenerationId.Should().Be(original.GenerationId);
        _ = roundTrip.FromAgent.Should().Be(original.FromAgent);
        _ = roundTrip.ToolCalls.Should().HaveCount(1);

        var roundTripCall = roundTrip.ToolCalls[0];
        _ = roundTripCall.ToolCallId.Should().Be(toolCall.ToolCallId);
        _ = roundTripCall.FunctionName.Should().Be(toolCall.FunctionName);

        // Verify JSON arguments are semantically equivalent
        var originalArgs = JsonDocument.Parse(toolCall.FunctionArgs!);
        var roundTripArgs = JsonDocument.Parse(roundTripCall.FunctionArgs!);

        var originalLocation = originalArgs.RootElement.GetProperty("location").GetString();
        var roundTripLocation = roundTripArgs.RootElement.GetProperty("location").GetString();
        _ = roundTripLocation.Should().Be(originalLocation);

        var originalUnits = originalArgs.RootElement.GetProperty("units").GetString();
        var roundTripUnits = roundTripArgs.RootElement.GetProperty("units").GetString();
        _ = roundTripUnits.Should().Be(originalUnits);
    }

    [Fact]
    public void RoundTrip_ToolsCallResultMessage_PreservesData()
    {
        // Arrange - Note: AG-UI tool result messages go back to LmCore as single-result messages
        var result = new ToolCallResult("call-result-1", "Temperature in Paris: 18°C");

        var original = new ToolsCallResultMessage
        {
            Role = Role.Tool,
            GenerationId = "gen-result-round",
            FromAgent = "ToolExecutor",
            ToolCallResults = [result],
        };

        // Act - LmCore -> AG-UI (creates one message per result)
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        _ = agUiMessages.Should().HaveCount(1);

        // Act - AG-UI -> LmCore
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        _ = converted.Should().BeOfType<ToolsCallResultMessage>();
        var roundTrip = (ToolsCallResultMessage)converted;

        _ = roundTrip.Role.Should().Be(original.Role);
        _ = roundTrip.FromAgent.Should().Be(original.FromAgent);
        _ = roundTrip.ToolCallResults.Should().HaveCount(1);

        var roundTripResult = roundTrip.ToolCallResults[0];
        _ = roundTripResult.ToolCallId.Should().Be(result.ToolCallId);
        _ = roundTripResult.Result.Should().Be(result.Result);
    }

    [Fact]
    public void RoundTrip_MessageHistory_PreservesOrder()
    {
        // Arrange
        var originalMessages = new List<IMessage>
        {
            new TextMessage
            {
                Text = "Message 1",
                Role = Role.User,
                GenerationId = "m1",
            },
            new TextMessage
            {
                Text = "Message 2",
                Role = Role.Assistant,
                GenerationId = "m2",
            },
            new TextMessage
            {
                Text = "Message 3",
                Role = Role.User,
                GenerationId = "m3",
            },
        };

        // Act - LmCore -> AG-UI
        var agUiMessages = _lmToAgUi.ConvertMessageHistory(originalMessages);
        _ = agUiMessages.Should().HaveCount(3);

        // Act - AG-UI -> LmCore
        var converted = _agUiToLm.ConvertMessageHistory(agUiMessages);

        // Assert
        _ = converted.Should().HaveCount(3);

        var msg1 = (TextMessage)converted[0];
        _ = msg1.Text.Should().Be("Message 1");
        _ = msg1.Role.Should().Be(Role.User);

        var msg2 = (TextMessage)converted[1];
        _ = msg2.Text.Should().Be("Message 2");
        _ = msg2.Role.Should().Be(Role.Assistant);

        var msg3 = (TextMessage)converted[2];
        _ = msg3.Text.Should().Be("Message 3");
        _ = msg3.Role.Should().Be(Role.User);
    }

    [Fact]
    public void RoundTrip_TextMessage_WithSpecialCharacters_PreservesContent()
    {
        // Arrange
        var original = new TextMessage
        {
            Text = "Special: <>&\"'éñ中文🎉 Test",
            Role = Role.User,
            GenerationId = "gen-special",
        };

        // Act
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        var roundTrip = (TextMessage)converted;
        _ = roundTrip.Text.Should().Be(original.Text);
    }

    [Fact]
    public void RoundTrip_ToolCall_WithComplexArguments_PreservesStructure()
    {
        // Arrange
        var complexArgs = """
            {
                "nested": {
                    "value": 42,
                    "array": [1, 2, 3],
                    "flag": true
                },
                "string": "test value"
            }
            """;

        var toolCall = new LmCoreToolCall
        {
            FunctionName = "complex_function",
            FunctionArgs = complexArgs,
            ToolCallId = "call-complex",
        };

        var original = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-complex",
            ToolCalls = [toolCall],
        };

        // Act
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        var roundTrip = (ToolsCallMessage)converted;
        var roundTripArgs = roundTrip.ToolCalls[0].FunctionArgs;

        // Parse and compare JSON structure
        var originalJson = JsonDocument.Parse(complexArgs);
        var roundTripJson = JsonDocument.Parse(roundTripArgs!);

        // Verify nested values
        _ = originalJson
            .RootElement.GetProperty("nested")
            .GetProperty("value")
            .GetInt32()
            .Should()
            .Be(roundTripJson.RootElement.GetProperty("nested").GetProperty("value").GetInt32());

        _ = originalJson
            .RootElement.GetProperty("string")
            .GetString()
            .Should()
            .Be(roundTripJson.RootElement.GetProperty("string").GetString());
    }

    [Fact]
    public void RoundTrip_MultipleToolCalls_PreservesAll()
    {
        // Arrange
        var toolCalls = ImmutableList.Create(
            new LmCoreToolCall
            {
                FunctionName = "func1",
                FunctionArgs = """{"a": 1}""",
                ToolCallId = "call-1",
            },
            new LmCoreToolCall
            {
                FunctionName = "func2",
                FunctionArgs = """{"b": 2}""",
                ToolCallId = "call-2",
            },
            new LmCoreToolCall
            {
                FunctionName = "func3",
                FunctionArgs = """{"c": 3}""",
                ToolCallId = "call-3",
            }
        );

        var original = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-multi",
            ToolCalls = toolCalls,
        };

        // Act
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        var roundTrip = (ToolsCallMessage)converted;
        _ = roundTrip.ToolCalls.Should().HaveCount(3);

        _ = roundTrip.ToolCalls[0].FunctionName.Should().Be("func1");
        _ = roundTrip.ToolCalls[0].ToolCallId.Should().Be("call-1");

        _ = roundTrip.ToolCalls[1].FunctionName.Should().Be("func2");
        _ = roundTrip.ToolCalls[1].ToolCallId.Should().Be("call-2");

        _ = roundTrip.ToolCalls[2].FunctionName.Should().Be("func3");
        _ = roundTrip.ToolCalls[2].ToolCallId.Should().Be("call-3");
    }

    [Theory]
    [InlineData(Role.System)]
    [InlineData(Role.User)]
    [InlineData(Role.Assistant)]
    public void RoundTrip_TextMessage_PreservesAllRoles(Role role)
    {
        // Arrange
        var original = new TextMessage
        {
            Text = "Test message",
            Role = role,
            GenerationId = $"gen-{role}",
        };

        // Act
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        var roundTrip = (TextMessage)converted;
        _ = roundTrip.Role.Should().Be(original.Role);
    }

    [Fact]
    public void RoundTrip_EmptyToolCallArguments_PreservesEmptyObject()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "no_args_function",
            FunctionArgs = "{}",
            ToolCallId = "call-empty",
        };

        var original = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-empty-args",
            ToolCalls = [toolCall],
        };

        // Act
        var agUiMessages = _lmToAgUi.ConvertMessage(original);
        var converted = _agUiToLm.ConvertMessage(agUiMessages[0]);

        // Assert
        var roundTrip = (ToolsCallMessage)converted;
        _ = roundTrip.ToolCalls[0].FunctionArgs.Should().Be("{}");
    }
}
