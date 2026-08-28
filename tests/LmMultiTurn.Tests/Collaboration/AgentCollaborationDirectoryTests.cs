using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers what the directory promises: admission is all-or-nothing, a canonical identifier always wins
/// over a name, a contested name resolves to nothing rather than to a guess, and an agent that has
/// left stays visible without staying addressable.
/// </summary>
/// <remarks>
/// Names are the hazard these tests are built around. A name is a convenience for a model choosing whom
/// to talk to, but it is not identity: silently retargeting a contested name would deliver one agent's
/// reply to a different agent, which is worse than refusing to resolve it at all.
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
    public void Resolve_RefusesAContestedName_PermanentlyAndWithoutGuessing()
    {
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        _ = directory.TryRegister(root.CreateChild("agent-a", AgentKind.SubAgent, "r", "d"), "reviewer", "running");
        _ = directory.TryRegister(root.CreateChild("agent-b", AgentKind.SubAgent, "r", "d"), "reviewer", "running");

        directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.AmbiguousName);

        // Latching, not toggling: once two agents have answered to a name, a sender still cannot know
        // which one it meant even after one of them leaves.
        _ = directory.TryMarkRetained("agent-b");
        directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.AmbiguousName);
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
