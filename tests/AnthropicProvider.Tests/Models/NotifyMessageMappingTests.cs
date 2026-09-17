using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     A <see cref="NotifyMessage"/> must reach the Anthropic request as a user-role text block carrying
///     its envelope — the mapper pattern-matches concrete <c>TextMessage</c>, so without an explicit arm
///     a notification would add no content and be dropped (an empty Anthropic message → 400 / silent loss).
/// </summary>
public class NotifyMessageMappingTests
{
    private static readonly GenerateReplyOptions Options = new() { ModelId = "claude-3-7-sonnet-20250219" };

    [Theory]
    [InlineData(AgentMessageType.DelegateTask)]
    [InlineData(AgentMessageType.Question)]
    [InlineData(AgentMessageType.Steer)]
    [InlineData(AgentMessageType.Response)]
    public void AgentMessage_AfterCompletedAssistant_MapsFollowUpAndReplyEnvelope(AgentMessageType messageType)
    {
        var followUp = AgentMessage.Create(
            "followup-1",
            messageType,
            "primary-id",
            "primary",
            body: "Please answer the follow-up."
        );
        var request = AnthropicRequest.FromMessages(
            [new TextMessage { Text = "Original task completed.", Role = Role.Assistant }, followUp],
            Options
        );

        var last = request.Messages.Last();
        Assert.Equal("user", last.Role);
        Assert.Contains(last.Content, content => content.Type == "text" && content.Text == followUp.Text);
        if (followUp.ExpectsReply)
        {
            Assert.Contains(
                last.Content,
                content => content.Text!.Contains("reply-instruction") && content.Text.Contains("primary-id")
            );
        }
    }

    [Fact]
    public void NotifyMessage_MapsToUserTextBlock_WithEnvelope()
    {
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "sub done",
            sourceToolName: "Agent",
            sourceToolCallId: "call-1"
        );

        var request = AnthropicRequest.FromMessages([notify], Options);

        var userMsg = Assert.Single(request.Messages);
        Assert.Equal("user", userMsg.Role);
        Assert.Contains(
            userMsg.Content,
            c =>
                c.Type == "text"
                && (c.Text ?? string.Empty).Contains("<notification")
                && (c.Text ?? string.Empty).Contains("subagent-completion")
        );
    }

    [Fact]
    public void NotifyMessage_AfterToolResultUserTurn_KeepsToolResult_AndEnvelopeLegible()
    {
        // Realistic ordering: a notify is appended to history AFTER a tool_result placeholder. Under
        // Anthropic's consecutive-same-role merge the two user-role messages combine into a single user
        // turn [tool_result, text(envelope)] — tool_result stays first (valid) and the envelope survives.
        IMessage[] messages =
        [
            new ToolCallMessage
            {
                FunctionName = "f",
                FunctionArgs = "{}",
                ToolCallId = "tc1",
                Role = Role.Assistant,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "tc1",
                ToolName = "f",
                Result = "ok",
                Role = Role.User,
            },
            NotifyMessage.Create(NotifyKinds.SubAgentCompletion, detail: "bg done"),
        ];

        var request = AnthropicRequest.FromMessages(messages, Options);

        var allContent = request.Messages.SelectMany(m => m.Content).ToList();
        Assert.Contains(allContent, c => c.Type == "tool_result");
        Assert.Contains(allContent, c => c.Type == "text" && (c.Text ?? string.Empty).Contains("<notification"));
    }

    [Fact]
    public void NotifyMessage_DeliveredBeforeItsToolResult_StillLeavesToolResultFirstInUserTurn()
    {
        // The client-notification tool delivers its NotifyMessage into history BEFORE returning the tool
        // result, so persisted history is tool_use → notify(user) → tool_result(user). The same-role merge
        // then produces a user turn [text(envelope), tool_result], which Anthropic rejects with
        // "tool_use ids were found without tool_result blocks immediately after". The tool_result must lead.
        IMessage[] messages =
        [
            new ToolCallMessage
            {
                FunctionName = "NotifyClient",
                FunctionArgs = "{}",
                ToolCallId = "toolu_X",
                Role = Role.Assistant,
            },
            NotifyMessage.Create(
                NotifyKinds.ClientNotification,
                detail: "client notified",
                sourceToolName: "NotifyClient",
                sourceToolCallId: "toolu_X"
            ),
            new ToolCallResultMessage
            {
                ToolCallId = "toolu_X",
                ToolName = "NotifyClient",
                Result = "ok",
                Role = Role.User,
            },
        ];

        var request = AnthropicRequest.FromMessages(messages, Options);

        var userTurn = Assert.Single(request.Messages, m => m.Role == "user");
        Assert.Equal("tool_result", userTurn.Content[0].Type);
        Assert.Equal("toolu_X", userTurn.Content[0].ToolUseId);
        Assert.Contains(
            userTurn.Content.Skip(1),
            c => c.Type == "text" && (c.Text ?? string.Empty).Contains("<notification")
        );
    }

    [Fact]
    public void AgentMessage_DeliveredBeforeAToolResult_StillLeavesToolResultFirstInUserTurn()
    {
        // Same hazard as the notify case: an agent-to-agent envelope landing between the tool call and its
        // result merges ahead of the tool_result in the user turn.
        var inbound = AgentMessage.Create(
            "followup-1",
            AgentMessageType.Response,
            "primary-id",
            "primary",
            body: "Here is the answer."
        );
        IMessage[] messages =
        [
            new ToolCallMessage
            {
                FunctionName = "f",
                FunctionArgs = "{}",
                ToolCallId = "toolu_Y",
                Role = Role.Assistant,
            },
            inbound,
            new ToolCallResultMessage
            {
                ToolCallId = "toolu_Y",
                ToolName = "f",
                Result = "ok",
                Role = Role.User,
            },
        ];

        var request = AnthropicRequest.FromMessages(messages, Options);

        var userTurn = Assert.Single(request.Messages, m => m.Role == "user");
        Assert.Equal("tool_result", userTurn.Content[0].Type);
        Assert.Equal("toolu_Y", userTurn.Content[0].ToolUseId);
        Assert.Contains(userTurn.Content.Skip(1), c => c.Type == "text" && c.Text == inbound.Text);
    }

    [Fact]
    public void UserTurn_WithOnlyText_KeepsItsOriginalBlockOrder()
    {
        IMessage[] messages =
        [
            new TextMessage { Text = "first", Role = Role.User },
            new TextMessage { Text = "second", Role = Role.User },
        ];

        var request = AnthropicRequest.FromMessages(messages, Options);

        var userTurn = Assert.Single(request.Messages);
        Assert.Equal(["first", "second"], userTurn.Content.Select(c => c.Text));
    }
}
