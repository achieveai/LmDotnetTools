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
        // Function tools stay curated (no sample demo tools); the one family enabledTools names is
        // the shared todo board's, whose full-list pin lives in ProgramModeToolNarrowingTests.
        mode.EnabledTools.Should().NotBeEmpty();
        mode.EnabledTools.Should().Contain("add-task").And.NotContain("get_weather");
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
                m.Id != SystemChatModes.WorkspaceAgentModeId && m.Id != SystemChatModes.WorkflowAuthorModeId
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

    // --- Per-mode sub-agent prompt fragment (#610): yaml binding + legacy compat ---------------

    /// <summary>
    /// A Prompts.yaml chat-mode entry exactly as it could exist BEFORE #610 — a frozen literal
    /// (not a round-trip self-check; the #590 lesson) proving old files load unchanged.
    /// </summary>
    private const string LegacyYaml = """
        chatModes:
          - id: legacy
            name: Legacy Mode
            description: Written before subAgentPrompt existed.
            systemPrompt: You are a legacy mode.
            enabledTools:
              - add-task
            enabledBuiltInTools:
              - web_search
            enabledCapabilityTools:
              - subagents:Agent
        """;

    [Fact]
    public void ParseModes_LegacyYamlWithoutFragmentFields_LoadsUnchanged()
    {
        var modes = SystemChatModes.ParseModes(LegacyYaml);

        var mode = modes.Should().ContainSingle().Subject;
        mode.Id.Should().Be("legacy");
        mode.Name.Should().Be("Legacy Mode");
        mode.SystemPrompt.Should().Be("You are a legacy mode.");
        mode.EnabledTools.Should().BeEquivalentTo(["add-task"]);
        mode.EnabledBuiltInTools.Should().BeEquivalentTo(["web_search"]);
        mode.EnabledCapabilityTools.Should().BeEquivalentTo(["subagents:Agent"]);
        mode.SubAgentPrompt.Should().BeNull();
        mode.SubAgentPromptPlacement.Should().BeNull();
    }

    [Fact]
    public void ParseModes_BindsSubAgentPromptAndPlacement()
    {
        var modes = SystemChatModes.ParseModes(
            """
            chatModes:
              - id: fragmented
                name: Fragmented
                systemPrompt: primary
                subAgentPrompt: Fragment for every child.
                subAgentPromptPlacement: prepend
            """
        );

        var mode = modes.Should().ContainSingle().Subject;
        mode.SubAgentPrompt.Should().Be("Fragment for every child.");
        mode.SubAgentPromptPlacement.Should().Be("prepend");
    }

    [Fact]
    public void ParseModes_FragmentWithoutPlacement_LeavesPlacementNull()
    {
        var modes = SystemChatModes.ParseModes(
            """
            chatModes:
              - id: fragmented
                name: Fragmented
                systemPrompt: primary
                subAgentPrompt: Fragment for every child.
            """
        );

        modes.Single().SubAgentPrompt.Should().Be("Fragment for every child.");
        modes.Single().SubAgentPromptPlacement.Should().BeNull();
    }

    [Theory]
    [InlineData("before")]
    [InlineData("Append")]
    [InlineData("PREPEND")]
    public void ParseModes_InvalidPlacement_IsRefusedAtLoad(string placement)
    {
        var act = () =>
            SystemChatModes.ParseModes(
                $"""
                chatModes:
                  - id: bad
                    name: Bad
                    systemPrompt: primary
                    subAgentPrompt: frag
                    subAgentPromptPlacement: {placement}
                """
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*subAgentPromptPlacement*");
    }

    [Fact]
    public void All_SystemModes_CarryNoFragmentToday()
    {
        // Shipping Prompts.yaml declares no fragment, so today's spawn behavior is untouched.
        SystemChatModes.All.Should().OnlyContain(m => m.SubAgentPrompt == null && m.SubAgentPromptPlacement == null);
    }
}
