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
        var coreMessages = chatMessage.ToMessages("TestAgent").ToArray();

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
                new ChatMessage.ReasoningDetail { Type = "reasoning.encrypted", Data = "ciphertext123" },
            ],
            Content = ChatMessage.CreateContent("Answer without chain-of-thought"),
        };

        // Act
        var coreMessages = chatMessage.ToMessages("TestAgent").ToArray();

        // Assert
        Assert.Equal(2, coreMessages.Length);
        var reasoning = Assert.IsType<ReasoningMessage>(coreMessages[0]);
        Assert.Equal("ciphertext123", reasoning.Reasoning);
        Assert.Equal(ReasoningVisibility.Encrypted, reasoning.Visibility);
    }

    [Fact]
    public void FromMessages_ToolCallTurnComposite_ReplaysReasoningOnToolCallMessageNotAsText()
    {
        // Arrange: the shape MessageTransformationMiddleware rebuilds for a tool-using turn.
        var turn = new CompositeMessage
        {
            Role = Role.Assistant,
            Messages =
            [
                new ReasoningMessage
                {
                    Role = Role.Assistant,
                    Reasoning = "PLAIN THOUGHT",
                    Visibility = ReasoningVisibility.Plain,
                },
                new ReasoningMessage
                {
                    Role = Role.Assistant,
                    Reasoning = "SIGNATURE",
                    Visibility = ReasoningVisibility.Encrypted,
                },
                new ToolsCallAggregateMessage(
                    new ToolsCallMessage
                    {
                        Role = Role.Assistant,
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                FunctionName = "calc",
                                FunctionArgs = "{}",
                                ToolCallId = "c1",
                            },
                        ],
                    },
                    new ToolsCallResultMessage { ToolCallResults = [new ToolCallResult("c1", "42")] }
                ),
            ],
        };

        // Act
        var request = ChatCompletionRequest.FromMessages(
            [new TextMessage { Role = Role.User, Text = "hi" }, turn],
            new LmCore.Core.GenerateReplyOptions { ModelId = "m" }
        );

        // Assert: user, assistant(tool_calls + encrypted reasoning), tool — nothing else.
        Assert.Equal([RoleEnum.User, RoleEnum.Assistant, RoleEnum.Tool], request.Messages.Select(m => m.Role!.Value));
        var assistant = request.Messages[1];
        Assert.Equal("calc", Assert.Single(assistant.ToolCalls!).Function.Name);
        var detail = Assert.Single(assistant.ReasoningDetails!);
        Assert.Equal(("reasoning.encrypted", "SIGNATURE"), (detail.Type, detail.Data));
        Assert.DoesNotContain(
            request.Messages,
            m => m.Content?.Is<string>() == true && m.Content.Get<string>() == "PLAIN THOUGHT"
        );
    }

    [Fact]
    public void FromMessages_ReasoningOnlyTurn_StaysOnItsOwnAssistantMessageNotTheNextUserMessage()
    {
        // Arrange: a turn that ended after thinking (interrupted, or no output), then the next question.
        var request = ChatCompletionRequest.FromMessages(
            [
                new TextMessage { Role = Role.User, Text = "q1" },
                new ReasoningMessage
                {
                    Role = Role.Assistant,
                    Reasoning = "T1",
                    Visibility = ReasoningVisibility.Plain,
                },
                new TextMessage { Role = Role.User, Text = "q2" },
            ],
            new LmCore.Core.GenerateReplyOptions { ModelId = "m" }
        );

        // Assert
        Assert.Equal([RoleEnum.User, RoleEnum.Assistant, RoleEnum.User], request.Messages.Select(m => m.Role!.Value));
        Assert.Equal("T1", request.Messages[1].Reasoning);
        Assert.Null(request.Messages[2].Reasoning);
    }

    [Fact]
    public void FromMessages_ReasoningOnlyComposite_DoesNotSendAnEmptyContentArray()
    {
        var request = ChatCompletionRequest.FromMessages(
            [
                new TextMessage { Role = Role.User, Text = "q1" },
                new CompositeMessage
                {
                    Role = Role.Assistant,
                    Messages =
                    [
                        new ReasoningMessage
                        {
                            Role = Role.Assistant,
                            Reasoning = "PLAIN",
                            Visibility = ReasoningVisibility.Plain,
                        },
                        new ReasoningMessage
                        {
                            Role = Role.Assistant,
                            Reasoning = "SIGNATURE",
                            Visibility = ReasoningVisibility.Encrypted,
                        },
                    ],
                },
            ],
            new LmCore.Core.GenerateReplyOptions { ModelId = "m" }
        );

        var assistant = Assert.Single(request.Messages, m => m.Role == RoleEnum.Assistant);
        Assert.True(assistant.Content!.Is<string>(), "a content array with no parts is rejected by the API");
        Assert.Equal("SIGNATURE", Assert.Single(assistant.ReasoningDetails!).Data);
    }

    [Fact]
    public void FromMessages_TextComposite_KeepsEveryPlainReasoningBlock()
    {
        var request = ChatCompletionRequest.FromMessages(
            [
                new TextMessage { Role = Role.User, Text = "q1" },
                new CompositeMessage
                {
                    Role = Role.Assistant,
                    Messages =
                    [
                        new ReasoningMessage
                        {
                            Role = Role.Assistant,
                            Reasoning = "FIRST",
                            Visibility = ReasoningVisibility.Plain,
                        },
                        new ReasoningMessage
                        {
                            Role = Role.Assistant,
                            Reasoning = "SECOND",
                            Visibility = ReasoningVisibility.Plain,
                        },
                        new TextMessage { Role = Role.Assistant, Text = "answer" },
                    ],
                },
            ],
            new LmCore.Core.GenerateReplyOptions { ModelId = "m" }
        );

        var assistant = Assert.Single(request.Messages, m => m.Role == RoleEnum.Assistant);
        Assert.Equal(["FIRST", "SECOND"], assistant.ReasoningDetails!.Select(d => d.Data));
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
