using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Persistence;

public class SystemChatModesTests
{
    [Fact]
    public void All_LoadsSystemModesFromPromptsYaml()
    {
        var modes = SystemChatModes.All;

        modes.Should().Contain(m => m.Id == SystemChatModes.DefaultModeId);
        modes.Should().Contain(m => m.Id == SystemChatModes.MedicalKnowledgeModeId);
        modes.Should().Contain(m => m.Id == SystemChatModes.WorkspaceAgentModeId);
        modes.Should().OnlyContain(m => m.IsSystemDefined);
    }

    [Fact]
    public void WorkspaceAgentMode_UsesYamlPromptAndSandboxToolConfiguration()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);

        mode.Should().NotBeNull();
        mode!.Name.Should().Be("Workspace Agent");
        mode.Description.Should().Contain("sandboxed workspace");
        mode.SystemPrompt.Should().Contain("You MUST use the sandbox tools");
        mode.SystemPrompt.Should().Contain("Read, Write, Edit, Glob, Grep, Bash, PowerShell");
        mode.EnabledTools.Should().BeEmpty();
    }

    [Fact]
    public void WorkspaceAgentMode_DeclaresTheCapabilitiesItUsedToGetFromItsId()
    {
        // These four were granted by `mode.Id == "workspace-agent"` before capability selection
        // existed. Pinning them here is what proves the yaml now carries the whole grant, so a copy
        // of this mode can inherit it.
        var mode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);
        var caps = ModeCapabilities.Resolve(mode!);

        caps.NeedsSandbox.Should().BeTrue();
        caps.SandboxToolAllowList.Should().BeNull("the mode declares sandbox:* rather than a list");
        caps.SubAgents.Should().BeTrue();
        caps.Collaboration.Should().BeTrue();
        caps.StartWorkflowTools.Should().BeTrue();
    }

    [Fact]
    public void WorkspaceAgentMode_SelectsTheSandboxWildcardRatherThanAnEnumeratedList()
    {
        // An enumerated list would silently drop tools a workspace's marketplace plugins add to the
        // gateway at runtime, which is exactly what the wildcard exists to prevent.
        var mode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);

        mode!.EnabledCapabilityTools.Should().Contain(ToolGroups.Wildcard(ToolGroups.Sandbox));
    }

    [Fact]
    public void WorkflowAuthorMode_KeepsItsReadOnlySandboxSliceAndLegacySubAgentSurface()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.WorkflowAuthorModeId);
        var caps = ModeCapabilities.Resolve(mode!);

        caps.NeedsSandbox.Should().BeTrue();
        caps.SandboxToolAllowList.Should().BeEquivalentTo(["Read", "Grep", "Skill"]);
        caps.WorkflowAuthoringTools.Should().BeTrue();
        caps.StartWorkflowTools.Should().BeTrue();
        caps.SubAgents.Should().BeTrue();
        // Never had the hierarchy-wide collaboration surface, and must not gain it here.
        caps.Collaboration.Should().BeFalse();
    }

    [Fact]
    public void OrdinaryModes_RecordNoCapabilitySelectionAndKeepLegacyDefaults()
    {
        // Every mode that is not one of the two capability modes must still resolve to the legacy
        // defaults, or this change would quietly strip sub-agents from them.
        var ordinary = SystemChatModes
            .All.Where(m =>
                m.Id != SystemChatModes.WorkspaceAgentModeId
                && m.Id != SystemChatModes.WorkflowAuthorModeId
            )
            .ToList();

        ordinary.Should().NotBeEmpty();
        ordinary.Should().OnlyContain(m => m.EnabledCapabilityTools == null);
        foreach (var mode in ordinary)
        {
            ModeCapabilities.Resolve(mode).Should().BeSameAs(ModeCapabilities.LegacyDefaults);
        }
    }

    [Fact]
    public void DefaultMode_LeavesEnabledToolsNullToEnableAllTools()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId);

        mode.Should().NotBeNull();
        mode!.EnabledTools.Should().BeNull();
    }
}
