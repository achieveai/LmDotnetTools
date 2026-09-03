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

    /// <summary>
    ///     The state after a restart: a fresh collaboration for the same root, holding only the live
    ///     root, into which the previous process's agent rows have been reconciled as tombstones.
    /// </summary>
    private static AgentCollaborationSetup RestartedRootWithTombstonedAgentOne(string rootThreadId, string agentName)
    {
        var previous = RootHoldingAgentOne(rootThreadId, agentName);
        var persisted = previous.Directory.SnapshotRecords();

        var restarted = AgentCollaborationSetup.CreateRoot(
            new AgentCollaborationOptions(),
            collaborationId: rootThreadId,
            agentId: rootThreadId,
            name: "conversation"
        );
        restarted.Directory.TryRegister(restarted.Context, "conversation", "running").Succeeded.Should().BeTrue();

        foreach (var record in persisted.Where(r => !string.Equals(r.AgentId, rootThreadId, StringComparison.Ordinal)))
        {
            restarted.Directory.MarkInvalidated(record).Should().BeTrue();
        }

        return restarted;
    }

    [Fact]
    public void AnAgentLostToARestart_IsUnreachableRatherThanUnknown()
    {
        // #676's tombstone seam: Resolve answers target_not_live, and the board has to hear "gone",
        // not "never existed" — the two lead to different recovery actions.
        var restarted = RestartedRootWithTombstonedAgentOne(RootA, "alpha");

        var resolved = TodoBoardIdentityWiring.Resolve(restarted.Directory, RootA, SubAgentThreadIds.AgentIdFor(1));

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unreachable);
    }

    [Fact]
    public void AnAgentLostToARestart_StillCarriesACanonicalIdentity()
    {
        // A tombstone resolves with a NULL directory entry, so the identity has to be asked for
        // separately. Resolving by DISPLAY NAME is the case that discriminates: answering with the
        // target would key this agent under "alpha" after a restart and under agent-1 before one, and
        // answering with nothing would make the board refuse an agent it can name.
        var restarted = RestartedRootWithTombstonedAgentOne(RootA, "alpha");

        var resolved = TodoBoardIdentityWiring.Resolve(restarted.Directory, RootA, "alpha");

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unreachable);
        resolved.CanonicalName.Should().Be(SubAgentThreadIds.AgentIdFor(1));
    }

    [Fact]
    public void ANameSharedByTwoAgentsLostToARestart_IsRefusedRatherThanGuessed()
    {
        // The one path #676 opens that has no single answer: the name is ambiguous among tombstones, so
        // the directory reports AmbiguousName with no live entry behind it and the candidate listing —
        // which reads live registrations — comes back empty. The refusal must survive an empty list.
        var previous = RootHoldingAgentOne(RootA, "alpha");
        var second = previous.Context.CreateChild(
            SubAgentThreadIds.AgentIdFor(2),
            AgentKind.SubAgent,
            "worker",
            "does the work"
        );
        previous.Directory.TryRegister(second, "alpha", "running").Succeeded.Should().BeTrue();

        var restarted = AgentCollaborationSetup.CreateRoot(
            new AgentCollaborationOptions(),
            collaborationId: RootA,
            agentId: RootA,
            name: "conversation"
        );
        restarted.Directory.TryRegister(restarted.Context, "conversation", "running").Succeeded.Should().BeTrue();
        foreach (
            var record in previous
                .Directory.SnapshotRecords()
                .Where(r => !string.Equals(r.AgentId, RootA, StringComparison.Ordinal))
        )
        {
            restarted.Directory.MarkInvalidated(record).Should().BeTrue();
        }

        var board = new TaskManager();
        _ = board.AddTask("Wire the SSE endpoint");
        TodoBoardIdentityWiring.Attach(board, restarted, RootA);

        var result = board.ClaimTask("1", "alpha");

        result.IsError.Should().BeTrue();
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void ARestartTombstoneInAnotherRoot_DoesNotMakeThisRootsAgentUnreachable()
    {
        // Scoping still applies to tombstones: MarkInvalidated keys on (collaboration, agent id), and
        // the reference carrying another root's scope must not reach this root's live agent-1 either.
        var rootA = RootHoldingAgentOne(RootA, "alpha");
        _ = RestartedRootWithTombstonedAgentOne(RootB, "beta");

        var resolved = TodoBoardIdentityWiring.Resolve(
            rootA.Directory,
            RootA,
            SubAgentThreadIds.For(RootB, SubAgentThreadIds.AgentIdFor(1))
        );

        resolved.Liveness.Should().Be(TaskManager.AssigneeLiveness.Unknown);
        resolved.AgentId.Should().BeNull();
    }

    [Fact]
    public void AttachedToABoard_AnAgentLostToARestartOwnsItsClaimUnderItsCanonicalId()
    {
        var restarted = RestartedRootWithTombstonedAgentOne(RootA, "alpha");
        var board = new TaskManager();
        _ = board.AddTask("Wire the SSE endpoint");
        TodoBoardIdentityWiring.Attach(board, restarted, RootA);

        var result = board.ClaimTask("1", SubAgentThreadIds.AgentIdFor(1));

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be(SubAgentThreadIds.AgentIdFor(1));
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
