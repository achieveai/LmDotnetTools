using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers what the directory promises: admission is all-or-nothing, a canonical identifier always wins
/// over a name, a contested name is granted to exactly one agent with the rest suffixed, and an agent
/// that has left stays visible without staying addressable.
/// </summary>
/// <remarks>
/// Names are the hazard these tests are built around. A name is a convenience for a model choosing whom
/// to talk to, but it is not identity: silently retargeting a contested name would deliver one agent's
/// reply to a different agent. Refusing to resolve it is safer than that, but it is not the answer
/// either — it costs BOTH agents an address they could have had. So the newcomer is renamed rather than
/// the name being contested, and only names shared by DEAD agents, where there is nothing left to
/// rename, still resolve to nothing.
/// </remarks>
public class AgentCollaborationDirectoryTests
{
    private const string CollaborationId = "collab-1";

    private static AgentCollaborationDirectory CreateDirectory(AgentCollaborationOptions? options = null)
    {
        return new AgentCollaborationDirectory(CollaborationId, options ?? new AgentCollaborationOptions());
    }

    private static AgentCollaborationContext RegisterRoot(AgentCollaborationDirectory directory)
    {
        var root = AgentCollaborationContext.ForRoot(CollaborationId, "agent-root");
        directory.TryRegister(root, "root", "running").Succeeded.Should().BeTrue();
        return root;
    }

    [Fact]
    public void TryRegister_AdmitsRoot_DescribingItStructurally()
    {
        var directory = CreateDirectory();

        var result = directory.TryRegister(
            AgentCollaborationContext.ForRoot(CollaborationId, "agent-root"),
            "root",
            "running"
        );

        // A root supplies no role or description, so it is described by what it is rather than being
        // forced to invent metadata nobody chooses between.
        result.Succeeded.Should().BeTrue();
        result.Entry!.Role.Should().Be(nameof(AgentKind.Root));
        result.Entry.Description.Should().Be(nameof(AgentKind.Root));
        result.Entry.IsLive.Should().BeTrue();
    }

    [Fact]
    public void TryRegister_AdmitsChild_CarryingItsAncestryAndDepths()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);

        var child = root.CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews diffs");
        var entry = directory.TryRegister(child, "reviewer", "queued", agentType: "code-reviewer").Entry!;

        entry.AncestorAgentIds.Should().Equal("agent-root");
        entry.StructuralDepth.Should().Be(1);
        entry.DelegationDepth.Should().Be(1);
        entry.AgentType.Should().Be("code-reviewer");
        entry.Status.Should().Be("queued");
    }

    [Fact]
    public void TryRegister_RefusesDuplicateIdentifier()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        var child = root.CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews diffs");
        _ = directory.TryRegister(child, "reviewer", "running");

        var second = directory.TryRegister(child, "reviewer-again", "running");

        second.Succeeded.Should().BeFalse();
        second.FailureCode.Should().Be(AgentDirectoryFailureCodes.DuplicateAgentId);
        directory.Count.Should().Be(2);
    }

    [Fact]
    public void TryRegister_RefusesAgentFromAnotherCollaboration()
    {
        var directory = CreateDirectory();
        var foreign = AgentCollaborationContext.ForRoot("collab-other", "agent-root");

        directory
            .TryRegister(foreign, "root", "running")
            .FailureCode.Should()
            .Be(AgentDirectoryFailureCodes.CrossCollaboration);
    }

    [Fact]
    public void TryRegister_RefusesUnknownParent_LeavingNothingHalfRegistered()
    {
        var directory = CreateDirectory();
        var orphanParent = AgentCollaborationContext.ForRoot(CollaborationId, "agent-ghost");
        var orphan = orphanParent.CreateChild("agent-a", AgentKind.SubAgent, "r", "d");

        var result = directory.TryRegister(orphan, "reviewer", "running");

        result.FailureCode.Should().Be(AgentDirectoryFailureCodes.UnknownParent);
        directory.Count.Should().Be(0);
        directory.FindById("agent-a").Should().BeNull();
    }

    [Fact]
    public void TryRegister_RefusesBeyondTheConfiguredDelegationDepth()
    {
        var directory = CreateDirectory(new AgentCollaborationOptions { MaxDelegationDepth = 1 });
        var root = RegisterRoot(directory);
        var child = root.CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews diffs");
        _ = directory.TryRegister(child, "reviewer", "running");

        var grandchild = child.CreateChild("agent-b", AgentKind.SubAgent, "tester", "runs tests");

        directory
            .TryRegister(grandchild, "tester", "running")
            .FailureCode.Should()
            .Be(AgentDirectoryFailureCodes.DepthLimit);
    }

    [Fact]
    public void TryRegister_AdmitsBeyondDelegationDepth_ThroughAZeroCostController()
    {
        var directory = CreateDirectory(new AgentCollaborationOptions { MaxDelegationDepth = 1 });
        var root = RegisterRoot(directory);

        var controller = root.CreateChild("agent-ctl", AgentKind.WorkflowController, "controller", "orchestrates");
        _ = directory.TryRegister(controller, "controller", "running");
        var worker = controller.CreateChild("agent-w", AgentKind.WorkflowDelegate, "worker", "works");

        // Structurally deeper than the ordinary case above, but the same delegation cost, so the
        // budget is spent on work rather than on orchestration.
        var result = directory.TryRegister(worker, "worker", "running");
        result.Succeeded.Should().BeTrue();
        result.Entry!.StructuralDepth.Should().Be(2);
        result.Entry.DelegationDepth.Should().Be(1);
    }

    [Fact]
    public void Resolve_PrefersCanonicalIdentifier_OverAColludingName()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        var impostor = root.CreateChild("agent-a", AgentKind.SubAgent, "r", "d");
        var target = root.CreateChild("agent-b", AgentKind.SubAgent, "r", "d");
        _ = directory.TryRegister(impostor, "agent-b", "running");
        _ = directory.TryRegister(target, "target", "running");

        // "agent-b" is one agent's identifier and another agent's name; identity wins, so a name can
        // never shadow the agent it was chosen to impersonate.
        directory.Resolve("agent-b").Entry!.AgentId.Should().Be("agent-b");
    }

    [Fact]
    public void TryRegister_WithANameAnotherAgentHolds_GrantsASuffixedNameInsteadOfContestingIt()
    {
        // Mutation that must go red: binding the requested name instead of the granted one.
        // Production already hit the old behaviour: 'finance-controller' named two agents and the task
        // board could only tell the model to "pass the agent id instead".
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "running");

        var second = directory.TryRegister(
            root.CreateChild("agent-2", AgentKind.SubAgent, "r", "d"),
            "reviewer",
            "running"
        );

        second.Succeeded.Should().BeTrue();
        second.Entry!.Name.Should().Be("reviewer-2", "the suffix is the agent's own ordinal");
    }

    [Fact]
    public void Resolve_AfterASuffixedRegistration_AddressesEachAgentUnambiguously()
    {
        // The point of suffixing rather than latching: BOTH agents stay reachable by name. The old
        // policy left neither reachable, permanently, even after one of them left.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "running");
        _ = directory.TryRegister(root.CreateChild("agent-2", AgentKind.SubAgent, "r", "d"), "reviewer", "running");

        directory.Resolve("reviewer").Entry!.AgentId.Should().Be("agent-1");
        directory.Resolve("reviewer-2").Entry!.AgentId.Should().Be("agent-2");

        // And the first agent leaving does not brick either name.
        _ = directory.TryMarkRetained("agent-1");
        directory.Resolve("reviewer-2").Entry!.AgentId.Should().Be("agent-2");
    }

    [Fact]
    public void TryRegister_WithANameARetiredAgentHolds_StillSuffixes()
    {
        // A retired agent's entry outlives it so a sender learns its target ENDED rather than that it
        // never existed. Reusing its name would turn that answer into a silent redirect.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "running");
        directory.TryMarkRetained("agent-1").Should().BeTrue();

        var second = directory.TryRegister(
            root.CreateChild("agent-2", AgentKind.SubAgent, "r", "d"),
            "reviewer",
            "running"
        );

        second.Entry!.Name.Should().Be("reviewer-2");
        directory.Resolve("reviewer").Entry!.AgentId.Should().Be("agent-1");
        directory.Resolve("reviewer").Entry!.IsLive.Should().BeFalse();
    }

    [Fact]
    public void TryRegister_WhenTheSuffixedNameIsAlsoTaken_KeepsGoing()
    {
        // The pathological case: a model literally named an earlier agent "reviewer-2".
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "running");
        _ = directory.TryRegister(root.CreateChild("agent-3", AgentKind.SubAgent, "r", "d"), "reviewer-2", "running");

        var second = directory.TryRegister(
            root.CreateChild("agent-2", AgentKind.SubAgent, "r", "d"),
            "reviewer",
            "running"
        );

        second.Entry!.Name.Should().Be("reviewer-2-1");
        directory.Resolve("reviewer-2").Entry!.AgentId.Should().Be("agent-3");
    }

    [Fact]
    public void TryRegister_WithANameShapedLikeAnotherAgentsId_GrantsASuffixedNameInstead()
    {
        // Mutation that must go red: dropping the IsOrdinalAgentId guard in GrantAndBindName.
        // Resolve consults ids before names, so agent-1 named "agent-2" was reachable by that name only
        // until agent-2 was minted — from then on every message to "agent-2" went to the newcomer.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);

        var first = directory.TryRegister(
            root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"),
            "agent-2",
            "running"
        );

        first.Succeeded.Should().BeTrue();
        first.Entry!.Name.Should().Be("agent-2-1", "the requested name is taken by the agent that id will denote");

        _ = directory.TryRegister(root.CreateChild("agent-2", AgentKind.SubAgent, "r", "d"), "worker", "running");

        directory.Resolve("agent-2").Entry!.AgentId.Should().Be("agent-2");
        directory
            .Resolve("agent-2-1")
            .Entry!.AgentId.Should()
            .Be("agent-1", "the granted name keeps pointing at its recipient");
    }

    [Fact]
    public void TryRegister_WithTheAgentsOwnIdAsItsName_GrantsItUnsuffixed()
    {
        // The one ordinal-shaped name that is not a lie: an agent asking for its own id is asking for a
        // name it already has, and suffixing it would manufacture a collision with nobody.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);

        var result = directory.TryRegister(
            root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"),
            "agent-1",
            "running"
        );

        result.Entry!.Name.Should().Be("agent-1");
        directory.Resolve("agent-1").Entry!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void TryWithdraw_ForgetsTheAgentAndFreesItsName_WhereRetirementKeepsBoth()
    {
        // Mutation that must go red: TryWithdraw delegating to TryMarkRetained. An agent whose spawn threw
        // before its first turn was never told its name and never addressed, so keeping its reservation
        // only suffixed the next spawn that asked for the same name (a retry, typically).
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "queued");

        directory.TryWithdraw("agent-1").Should().BeTrue();

        directory.FindById("agent-1").Should().BeNull("a withdrawn agent leaves no entry");
        directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);

        var retry = directory.TryRegister(
            root.CreateChild("agent-2", AgentKind.SubAgent, "r", "d"),
            "reviewer",
            "running"
        );
        retry.Entry!.Name.Should().Be("reviewer", "the name the failed spawn held is free again");
        directory.Resolve("reviewer").Entry!.AgentId.Should().Be("agent-2");
    }

    [Fact]
    public void TryWithdraw_RefusesAnAgentThatRanAndRetired_AndAnUnknownOne()
    {
        // Withdrawal is for an agent that never existed as far as anyone else knows. One that ran and
        // retired has been seen, named and possibly messaged; forgetting it would turn "FINISHED" into
        // "never existed" for every later sender.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "running");
        _ = directory.TryMarkRetained("agent-1");

        directory.TryWithdraw("agent-1").Should().BeFalse();
        directory.TryWithdraw("agent-9").Should().BeFalse();

        directory.FindById("agent-1").Should().NotBeNull();
        directory.Resolve("reviewer").Entry!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void TryRegister_WithANonOrdinalAgentId_SuffixesWithTheIdItself()
    {
        // A root's id is its thread id, not an ordinal. There is no number to borrow, so the id is the
        // only thing guaranteed unique — an ugly name beats a colliding one.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-1", AgentKind.SubAgent, "r", "d"), "reviewer", "running");

        var second = directory.TryRegister(
            root.CreateChild("thread-xyz", AgentKind.SubAgent, "r", "d"),
            "reviewer",
            "running"
        );

        second.Entry!.Name.Should().Be("reviewer-thread-xyz");
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("primary")]
    public void Resolve_PrimaryAlias_PreservesTheRootsNameAndIdentity(string name)
    {
        var directory = CreateDirectory();
        var root = AgentCollaborationContext.ForRoot(CollaborationId, "agent-root");
        directory.TryRegister(root, name, "running").Succeeded.Should().BeTrue();

        directory.Resolve("primary").Entry.Should().NotBeNull();
        directory.Resolve("primary").Entry!.AgentId.Should().Be("agent-root");
        directory.Resolve("primary").Entry!.Name.Should().Be(name);
        directory.Resolve(name).Entry!.AgentId.Should().Be("agent-root");
        directory.Resolve("lead").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);

        directory.TryMarkRetained("agent-root").Should().BeTrue();
        directory.Resolve("primary").Entry!.IsLive.Should().BeFalse();
    }

    [Fact]
    public void Resolve_AChildAskingForThePrimaryAlias_DoesNotTakeItFromTheRoot()
    {
        // `primary` is the one name every agent can rely on to reach the top of the conversation, so a
        // child claiming it used to brick the alias for the whole hierarchy. The child is suffixed
        // instead, and the alias keeps pointing where it always did.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);

        var child = directory.TryRegister(
            root.CreateChild("child", AgentKind.SubAgent, "r", "d"),
            "primary",
            "running"
        );

        child.Entry!.Name.Should().Be("primary-child");
        directory.Resolve("primary").Entry!.AgentId.Should().Be("agent-root");
        directory.Resolve("primary-child").Entry!.AgentId.Should().Be("child");
        directory.Resolve("agent-root").Entry!.Name.Should().Be("root");
    }

    [Fact]
    public void Resolve_PrimaryAlias_DoesNotShadowACanonicalId()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        directory
            .TryRegister(root.CreateChild("primary", AgentKind.SubAgent, "r", "d"), "worker", "running")
            .Succeeded.Should()
            .BeTrue();

        directory.Resolve("primary").Entry!.Name.Should().Be("worker");
        directory.Resolve("agent-root").Entry!.Name.Should().Be("root");
    }

    [Fact]
    public void Resolve_ReportsNotFound_ForAnUnknownTarget()
    {
        var directory = CreateDirectory();

        directory.Resolve("nobody").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
        directory.Resolve("  ").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
    }

    [Fact]
    public void TryMarkRetained_KeepsAnAgentVisibleAfterItLeaves()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-a", AgentKind.SubAgent, "r", "d"), "reviewer", "running");

        directory.TryUpdateStatus("agent-a", "completed").Should().BeTrue();
        directory.TryMarkRetained("agent-a").Should().BeTrue();

        // A sender holding an open question needs to learn what became of its target; an entry that
        // vanished would be indistinguishable from one that never existed.
        var entry = directory.Resolve("reviewer").Entry!;
        entry.Status.Should().Be("completed");
        entry.IsTerminal.Should().BeTrue();
        entry.IsLive.Should().BeFalse();
    }

    [Fact]
    public void TryUpdateStatus_ReportsFailure_ForAnUnknownAgent()
    {
        var directory = CreateDirectory();

        directory.TryUpdateStatus("nobody", "running").Should().BeFalse();
        directory.TryMarkRetained("nobody").Should().BeFalse();
    }

    [Fact]
    public void Snapshot_IsOrderedByIdentifier_SoListingsAreStable()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        foreach (var id in new[] { "agent-c", "agent-a", "agent-b" })
        {
            _ = directory.TryRegister(root.CreateChild(id, AgentKind.SubAgent, "r", "d"), id, "running");
        }

        directory
            .Snapshot()
            .Select(entry => entry.AgentId)
            .Should()
            .Equal("agent-a", "agent-b", "agent-c", "agent-root");
    }

    [Fact]
    public void GetInbox_IsSizedByOptions_AndScopedToOneAgent()
    {
        var directory = CreateDirectory(new AgentCollaborationOptions { MaxInboxMessages = 2 });
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-a", AgentKind.SubAgent, "r", "d"), "reviewer", "running");

        var inbox = directory.GetInbox("agent-a")!;
        inbox.Capacity.Should().Be(2);
        directory.GetInbox("agent-root").Should().NotBeSameAs(inbox);
        directory.GetInbox("nobody").Should().BeNull();
    }

    [Fact]
    public void Capacity_IsSharedAcrossTheWholeCollaboration()
    {
        var directory = CreateDirectory(new AgentCollaborationOptions { MaxTotalAgents = 1 });

        directory.TryAcquireCapacity("agent-a").Should().NotBeNull();
        // The permit is taken before any per-manager gate, so a second branch of the hierarchy sees
        // the cap that its own gate could never have known about.
        directory.TryAcquireCapacity("agent-b").Should().BeNull();
    }
}
