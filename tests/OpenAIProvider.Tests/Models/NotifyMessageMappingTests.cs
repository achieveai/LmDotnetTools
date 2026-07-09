using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

/// <summary>
///     OpenAI chat maps a <see cref="NotifyMessage"/> for free via its <c>ICanGetText</c> path — no
///     bespoke case is added. This regression pins that the envelope still reaches the request as a
///     user message (so the "no silent drops on any backend" guarantee holds here too).
/// </summary>
public class NotifyMessageMappingTests
{
    [Fact]
    public void NotifyMessage_MapsToUserMessage_ViaICanGetText()
    {
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion, detail: "done", sourceToolName: "Agent", sourceToolCallId: "c1");

        var chatMessages = ChatCompletionRequest.FromMessage(notify).ToList();

        var msg = Assert.Single(chatMessages);
        Assert.Equal(RoleEnum.User, msg.Role);
        var content = msg.Content!.Get<string>();
        Assert.Contains("<notification", content);
        Assert.Contains("subagent-completion", content);
    }
}
