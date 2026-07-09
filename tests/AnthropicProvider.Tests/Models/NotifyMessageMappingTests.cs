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

    [Fact]
    public void NotifyMessage_MapsToUserTextBlock_WithEnvelope()
    {
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion, detail: "sub done", sourceToolName: "Agent", sourceToolCallId: "call-1");

        var request = AnthropicRequest.FromMessages([notify], Options);

        var userMsg = Assert.Single(request.Messages);
        Assert.Equal("user", userMsg.Role);
        Assert.Contains(
            userMsg.Content,
            c => c.Type == "text" && (c.Text ?? string.Empty).Contains("<notification")
                && (c.Text ?? string.Empty).Contains("subagent-completion"));
    }

    [Fact]
    public void NotifyMessage_AfterToolResultUserTurn_KeepsToolResult_AndEnvelopeLegible()
    {
        // Realistic ordering: a notify is appended to history AFTER a tool_result placeholder. Under
        // Anthropic's consecutive-same-role merge the two user-role messages combine into a single user
        // turn [tool_result, text(envelope)] — tool_result stays first (valid) and the envelope survives.
        IMessage[] messages =
        [
            new ToolCallMessage { FunctionName = "f", FunctionArgs = "{}", ToolCallId = "tc1", Role = Role.Assistant },
            new ToolCallResultMessage { ToolCallId = "tc1", ToolName = "f", Result = "ok", Role = Role.User },
            NotifyMessage.Create(NotifyKinds.SubAgentCompletion, detail: "bg done"),
        ];

        var request = AnthropicRequest.FromMessages(messages, Options);

        var allContent = request.Messages.SelectMany(m => m.Content).ToList();
        Assert.Contains(allContent, c => c.Type == "tool_result");
        Assert.Contains(allContent, c => c.Type == "text" && (c.Text ?? string.Empty).Contains("<notification"));
    }
}
