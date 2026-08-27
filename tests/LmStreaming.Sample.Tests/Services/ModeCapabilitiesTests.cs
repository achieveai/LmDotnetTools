using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins how a mode's capability selection resolves to what it is actually allowed to do.
/// </summary>
/// <remarks>
/// The gating this replaces was <c>mode.Id == "workspace-agent"</c>, so the behaviour that matters
/// most here is that a capability is a function of the SELECTION and of nothing else — no id, no
/// name. <see cref="ModeCapabilitiesCloneTests"/> covers the clone case that motivated the change.
/// </remarks>
public class ModeCapabilitiesTests
{
    [Fact]
    public void NullSelection_ResolvesToLegacyDefaults()
    {
        var caps = ModeCapabilities.Resolve((IReadOnlyList<string>?)null);

        caps.Should().BeSameAs(ModeCapabilities.LegacyDefaults);
    }

    [Fact]
    public void LegacyDefaults_KeepSubAgentsOn()
    {
        // Sub-agents have always been wired for every middleware-provider conversation. If a null
        // selection resolved SubAgents to false, every mode that predates this field would silently
        // lose the Agent tool on the next deploy.
        ModeCapabilities.LegacyDefaults.SubAgents.Should().BeTrue();
        ModeCapabilities.LegacyDefaults.NeedsSandbox.Should().BeFalse();
        ModeCapabilities.LegacyDefaults.WorkflowAuthoringTools.Should().BeFalse();
        ModeCapabilities.LegacyDefaults.StartWorkflowTools.Should().BeFalse();
        ModeCapabilities.LegacyDefaults.Collaboration.Should().BeFalse();
    }

    [Fact]
    public void EmptySelection_IsExplicitNone_NotLegacy()
    {
        // The distinction the whole design rests on: [] means the user unchecked everything, and
        // must NOT be read as "no choice recorded".
        var caps = ModeCapabilities.Resolve([]);

        caps.Should().NotBeSameAs(ModeCapabilities.LegacyDefaults);
        caps.SubAgents.Should().BeFalse();
        caps.NeedsSandbox.Should().BeFalse();
        caps.WorkflowAuthoringTools.Should().BeFalse();
        caps.StartWorkflowTools.Should().BeFalse();
        caps.Collaboration.Should().BeFalse();
    }

    [Fact]
    public void SandboxWildcard_TakesTheWholeGatewaySurface()
    {
        var caps = ModeCapabilities.Resolve([ToolGroups.Wildcard(ToolGroups.Sandbox)]);

        caps.NeedsSandbox.Should().BeTrue();
        // Null, not "every name we happen to know today": a marketplace plugin can add a tool after
        // the mode was saved, and the wildcard must still cover it.
        caps.SandboxToolAllowList.Should().BeNull();
    }

    [Fact]
    public void NamedSandboxTools_ProduceABareNameAllowList()
    {
        var caps = ModeCapabilities.Resolve(
            [
                ToolGroups.Qualify(ToolGroups.Sandbox, "Read"),
                ToolGroups.Qualify(ToolGroups.Sandbox, "Grep"),
            ]
        );

        caps.NeedsSandbox.Should().BeTrue();
        // Bare names: the `sandbox:` prefix is a selection id and must never reach the tool wiring.
        caps.SandboxToolAllowList.Should().BeEquivalentTo(["Read", "Grep"]);
    }

    [Fact]
    public void NoSandboxSelection_LeavesTheAllowListNull()
    {
        // A caller must not be able to read "no sandbox" as "connect a sandbox exposing nothing".
        var caps = ModeCapabilities.Resolve([ToolGroups.Qualify(ToolGroups.SubAgents, "Agent")]);

        caps.NeedsSandbox.Should().BeFalse();
        caps.SandboxToolAllowList.Should().BeNull();
    }

    [Fact]
    public void WorkflowAuthoringAndLaunchFamilies_AreIndependent()
    {
        var authoringOnly = ModeCapabilities.Resolve(
            [ToolGroups.Qualify(ToolGroups.Workflow, WorkflowToolProvider.AllToolNames[0])]
        );
        var launchOnly = ModeCapabilities.Resolve(
            [ToolGroups.Qualify(ToolGroups.Workflow, StartWorkflowToolProvider.ToolNames[0])]
        );

        authoringOnly.WorkflowAuthoringTools.Should().BeTrue();
        authoringOnly.StartWorkflowTools.Should().BeFalse();

        launchOnly.WorkflowAuthoringTools.Should().BeFalse();
        launchOnly.StartWorkflowTools.Should().BeTrue();
    }

    [Fact]
    public void WorkflowWildcard_TurnsOnBothWorkflowFamilies()
    {
        var caps = ModeCapabilities.Resolve([ToolGroups.Wildcard(ToolGroups.Workflow)]);

        caps.WorkflowAuthoringTools.Should().BeTrue();
        caps.StartWorkflowTools.Should().BeTrue();
    }

    [Fact]
    public void LegacySubAgentTools_DoNotTurnOnCollaboration()
    {
        var caps = ModeCapabilities.Resolve(
            [
                ToolGroups.Qualify(ToolGroups.SubAgents, SubAgentToolProvider.SpawnToolName),
                ToolGroups.Qualify(ToolGroups.SubAgents, SubAgentToolProvider.WaitAgentToolName),
            ]
        );

        caps.SubAgents.Should().BeTrue();
        caps.Collaboration.Should().BeFalse();
    }

    [Theory]
    [InlineData(SubAgentToolProvider.CheckAgentsToolName)]
    [InlineData(SubAgentToolProvider.WaitForAgentsToolName)]
    [InlineData(SubAgentToolProvider.GetAgentsToolName)]
    public void AnyCollaborationTool_TurnsOnTheCollaborationSurface(string toolName)
    {
        var caps = ModeCapabilities.Resolve([ToolGroups.Qualify(ToolGroups.SubAgents, toolName)]);

        caps.SubAgents.Should().BeTrue();
        caps.Collaboration.Should().BeTrue();
    }

    [Fact]
    public void CollaborationToolNames_MatchTheProvidersOwnConstants()
    {
        // The list exists so ModeCapabilities and SubAgentToolProvider cannot drift; assert the
        // membership rather than trusting two hand-written lists to stay equal.
        ModeCapabilities
            .CollaborationToolNames.Should()
            .BeSubsetOf(SubAgentToolProvider.AllToolNames);
    }

    [Fact]
    public void BareEntries_AreIgnored()
    {
        // EnabledTools names (bare) handed to the wrong field must not be mistaken for capabilities.
        var caps = ModeCapabilities.Resolve(["web_search", "Read", "add-task"]);

        caps.Should().NotBeSameAs(ModeCapabilities.LegacyDefaults);
        caps.NeedsSandbox.Should().BeFalse();
        caps.SubAgents.Should().BeFalse();
    }

    [Fact]
    public void UnknownGroupPrefix_IsIgnored()
    {
        var caps = ModeCapabilities.Resolve(["totally-made-up:Read"]);

        caps.NeedsSandbox.Should().BeFalse();
        caps.SubAgents.Should().BeFalse();
    }

    [Fact]
    public void NamedWorkflowTool_NarrowsTheGrantToThatName()
    {
        // The editor lists the seven authoring tools separately, so picking one must grant one.
        // Before this, any single pick turned on the whole WorkflowToolProvider family.
        var caps = ModeCapabilities.Resolve([$"{ToolGroups.Workflow}:{WorkflowToolProvider.AddNodeToolName}"]);

        caps.WorkflowAuthoringTools.Should().BeTrue();
        caps.WorkflowToolAllowList.Should().BeEquivalentTo([WorkflowToolProvider.AddNodeToolName]);
    }

    [Fact]
    public void WorkflowWildcard_LeavesTheAllowListNullSoLaterToolsFlowThrough()
    {
        var caps = ModeCapabilities.Resolve([ToolGroups.Wildcard(ToolGroups.Workflow)]);

        caps.WorkflowAuthoringTools.Should().BeTrue();
        caps.StartWorkflowTools.Should().BeTrue();
        caps.WorkflowToolAllowList.Should().BeNull();
    }

    [Fact]
    public void NoWorkflowSelection_LeavesTheAllowListNullRatherThanEmpty()
    {
        // An empty set would read as "register the provider and expose nothing"; null says the
        // provider is never built at all, which is what the two booleans already encode.
        var caps = ModeCapabilities.Resolve([ToolGroups.Wildcard(ToolGroups.Sandbox)]);

        caps.WorkflowAuthoringTools.Should().BeFalse();
        caps.StartWorkflowTools.Should().BeFalse();
        caps.WorkflowToolAllowList.Should().BeNull();
    }

    [Fact]
    public void MixedWorkflowFamilies_ShareOneAllowListAcrossBothProviders()
    {
        // Authoring and launch tools live in one group namespace, so one allow-list narrows both.
        var caps = ModeCapabilities.Resolve(
            [
                $"{ToolGroups.Workflow}:{WorkflowToolProvider.AddNodeToolName}",
                $"{ToolGroups.Workflow}:{StartWorkflowToolProvider.StartWorkflowToolName}",
            ]
        );

        caps.WorkflowAuthoringTools.Should().BeTrue();
        caps.StartWorkflowTools.Should().BeTrue();
        caps.WorkflowToolAllowList
            .Should()
            .BeEquivalentTo(
                [WorkflowToolProvider.AddNodeToolName, StartWorkflowToolProvider.StartWorkflowToolName]
            );
    }

    [Fact]
    public void NamedSubAgentTool_NarrowsTheGrantToThatName()
    {
        var caps = ModeCapabilities.Resolve([$"{ToolGroups.SubAgents}:{SubAgentToolProvider.SpawnToolName}"]);

        caps.SubAgents.Should().BeTrue();
        caps.SubAgentToolAllowList.Should().BeEquivalentTo([SubAgentToolProvider.SpawnToolName]);
    }

    [Fact]
    public void SubAgentWildcard_LeavesTheAllowListNull()
    {
        var caps = ModeCapabilities.Resolve([ToolGroups.Wildcard(ToolGroups.SubAgents)]);

        caps.SubAgents.Should().BeTrue();
        caps.SubAgentToolAllowList.Should().BeNull();
    }

    [Fact]
    public void LegacyMode_KeepsAnUnnarrowedSubAgentSurface()
    {
        // The regression that would hurt most: every mode predating capability selection resolves
        // here, and a non-null allow-list would silently shrink what it has always had.
        var caps = ModeCapabilities.Resolve((IReadOnlyList<string>?)null);

        caps.SubAgents.Should().BeTrue();
        caps.SubAgentToolAllowList.Should().BeNull();
        caps.WorkflowToolAllowList.Should().BeNull();
    }

    [Fact]
    public void EqualSelections_AreEqualCapabilities_EvenThoughTheirSetsAreDistinctInstances()
    {
        // The record's synthesized equality compares the allow-list sets BY REFERENCE, so two
        // resolutions of the same selection read as different capabilities. That breaks the one
        // question this type exists to answer - "does a copy behave like its original?".
        IReadOnlyList<string> selection =
            [
                $"{ToolGroups.Sandbox}:Read",
                $"{ToolGroups.SubAgents}:{SubAgentToolProvider.SpawnToolName}",
                $"{ToolGroups.Workflow}:{WorkflowToolProvider.AddNodeToolName}",
            ];

        var left = ModeCapabilities.Resolve(selection);
        var right = ModeCapabilities.Resolve(selection);

        left.SandboxToolAllowList.Should().NotBeSameAs(right.SandboxToolAllowList);
        left.Should().Be(right);
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    [Fact]
    public void SelectionOrder_DoesNotChangeEquality()
    {
        var forward = ModeCapabilities.Resolve([$"{ToolGroups.Sandbox}:Read", $"{ToolGroups.Sandbox}:Grep"]);
        var reversed = ModeCapabilities.Resolve([$"{ToolGroups.Sandbox}:Grep", $"{ToolGroups.Sandbox}:Read"]);

        forward.Should().Be(reversed);
        forward.GetHashCode().Should().Be(reversed.GetHashCode());
    }

    [Fact]
    public void DifferentAllowLists_AreNotEqual()
    {
        // Non-vacuity: an Equals that ignored the sets entirely would pass both tests above.
        var read = ModeCapabilities.Resolve([$"{ToolGroups.Sandbox}:Read"]);
        var write = ModeCapabilities.Resolve([$"{ToolGroups.Sandbox}:Write"]);

        read.Should().NotBe(write);
    }

    [Fact]
    public void DifferentWorkflowAllowLists_AreNotEqual()
    {
        // Three same-typed sets hang off this record. Each needs its own witness, or an Equals that
        // compares only the first of them reads as fully covered.
        var addNode = ModeCapabilities.Resolve([$"{ToolGroups.Workflow}:{WorkflowToolProvider.AllToolNames[0]}"]);
        var other = ModeCapabilities.Resolve([$"{ToolGroups.Workflow}:{WorkflowToolProvider.AllToolNames[1]}"]);

        addNode.Should().NotBe(other);
    }

    [Fact]
    public void DifferentSubAgentAllowLists_AreNotEqual()
    {
        var spawn = ModeCapabilities.Resolve([$"{ToolGroups.SubAgents}:{SubAgentToolProvider.AllToolNames[0]}"]);
        var other = ModeCapabilities.Resolve([$"{ToolGroups.SubAgents}:{SubAgentToolProvider.AllToolNames[1]}"]);

        spawn.Should().NotBe(other);
    }

    [Fact]
    public void AnEmptyAllowList_IsNotTheSameAsNoAllowListAtAll()
    {
        // null means "the whole family, including tools added later"; empty means "none of it". An
        // equality that folded the two together would report a mode granting everything as identical
        // to one granting nothing - and this record is what the clone check compares.
        var unrestricted = ModeCapabilities.LegacyDefaults with { SubAgentToolAllowList = null };
        var nothing = ModeCapabilities.LegacyDefaults with
        {
            SubAgentToolAllowList = new HashSet<string>(),
        };

        unrestricted.Should().NotBe(nothing);
    }

    [Fact]
    public void WildcardAndNamedSelection_AreNotEqual()
    {
        // null (everything, including tools added later) must never compare equal to an explicit
        // list that happens to name today's tools.
        var wildcard = ModeCapabilities.Resolve([ToolGroups.Wildcard(ToolGroups.Sandbox)]);
        var named = ModeCapabilities.Resolve([$"{ToolGroups.Sandbox}:Read"]);

        wildcard.SandboxToolAllowList.Should().BeNull();
        wildcard.Should().NotBe(named);
    }
}
