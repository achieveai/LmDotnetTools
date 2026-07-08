using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     A <see cref="NotifyMessage"/> must map to a Responses <c>input_text</c> message item carrying its
///     envelope. The mapper's <c>default:</c> arm silently drops unknown types, so an explicit case is
///     required or the notification never reaches the model on this backend.
/// </summary>
public sealed class NotifyMessageMappingTests
{
    [Fact]
    public void NotifyMessage_MapsTo_UserInputTextMessage_WithEnvelope()
    {
        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion, detail: "done", sourceToolName: "Agent", sourceToolCallId: "c1");

        var request = MessageMapper.BuildRequest([notify], options: null);

        request.Input.Should().HaveCount(1);
        var item = request.Input[0];
        item.Type.Should().Be("message");
        item.Role.Should().Be("user");
        item.Content.Should().NotBeNull();
        item.Content![0].Type.Should().Be("input_text");
        item.Content[0].Text.Should().Contain("<notification").And.Contain("subagent-completion");
    }
}
