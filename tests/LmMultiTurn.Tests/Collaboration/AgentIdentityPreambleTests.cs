using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// An agent that is never told who it is cannot tell a peer how to reach it. Production logs show
/// models addressing peers as 'lead', 'parent' and 'manager' — role words, never the ordinal — so
/// the name has to arrive in the prompt, not only in a GetAgents result the agent may never call.
/// </summary>
public class AgentIdentityPreambleTests
{
    [Fact]
    public void Compose_WithNoCollaboration_ReturnsNull()
    {
        AgentIdentityPreamble.Compose(null).Should().BeNull();
    }

    [Fact]
    public void Compose_ForARoot_NamesTheAgentAndOmitsAParent()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        var preamble = AgentIdentityPreamble.Compose(setup);

        preamble.Should().NotBeNull();
        preamble!.Should().Contain("MainAgent").And.Contain(setup.AgentId);
        preamble.Should().NotContain("You report to");
    }

    [Fact]
    public void Compose_ForAChild_NamesItsParentByName()
    {
        // The parent's NAME, not its id: the whole point is that the child can address it.
        var root = RegisteredRoot();

        var child = root.ForChild(ChildContextOf(root, "agent-1"), "reviewer");

        var preamble = AgentIdentityPreamble.Compose(child);

        preamble.Should().Contain("reviewer").And.Contain("agent-1");
        preamble.Should().Contain("You report to `MainAgent`");
    }

    [Fact]
    public void Compose_ForAChildWhoseParentIsNotInTheDirectory_OmitsTheParentClause()
    {
        // A wrong address is worse than a missing sentence: an id here would be a handle the model
        // then sends messages to. This is the branch a restart hits before the root re-registers.
        var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        var child = root.ForChild(ChildContextOf(root, "agent-1"), "reviewer");

        AgentIdentityPreamble.Compose(child)!.Should().NotContain("You report to");
    }

    [Fact]
    public void Prepend_PutsTheIdentityBeforeTheCallersPromptAndKeepsIt()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        var composed = AgentIdentityPreamble.Prepend("You are a helpful assistant.", setup);

        composed.Should().StartWith("You are `MainAgent`");
        composed.Should().EndWith("You are a helpful assistant.");
    }

    [Fact]
    public void Prepend_WithNoCollaboration_ReturnsThePromptUnchanged()
    {
        // Mutation that must go red: dropping the null-collaboration guard. A non-collaborating
        // loop must keep byte-identical prompts, or every legacy conversation changes behaviour.
        AgentIdentityPreamble.Prepend("unchanged", null).Should().Be("unchanged");
    }

    [Fact]
    public void Prepend_WithNoCallerPrompt_IsJustTheIdentity()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        AgentIdentityPreamble.Prepend(null, setup).Should().Be(AgentIdentityPreamble.Compose(setup));
    }

    private static AgentCollaborationSetup RegisteredRoot()
    {
        var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = root.Directory.TryRegister(root.Context, root.Name, AgentCollaborationStatuses.Running);
        return root;
    }

    private static AgentCollaborationContext ChildContextOf(AgentCollaborationSetup root, string agentId) =>
        root.Context.CreateChild(agentId, AgentKind.SubAgent, "worker", "Does work.");
}
