using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for MessagePersistenceConverter.
/// </summary>
public class MessagePersistenceConverterTests
{
    private readonly JsonSerializerOptions _jsonOptions;

    public MessagePersistenceConverterTests()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
        };
        _jsonOptions.Converters.Add(new IMessageJsonConverter());
    }

    #region ToPersistedMessage Tests

    [Fact]
    public void ToPersistedMessage_ConvertsTextMessage()
    {
        // Arrange
        var message = new TextMessage
        {
            Text = "Hello, world!",
            Role = Role.User,
            FromAgent = "user",
            GenerationId = "gen-123",
            MessageOrderIdx = 5,
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted.ThreadId.Should().Be("thread-1");
        persisted.RunId.Should().Be("run-1");
        persisted.Role.Should().Be("User");
        persisted.FromAgent.Should().Be("user");
        persisted.GenerationId.Should().Be("gen-123");
        persisted.MessageOrderIdx.Should().Be(5);
        persisted.MessageType.Should().Be("TextMessage");
        persisted.MessageJson.Should().Contain("Hello, world!");
    }

    [Fact]
    public void ToPersistedMessage_GeneratesUniqueId()
    {
        // Arrange
        var message = new TextMessage { Text = "Test", Role = Role.User };

        // Act
        var persisted1 = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);
        var persisted2 = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted1.Id.Should().NotBe(persisted2.Id);
    }

    [Fact]
    public void ToPersistedMessage_SetsTimestamp()
    {
        // Arrange
        var message = new TextMessage { Text = "Test", Role = Role.User };
        var beforeTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);

        var afterTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Assert
        persisted.Timestamp.Should().BeInRange(beforeTimestamp, afterTimestamp);
    }

    [Fact]
    public void ToPersistedMessage_HandlesToolCallMessage()
    {
        // Arrange
        var message = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"city\": \"Seattle\"}",
            ToolCallId = "call-123",
            Role = Role.Assistant,
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted.MessageType.Should().Be("ToolCallMessage");
        persisted.MessageJson.Should().Contain("get_weather");
        persisted.MessageJson.Should().Contain("Seattle");
    }

    [Fact]
    public void ToPersistedMessage_HandlesToolCallResultMessage()
    {
        // Arrange
        var message = new ToolCallResultMessage
        {
            ToolCallId = "call-123",
            Result = "{\"temperature\": 72}",
            Role = Role.User,
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted.MessageType.Should().Be("ToolCallResultMessage");
        persisted.MessageJson.Should().Contain("call-123");
        persisted.MessageJson.Should().Contain("temperature");
    }

    [Fact]
    public void ToPersistedMessage_SetsParentRunIdFromMessage()
    {
        // Arrange
        var message = new TextMessage
        {
            Text = "Test",
            Role = Role.User,
            ParentRunId = "parent-run-0",
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            message, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted.ParentRunId.Should().Be("parent-run-0");
    }

    #endregion

    #region FromPersistedMessage Tests

    [Fact]
    public void FromPersistedMessage_RestoresTextMessage()
    {
        // Arrange
        var original = new TextMessage
        {
            Text = "Hello, world!",
            Role = Role.User,
            FromAgent = "user",
            GenerationId = "gen-123",
            MessageOrderIdx = 5,
        };

        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            original, "thread-1", "run-1", _jsonOptions);

        // Act
        var restored = MessagePersistenceConverter.FromPersistedMessage(persisted, _jsonOptions);

        // Assert
        restored.Should().BeOfType<TextMessage>();
        var textMessage = (TextMessage)restored;
        textMessage.Text.Should().Be("Hello, world!");
        textMessage.Role.Should().Be(Role.User);
        textMessage.FromAgent.Should().Be("user");
        textMessage.GenerationId.Should().Be("gen-123");
        textMessage.MessageOrderIdx.Should().Be(5);
    }

    [Fact]
    public void FromPersistedMessage_RestoresToolCallMessage()
    {
        // Arrange
        var original = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"city\": \"Seattle\"}",
            ToolCallId = "call-123",
            Role = Role.Assistant,
        };

        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            original, "thread-1", "run-1", _jsonOptions);

        // Act
        var restored = MessagePersistenceConverter.FromPersistedMessage(persisted, _jsonOptions);

        // Assert
        restored.Should().BeOfType<ToolCallMessage>();
        var toolCall = (ToolCallMessage)restored;
        toolCall.FunctionName.Should().Be("get_weather");
        toolCall.FunctionArgs.Should().Be("{\"city\": \"Seattle\"}");
        toolCall.ToolCallId.Should().Be("call-123");
    }

    [Fact]
    public void FromPersistedMessage_RestoresToolCallResultMessage()
    {
        // Arrange
        var original = new ToolCallResultMessage
        {
            ToolCallId = "call-123",
            Result = "{\"temperature\": 72}",
            Role = Role.User,
        };

        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            original, "thread-1", "run-1", _jsonOptions);

        // Act
        var restored = MessagePersistenceConverter.FromPersistedMessage(persisted, _jsonOptions);

        // Assert
        restored.Should().BeOfType<ToolCallResultMessage>();
        var result = (ToolCallResultMessage)restored;
        result.ToolCallId.Should().Be("call-123");
        result.Result.Should().Be("{\"temperature\": 72}");
    }

    #endregion

    #region Batch Conversion Tests

    [Fact]
    public void ToPersistedMessages_ConvertsBatch()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            new TextMessage { Text = "Hi there!", Role = Role.Assistant },
            new ToolCallMessage { FunctionName = "test", ToolCallId = "call-1", Role = Role.Assistant },
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessages(
            messages, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted.Should().HaveCount(3);
        persisted[0].MessageType.Should().Be("TextMessage");
        persisted[1].MessageType.Should().Be("TextMessage");
        persisted[2].MessageType.Should().Be("ToolCallMessage");
    }

    [Fact]
    public void FromPersistedMessages_RestoresBatch()
    {
        // Arrange
        var originalMessages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            new TextMessage { Text = "Hi there!", Role = Role.Assistant },
            new ToolCallMessage { FunctionName = "test", ToolCallId = "call-1", Role = Role.Assistant },
        };

        var persisted = MessagePersistenceConverter.ToPersistedMessages(
            originalMessages, "thread-1", "run-1", _jsonOptions);

        // Act
        var restored = MessagePersistenceConverter.FromPersistedMessages(persisted, _jsonOptions);

        // Assert
        restored.Should().HaveCount(3);
        restored[0].Should().BeOfType<TextMessage>();
        restored[1].Should().BeOfType<TextMessage>();
        restored[2].Should().BeOfType<ToolCallMessage>();

        ((TextMessage)restored[0]).Text.Should().Be("Hello");
        ((TextMessage)restored[1]).Text.Should().Be("Hi there!");
        ((ToolCallMessage)restored[2]).FunctionName.Should().Be("test");
    }

    [Fact]
    public void ToPersistedMessages_PreservesOrder()
    {
        // Arrange
        var messages = Enumerable.Range(0, 10)
            .Select(i => new TextMessage
            {
                Text = $"Message {i}",
                Role = Role.User,
                MessageOrderIdx = i,
            })
            .Cast<IMessage>()
            .ToList();

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessages(
            messages, "thread-1", "run-1", _jsonOptions);

        // Assert
        for (var i = 0; i < 10; i++)
        {
            persisted[i].MessageOrderIdx.Should().Be(i);
        }
    }

    [Fact]
    public void ToPersistedMessages_WithEmptyList_ReturnsEmptyList()
    {
        // Arrange
        var messages = new List<IMessage>();

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessages(
            messages, "thread-1", "run-1", _jsonOptions);

        // Assert
        persisted.Should().BeEmpty();
    }

    [Fact]
    public void FromPersistedMessages_WithEmptyList_ReturnsEmptyList()
    {
        // Arrange
        var persisted = new List<PersistedMessage>();

        // Act
        var restored = MessagePersistenceConverter.FromPersistedMessages(persisted, _jsonOptions);

        // Assert
        restored.Should().BeEmpty();
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_PreservesAllMessageProperties()
    {
        // Arrange
        var original = new TextMessage
        {
            Text = "Complex message with special chars: <>&\"'",
            Role = Role.Assistant,
            FromAgent = "test-agent",
            GenerationId = "gen-456",
            MessageOrderIdx = 42,
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            original, "thread-1", "run-1", _jsonOptions);
        var restored = MessagePersistenceConverter.FromPersistedMessage(persisted, _jsonOptions);

        // Assert
        restored.Should().BeOfType<TextMessage>();
        var textMessage = (TextMessage)restored;
        textMessage.Text.Should().Be(original.Text);
        textMessage.Role.Should().Be(original.Role);
        textMessage.FromAgent.Should().Be(original.FromAgent);
        textMessage.GenerationId.Should().Be(original.GenerationId);
        textMessage.MessageOrderIdx.Should().Be(original.MessageOrderIdx);
    }

    [Fact]
    public void RoundTrip_PreservesToolCallDetails()
    {
        // Arrange
        var original = new ToolCallMessage
        {
            FunctionName = "complex_function",
            FunctionArgs = "{\"nested\": {\"value\": 123}, \"array\": [1, 2, 3]}",
            ToolCallId = "call-with-special-id-123",
            Role = Role.Assistant,
            FromAgent = "tool-agent",
        };

        // Act
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            original, "thread-1", "run-1", _jsonOptions);
        var restored = MessagePersistenceConverter.FromPersistedMessage(persisted, _jsonOptions);

        // Assert
        restored.Should().BeOfType<ToolCallMessage>();
        var toolCall = (ToolCallMessage)restored;
        toolCall.FunctionName.Should().Be(original.FunctionName);
        toolCall.FunctionArgs.Should().Be(original.FunctionArgs);
        toolCall.ToolCallId.Should().Be(original.ToolCallId);
        toolCall.FromAgent.Should().Be(original.FromAgent);
    }

    #endregion
}
