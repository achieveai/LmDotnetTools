using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// The truth table for the collaboration's only real disclosure boundary: who may read another agent's
/// transcript.
/// </summary>
/// <remarks>
/// Contact and read are separate privileges. Every member may address every other member, because that
/// is what makes peer collaboration work; reading is narrower, because a peer's transcript can contain
/// reasoning and content its own caller never intended to publish. These tests pin that separation, and
/// pin that widening it is only ever an explicit configuration choice.
/// </remarks>
public class TranscriptVisibilityPolicyTests
{
    private static AgentDirectoryEntry Entry(
        string agentId,
        string collaborationId = "collab-1",
        params string[] ancestors
    )
    {
        return new AgentDirectoryEntry
        {
            AgentId = agentId,
            CollaborationId = collaborationId,
            Name = agentId,
            AncestorAgentIds = [.. ancestors],
            Kind = AgentKind.SubAgent,
            Role = "role",
            Description = "description",
            Status = "running",
        };
    }

    [Theory]
    [InlineData(TranscriptVisibilityMode.Ancestors)]
    [InlineData(TranscriptVisibilityMode.Open)]
    public void AnAgentMayAlwaysReadItself(TranscriptVisibilityMode mode)
    {
        var self = Entry("agent-a");

        var decision = TranscriptVisibilityPolicy.Evaluate(self, self, mode);

        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().Be(TranscriptAccessReasons.Self);
    }

    [Theory]
    [InlineData(TranscriptVisibilityMode.Ancestors)]
    [InlineData(TranscriptVisibilityMode.Open)]
    public void AnAncestorMayReadADescendant_AtAnyDistance(TranscriptVisibilityMode mode)
    {
        var root = Entry("agent-root");
        var grandchild = Entry("agent-b", "collab-1", "agent-root", "agent-a");

        var decision = TranscriptVisibilityPolicy.Evaluate(root, grandchild, mode);

        // The chain above a target already sees its material through its own children, so reading it
        // directly discloses nothing new.
        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().Be(TranscriptAccessReasons.Ancestor);
    }

    [Fact]
    public void ASiblingMayNotRead_UnderTheDefaultMode()
    {
        var sibling = Entry("agent-a", "collab-1", "agent-root");
        var target = Entry("agent-b", "collab-1", "agent-root");

        var decision = TranscriptVisibilityPolicy.Evaluate(
            sibling,
            target,
            TranscriptVisibilityMode.Ancestors
        );

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(TranscriptAccessReasons.NotAnAncestor);
    }

    [Fact]
    public void ADescendantMayNotReadItsAncestor_UnderTheDefaultMode()
    {
        // Ancestry is directional. A child seeing its parent's transcript would expose the parent's
        // other branches, which the child was never party to.
        var child = Entry("agent-a", "collab-1", "agent-root");
        var parent = Entry("agent-root");

        TranscriptVisibilityPolicy
            .Evaluate(child, parent, TranscriptVisibilityMode.Ancestors)
            .IsAllowed.Should()
            .BeFalse();
    }

    [Fact]
    public void ASiblingMayRead_OnlyWhenTheCollaborationIsExplicitlyOpen()
    {
        var sibling = Entry("agent-a", "collab-1", "agent-root");
        var target = Entry("agent-b", "collab-1", "agent-root");

        var decision = TranscriptVisibilityPolicy.Evaluate(
            sibling,
            target,
            TranscriptVisibilityMode.Open
        );

        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().Be(TranscriptAccessReasons.OpenCollaboration);
    }

    [Theory]
    [InlineData(TranscriptVisibilityMode.Ancestors)]
    [InlineData(TranscriptVisibilityMode.Open)]
    public void CrossCollaborationIsRefused_EvenWhenAncestryWouldOtherwiseAllowIt(
        TranscriptVisibilityMode mode
    )
    {
        // Checked before anything that could allow: a shared identifier across two collaborations must
        // never be enough to read across the boundary between them, and Open widens one collaboration
        // rather than all of them.
        var reader = Entry("agent-root");
        var target = Entry("agent-b", "collab-2", "agent-root");

        var decision = TranscriptVisibilityPolicy.Evaluate(reader, target, mode);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(TranscriptAccessReasons.CrossCollaboration);
    }

    [Theory]
    [InlineData(TranscriptVisibilityMode.Ancestors)]
    [InlineData(TranscriptVisibilityMode.Open)]
    public void AnUnregisteredReaderOrTargetIsDenied_RatherThanTreatedAsAnError(
        TranscriptVisibilityMode mode
    )
    {
        // The caller is frequently a model-driven tool invocation carrying an arbitrary string, so "no"
        // is the honest and safe answer to a question about an agent that does not exist.
        var known = Entry("agent-a");

        TranscriptVisibilityPolicy
            .Evaluate(null, known, mode)
            .Reason.Should()
            .Be(TranscriptAccessReasons.UnknownReader);
        TranscriptVisibilityPolicy
            .Evaluate(known, null, mode)
            .Reason.Should()
            .Be(TranscriptAccessReasons.UnknownTarget);
    }

    [Fact]
    public void Reasons_AreContentFree_SoADenialIsSafeToSurfaceAndLog()
    {
        var target = Entry("agent-b", "collab-1", "agent-root");
        var reader = Entry("agent-a", "collab-1", "agent-root");

        var decision = TranscriptVisibilityPolicy.Evaluate(
            reader,
            target,
            TranscriptVisibilityMode.Ancestors
        );

        decision.Reason.Should().NotContain(target.Role).And.NotContain(target.Description);
    }
}
