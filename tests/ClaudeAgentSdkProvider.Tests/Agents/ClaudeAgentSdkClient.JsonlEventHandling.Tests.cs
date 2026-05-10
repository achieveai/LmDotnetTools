using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ClaudeAgentSdkClientJsonlEventHandlingTests
{
    [Fact]
    public void ConvertNonTerminalJsonlEventToMessages_IgnoresStreamEventAndPreservesAssistantMessages()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var events = new JsonlEventBase[]
        {
            new StreamEvent
            {
                Event = JsonDocument.Parse("""{"type":"content_block_delta"}""").RootElement,
                SessionId = "session-1",
                Uuid = "stream-1",
            },
            new AssistantMessageEvent
            {
                Uuid = "assistant-1",
                SessionId = "session-1",
                Message = new AssistantMessage
                {
                    Id = "msg-1",
                    Model = "claude-test",
                    Role = "assistant",
                    Content = [new ContentBlock { Type = "text", Text = "assistant survived stream event" }],
                },
            },
        };

        var messages = events
            .SelectMany(e => client.ConvertNonTerminalJsonlEventToMessages(e, emitSystemInit: false))
            .ToList();

        var text = Assert.Single(messages.OfType<TextMessage>());
        Assert.Equal("assistant survived stream event", text.Text);
    }
}
