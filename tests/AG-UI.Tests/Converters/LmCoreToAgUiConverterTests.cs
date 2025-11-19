using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LmCoreToolCall = AchieveAi.LmDotnetTools.LmCore.Messages.ToolCall;

namespace AchieveAi.LmDotnetTools.AgUi.Tests.Converters;

public class LmCoreToAgUiConverterTests
{
    private readonly LmCoreToAgUiConverter _converter;

    public LmCoreToAgUiConverterTests()
    {
        _converter = new LmCoreToAgUiConverter(NullLogger<LmCoreToAgUiConverter>.Instance);
    }

    #region TextMessage Tests

    [Fact]
    public void ConvertMessage_TextMessage_CreatesCorrectAgUiMessage()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Hello world",
            Role = Role.Assistant,
            GenerationId = "gen-123"
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);

        var message = result[0];
        message.Id.Should().Be("gen-123");
        message.Role.Should().Be("assistant");
        message.Content.Should().Be("Hello world");
        message.ToolCalls.Should().BeNull();
        message.Name.Should().BeNull();
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithoutGenerationId_ThrowsException()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Hello",
            Role = Role.User,
            GenerationId = null
        };

        // Act & Assert
        var action = () => _converter.ConvertMessage(textMessage);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*GenerationId*");
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithEmptyGenerationId_ThrowsException()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Hello",
            Role = Role.User,
            GenerationId = ""
        };

        // Act & Assert
        var action = () => _converter.ConvertMessage(textMessage);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*GenerationId*");
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithFromAgent_SetsName()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Agent response",
            Role = Role.Assistant,
            GenerationId = "gen-456",
            FromAgent = "MyAgent"
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        result[0].Name.Should().Be("MyAgent");
    }

    [Theory]
    [InlineData(Role.System, "system")]
    [InlineData(Role.User, "user")]
    [InlineData(Role.Assistant, "assistant")]
    [InlineData(Role.Tool, "tool")]
    public void ConvertMessage_TextMessage_ConvertsRoleCorrectly(Role lmCoreRole, string expectedAgUiRole)
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Test",
            Role = lmCoreRole,
            GenerationId = "gen-789"
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        result[0].Role.Should().Be(expectedAgUiRole);
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithNullText_PreservesNull()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = null!,
            Role = Role.User,
            GenerationId = "gen-null"
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        result[0].Content.Should().BeNull();
    }

    #endregion

    #region ToolsCallMessage Tests

    [Fact]
    public void ConvertMessage_ToolsCallMessage_CreatesMessageWithToolCalls()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "get_weather",
            FunctionArgs: """{"location": "San Francisco"}"""
        )
        {
            ToolCallId = "call-123"
        };

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-tools-1",
            ToolCalls = ImmutableList.Create(toolCall)
        };

        // Act
        var result = _converter.ConvertMessage(toolsCallMessage);

        // Assert
        result.Should().HaveCount(1);

        var message = result[0];
        message.Id.Should().Be("gen-tools-1");
        message.Role.Should().Be("assistant");
        message.Content.Should().BeNull();
        message.ToolCalls.Should().NotBeNull();
        message.ToolCalls.Should().HaveCount(1);

        var convertedToolCall = message.ToolCalls![0];
        convertedToolCall.Id.Should().Be("call-123");
        convertedToolCall.Type.Should().Be("function");
        convertedToolCall.Function.Name.Should().Be("get_weather");
    }

    [Fact]
    public void ConvertMessage_ToolsCallMessage_ParsesJsonArguments()
    {
        // Arrange
        var jsonArgs = """{"location": "Paris", "units": "celsius"}""";
        var toolCall = new LmCoreToolCall(
            FunctionName: "get_weather",
            FunctionArgs: jsonArgs
        )
        {
            ToolCallId = "call-456"
        };

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-tools-2",
            ToolCalls = ImmutableList.Create(toolCall)
        };

        // Act
        var result = _converter.ConvertMessage(toolsCallMessage);

        // Assert
        var convertedToolCall = result[0].ToolCalls![0];
        var args = convertedToolCall.Function.Arguments;

        args.ValueKind.Should().Be(JsonValueKind.Object);
        args.GetProperty("location").GetString().Should().Be("Paris");
        args.GetProperty("units").GetString().Should().Be("celsius");
    }

    [Fact]
    public void ConvertMessage_ToolsCallMessage_WithMultipleToolCalls()
    {
        // Arrange
        var toolCalls = ImmutableList.Create(
            new LmCoreToolCall("func1", """{"arg1": "value1"}""") { ToolCallId = "call-1" },
            new LmCoreToolCall("func2", """{"arg2": "value2"}""") { ToolCallId = "call-2" },
            new LmCoreToolCall("func3", """{"arg3": "value3"}""") { ToolCallId = "call-3" }
        );

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-multi",
            ToolCalls = toolCalls
        };

        // Act
        var result = _converter.ConvertMessage(toolsCallMessage);

        // Assert
        result[0].ToolCalls.Should().HaveCount(3);
        result[0].ToolCalls![0].Function.Name.Should().Be("func1");
        result[0].ToolCalls![1].Function.Name.Should().Be("func2");
        result[0].ToolCalls![2].Function.Name.Should().Be("func3");
    }

    #endregion

    #region ToolsCallResultMessage Tests

    [Fact]
    public void ConvertMessage_ToolsCallResultMessage_SingleResult_CreatesSingleMessage()
    {
        // Arrange
        var result = new ToolCallResult(
            ToolCallId: "call-123",
            Result: "Weather data: 72°F"
        );

        var resultMessage = new ToolsCallResultMessage
        {
            Role = Role.Tool,
            GenerationId = "gen-result-1",
            ToolCallResults = ImmutableList.Create(result)
        };

        // Act
        var converted = _converter.ConvertMessage(resultMessage);

        // Assert
        converted.Should().HaveCount(1);

        var message = converted[0];
        message.Id.Should().Be("gen-result-1_result_0");
        message.Role.Should().Be("tool");
        message.Content.Should().Be("Weather data: 72°F");
        message.ToolCallId.Should().Be("call-123");
    }

    [Fact]
    public void ConvertMessage_ToolsCallResultMessage_MultipleResults_CreatesMultipleMessages()
    {
        // Arrange
        var results = ImmutableList.Create(
            new ToolCallResult("call-1", "Result 1"),
            new ToolCallResult("call-2", "Result 2"),
            new ToolCallResult("call-3", "Result 3")
        );

        var resultMessage = new ToolsCallResultMessage
        {
            Role = Role.Tool,
            GenerationId = "gen-multi-results",
            ToolCallResults = results
        };

        // Act
        var converted = _converter.ConvertMessage(resultMessage);

        // Assert
        converted.Should().HaveCount(3);

        converted[0].Id.Should().Be("gen-multi-results_result_0");
        converted[0].Content.Should().Be("Result 1");
        converted[0].ToolCallId.Should().Be("call-1");

        converted[1].Id.Should().Be("gen-multi-results_result_1");
        converted[1].Content.Should().Be("Result 2");
        converted[1].ToolCallId.Should().Be("call-2");

        converted[2].Id.Should().Be("gen-multi-results_result_2");
        converted[2].Content.Should().Be("Result 3");
        converted[2].ToolCallId.Should().Be("call-3");
    }

    #endregion

    #region CompositeMessage Tests

    [Fact]
    public void ConvertMessage_CompositeMessage_FlattensToMultipleMessages()
    {
        // Arrange
        var nestedMessages = ImmutableList.Create<IMessage>(
            new TextMessage { Text = "First", Role = Role.User, GenerationId = "nested-1" },
            new TextMessage { Text = "Second", Role = Role.Assistant, GenerationId = "nested-2" }
        );

        var compositeMessage = new CompositeMessage
        {
            Messages = nestedMessages,
            GenerationId = "composite-1",
            Role = Role.Assistant
        };

        // Act
        var result = _converter.ConvertMessage(compositeMessage);

        // Assert
        result.Should().HaveCount(2);

        result[0].Id.Should().Be("composite-1_0");
        result[0].Content.Should().Be("First");
        result[0].Role.Should().Be("user");

        result[1].Id.Should().Be("composite-1_1");
        result[1].Content.Should().Be("Second");
        result[1].Role.Should().Be("assistant");
    }

    [Fact]
    public void ConvertMessage_CompositeMessage_GeneratesIndexedIds()
    {
        // Arrange
        var nestedMessages = ImmutableList.Create<IMessage>(
            new TextMessage { Text = "Msg1", Role = Role.User, GenerationId = "n1" },
            new TextMessage { Text = "Msg2", Role = Role.User, GenerationId = "n2" },
            new TextMessage { Text = "Msg3", Role = Role.User, GenerationId = "n3" }
        );

        var compositeMessage = new CompositeMessage
        {
            Messages = nestedMessages,
            GenerationId = "base-id",
            Role = Role.User
        };

        // Act
        var result = _converter.ConvertMessage(compositeMessage);

        // Assert
        result[0].Id.Should().Be("base-id_0");
        result[1].Id.Should().Be("base-id_1");
        result[2].Id.Should().Be("base-id_2");
    }

    #endregion

    #region ToolsCallAggregateMessage Tests

    [Fact]
    public void ConvertMessage_ToolsCallAggregateMessage_CreatesToolCallAndResults()
    {
        // Arrange - Note: The converter uses each message's individual GenerationId
        var toolCall = new LmCoreToolCall("get_time", """{"timezone": "UTC"}""") { ToolCallId = "call-time" };
        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "call-gen-id",
            ToolCalls = ImmutableList.Create(toolCall)
        };

        var result = new ToolCallResult("call-time", "12:00 PM UTC");
        var toolsCallResult = new ToolsCallResultMessage
        {
            Role = Role.Tool,
            GenerationId = "result-gen-id",
            ToolCallResults = ImmutableList.Create(result)
        };

        var aggregateMessage = new ToolsCallAggregateMessage(
            toolsCallMessage,
            toolsCallResult
        );

        // Act
        var converted = _converter.ConvertMessage(aggregateMessage);

        // Assert
        converted.Should().HaveCount(2);

        // First message is the tool call - uses the ToolsCallMessage's GenerationId
        converted[0].Id.Should().Be("call-gen-id");
        converted[0].ToolCalls.Should().HaveCount(1);
        converted[0].ToolCalls![0].Id.Should().Be("call-time");

        // Second message is the result - uses the ToolsCallResultMessage's GenerationId with index
        converted[1].Id.Should().Be("result-gen-id_result_0");
        converted[1].Role.Should().Be("tool");
        converted[1].Content.Should().Be("12:00 PM UTC");
        converted[1].ToolCallId.Should().Be("call-time");
    }

    #endregion

    #region ConvertMessageHistory Tests

    [Fact]
    public void ConvertMessageHistory_MultipleMessages_ConvertsAll()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "User message", Role = Role.User, GenerationId = "msg-1" },
            new TextMessage { Text = "Assistant reply", Role = Role.Assistant, GenerationId = "msg-2" },
            new TextMessage { Text = "Follow-up", Role = Role.User, GenerationId = "msg-3" }
        };

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert
        result.Should().HaveCount(3);
        result[0].Content.Should().Be("User message");
        result[1].Content.Should().Be("Assistant reply");
        result[2].Content.Should().Be("Follow-up");
    }

    [Fact]
    public void ConvertMessageHistory_WithInvalidMessage_ContinuesProcessing()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Valid", Role = Role.User, GenerationId = "msg-1" },
            new TextMessage { Text = "Invalid", Role = Role.User, GenerationId = null }, // Invalid!
            new TextMessage { Text = "Also valid", Role = Role.Assistant, GenerationId = "msg-3" }
        };

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert - Should skip invalid message but process others
        result.Should().HaveCount(2);
        result[0].Content.Should().Be("Valid");
        result[1].Content.Should().Be("Also valid");
    }

    [Fact]
    public void ConvertMessageHistory_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var messages = new List<IMessage>();

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region ConvertToolCall Tests

    [Fact]
    public void ConvertToolCall_ValidToolCall_CreatesCorrectStructure()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "calculate",
            FunctionArgs: """{"expression": "2 + 2"}"""
        )
        {
            ToolCallId = "call-calc"
        };

        // Act
        var result = _converter.ConvertToolCall(toolCall);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be("call-calc");
        result.Type.Should().Be("function");
        result.Function.Should().NotBeNull();
        result.Function.Name.Should().Be("calculate");
        result.Function.Arguments.GetProperty("expression").GetString().Should().Be("2 + 2");
    }

    [Fact]
    public void ConvertToolCall_WithEmptyArgs_ParsesEmptyObject()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "no_args_func",
            FunctionArgs: "{}"
        )
        {
            ToolCallId = "call-empty"
        };

        // Act
        var result = _converter.ConvertToolCall(toolCall);

        // Assert
        result.Function.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        result.Function.Arguments.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void ConvertToolCall_WithNullArgs_ParsesEmptyObject()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "null_args_func",
            FunctionArgs: null
        )
        {
            ToolCallId = "call-null"
        };

        // Act
        var result = _converter.ConvertToolCall(toolCall);

        // Assert
        result.Function.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        result.Function.Arguments.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void ConvertToolCall_WithInvalidJson_ThrowsJsonException()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "bad_json",
            FunctionArgs: "{not valid json}"
        )
        {
            ToolCallId = "call-bad"
        };

        // Act & Assert
        var action = () => _converter.ConvertToolCall(toolCall);
        action.Should().Throw<JsonException>();
    }

    [Fact]
    public void ConvertToolCall_WithNullToolCallId_ThrowsArgumentException()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "func",
            FunctionArgs: "{}"
        )
        {
            ToolCallId = null!
        };

        // Act & Assert
        var action = () => _converter.ConvertToolCall(toolCall);
        action.Should().Throw<ArgumentException>()
            .WithMessage("*ToolCallId*");
    }

    [Fact]
    public void ConvertToolCall_WithEmptyFunctionName_ThrowsArgumentException()
    {
        // Arrange
        var toolCall = new LmCoreToolCall(
            FunctionName: "",
            FunctionArgs: "{}"
        )
        {
            ToolCallId = "call-1"
        };

        // Act & Assert
        var action = () => _converter.ConvertToolCall(toolCall);
        action.Should().Throw<ArgumentException>()
            .WithMessage("*FunctionName*");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ConvertMessage_UnsupportedMessageType_ReturnsEmptyList()
    {
        // Arrange - Using ImageMessage as an unsupported type
        var imageMessage = new ImageMessage
        {
            ImageData = BinaryData.FromString("test"),
            Role = Role.User,
            GenerationId = "img-1"
        };

        // Act
        var result = _converter.ConvertMessage(imageMessage);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ConvertMessage_WithSpecialCharacters_PreservesContent()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Special chars: <>&\"'éñ中文🎉",
            Role = Role.User,
            GenerationId = "msg-special"
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        result[0].Content.Should().Be("Special chars: <>&\"'éñ中文🎉");
    }

    [Fact]
    public void ConvertMessage_WithUnicodeContent_PreservesUnicode()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Unicode test: 你好世界 🌍 Привет мир",
            Role = Role.Assistant,
            GenerationId = "msg-unicode"
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        result[0].Content.Should().Be("Unicode test: 你好世界 🌍 Привет мир");
    }

    [Fact]
    public void ConvertMessage_NullMessage_ThrowsArgumentNullException()
    {
        // Act & Assert
        var action = () => _converter.ConvertMessage(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConvertMessageHistory_NullHistory_ThrowsArgumentNullException()
    {
        // Act & Assert
        var action = () => _converter.ConvertMessageHistory(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
