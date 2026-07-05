using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Unit tests for <see cref="WorkspaceSubAgentLoader"/>'s pure helpers:
/// <see cref="SubAgentTemplateMapper.Map"/> (mapping table),
/// <see cref="WorkspaceSubAgentLoader.TryResolveContainedPath"/> (path-injection guard) and
/// <see cref="WorkspaceSubAgentLoader.NormalizeBasePath"/> (trailing-separator pin).
/// The HTTP-driven <c>LoadAsync</c> orchestration is thin (registry → filter → resolve →
/// read → parse → map) and is exercised end-to-end by the live workspace mode; the risky
/// surface (mapping table + traversal guard + parser) is pinned here.
/// </summary>
public class WorkspaceSubAgentLoaderTests
{
    private static readonly Mock<IStreamingAgent> AgentStub = new();
    private static readonly Func<IStreamingAgent> AgentFactory = () => AgentStub.Object;

    [Fact]
    public void MapToTemplate_DescriptionMapsToBothDescriptionAndWhenToUse()
    {
        var parsed = new ParsedSubAgent(
            Name: "echo",
            Description: "Echoes a discovered marker.",
            Model: null,
            Tools: null,
            SystemPrompt: "You are echo."
        );

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.Name.Should().Be("echo");
        template.Description.Should().Be("Echoes a discovered marker.");
        template.WhenToUse.Should().Be("Echoes a discovered marker.");
        template.SystemPrompt.Should().Be("You are echo.");
    }

    [Fact]
    public void MapToTemplate_ModelOverride_SetsDefaultOptionsModelId()
    {
        var parsed = new ParsedSubAgent(
            Name: "echo",
            Description: null,
            Model: "claude-sonnet-4-5",
            Tools: null,
            SystemPrompt: "Body."
        );

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.DefaultOptions.Should().NotBeNull();
        template.DefaultOptions!.ModelId.Should().Be("claude-sonnet-4-5");
    }

    [Fact]
    public void MapToTemplate_NoModelOverride_LeavesDefaultOptionsNull()
    {
        var parsed = new ParsedSubAgent(
            Name: "echo",
            Description: null,
            Model: null,
            Tools: null,
            SystemPrompt: "Body."
        );

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.DefaultOptions.Should().BeNull();
    }

    [Fact]
    public void MapToTemplate_ToolsAbsent_NullEnabledTools_InheritsAllParentTools()
    {
        var parsed = new ParsedSubAgent("echo", null, null, Tools: null, SystemPrompt: "Body.");

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.EnabledTools.Should().BeNull();
    }

    [Fact]
    public void MapToTemplate_EmptyTools_EmptyEnabledTools_DistinctFromNull()
    {
        var parsed = new ParsedSubAgent("echo", null, null, Tools: [], SystemPrompt: "Body.");

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.EnabledTools.Should().NotBeNull();
        template.EnabledTools.Should().BeEmpty();
    }

    [Fact]
    public void MapToTemplate_ToolsList_PreservedAsEnabledTools()
    {
        var parsed = new ParsedSubAgent("echo", null, null, Tools: ["Read", "Glob"], SystemPrompt: "Body.");

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.EnabledTools.Should().Equal("Read", "Glob");
    }

    [Fact]
    public void MapToTemplate_MaxTurnsPerRun_MatchesProductionTemplates()
    {
        var parsed = new ParsedSubAgent("echo", null, null, null, "Body.");

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);

        template.MaxTurnsPerRun.Should().Be(WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);
    }

    [Fact]
    public void TryResolveContainedPath_DotDotEscape_Rejected()
    {
        var basePath = WorkspaceSubAgentLoader.NormalizeBasePath(Path.Combine(Path.GetTempPath(), "wi76-base"));

        var contained = WorkspaceSubAgentLoader.TryResolveContainedPath(
            basePath,
            relativeOrAbsolute: Path.Combine("..", "outside.md"),
            out var resolved
        );

        contained.Should().BeFalse();
        resolved.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveContainedPath_SiblingPrefixAttack_Rejected()
    {
        // Pin the trailing-separator normalisation: a sibling whose path begins with the bare
        // base ("C:\\work") plus extra characters must NOT be considered contained in "C:\\work".
        var root = Path.Combine(Path.GetTempPath(), "wi76-work");
        var basePath = WorkspaceSubAgentLoader.NormalizeBasePath(root);
        var sibling = root + "-evil";

        var contained = WorkspaceSubAgentLoader.TryResolveContainedPath(
            basePath,
            relativeOrAbsolute: Path.Combine(sibling, "agent.md"),
            out _
        );

        contained.Should().BeFalse();
    }

    [Fact]
    public void TryResolveContainedPath_RelativeUnderBase_Accepted()
    {
        var basePath = WorkspaceSubAgentLoader.NormalizeBasePath(Path.Combine(Path.GetTempPath(), "wi76-base"));

        var contained = WorkspaceSubAgentLoader.TryResolveContainedPath(
            basePath,
            relativeOrAbsolute: Path.Combine(".claude", "agents", "echo.md"),
            out var resolved
        );

        contained.Should().BeTrue();
        resolved.Should().StartWith(basePath);
        resolved.Should().EndWith("echo.md");
    }

    [Fact]
    public void TryResolveContainedPath_AbsoluteUnderBase_Accepted()
    {
        var basePath = WorkspaceSubAgentLoader.NormalizeBasePath(Path.Combine(Path.GetTempPath(), "wi76-base"));
        var absolute = Path.Combine(basePath, ".claude", "agents", "echo.md");

        var contained = WorkspaceSubAgentLoader.TryResolveContainedPath(
            basePath,
            relativeOrAbsolute: absolute,
            out var resolved
        );

        contained.Should().BeTrue();
        resolved.Should().EndWith("echo.md");
    }

    [Fact]
    public void TryResolveContainedPath_EmptyInput_Rejected()
    {
        var basePath = WorkspaceSubAgentLoader.NormalizeBasePath(Path.GetTempPath());

        WorkspaceSubAgentLoader.TryResolveContainedPath(basePath, string.Empty, out _).Should().BeFalse();
        WorkspaceSubAgentLoader.TryResolveContainedPath(basePath, "   ", out _).Should().BeFalse();
    }

    [Fact]
    public void NormalizeBasePath_AlwaysEndsWithSeparator()
    {
        var input = Path.Combine(Path.GetTempPath(), "wi76-trailing");

        var normalized = WorkspaceSubAgentLoader.NormalizeBasePath(input);

        normalized.Should().EndWith(Path.DirectorySeparatorChar.ToString());
    }
}
