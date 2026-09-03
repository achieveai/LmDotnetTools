using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     #672 + #705: agent identifiers are ordinals handed out per ROOT conversation, so <c>agent-1</c>
///     names a different agent in every conversation. The board's assignee resolver therefore scopes
///     its lookup to the root it was attached to: a reference carrying another conversation's scope
///     resolves to nothing rather than to this conversation's agent of the same number.
/// </summary>
/// <remarks>
///     Every scope here is derived through <see cref="SubAgentThreadIds" />, never by writing the
///     <c>subagent-{scope}-agent-N</c> shape out by hand — the point of the rule is that one type owns
///     that shape.
/// </remarks>
public sealed class TodoBoardIdentityWiringTests
{
    private const string RootA = "conv-a";
    private const string RootB = "conv-b";

    /// <summary>A collaboration rooted at <paramref name="rootThreadId"/> holding one named agent-1.</summary>
    private static AgentCollaborationSetup RootHoldingAgentOne(string rootThreadId, string agentName)
    {
        var setup = AgentCollaborationSetup.CreateRoot(
            new AgentCollaborationOptions(),
            collaborationId: rootThreadId,
            agentId: rootThreadId,
            name: "conversation"
        );

        setup.Directory.TryRegister(setup.Context, "conversation", "running").Succeeded.Should().BeTrue();

        var agentId = SubAgentThreadIds.AgentIdFor(1);
        var child = setup.Context.CreateChild(agentId, AgentKind.SubAgent, "worker", "does the work");
        setup.Directory.TryRegister(child, agentName, "running").Succeeded.Should().BeTrue();
        return setup;
    }

    [Fact]
    public void AReferenceScopedToAnotherRoot_DoesNotResolveToThisRootsAgentOfTheSameNumber()
    {
        // The defect this whole file exists for: two live conversations each have an agent-1, and the
        // only thing telling them apart is the scope segment of the transcript thread id.
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        _ = RootHoldingAgentOne(RootB, "beta");

        var otherRootsAgentOne = SubAgentThreadIds.For(RootB, SubAgentThreadIds.AgentIdFor(1));

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, otherRootsAgentOne);

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unknown);
        resolved.AgentId.Should().BeNull();
    }

    [Fact]
    public void AReferenceScopedToThisRoot_ResolvesToItsAgent()
    {
        // The other half of the same claim: scoping must refuse the foreign reference WITHOUT refusing
        // the local one, or "always Unknown" would pass the test above.
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        var ownAgentOne = SubAgentThreadIds.For(RootA, SubAgentThreadIds.AgentIdFor(1));

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, ownAgentOne);

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Live);
        resolved.AgentId.Should().Be(SubAgentThreadIds.AgentIdFor(1));
    }

    [Fact]
    public void ABareOrdinalResolvesWithinTheAttachedRoot()
    {
        var rootA = RootHoldingAgentOne(RootA, "alpha");

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, SubAgentThreadIds.AgentIdFor(1));

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Live);
        resolved.CanonicalName.Should().Be(SubAgentThreadIds.AgentIdFor(1));
    }

    [Fact]
    public void ANameBelongingToAnotherRoot_IsUnknown()
    {
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        _ = RootHoldingAgentOne(RootB, "beta");

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, "beta");

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unknown);
    }

    [Fact]
    public void ADisplayNameResolvesToTheCanonicalIdentifier()
    {
        var rootA = RootHoldingAgentOne(RootA, "alpha");

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, "alpha");

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Live);
        resolved.CanonicalName.Should().Be(SubAgentThreadIds.AgentIdFor(1));
    }

    [Fact]
    public void ARetainedAgentIsUnreachableRatherThanUnknown()
    {
        // A stopped agent still owns what it claimed; "gone" and "never existed" are different answers.
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        rootA.Directory.TryMarkRetained(SubAgentThreadIds.AgentIdFor(1)).Should().BeTrue();

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, "alpha");

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unreachable);
        resolved.AgentId.Should().Be(SubAgentThreadIds.AgentIdFor(1));
    }

    [Fact]
    public void AContestedNameReportsEveryCandidate()
    {
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        var second = rootA.Context.CreateChild(
            SubAgentThreadIds.AgentIdFor(2),
            AgentKind.SubAgent,
            "worker",
            "does the work"
        );
        rootA.Directory.TryRegister(second, "alpha", "running").Succeeded.Should().BeTrue();

        var resolved = TodoBoardIdentityWiring.Resolve(rootA.Directory, RootA, "alpha");

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unknown);
        resolved.Candidates.Should().Equal(SubAgentThreadIds.AgentIdFor(1), SubAgentThreadIds.AgentIdFor(2));
    }

    [Fact]
    public void AttachedToABoard_AForeignAgentCannotClaimATask()
    {
        // The end-to-end shape of the guarantee: cross-conversation scoping reaches the tool refusal,
        // not just the resolver.
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        _ = RootHoldingAgentOne(RootB, "beta");

        var board = new TaskManager();
        _ = board.AddTask("Wire the SSE endpoint");
        TodoBoardIdentityWiring.Attach(board, rootA, RootA);

        var foreign = board.ClaimTask("1", SubAgentThreadIds.For(RootB, SubAgentThreadIds.AgentIdFor(1)));
        foreign.IsError.Should().BeTrue();
        foreign.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();

        var own = board.ClaimTask("1", SubAgentThreadIds.For(RootA, SubAgentThreadIds.AgentIdFor(1)));
        own.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be(SubAgentThreadIds.AgentIdFor(1));
    }
}
