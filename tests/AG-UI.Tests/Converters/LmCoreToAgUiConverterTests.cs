using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LmCoreToolCall = AchieveAi.LmDotnetTools.LmCore.Messages.ToolCall;
using ToolCall = AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs.ToolCall;

namespace AchieveAi.LmDotnetTools.AgUi.Tests.Converters;

public class LmCoreToAgUiConverterTests
{
    private readonly LmCoreToAgUiConverter _converter;

    public LmCoreToAgUiConverterTests()
    {
        _converter = new LmCoreToAgUiConverter(NullLogger<LmCoreToAgUiConverter>.Instance);
    }

    #region ToolsCallAggregateMessage Tests

    [Fact]
    public void ConvertMessage_ToolsCallAggregateMessage_CreatesToolCallAndResults()
    {
        // Arrange - Note: The converter uses each message's individual GenerationId
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "get_time",
            FunctionArgs = """{"timezone": "UTC"}""",
            ToolCallId = "call-time",
        };
        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "call-gen-id",
            ToolCalls = [toolCall],
        };

        var result = new ToolCallResult("call-time", "12:00 PM UTC");
        var toolsCallResult = new ToolsCallResultMessage
        {
            Role = Role.Tool,
            GenerationId = "result-gen-id",
            ToolCallResults = [result],
        };

        var aggregateMessage = new ToolsCallAggregateMessage(toolsCallMessage, toolsCallResult);

        // Act
        var converted = _converter.ConvertMessage(aggregateMessage);

        // Assert
        _ = converted.Should().HaveCount(2);

        // First message is the tool call - uses the ToolsCallMessage's GenerationId
        _ = converted[0].Id.Should().Be("call-gen-id");
        _ = converted[0].ToolCalls.Should().HaveCount(1);
        _ = converted[0].ToolCalls![0].Id.Should().Be("call-time");

        // Second message is the result - uses the ToolsCallResultMessage's GenerationId with index
        _ = converted[1].Id.Should().Be("result-gen-id_result_0");
        _ = converted[1].Role.Should().Be("tool");
        _ = converted[1].Content.Should().Be("12:00 PM UTC");
        _ = converted[1].ToolCallId.Should().Be("call-time");
    }

    #endregion

    #region TextMessage Tests

    [Fact]
    public void ConvertMessage_TextMessage_CreatesCorrectAgUiMessage()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Hello world",
            Role = Role.Assistant,
            GenerationId = "gen-123",
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.Should().HaveCount(1);

        var message = result[0];
        _ = message.Id.Should().Be("gen-123");
        _ = message.Role.Should().Be("assistant");
        _ = message.Content.Should().Be("Hello world");
        _ = message.ToolCalls.Should().BeNull();
        _ = message.Name.Should().BeNull();
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithoutGenerationId_ThrowsException()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Hello",
            Role = Role.User,
            GenerationId = null,
        };

        // Act & Assert
        var action = () => _converter.ConvertMessage(textMessage);
        _ = action.Should().Throw<InvalidOperationException>().WithMessage("*GenerationId*");
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithEmptyGenerationId_ThrowsException()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Hello",
            Role = Role.User,
            GenerationId = "",
        };

        // Act & Assert
        var action = () => _converter.ConvertMessage(textMessage);
        _ = action.Should().Throw<InvalidOperationException>().WithMessage("*GenerationId*");
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
            FromAgent = "MyAgent",
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        _ = result[0].Name.Should().Be("MyAgent");
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
            GenerationId = "gen-789",
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        _ = result[0].Role.Should().Be(expectedAgUiRole);
    }

    [Fact]
    public void ConvertMessage_TextMessage_WithNullText_PreservesNull()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = null!,
            Role = Role.User,
            GenerationId = "gen-null",
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        _ = result[0].Content.Should().BeNull();
    }

    #endregion

    #region ToolsCallMessage Tests

    [Fact]
    public void ConvertMessage_ToolsCallMessage_CreatesMessageWithToolCalls()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "get_weather",
            FunctionArgs = """{"location": "San Francisco"}""",
            ToolCallId = "call-123",
        };

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-tools-1",
            ToolCalls = [toolCall],
        };

        // Act
        var result = _converter.ConvertMessage(toolsCallMessage);

        // Assert
        _ = result.Should().HaveCount(1);

        var message = result[0];
        _ = message.Id.Should().Be("gen-tools-1");
        _ = message.Role.Should().Be("assistant");
        _ = message.Content.Should().BeNull();
        _ = message.ToolCalls.Should().NotBeNull();
        _ = message.ToolCalls.Should().HaveCount(1);

        var convertedToolCall = message.ToolCalls![0];
        _ = convertedToolCall.Id.Should().Be("call-123");
        _ = ToolCall.Type.Should().Be("function");
        _ = convertedToolCall.Function.Name.Should().Be("get_weather");
    }

    [Fact]
    public void ConvertMessage_ToolsCallMessage_ParsesJsonArguments()
    {
        // Arrange
        var jsonArgs = """{"location": "Paris", "units": "celsius"}""";
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "get_weather",
            FunctionArgs = jsonArgs,
            ToolCallId = "call-456",
        };

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-tools-2",
            ToolCalls = [toolCall],
        };

        // Act
        var result = _converter.ConvertMessage(toolsCallMessage);

        // Assert
        var convertedToolCall = result[0].ToolCalls![0];
        var args = convertedToolCall.Function.Arguments;

        _ = args.ValueKind.Should().Be(JsonValueKind.Object);
        _ = args.GetProperty("location").GetString().Should().Be("Paris");
        _ = args.GetProperty("units").GetString().Should().Be("celsius");
    }

    [Fact]
    public void ConvertMessage_ToolsCallMessage_WithMultipleToolCalls()
    {
        // Arrange
        var toolCalls = ImmutableList.Create(
            new LmCoreToolCall
            {
                FunctionName = "func1",
                FunctionArgs = """{"arg1": "value1"}""",
                ToolCallId = "call-1",
            },
            new LmCoreToolCall
            {
                FunctionName = "func2",
                FunctionArgs = """{"arg2": "value2"}""",
                ToolCallId = "call-2",
            },
            new LmCoreToolCall
            {
                FunctionName = "func3",
                FunctionArgs = """{"arg3": "value3"}""",
                ToolCallId = "call-3",
            }
        );

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            GenerationId = "gen-multi",
            ToolCalls = toolCalls,
        };

        // Act
        var result = _converter.ConvertMessage(toolsCallMessage);

        // Assert
        _ = result[0].ToolCalls.Should().HaveCount(3);
        _ = result[0].ToolCalls![0].Function.Name.Should().Be("func1");
        _ = result[0].ToolCalls![1].Function.Name.Should().Be("func2");
        _ = result[0].ToolCalls![2].Function.Name.Should().Be("func3");
    }

    #endregion

    #region ToolsCallResultMessage Tests

    [Fact]
    public void ConvertMessage_ToolsCallResultMessage_SingleResult_CreatesSingleMessage()
    {
        // Arrange
        var result = new ToolCallResult("call-123", "Weather data: 72°F");

        var resultMessage = new ToolsCallResultMessage
        {
            Role = Role.Tool,
            GenerationId = "gen-result-1",
            ToolCallResults = [result],
        };

        // Act
        var converted = _converter.ConvertMessage(resultMessage);

        // Assert
        _ = converted.Should().HaveCount(1);

        var message = converted[0];
        _ = message.Id.Should().Be("gen-result-1_result_0");
        _ = message.Role.Should().Be("tool");
        _ = message.Content.Should().Be("Weather data: 72°F");
        _ = message.ToolCallId.Should().Be("call-123");
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
            ToolCallResults = results,
        };

        // Act
        var converted = _converter.ConvertMessage(resultMessage);

        // Assert
        _ = converted.Should().HaveCount(3);

        _ = converted[0].Id.Should().Be("gen-multi-results_result_0");
        _ = converted[0].Content.Should().Be("Result 1");
        _ = converted[0].ToolCallId.Should().Be("call-1");

        _ = converted[1].Id.Should().Be("gen-multi-results_result_1");
        _ = converted[1].Content.Should().Be("Result 2");
        _ = converted[1].ToolCallId.Should().Be("call-2");

        _ = converted[2].Id.Should().Be("gen-multi-results_result_2");
        _ = converted[2].Content.Should().Be("Result 3");
        _ = converted[2].ToolCallId.Should().Be("call-3");
    }

    #endregion

    #region CompositeMessage Tests

    [Fact]
    public void ConvertMessage_CompositeMessage_FlattensToMultipleMessages()
    {
        // Arrange
        var nestedMessages = ImmutableList.Create<IMessage>(
            new TextMessage
            {
                Text = "First",
                Role = Role.User,
                GenerationId = "nested-1",
            },
            new TextMessage
            {
                Text = "Second",
                Role = Role.Assistant,
                GenerationId = "nested-2",
            }
        );

        var compositeMessage = new CompositeMessage
        {
            Messages = nestedMessages,
            GenerationId = "composite-1",
            Role = Role.Assistant,
        };

        // Act
        var result = _converter.ConvertMessage(compositeMessage);

        // Assert
        _ = result.Should().HaveCount(2);

        _ = result[0].Id.Should().Be("composite-1_0");
        _ = result[0].Content.Should().Be("First");
        _ = result[0].Role.Should().Be("user");

        _ = result[1].Id.Should().Be("composite-1_1");
        _ = result[1].Content.Should().Be("Second");
        _ = result[1].Role.Should().Be("assistant");
    }

    [Fact]
    public void ConvertMessage_CompositeMessage_GeneratesIndexedIds()
    {
        // Arrange
        var nestedMessages = ImmutableList.Create<IMessage>(
            new TextMessage
            {
                Text = "Msg1",
                Role = Role.User,
                GenerationId = "n1",
            },
            new TextMessage
            {
                Text = "Msg2",
                Role = Role.User,
                GenerationId = "n2",
            },
            new TextMessage
            {
                Text = "Msg3",
                Role = Role.User,
                GenerationId = "n3",
            }
        );

        var compositeMessage = new CompositeMessage
        {
            Messages = nestedMessages,
            GenerationId = "base-id",
            Role = Role.User,
        };

        // Act
        var result = _converter.ConvertMessage(compositeMessage);

        // Assert
        _ = result[0].Id.Should().Be("base-id_0");
        _ = result[1].Id.Should().Be("base-id_1");
        _ = result[2].Id.Should().Be("base-id_2");
    }

    #endregion

    #region ConvertMessageHistory Tests

    [Fact]
    public void ConvertMessageHistory_MultipleMessages_ConvertsAll()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage
            {
                Text = "User message",
                Role = Role.User,
                GenerationId = "msg-1",
            },
            new TextMessage
            {
                Text = "Assistant reply",
                Role = Role.Assistant,
                GenerationId = "msg-2",
            },
            new TextMessage
            {
                Text = "Follow-up",
                Role = Role.User,
                GenerationId = "msg-3",
            },
        };

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert
        _ = result.Should().HaveCount(3);
        _ = result[0].Content.Should().Be("User message");
        _ = result[1].Content.Should().Be("Assistant reply");
        _ = result[2].Content.Should().Be("Follow-up");
    }

    [Fact]
    public void ConvertMessageHistory_WithInvalidMessage_ContinuesProcessing()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage
            {
                Text = "Valid",
                Role = Role.User,
                GenerationId = "msg-1",
            },
            new TextMessage
            {
                Text = "Invalid",
                Role = Role.User,
                GenerationId = null,
            }, // Invalid!
            new TextMessage
            {
                Text = "Also valid",
                Role = Role.Assistant,
                GenerationId = "msg-3",
            },
        };

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert - Should skip invalid message but process others
        _ = result.Should().HaveCount(2);
        _ = result[0].Content.Should().Be("Valid");
        _ = result[1].Content.Should().Be("Also valid");
    }

    [Fact]
    public void ConvertMessageHistory_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var messages = new List<IMessage>();

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert
        _ = result.Should().BeEmpty();
    }

    #endregion

    #region ConvertToolCall Tests

    [Fact]
    public void ConvertToolCall_ValidToolCall_CreatesCorrectStructure()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "calculate",
            FunctionArgs = """{"expression": "2 + 2"}""",
            ToolCallId = "call-calc",
        };

        // Act
        var result = _converter.ConvertToolCall(toolCall);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.Id.Should().Be("call-calc");
        _ = ToolCall.Type.Should().Be("function");
        _ = result.Function.Should().NotBeNull();
        _ = result.Function.Name.Should().Be("calculate");
        _ = result.Function.Arguments.GetProperty("expression").GetString().Should().Be("2 + 2");
    }

    [Fact]
    public void ConvertToolCall_WithEmptyArgs_ParsesEmptyObject()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "no_args_func",
            FunctionArgs = "{}",
            ToolCallId = "call-empty",
        };

        // Act
        var result = _converter.ConvertToolCall(toolCall);

        // Assert
        _ = result.Function.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        _ = result.Function.Arguments.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void ConvertToolCall_WithNullArgs_ParsesEmptyObject()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "null_args_func",
            FunctionArgs = null,
            ToolCallId = "call-null",
        };

        // Act
        var result = _converter.ConvertToolCall(toolCall);

        // Assert
        _ = result.Function.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        _ = result.Function.Arguments.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void ConvertToolCall_WithInvalidJson_ThrowsJsonException()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "bad_json",
            FunctionArgs = "{not valid json}",
            ToolCallId = "call-bad",
        };

        // Act & Assert
        var action = () => _converter.ConvertToolCall(toolCall);
        _ = action.Should().Throw<JsonException>();
    }

    [Fact]
    public void ConvertToolCall_WithNullToolCallId_ThrowsArgumentException()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "func",
            FunctionArgs = "{}",
            ToolCallId = null!,
        };

        // Act & Assert
        var action = () => _converter.ConvertToolCall(toolCall);
        _ = action.Should().Throw<ArgumentException>().WithMessage("*ToolCallId*");
    }

    [Fact]
    public void ConvertToolCall_WithEmptyFunctionName_ThrowsArgumentException()
    {
        // Arrange
        var toolCall = new LmCoreToolCall
        {
            FunctionName = "",
            FunctionArgs = "{}",
            ToolCallId = "call-1",
        };

        // Act & Assert
        var action = () => _converter.ConvertToolCall(toolCall);
        _ = action.Should().Throw<ArgumentException>().WithMessage("*FunctionName*");
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
            GenerationId = "img-1",
        };

        // Act
        var result = _converter.ConvertMessage(imageMessage);

        // Assert
        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void ConvertMessage_WithSpecialCharacters_PreservesContent()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Special chars: <>&\"'éñ中文🎉",
            Role = Role.User,
            GenerationId = "msg-special",
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        _ = result[0].Content.Should().Be("Special chars: <>&\"'éñ中文🎉");
    }

    [Fact]
    public void ConvertMessage_WithUnicodeContent_PreservesUnicode()
    {
        // Arrange
        var textMessage = new TextMessage
        {
            Text = "Unicode test: 你好世界 🌍 Привет мир",
            Role = Role.Assistant,
            GenerationId = "msg-unicode",
        };

        // Act
        var result = _converter.ConvertMessage(textMessage);

        // Assert
        _ = result[0].Content.Should().Be("Unicode test: 你好世界 🌍 Привет мир");
    }

    [Fact]
    public void ConvertMessage_NullMessage_ThrowsArgumentNullException()
    {
        // Act & Assert
        var action = () => _converter.ConvertMessage(null!);
        _ = action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConvertMessageHistory_NullHistory_ThrowsArgumentNullException()
    {
        // Act & Assert
        var action = () => _converter.ConvertMessageHistory(null!);
        _ = action.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
