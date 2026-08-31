namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The system-prompt suffix a sandbox-backed mode gets must name the tools it actually has.
/// </summary>
/// <remarks>
/// This used to be a two-way branch on the mode id: Workspace Agent's wording or Workflow Author's
/// wording, nothing else. A narrowed copy therefore received one of two prompts written for a
/// different tool set, and a model told it has Write/Bash will call them.
/// </remarks>
public class BuildWorkspaceSuffixTests
{
    private const string HostPath = "/host/workspaces/w1";

    [Fact]
    public void WholeSurface_KeepsTheLongStandingWording()
    {
        var suffix = Program.BuildWorkspaceSuffix(HostPath, sandboxToolAllowList: null);

        suffix.Should().Contain(HostPath);
        suffix.Should().Contain("Read, Write, Edit, Glob, Grep");
        suffix.Should().Contain("Bash, PowerShell");
    }

    [Fact]
    public void NarrowedSelection_NamesOnlyTheToolsTheModeHas()
    {
        var suffix = Program.BuildWorkspaceSuffix(HostPath, new HashSet<string> { "Read", "Grep", "Skill" });

        suffix.Should().Contain(HostPath);
        suffix.Should().Contain("Read");
        suffix.Should().Contain("Grep");
        suffix.Should().Contain("Skill");
        suffix.Should().NotContain("Write");
        suffix.Should().NotContain("Bash");
        suffix.Should().NotContain("PowerShell");
    }

    [Fact]
    public void NarrowedSelection_TellsTheModelNothingElseExists()
    {
        var suffix = Program.BuildWorkspaceSuffix(HostPath, new HashSet<string> { "Read" });

        suffix.Should().Contain("No other file or shell tools exist in this mode");
    }

    [Fact]
    public void SingleTool_ReadsAsASingleName()
    {
        var suffix = Program.BuildWorkspaceSuffix(HostPath, new HashSet<string> { "Read" });

        suffix.Should().Contain("available to you: Read.");
    }

    [Fact]
    public void ToolNames_AreOrderedSoTheSuffixIsStableAcrossRestarts()
    {
        // A HashSet has no order; an unstable suffix would churn the system prompt (and any prompt
        // cache keyed on it) between restarts for a mode nobody edited.
        var suffix = Program.BuildWorkspaceSuffix(HostPath, new HashSet<string> { "Skill", "Grep", "Read" });

        suffix.Should().Contain("Grep, Read and Skill");
    }

    [Fact]
    public void EmptyAllowList_PromisesNothing()
    {
        var suffix = Program.BuildWorkspaceSuffix(HostPath, new HashSet<string>());

        suffix.Should().Contain(HostPath);
        suffix.Should().Contain("No workspace file or shell tools are available in this mode.");
        suffix.Should().NotContain("Read");
    }

    [Fact]
    public void ReviewNavigation_Included_WhenFlagTrue()
    {
        var suffix = Program.BuildWorkspaceSuffix(HostPath, null, includeReviewKnowledgeNavigation: true);

        suffix.Should().Contain("KnowledgeBase/");
        suffix.Should().Contain("KnowledgeBase/_toc.md");
        suffix.Should().Contain("workspace root");
    }

    [Fact]
    public void ReviewNavigation_Omitted_WhenFlagFalseOrDefault()
    {
        Program.BuildWorkspaceSuffix(HostPath, null).Should().NotContain("KnowledgeBase/");
        Program
            .BuildWorkspaceSuffix(HostPath, null, includeReviewKnowledgeNavigation: false)
            .Should()
            .NotContain("KnowledgeBase/");
    }
}
