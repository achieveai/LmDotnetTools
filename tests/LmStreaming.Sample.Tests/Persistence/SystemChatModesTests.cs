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
    public void DefaultMode_LeavesEnabledToolsNullToEnableAllTools()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId);

        mode.Should().NotBeNull();
        mode!.EnabledTools.Should().BeNull();
    }
}
