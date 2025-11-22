using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

public class ReasoningMessageTests
{
    [Fact]
    public void ChatMessage_WithReasoningContent_YieldsReasoningAndTextMessages()
    {
        // Arrange: simulate an OpenAI chat completion choice containing reasoning_content
        var chatMessage = new ChatMessage
        {
            Role = RoleEnum.Assistant,
            ReasoningContent = "I compare 9.11 and 9.9; 9.9 has a greater tenths digit.",
            Content = ChatMessage.CreateContent("9.9 is greater than 9.11."),
        };

        // Act
        var coreMessages = chatMessage.ToMessages(name: "TestAgent").ToArray();

        // Assert ordering and types
        Assert.Equal(2, coreMessages.Length);
        _ = Assert.IsType<ReasoningMessage>(coreMessages[0]);
        _ = Assert.IsType<TextMessage>(coreMessages[1]);

        var reasoning = (ReasoningMessage)coreMessages[0];
        Assert.Equal("I compare 9.11 and 9.9; 9.9 has a greater tenths digit.", reasoning.Reasoning);
        Assert.Equal(ReasoningVisibility.Plain, reasoning.Visibility);

        var answer = (TextMessage)coreMessages[1];
        Assert.Equal("9.9 is greater than 9.11.", answer.Text);
    }

    [Fact]
    public void ChatMessage_WithEncryptedReasoningDetails_YieldsEncryptedReasoningMessage()
    {
        // Arrange: simulate o-series response with encrypted reasoning_details
        var chatMessage = new ChatMessage
        {
            Role = RoleEnum.Assistant,
            ReasoningDetails =
            [
                new() { Type = "reasoning.encrypted", Data = "ciphertext123" },
            ],
            Content = ChatMessage.CreateContent("Answer without chain-of-thought"),
        };

        // Act
        var coreMessages = chatMessage.ToMessages(name: "TestAgent").ToArray();

        // Assert
        Assert.Equal(2, coreMessages.Length);
        var reasoning = Assert.IsType<ReasoningMessage>(coreMessages[0]);
        Assert.Equal("ciphertext123", reasoning.Reasoning);
        Assert.Equal(ReasoningVisibility.Encrypted, reasoning.Visibility);
    }

    [Fact]
    public void ReasoningMessageBuilder_AccumulatesStreamingUpdates()
    {
        // Arrange
        var builder = new ReasoningMessageBuilder { FromAgent = "Assistant", GenerationId = "gen-123" };

        var updates = new[]
        {
            new ReasoningUpdateMessage { Reasoning = "First part ", GenerationId = "gen-123" },
            new ReasoningUpdateMessage { Reasoning = "second part.", GenerationId = "gen-123" },
        };

        // Act
        foreach (var u in updates)
        {
            builder.Add(u);
        }

        var finalMsg = builder.Build();

        // Assert
        Assert.Equal("First part second part.", finalMsg.Reasoning);
        Assert.Equal("Assistant", finalMsg.FromAgent);
        Assert.Equal("gen-123", finalMsg.GenerationId);
        Assert.Equal(ReasoningVisibility.Plain, finalMsg.Visibility);
    }
}
