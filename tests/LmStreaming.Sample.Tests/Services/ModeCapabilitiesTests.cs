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
}
