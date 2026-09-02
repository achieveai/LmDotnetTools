using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

/// <summary>
///     OpenAI chat maps an <see cref="AgentMessage"/> for free via its <c>ICanGetText</c> path — no
///     bespoke case is added. This regression pins that the envelope still reaches the request as a
///     user message, so the "no silent drops on any backend" guarantee (#688) holds here too.
/// </summary>
public class AgentMessageMappingTests
{
    [Fact]
    public void AgentMessage_MapsToUserMessage_ViaICanGetText()
    {
        var agentMessage = AgentMessage.Create(
            messageId: "msg-1",
            AgentMessageType.Steer,
            fromAgentId: "agent-parent",
            fromName: "parent",
            body: "please focus on the tests"
        );

        var chatMessages = ChatCompletionRequest.FromMessage(agentMessage).ToList();

        var msg = Assert.Single(chatMessages);
        Assert.Equal(RoleEnum.User, msg.Role);
        var content = msg.Content!.Get<string>();
        Assert.Contains("<agent-message", content);
        Assert.Contains("please focus on the tests", content);
    }
}
