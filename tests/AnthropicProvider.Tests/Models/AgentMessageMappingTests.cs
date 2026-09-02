using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     An <see cref="AgentMessage"/> must reach the Anthropic request as a user-role text block carrying
///     its envelope. The mapper pattern-matches concrete message classes; without an explicit arm a typed
///     cross-agent message added no content and was dropped, which left an assistant-final history that
///     Anthropic rejects with "The conversation must end with a user message" (#688).
/// </summary>
public class AgentMessageMappingTests
{
    private static readonly GenerateReplyOptions Options = new() { ModelId = "claude-3-7-sonnet-20250219" };

    [Fact]
    public void AgentMessage_MapsToUserTextBlock_WithEnvelope()
    {
        var agentMessage = AgentMessage.Create(
            messageId: "msg-1",
            AgentMessageType.Steer,
            fromAgentId: "agent-parent",
            fromName: "parent",
            body: "please focus on the tests"
        );

        var request = AnthropicRequest.FromMessages([agentMessage], Options);

        var userMsg = Assert.Single(request.Messages);
        Assert.Equal("user", userMsg.Role);
        Assert.Contains(
            userMsg.Content,
            c =>
                c.Type == "text"
                && (c.Text ?? string.Empty).Contains("<agent-message")
                && (c.Text ?? string.Empty).Contains("please focus on the tests")
        );
    }

    [Fact]
    public void AgentMessage_AfterAssistantFinalTurn_EndsRequestWithUserMessage()
    {
        // The restart-on-message shape: a finished run (assistant-final history) receives a typed
        // AgentMessage as its ONLY new input. Dropping it leaves the request assistant-final, which
        // Anthropic rejects as a prefill.
        IMessage[] messages =
        [
            new TextMessage { Role = Role.User, Text = "do the thing" },
            new TextMessage { Role = Role.Assistant, Text = "done" },
            AgentMessage.Create(
                messageId: "msg-2",
                AgentMessageType.Question,
                fromAgentId: "agent-root",
                fromName: "root",
                body: "what did you find?"
            ),
        ];

        var request = AnthropicRequest.FromMessages(messages, Options);

        var last = request.Messages[^1];
        Assert.Equal("user", last.Role);
        Assert.Contains(last.Content, c => c.Type == "text" && (c.Text ?? string.Empty).Contains("what did you find?"));
    }
}
