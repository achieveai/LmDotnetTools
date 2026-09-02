using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     An <see cref="AgentMessage"/> must map to a Responses <c>input_text</c> message item carrying its
///     envelope. The mapper's <c>default:</c> arm silently drops unknown types, so an explicit case is
///     required or a typed cross-agent message never reaches the model on this backend (#688).
/// </summary>
public sealed class AgentMessageMappingTests
{
    [Fact]
    public void AgentMessage_MapsTo_UserInputTextMessage_WithEnvelope()
    {
        var agentMessage = AgentMessage.Create(
            messageId: "msg-1",
            AgentMessageType.Steer,
            fromAgentId: "agent-parent",
            fromName: "parent",
            body: "please focus on the tests"
        );

        var request = MessageMapper.BuildRequest([agentMessage], options: null);

        request.Input.Should().HaveCount(1);
        var item = request.Input[0];
        item.Type.Should().Be("message");
        item.Role.Should().Be("user");
        item.Content.Should().NotBeNull();
        item.Content![0].Type.Should().Be("input_text");
        item.Content[0].Text.Should().Contain("<agent-message").And.Contain("please focus on the tests");
    }
}
