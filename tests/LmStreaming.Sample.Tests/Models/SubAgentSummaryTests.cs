using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

namespace LmStreaming.Sample.Tests.Models;

/// <summary>
/// The convergence point between the sample's tab row and the collaboration core's persisted node
/// shape (#244). These tests pin three things that a later change could silently break: the additive
/// fields are genuinely optional (a pre-#244 row still loads), a row carrying hierarchy metadata
/// round-trips through <see cref="CollaborationNodeRecord"/> without losing a field, and the JSON
/// names the client parses stay exactly what they are today.
/// </summary>
public sealed class SubAgentSummaryTests
{
    /// <summary>Matches the options the tab index and the HTTP surface both serialize with.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static AgentDirectoryEntry Entry(
        AgentKind kind = AgentKind.SubAgent,
        int structuralDepth = 1,
        int delegationDepth = 1,
        bool isLive = true
    ) =>
        new()
        {
            AgentId = "a-1",
            CollaborationId = "thread-root",
            Name = "researcher",
            ParentAgentId = "thread-root",
            AncestorAgentIds = ["thread-root"],
            Kind = kind,
            Role = "find prior art",
            Description = "contact for anything about prior art",
            AgentType = "general-purpose",
            StructuralDepth = structuralDepth,
            DelegationDepth = delegationDepth,
            Status = AgentCollaborationStatuses.Running,
            IsLive = isLive,
        };

    private static SubAgentSummary LegacyTab() =>
        new()
        {
            AgentId = "a-1",
            Name = "researcher",
            Template = "general-purpose",
            Task = "find prior art",
            Status = AgentCollaborationStatuses.Running,
            ThreadId = "subagent-a-1",
        };

    [Fact]
    public void LegacyTab_CarriesNoCollaborationMetadata()
    {
        var tab = LegacyTab();

        tab.SchemaVersion.Should().Be(0, "a row nobody stamped predates the versioned shape");
        tab.CollaborationId.Should().BeNull();
        tab.AgentKind.Should().BeNull();
        tab.IsLive.Should().BeNull("liveness is only asserted where collaboration tracks it");
        tab.AncestorAgentIds.Should().BeNull("an unknown ancestry is not an empty one");
        tab.ToNodeRecord().Should().BeNull("there is no hierarchy to project, and inventing one would lie");
    }

    [Fact]
    public void WithCollaboration_CopiesEveryHierarchyFieldAndStampsTheSharedVersion()
    {
        var entry = Entry();

        var tab = LegacyTab().WithCollaboration(entry);

        tab.SchemaVersion.Should().Be(CollaborationNodeRecord.CurrentSchemaVersion);
        tab.CollaborationId.Should().Be(entry.CollaborationId);
        tab.AgentKind.Should().Be(nameof(AgentKind.SubAgent));
        tab.AgentType.Should().Be(entry.AgentType);
        tab.Role.Should().Be(entry.Role);
        tab.Description.Should().Be(entry.Description);
        tab.ParentAgentId.Should().Be(entry.ParentAgentId);
        tab.AncestorAgentIds.Should().Equal(entry.AncestorAgentIds);
        tab.StructuralDepth.Should().Be(entry.StructuralDepth);
        tab.DelegationDepth.Should().Be(entry.DelegationDepth);
        tab.IsLive.Should().BeTrue();
    }

    [Fact]
    public void WithCollaboration_LeavesPresentationFieldsAlone()
    {
        var tab = LegacyTab() with
        {
            Task = "the original spawn prompt",
            EffectiveModelId = "claude-opus-4.8",
            ModelSelectionSource = "spawn-tier",
        };

        var enriched = tab.WithCollaboration(Entry());

        enriched.Task.Should().Be("the original spawn prompt", "the role is not a substitute for the task");
        enriched.ThreadId.Should().Be(tab.ThreadId);
        enriched.Template.Should().Be(tab.Template);
        enriched.EffectiveModelId.Should().Be("claude-opus-4.8");
        enriched.ModelSelectionSource.Should().Be("spawn-tier");
    }

    [Fact]
    public void Template_StaysAnAliasOfAgentType()
    {
        var tab = LegacyTab().WithCollaboration(Entry());

        tab.AgentType.Should().Be(tab.Template, "the pre-#244 name is a permanent alias, not migration scaffolding");
    }

    [Theory]
    [InlineData(AgentKind.Root, SubAgentSummary.SubAgentTabKind)]
    [InlineData(AgentKind.SubAgent, SubAgentSummary.SubAgentTabKind)]
    [InlineData(AgentKind.WorkflowDelegate, SubAgentSummary.SubAgentTabKind)]
    [InlineData(AgentKind.WorkflowController, SubAgentSummary.WorkflowTabKind)]
    public void TabKindFor_SurfacesOnlyControllersAsWorkflowTabs(AgentKind kind, string expected)
    {
        SubAgentSummary
            .TabKindFor(kind)
            .Should()
            .Be(expected, "the merge key is (Kind, AgentId), so one agent must map to exactly one tab kind");
    }

    [Fact]
    public void FromDirectoryEntry_BuildsAReadableRowForANodeWithNoLiveTab()
    {
        var tab = SubAgentSummary.FromDirectoryEntry(Entry(AgentKind.WorkflowDelegate));

        tab.AgentId.Should().Be("a-1");
        tab.Kind.Should().Be(SubAgentSummary.SubAgentTabKind);
        tab.Name.Should().Be("researcher");
        tab.Template.Should().Be("general-purpose");
        tab.ThreadId.Should().Be("subagent-a-1", "descendants persist under the sample's subagent- ids");
        tab.Status.Should().Be(AgentCollaborationStatuses.Running);
        tab.DelegationDepth.Should().Be(1);
    }

    [Fact]
    public void FromDirectoryEntry_KeepsTheRootOnItsOwnThread()
    {
        var tab = SubAgentSummary.FromDirectoryEntry(
            Entry(AgentKind.Root, structuralDepth: 0, delegationDepth: 0) with
            {
                AgentId = "thread-root",
                ParentAgentId = null,
                AncestorAgentIds = [],
            }
        );

        tab.ThreadId.Should().Be("thread-root", "the root's transcript is the conversation itself");
        tab.StructuralDepth.Should().Be(0);
        tab.DelegationDepth.Should().Be(0);
    }

    [Fact]
    public void ToNodeRecord_RoundTripsThroughTheSharedPersistedShape()
    {
        var entry = Entry(AgentKind.WorkflowController, structuralDepth: 2, delegationDepth: 1);

        var restored = SubAgentSummary.FromDirectoryEntry(entry).ToNodeRecord()!.ToEntry();

        restored
            .Should()
            .BeEquivalentTo(
                entry with
                {
                    IsLive = false,
                },
                "a persisted node describes an agent that is no longer reachable here"
            );
    }

    [Fact]
    public void ToNodeRecord_StampsTheCurrentSchemaVersion()
    {
        SubAgentSummary
            .FromDirectoryEntry(Entry())
            .ToNodeRecord()!
            .SchemaVersion.Should()
            .Be(CollaborationNodeRecord.CurrentSchemaVersion);
    }

    [Fact]
    public void AsRetained_DemotesInFlightRowsAndClearsViewerScopedFlags()
    {
        var tab = LegacyTab().WithCollaboration(Entry()) with { IsCurrent = true, IsReadable = true };

        var retained = tab.AsRetained();

        retained.Status.Should().Be(SubAgentSummary.InterruptedStatus);
        retained.IsLive.Should().BeFalse();
        retained.IsCurrent.Should().BeFalse("\"you\" is answered per reader, never from storage");
        retained.IsReadable.Should().BeFalse("permission is re-evaluated per reader, never from storage");
    }

    [Fact]
    public void AsRetained_LeavesATerminalStatusAlone()
    {
        var tab = LegacyTab() with { Status = AgentCollaborationStatuses.Completed };

        tab.AsRetained().Status.Should().Be(AgentCollaborationStatuses.Completed);
    }

    [Fact]
    public void AsRetained_DoesNotInventLivenessForAPre244Row()
    {
        LegacyTab()
            .AsRetained()
            .IsLive.Should()
            .BeNull("adding isLive to a legacy row would change the shape a pre-#244 client parses");
    }

    [Fact]
    public void LegacyRow_SerializesToExactlyThePre244FieldSet()
    {
        var json = JsonSerializer.SerializeToNode(LegacyTab(), Web)!.AsObject();

        json.Select(p => p.Key)
            .Should()
            .BeEquivalentTo(
                [
                    "agentId",
                    "kind",
                    "name",
                    "template",
                    "task",
                    "status",
                    "threadId",
                    "lastActivityUtc",
                    "parentThreadId",
                    "depth",
                    "terminalAtUtc",
                    "failureCode",
                    "effectiveModelId",
                    "effectiveModelIntelligence",
                    "modelSelectionSource",
                ],
                "a host that never enabled collaboration must retain the current main-branch legacy shape — "
                    + "every collaboration-only #244 member is omitted when it has nothing to say"
            );
    }

    [Fact]
    public void CollaborationRow_PublishesTheHierarchyNamesTheClientParses()
    {
        var json = JsonSerializer
            .SerializeToNode(
                SubAgentSummary.FromDirectoryEntry(Entry()) with
                {
                    IsCurrent = true,
                    IsReadable = true,
                },
                Web
            )!
            .AsObject();

        json.Select(p => p.Key)
            .Should()
            .Contain(
                [
                    "schemaVersion",
                    "collaborationId",
                    "agentType",
                    "agentKind",
                    "role",
                    "description",
                    "parentAgentId",
                    "ancestorAgentIds",
                    "structuralDepth",
                    "delegationDepth",
                    "isLive",
                    "isCurrent",
                    "isReadable",
                ],
                "the client parses these names literally; renaming one is a breaking change"
            );
        json["template"]!.GetValue<string>().Should().Be(json["agentType"]!.GetValue<string>());
    }

    [Fact]
    public void ReasoningProvenance_PublishesTheNamesTheDaemonParses()
    {
        var json = JsonSerializer
            .SerializeToNode(
                SubAgentSummary.FromDirectoryEntry(Entry()) with
                {
                    RequestedReasoningEffort = "xhigh",
                    ShapedReasoningEffort = "high",
                },
                Web
            )!
            .AsObject();

        json["requestedReasoningEffort"]!.GetValue<string>().Should().Be("xhigh");
        json["shapedReasoningEffort"]!.GetValue<string>().Should().Be("high");
    }

    [Fact]
    public void Pre244Json_StillDeserializes()
    {
        // Byte-for-byte the shape a pre-#244 build wrote into the tab index.
        const string Historical = """
            {
              "agentId": "d1",
              "kind": "workflow",
              "name": "reviewer",
              "template": "code-reviewer",
              "task": "review the diff",
              "status": "completed",
              "threadId": "subagent-d1",
              "lastActivityUtc": "2026-07-01T10:00:00+00:00",
              "effectiveModelId": "gpt-5.5",
              "effectiveModelIntelligence": 4,
              "modelSelectionSource": "template-tier"
            }
            """;

        var tab = JsonSerializer.Deserialize<SubAgentSummary>(Historical, Web)!;

        tab.AgentId.Should().Be("d1");
        tab.Kind.Should().Be(SubAgentSummary.WorkflowTabKind);
        tab.Template.Should().Be("code-reviewer");
        tab.EffectiveModelIntelligence.Should().Be(4);
        tab.SchemaVersion.Should().Be(0);
        tab.CollaborationId.Should().BeNull();
        tab.AncestorAgentIds.Should().BeNull("a row that omits it never had a hierarchy to report");
    }

    [Fact]
    public void ViewerScopedFlags_AreWrittenOnlyWhenTrue()
    {
        var off = JsonSerializer.SerializeToNode(LegacyTab(), Web)!.AsObject();
        var on = JsonSerializer
            .SerializeToNode(LegacyTab() with { IsCurrent = true, IsReadable = true }, Web)!
            .AsObject();

        off.Should().NotContainKey("isCurrent").And.NotContainKey("isReadable");
        on["isCurrent"]!.GetValue<bool>().Should().BeTrue();
        on["isReadable"]!.GetValue<bool>().Should().BeTrue();
    }
}
