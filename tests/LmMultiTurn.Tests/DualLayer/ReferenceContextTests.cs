using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// The block the executor reads for reference. Each message must say where it came from, so the
/// executor can tell the user's words from a sub-agent's notice or a peer's message.
/// </summary>
public class ReferenceContextTests
{
    [Fact]
    public void Each_message_is_labelled_with_its_origin_inside_the_reference_tag()
    {
        var block = ReferenceContext.Render([
            new TextMessage { Role = Role.User, Text = "Please tidy the README." },
            NotifyMessage.Create(NotifyKinds.SubAgentCompletion, detail: "child finished", label: "linter"),
            new AgentMessage
            {
                MessageId = "m1",
                AgentMessageType = AgentMessageType.Question,
                FromAgentId = "agent-7",
                FromName = "reviewer",
                Body = "Which branch?",
            },
        ]);

        block
            .Should()
            .StartWith("<conversation-context>\nWhat the planner received since your last call. For reference only")
            .And.Contain("\n[user] Please tidy the README.")
            .And.Contain("\n[notification subagent-completion \"linter\"] child finished")
            .And.Contain("\n[message from agent reviewer (agent-7)] Which branch?")
            .And.EndWith("\n</conversation-context>");
    }

    [Fact]
    public void A_notification_without_a_label_has_no_empty_quotes()
    {
        ReferenceContext
            .Render([NotifyMessage.Create(NotifyKinds.WorkflowCompletion, detail: "run 3 done")])
            .Should()
            .Contain("[notification workflow-completion] run 3 done");
    }
}
