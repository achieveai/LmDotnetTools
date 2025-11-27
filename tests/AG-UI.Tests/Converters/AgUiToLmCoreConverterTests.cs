using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgUiToolCall = AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs.ToolCall;

namespace AchieveAi.LmDotnetTools.AgUi.Tests.Converters;

public class AgUiToLmCoreConverterTests
{
    private static readonly string[] value = ["END", "STOP"];
    private static readonly string[] expectation = ["END", "STOP"];
    private readonly AgUiToLmCoreConverter _converter;

    public AgUiToLmCoreConverterTests()
    {
        _converter = new AgUiToLmCoreConverter(NullLogger<AgUiToLmCoreConverter>.Instance);
    }

    #region TextMessage Conversion

    [Fact]
    public void ConvertMessage_UserMessage_CreatesTextMessage()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-1",
            Role = "user",
            Content = "Hello from user",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        _ = result.Should().BeOfType<TextMessage>();
        var textMsg = (TextMessage)result;
        _ = textMsg.Text.Should().Be("Hello from user");
        _ = textMsg.Role.Should().Be(Role.User);
        _ = textMsg.GenerationId.Should().Be("msg-1");
        _ = textMsg.FromAgent.Should().BeNull();
    }

    [Fact]
    public void ConvertMessage_AssistantMessage_CreatesTextMessage()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-2",
            Role = "assistant",
            Content = "Assistant response",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        _ = result.Should().BeOfType<TextMessage>();
        var textMsg = (TextMessage)result;
        _ = textMsg.Text.Should().Be("Assistant response");
        _ = textMsg.Role.Should().Be(Role.Assistant);
    }

    [Fact]
    public void ConvertMessage_MessageWithName_SetsFromAgent()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-3",
            Role = "assistant",
            Content = "Response",
            Name = "MyAgent",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var textMsg = (TextMessage)result;
        _ = textMsg.FromAgent.Should().Be("MyAgent");
    }

    [Fact]
    public void ConvertMessage_MessageWithNullContent_CreatesEmptyText()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-4",
            Role = "user",
            Content = null,
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var textMsg = (TextMessage)result;
        _ = textMsg.Text.Should().BeEmpty();
    }

    [Theory]
    [InlineData("system", Role.System)]
    [InlineData("user", Role.User)]
    [InlineData("assistant", Role.Assistant)]
    [InlineData("SYSTEM", Role.System)]
    [InlineData("USER", Role.User)]
    public void ConvertMessage_DifferentRoles_ConvertsCorrectly(string agUiRole, Role expectedRole)
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-role",
            Role = agUiRole,
            Content = "Test",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var textMsg = (TextMessage)result;
        _ = textMsg.Role.Should().Be(expectedRole);
    }

    #endregion

    #region ToolsCallMessage Conversion

    [Fact]
    public void ConvertMessage_MessageWithToolCalls_CreatesToolsCallMessage()
    {
        // Arrange
        var arguments = JsonDocument.Parse("""{"location": "Boston"}""").RootElement;
        var toolCall = new AgUiToolCall
        {
            Id = "call-1",
            Function = new FunctionCall { Name = "get_weather", Arguments = arguments },
        };

        var agUiMessage = new Message
        {
            Id = "msg-tools",
            Role = "assistant",
            ToolCalls = [toolCall],
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        _ = result.Should().BeOfType<ToolsCallMessage>();
        var toolsMsg = (ToolsCallMessage)result;
        _ = toolsMsg.Role.Should().Be(Role.Assistant);
        _ = toolsMsg.GenerationId.Should().Be("msg-tools");
        _ = toolsMsg.ToolCalls.Should().HaveCount(1);

        var convertedCall = toolsMsg.ToolCalls[0];
        _ = convertedCall.ToolCallId.Should().Be("call-1");
        _ = convertedCall.FunctionName.Should().Be("get_weather");
        _ = convertedCall.FunctionArgs.Should().Contain("Boston");
    }

    [Fact]
    public void ConvertMessage_MessageWithMultipleToolCalls_ConvertsAll()
    {
        // Arrange
        var args1 = JsonDocument.Parse("""{"arg1": "value1"}""").RootElement;
        var args2 = JsonDocument.Parse("""{"arg2": "value2"}""").RootElement;

        var toolCalls = ImmutableList.Create(
            new AgUiToolCall
            {
                Id = "call-1",
                Function = new FunctionCall { Name = "func1", Arguments = args1 },
            },
            new AgUiToolCall
            {
                Id = "call-2",
                Function = new FunctionCall { Name = "func2", Arguments = args2 },
            }
        );

        var agUiMessage = new Message
        {
            Id = "msg-multi-tools",
            Role = "assistant",
            ToolCalls = toolCalls,
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var toolsMsg = (ToolsCallMessage)result;
        _ = toolsMsg.ToolCalls.Should().HaveCount(2);
        _ = toolsMsg.ToolCalls[0].FunctionName.Should().Be("func1");
        _ = toolsMsg.ToolCalls[1].FunctionName.Should().Be("func2");
    }

    [Fact]
    public void ConvertMessage_ToolCallWithComplexArguments_SerializesCorrectly()
    {
        // Arrange
        var complexArgs = JsonDocument
            .Parse(
                """
                {
                    "nested": {
                        "value": 123,
                        "array": [1, 2, 3],
                        "bool": true
                    },
                    "string": "test"
                }
                """
            )
            .RootElement;

        var toolCall = new AgUiToolCall
        {
            Id = "call-complex",
            Function = new FunctionCall { Name = "complex_func", Arguments = complexArgs },
        };

        var agUiMessage = new Message
        {
            Id = "msg-complex",
            Role = "assistant",
            ToolCalls = [toolCall],
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var toolsMsg = (ToolsCallMessage)result;
        var argsJson = toolsMsg.ToolCalls[0].FunctionArgs;

        // Verify JSON is valid and contains expected data
        var parsed = JsonDocument.Parse(argsJson!);
        _ = parsed.RootElement.GetProperty("nested").GetProperty("value").GetInt32().Should().Be(123);
        _ = parsed.RootElement.GetProperty("nested").GetProperty("bool").GetBoolean().Should().BeTrue();
        _ = parsed.RootElement.GetProperty("string").GetString().Should().Be("test");
    }

    #endregion

    #region ToolsCallResultMessage Conversion

    [Fact]
    public void ConvertMessage_ToolResultMessage_CreatesToolsCallResultMessage()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-result",
            Role = "tool",
            Content = "Function returned: 42",
            ToolCallId = "call-123",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        _ = result.Should().BeOfType<ToolsCallResultMessage>();
        var resultMsg = (ToolsCallResultMessage)result;
        _ = resultMsg.Role.Should().Be(Role.Tool);
        _ = resultMsg.GenerationId.Should().Be("msg-result");
        _ = resultMsg.ToolCallResults.Should().HaveCount(1);

        var toolResult = resultMsg.ToolCallResults[0];
        _ = toolResult.ToolCallId.Should().Be("call-123");
        _ = toolResult.Result.Should().Be("Function returned: 42");
    }

    [Fact]
    public void ConvertMessage_ToolResultWithNullContent_CreatesEmptyResult()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-result-null",
            Role = "tool",
            Content = null,
            ToolCallId = "call-456",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var resultMsg = (ToolsCallResultMessage)result;
        _ = resultMsg.ToolCallResults[0].Result.Should().BeEmpty();
    }

    #endregion

    #region ConvertMessageHistory

    [Fact]
    public void ConvertMessageHistory_MultipleMessages_ConvertsAll()
    {
        // Arrange
        var messages = ImmutableList.Create(
            new Message
            {
                Id = "1",
                Role = "user",
                Content = "Hello",
            },
            new Message
            {
                Id = "2",
                Role = "assistant",
                Content = "Hi there",
            },
            new Message
            {
                Id = "3",
                Role = "user",
                Content = "How are you?",
            }
        );

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert
        _ = result.Should().HaveCount(3);
        _ = result.Should().AllBeOfType<TextMessage>();

        var textMessages = result.Cast<TextMessage>().ToList();
        _ = textMessages[0].Text.Should().Be("Hello");
        _ = textMessages[1].Text.Should().Be("Hi there");
        _ = textMessages[2].Text.Should().Be("How are you?");
    }

    [Fact]
    public void ConvertMessageHistory_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var messages = ImmutableList<Message>.Empty;

        // Act
        var result = _converter.ConvertMessageHistory(messages);

        // Assert
        _ = result.Should().BeEmpty();
    }

    #endregion

    #region ConvertRunAgentInput

    [Fact]
    public void ConvertRunAgentInput_WithoutHistory_CreatesUserMessage()
    {
        // Arrange
        var input = new RunAgentInput { Message = "User's new message" };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input);

        // Assert
        _ = messages.Should().HaveCount(1);
        var userMsg = messages.First();
        _ = userMsg.Should().BeOfType<TextMessage>();
        var textMsg = (TextMessage)userMsg;
        _ = textMsg.Text.Should().Be("User's new message");
        _ = textMsg.Role.Should().Be(Role.User);
        _ = textMsg.GenerationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ConvertRunAgentInput_WithHistory_CombinesHistoryAndNewMessage()
    {
        // Arrange
        var history = ImmutableList.Create(
            new Message
            {
                Id = "h1",
                Role = "user",
                Content = "Previous question",
            },
            new Message
            {
                Id = "h2",
                Role = "assistant",
                Content = "Previous answer",
            }
        );

        var input = new RunAgentInput { Message = "New question", History = history };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input);

        // Assert
        _ = messages.Should().HaveCount(3);
        var messageList = messages.ToList();

        var msg1 = (TextMessage)messageList[0];
        _ = msg1.Text.Should().Be("Previous question");

        var msg2 = (TextMessage)messageList[1];
        _ = msg2.Text.Should().Be("Previous answer");

        var msg3 = (TextMessage)messageList[2];
        _ = msg3.Text.Should().Be("New question");
        _ = msg3.Role.Should().Be(Role.User);
    }

    [Fact]
    public void ConvertRunAgentInput_WithContext_MergesAsMetadata()
    {
        // Arrange
        var context = ImmutableDictionary<string, object>.Empty.Add("session_id", "abc123").Add("user_id", 42);

        var input = new RunAgentInput { Message = "Test", Context = context };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input);

        // Assert
        var userMsg = (TextMessage)messages.First();
        _ = userMsg.Metadata.Should().NotBeNull();
        _ = userMsg.Metadata.Should().ContainKey("session_id");
        _ = userMsg.Metadata.Should().ContainKey("user_id");
    }

    [Fact]
    public void ConvertRunAgentInput_WithConfiguration_CreatesOptions()
    {
        // Arrange
        var config = new RunConfiguration
        {
            Model = "gpt-4",
            Temperature = 0.7,
            MaxTokens = 1000,
        };

        var input = new RunAgentInput { Message = "Test", Configuration = config };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input);

        // Assert
        _ = options.Should().NotBeNull();
        _ = options.ModelId.Should().Be("gpt-4");
        _ = options.Temperature.Should().Be(0.7f);
        _ = options.MaxToken.Should().Be(1000);
    }

    [Fact]
    public void ConvertRunAgentInput_WithEnabledTools_FiltersToolContracts()
    {
        // Arrange
        var config = new RunConfiguration { EnabledTools = ["get_weather", "calculator"] };

        var availableFunctions = new[]
        {
            new FunctionContract { Name = "get_weather", Description = "Get weather" },
            new FunctionContract { Name = "calculator", Description = "Calculate" },
            new FunctionContract { Name = "search", Description = "Search web" },
        };

        var input = new RunAgentInput { Message = "Test", Configuration = config };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input, availableFunctions);

        // Assert
        _ = options.Functions.Should().NotBeNull();
        _ = options.Functions.Should().HaveCount(2);
        _ = options.Functions.Should().Contain(f => f.Name == "get_weather");
        _ = options.Functions.Should().Contain(f => f.Name == "calculator");
        _ = options.Functions.Should().NotContain(f => f.Name == "search");
    }

    [Fact]
    public void ConvertRunAgentInput_WithModelParameters_MapsToOptions()
    {
        // Arrange
        var modelParams = new Dictionary<string, object>
        {
            { "top_p", 0.9 },
            { "seed", 12345 },
            { "stop", value },
            { "custom_param", "custom_value" },
        };

        var config = new RunConfiguration { ModelParameters = modelParams };

        var input = new RunAgentInput { Message = "Test", Configuration = config };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input);

        // Assert
        _ = options.TopP.Should().Be(0.9f);
        _ = options.RandomSeed.Should().Be(12345);
        _ = options.StopSequence.Should().BeEquivalentTo(expectation);
        _ = options.ExtraProperties.Should().ContainKey("custom_param");
        _ = options.ExtraProperties!["custom_param"].Should().Be("custom_value");
    }

    [Fact]
    public void ConvertRunAgentInput_WithoutConfiguration_CreatesDefaultOptions()
    {
        // Arrange
        var input = new RunAgentInput { Message = "Test" };

        // Act
        var (messages, options) = _converter.ConvertRunAgentInput(input);

        // Assert
        _ = options.Should().NotBeNull();
        _ = options.ModelId.Should().BeEmpty();
        _ = options.Temperature.Should().BeNull();
        _ = options.MaxToken.Should().BeNull();
    }

    #endregion

    #region ConvertToolCall

    [Fact]
    public void ConvertToolCall_ValidAgUiToolCall_CreatesLmCoreToolCall()
    {
        // Arrange
        var arguments = JsonDocument.Parse("""{"param": "value"}""").RootElement;
        var agUiToolCall = new AgUiToolCall
        {
            Id = "call-abc",
            Function = new FunctionCall { Name = "my_function", Arguments = arguments },
        };

        // Act
        var result = _converter.ConvertToolCall(agUiToolCall);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.ToolCallId.Should().Be("call-abc");
        _ = result.FunctionName.Should().Be("my_function");
        _ = result.FunctionArgs.Should().Contain("param");
        _ = result.FunctionArgs.Should().Contain("value");

        // Verify it's valid JSON
        var parsed = JsonDocument.Parse(result.FunctionArgs!);
        _ = parsed.RootElement.GetProperty("param").GetString().Should().Be("value");
    }

    [Fact]
    public void ConvertToolCall_WithEmptyArguments_SerializesEmptyObject()
    {
        // Arrange
        var arguments = JsonDocument.Parse("{}").RootElement;
        var agUiToolCall = new AgUiToolCall
        {
            Id = "call-empty",
            Function = new FunctionCall { Name = "no_args", Arguments = arguments },
        };

        // Act
        var result = _converter.ConvertToolCall(agUiToolCall);

        // Assert
        _ = result.FunctionArgs.Should().Be("{}");
    }

    [Fact]
    public void ConvertToolCall_NullToolCall_ThrowsArgumentNullException()
    {
        // Act & Assert
        var action = () => _converter.ConvertToolCall(null!);
        _ = action.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Edge Cases

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

    [Fact]
    public void ConvertRunAgentInput_NullInput_ThrowsArgumentNullException()
    {
        // Act & Assert
        var action = () => _converter.ConvertRunAgentInput(null!);
        _ = action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConvertMessage_WithSpecialCharacters_PreservesContent()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-special",
            Role = "user",
            Content = "Special: <>&\"'éñ中文🎉",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var textMsg = (TextMessage)result;
        _ = textMsg.Text.Should().Be("Special: <>&\"'éñ中文🎉");
    }

    [Fact]
    public void ConvertMessage_WithUnicodeContent_PreservesUnicode()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-unicode",
            Role = "assistant",
            Content = "Unicode: 你好 🌍 Привет",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var textMsg = (TextMessage)result;
        _ = textMsg.Text.Should().Be("Unicode: 你好 🌍 Привет");
    }

    [Fact]
    public void ConvertMessage_UnknownRole_DefaultsToAssistant()
    {
        // Arrange
        var agUiMessage = new Message
        {
            Id = "msg-unknown",
            Role = "unknown_role",
            Content = "Test",
        };

        // Act
        var result = _converter.ConvertMessage(agUiMessage);

        // Assert
        var textMsg = (TextMessage)result;
        _ = textMsg.Role.Should().Be(Role.Assistant);
    }

    #endregion
}
