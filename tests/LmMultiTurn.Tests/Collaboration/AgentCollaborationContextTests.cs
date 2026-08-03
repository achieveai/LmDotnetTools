using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers the hierarchy arithmetic and the persisted projection of it: who is above whom, what a hop
/// costs, what metadata is admissible, and what survives a restart.
/// </summary>
/// <remarks>
/// The distinction defended throughout is between <i>structure</i> and <i>budget</i>. A workflow
/// controller is a real node — it must appear in a listing, and it must be an ancestor for transcript
/// purposes — while costing nothing against the delegation bound. Collapsing the two axes would either
/// make orchestration invisible or make it unaffordable.
/// </remarks>
public class AgentCollaborationContextTests
{
    private static AgentCollaborationContext Root()
    {
        return AgentCollaborationContext.ForRoot("collab-1", "agent-root");
    }

    [Fact]
    public void ForRoot_SitsAtDepthZero_WithNoAncestry()
    {
        var root = Root();

        root.Kind.Should().Be(AgentKind.Root);
        root.ParentAgentId.Should().BeNull();
        root.AncestorAgentIds.Should().BeEmpty();
        root.StructuralDepth.Should().Be(0);
        root.DelegationDepth.Should().Be(0);
        // A root is described structurally; nothing has to choose whether to contact it.
        root.Role.Should().BeNull();
        root.Description.Should().BeNull();
    }

    [Fact]
    public void CreateChild_AccumulatesAncestry_RootFirst()
    {
        var child = Root().CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews diffs");
        var grandchild = child.CreateChild("agent-b", AgentKind.SubAgent, "tester", "runs tests");

        child.ParentAgentId.Should().Be("agent-root");
        child.AncestorAgentIds.Should().Equal("agent-root");
        grandchild.AncestorAgentIds.Should().Equal("agent-root", "agent-a");
        grandchild.CollaborationId.Should().Be("collab-1");
    }

    [Theory]
    [InlineData(AgentKind.SubAgent, 1)]
    [InlineData(AgentKind.WorkflowDelegate, 1)]
    [InlineData(AgentKind.WorkflowController, 0)]
    public void CreateChild_ChargesDelegationBudget_ExceptForAWorkflowController(
        AgentKind kind,
        int expectedDelegationDepth
    )
    {
        var child = Root().CreateChild("agent-a", kind, "role", "description");

        // Structural depth counts every node, so orchestration stays visible either way.
        child.StructuralDepth.Should().Be(1);
        child.DelegationDepth.Should().Be(expectedDelegationDepth);
    }

    [Fact]
    public void CreateChild_ThroughAWorkflowController_CostsExactlyOneHopOverall()
    {
        // The whole point of the zero-cost hop: routing work through a controller must not consume a
        // level of the delegation budget that an ordinary spawn would have had.
        var controller = Root()
            .CreateChild("agent-ctl", AgentKind.WorkflowController, "controller", "orchestrates");
        var worker = controller.CreateChild(
            "agent-w",
            AgentKind.WorkflowDelegate,
            "worker",
            "does the work"
        );

        worker.StructuralDepth.Should().Be(2);
        worker.DelegationDepth.Should().Be(1);
        worker.AncestorAgentIds.Should().Equal("agent-root", "agent-ctl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateChild_RejectsBlankIdentity(string childAgentId)
    {
        FluentActions
            .Invoking(() => Root().CreateChild(childAgentId, AgentKind.SubAgent, "r", "d"))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void CreateChild_RejectsOversizedMetadata_WithoutEchoingIt()
    {
        var oversizedRole = new string('r', AgentCollaborationContext.MaxRoleLength + 1);

        var thrown = FluentActions
            .Invoking(() => Root().CreateChild("agent-a", AgentKind.SubAgent, oversizedRole, "d"))
            .Should()
            .Throw<ArgumentException>()
            .Which;

        // Role is collaboration-visible content, so a validation failure must not be the route by
        // which it reaches a log.
        thrown.Message.Should().NotContain(oversizedRole);
    }

    [Fact]
    public void Metadata_IsBoundedInScalarValues_NotUtf16CodeUnits()
    {
        // An astral character is two UTF-16 code units but one thing a human reads, so counting code
        // units would silently halve the usable budget for non-Latin text.
        var maxScalars = string.Concat(
            Enumerable.Repeat("\U0001F600", AgentCollaborationContext.MaxRoleLength)
        );
        var oneTooMany = maxScalars + "\U0001F600";

        AgentCollaborationContext.IsMetadataValid(maxScalars, "d").Should().BeTrue();
        AgentCollaborationContext.IsMetadataValid(oneTooMany, "d").Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "d")]
    [InlineData("r", null)]
    [InlineData("  ", "d")]
    [InlineData("r", "  ")]
    public void IsMetadataValid_RejectsMissingOrBlank(string? role, string? description)
    {
        AgentCollaborationContext.IsMetadataValid(role, description).Should().BeFalse();
    }

    [Fact]
    public void CollaborationNodeRecord_RoundTripsThroughJson_WithReadableEnumsAndSnakeCaseKeys()
    {
        var entry = new AgentDirectoryEntry
        {
            AgentId = "agent-a",
            CollaborationId = "collab-1",
            Name = "reviewer",
            ParentAgentId = "agent-root",
            AncestorAgentIds = ["agent-root"],
            Kind = AgentKind.WorkflowController,
            Role = "reviewer",
            Description = "reviews diffs",
            AgentType = "code-reviewer",
            StructuralDepth = 1,
            DelegationDepth = 0,
            Status = "running",
        };

        var json = JsonSerializer.Serialize(CollaborationNodeRecord.FromEntry(entry));
        var round = JsonSerializer.Deserialize<CollaborationNodeRecord>(json)!;

        // A persisted row has to be readable by a build that predates the writer, so the kind is a
        // name rather than an ordinal that would change meaning if a member were ever inserted.
        json.Should().Contain("\"kind\":\"WorkflowController\"");
        json.Should().Contain("\"schema_version\":1");
        round.SchemaVersion.Should().Be(CollaborationNodeRecord.CurrentSchemaVersion);
        round.ToEntry().Should().BeEquivalentTo(entry with { IsLive = false });
    }

    [Fact]
    public void CollaborationNodeRecord_RehydratesAsNotLive()
    {
        // A row written before a restart describes an agent that is no longer running. Treating it as
        // reachable is exactly how a caller ends up addressing a dead endpoint.
        var entry = new AgentDirectoryEntry
        {
            AgentId = "agent-a",
            CollaborationId = "collab-1",
            Name = "reviewer",
            Kind = AgentKind.SubAgent,
            Role = "reviewer",
            Description = "reviews diffs",
            Status = "running",
            IsLive = true,
        };

        CollaborationNodeRecord.FromEntry(entry).ToEntry().IsLive.Should().BeFalse();
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("error", true)]
    [InlineData("stopped", true)]
    [InlineData("running", false)]
    [InlineData("queued", false)]
    public void AgentDirectoryEntry_ReportsTerminality_UsingTheExistingStatusVocabulary(
        string status,
        bool expectedTerminal
    )
    {
        var entry = new AgentDirectoryEntry
        {
            AgentId = "agent-a",
            CollaborationId = "collab-1",
            Name = "reviewer",
            Kind = AgentKind.SubAgent,
            Role = "reviewer",
            Description = "reviews diffs",
            Status = status,
        };

        entry.IsTerminal.Should().Be(expectedTerminal);
    }
}
