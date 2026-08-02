using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The single hierarchy projection every #244 reader shares. These tests pin the four properties the
/// listing, the transcript endpoint, and the transcript tool all depend on: the live directory wins
/// over a retained row, a hierarchy node with no tab still appears, a tab the collaboration knows
/// nothing about is passed through untouched, and the viewer-scoped flags are answered per reader by
/// the trusted policy rather than by anything the caller supplied.
/// </summary>
public sealed class AgentHierarchyProjectionTests
{
    private const string Root = "thread-root";

    private static AgentDirectoryEntry Node(
        string agentId,
        AgentKind kind = AgentKind.SubAgent,
        string? parent = Root,
        IReadOnlyList<string>? ancestors = null,
        int structuralDepth = 1,
        int delegationDepth = 1,
        bool isLive = true,
        string status = AgentCollaborationStatuses.Running) =>
        new()
        {
            AgentId = agentId,
            CollaborationId = Root,
            Name = agentId,
            ParentAgentId = parent,
            AncestorAgentIds = [.. ancestors ?? DefaultAncestors(parent)],
            Kind = kind,
            Role = "role of " + agentId,
            Description = "when to contact " + agentId,
            AgentType = "general-purpose",
            StructuralDepth = structuralDepth,
            DelegationDepth = delegationDepth,
            Status = status,
            IsLive = isLive,
        };

    private static AgentDirectoryEntry RootNode() =>
        Node(Root, AgentKind.Root, parent: null, ancestors: [], structuralDepth: 0, delegationDepth: 0);

    /// <summary>A node one level down has exactly its parent above it.</summary>
    private static IReadOnlyList<string> DefaultAncestors(string? parent) =>
        parent is null ? [] : [parent];

    private static SubAgentSummary Tab(string agentId, string kind = SubAgentSummary.SubAgentTabKind) =>
        new()
        {
            AgentId = agentId,
            Kind = kind,
            Name = agentId,
            Template = "general-purpose",
            Task = "the original spawn prompt",
            Status = AgentCollaborationStatuses.Running,
            ThreadId = $"subagent-{agentId}",
        };

    private static IReadOnlyList<SubAgentSummary> Project(
        IReadOnlyList<SubAgentSummary> tabs,
        IReadOnlyList<AgentDirectoryEntry> nodes,
        string? viewer = Root,
        TranscriptVisibilityMode visibility = TranscriptVisibilityMode.Ancestors) =>
        AgentHierarchyProjection.Project(tabs, nodes, viewer, visibility);

    [Fact]
    public void NoNodes_LeavesEveryTabExactlyAsItWas()
    {
        var tabs = new[] { Tab("a-1"), Tab("a-2") };

        Project(tabs, []).Should().Equal(tabs,
            "a host that never enabled collaboration must see the pre-#244 rows unchanged");
    }

    [Fact]
    public void MatchingNode_EnrichesTheTabWithoutDisturbingItsPresentation()
    {
        var row = Project([Tab("a-1")], [RootNode(), Node("a-1")]).Single();

        row.AgentNodeId.Should().Be("a-1");
        row.CollaborationId.Should().Be(Root);
        row.ParentAgentId.Should().Be(Root);
        row.StructuralDepth.Should().Be(1);
        row.DelegationDepth.Should().Be(1);
        row.Task.Should().Be("the original spawn prompt", "the tab's own task is not replaced by the role");
        row.ThreadId.Should().Be("subagent-a-1");
    }

    [Fact]
    public void NodeWithNoTab_StillAppears()
    {
        // A grandchild owned by a-1's OWN manager: this conversation's loop cannot see it, and the
        // whole point of the shared directory is that it appears anyway.
        var rows = Project(
            [Tab("a-1")],
            [RootNode(), Node("a-1"), Node("g-1", parent: "a-1", ancestors: [Root, "a-1"], structuralDepth: 2, delegationDepth: 2)]);

        rows.Should().HaveCount(2);
        var grandchild = rows.Single(r => r.AgentId == "g-1");
        grandchild.ParentAgentId.Should().Be("a-1");
        grandchild.AncestorAgentIds.Should().Equal(Root, "a-1");
        grandchild.StructuralDepth.Should().Be(2);
        grandchild.ThreadId.Should().Be("subagent-g-1");
    }

    [Fact]
    public void RootNode_IsNeverListedAsOneOfItsOwnChildren()
    {
        Project([], [RootNode()]).Should().BeEmpty(
            "the root is the conversation itself; a tab for it would be self-referential");
    }

    [Fact]
    public void LiveNode_WinsOverTheRetainedRowItMatches()
    {
        // Exactly the restart-then-resume shape: the durable index says the agent is gone, the live
        // directory says it is back. The fresher of the two is the directory.
        var retained = Tab("a-1")
            .WithCollaboration(Node("a-1", isLive: false, status: SubAgentSummary.InterruptedStatus))
            .AsRetained();

        var row = Project([retained], [RootNode(), Node("a-1")]).Single();

        row.IsLive.Should().BeTrue();
        row.Status.Should().Be(SubAgentSummary.InterruptedStatus,
            "liveness comes from the directory; the tab's own status is still the tab's to report");
    }

    [Fact]
    public void RetainedRowWithNoNode_KeepsItsPersistedHierarchyAndStaysNotLive()
    {
        var retained = Tab("a-1").WithCollaboration(Node("a-1")).AsRetained();

        var row = Project([retained], [RootNode()]).Single();

        row.IsLive.Should().BeFalse();
        row.ParentAgentId.Should().Be(Root, "the index remembered where it sat in the tree");
        row.IsReadable.Should().BeTrue("its transcript is on disk and the root is above it");
    }

    [Fact]
    public void WorkflowTab_IsJoinedToItsControllerNode()
    {
        var controllerId = WorkflowCollaboration.ComposeControllerAgentId("w1");
        var rows = Project(
            [Tab("w1", SubAgentSummary.WorkflowTabKind)],
            [
                RootNode(),
                Node(controllerId, AgentKind.WorkflowController, delegationDepth: 0),
                Node("d-1", AgentKind.WorkflowDelegate, parent: controllerId, ancestors: [Root, controllerId], structuralDepth: 2),
            ]);

        var workflow = rows.Single(r => r.Kind == SubAgentSummary.WorkflowTabKind);
        workflow.AgentId.Should().Be("w1", "the tab keeps the id it has always been addressed by");
        workflow.AgentNodeId.Should().Be(controllerId);
        workflow.DelegationDepth.Should().Be(0, "a controller is a structural hop that spends no budget");

        rows.Single(r => r.AgentId == "d-1").ParentAgentId.Should().Be(
            workflow.AgentNodeId, "a delegate must be linkable to the workflow tab above it");
    }

    [Fact]
    public void ControllerNodeWithNoRun_IsNotInvented()
    {
        var controllerId = WorkflowCollaboration.ComposeControllerAgentId("w1");

        Project([], [RootNode(), Node(controllerId, AgentKind.WorkflowController)]).Should().BeEmpty(
            "a controller's tab id and thread belong to the run that produced it, not to a formula");
    }

    [Fact]
    public void ViewerFlags_AreAnsweredForTheReaderThatAsked()
    {
        var nodes = new[] { RootNode(), Node("a-1"), Node("a-2") };

        var asRoot = Project([Tab("a-1"), Tab("a-2")], nodes);
        asRoot.Should().OnlyContain(r => r.IsReadable, "the root is an ancestor of everything");
        asRoot.Should().NotContain(r => r.IsCurrent, "the root has no tab of its own");

        var asChild = Project([Tab("a-1"), Tab("a-2")], nodes, viewer: "a-1");
        asChild.Single(r => r.AgentId == "a-1").IsCurrent.Should().BeTrue();
        asChild.Single(r => r.AgentId == "a-1").IsReadable.Should().BeTrue("an agent may read itself");
        asChild.Single(r => r.AgentId == "a-2").IsReadable.Should().BeFalse(
            "a sibling is neither the target nor above it");
    }

    [Fact]
    public void OpenVisibility_LetsSiblingsRead()
    {
        var rows = Project(
            [Tab("a-2")],
            [RootNode(), Node("a-1"), Node("a-2")],
            viewer: "a-1",
            visibility: TranscriptVisibilityMode.Open);

        rows.Single(r => r.AgentId == "a-2").IsReadable.Should().BeTrue();
    }

    [Fact]
    public void UnknownViewer_ReadsNothing()
    {
        var rows = Project([Tab("a-1")], [RootNode(), Node("a-1")], viewer: "not-in-this-collaboration");

        rows.Single().IsReadable.Should().BeFalse(
            "an unregistered reader is denied rather than treated as an error");
        rows.Single().IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void LegacyTabWithNoNode_MakesNoViewerClaimAtAll()
    {
        var row = Project([Tab("a-1")], [RootNode()]).Single();

        row.IsReadable.Should().BeFalse();
        row.IsCurrent.Should().BeFalse();
        row.CollaborationId.Should().BeNull("nothing in the hierarchy claims this row");
    }

    [Fact]
    public void Find_AcceptsEitherIdentifierARowPublishes()
    {
        var rows = Project(
            [Tab("w1", SubAgentSummary.WorkflowTabKind)],
            [RootNode(), Node(WorkflowCollaboration.ComposeControllerAgentId("w1"), AgentKind.WorkflowController)]);

        AgentHierarchyProjection.Find(rows, "w1").Should().NotBeNull();
        AgentHierarchyProjection.Find(rows, WorkflowCollaboration.ComposeControllerAgentId("w1"))
            .Should().NotBeNull();
        AgentHierarchyProjection.Find(rows, "nobody").Should().BeNull();
    }
}
