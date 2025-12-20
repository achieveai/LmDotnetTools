using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using FluentAssertions;
using Xunit;

namespace LmStreaming.AspNetCore.Tests;

public class IMessageSerializationTests
{
    private readonly JsonSerializerOptions _jsonOptions = JsonSerializerOptionsFactory.CreateForProduction();

    [Fact]
    public void TextMessage_ShouldSerializeWithTypeDiscriminator()
    {
        // Arrange
        var message = new TextMessage { Role = Role.User, Text = "Hello" };

        // Act
        var json = JsonSerializer.Serialize<IMessage>(message, _jsonOptions);

        // Assert
        json.Should().Contain("\"$type\":\"text\"");
        json.Should().Contain("\"text\":\"Hello\"");
        json.Should().Contain("\"role\":\"user\"");
    }

    [Fact]
    public void TextUpdateMessage_ShouldSerializeWithTypeDiscriminator()
    {
        // Arrange
        var message = new TextUpdateMessage { Role = Role.Assistant, Text = "Hi", IsUpdate = true };

        // Act
        var json = JsonSerializer.Serialize<IMessage>(message, _jsonOptions);

        // Assert
        json.Should().Contain("\"$type\":\"text_update\"");
        json.Should().Contain("\"text\":\"Hi\"");
    }

    [Fact]
    public void TextMessage_ShouldRoundTrip()
    {
        // Arrange
        var original = new TextMessage { Role = Role.User, Text = "Hello world" };

        // Act
        var json = JsonSerializer.Serialize<IMessage>(original, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<IMessage>(json, _jsonOptions);

        // Assert
        deserialized.Should().BeOfType<TextMessage>();
        var textMessage = (TextMessage)deserialized!;
        textMessage.Role.Should().Be(Role.User);
        textMessage.Text.Should().Be("Hello world");
    }

    [Fact]
    public void ToolsCallMessage_ShouldRoundTrip()
    {
        // Arrange
        var original = new ToolsCallMessage
        {
            Role = Role.Assistant,
            ToolCalls = [new ToolCall { FunctionName = "test_func", ToolCallId = "call_1", FunctionArgs = "{}" }]
        };

        // Act
        var json = JsonSerializer.Serialize<IMessage>(original, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<IMessage>(json, _jsonOptions);

        // Assert
        deserialized.Should().BeOfType<ToolsCallMessage>();
        var toolsCallMessage = (ToolsCallMessage)deserialized!;
        toolsCallMessage.ToolCalls.Should().HaveCount(1);
        toolsCallMessage.ToolCalls[0].FunctionName.Should().Be("test_func");
    }

    [Fact]
    public void MultipleMessageTypes_ShouldSerializeToArray()
    {
        // Arrange
        IMessage[] messages =
        [
            new TextMessage { Role = Role.User, Text = "Hello" },
            new TextUpdateMessage { Role = Role.Assistant, Text = "Hi", IsUpdate = true },
            new TextMessage { Role = Role.Assistant, Text = "Hi there!" }
        ];

        // Act
        var json = JsonSerializer.Serialize(messages, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<IMessage[]>(json, _jsonOptions);

        // Assert
        deserialized.Should().HaveCount(3);
        deserialized![0].Should().BeOfType<TextMessage>();
        deserialized[1].Should().BeOfType<TextUpdateMessage>();
        deserialized[2].Should().BeOfType<TextMessage>();
    }
}
