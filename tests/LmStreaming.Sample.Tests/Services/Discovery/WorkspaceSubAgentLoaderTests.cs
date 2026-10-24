using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Unit tests for <see cref="WorkspaceSubAgentLoader"/>'s pure helpers:
/// <see cref="SubAgentTemplateMapper.Map"/> (mapping table) and
/// <see cref="WorkspaceSubAgentLoader.MarketplaceOf"/> (marketplace from a gateway path).
/// The HTTP-driven <c>LoadAsync</c> orchestration is thin (registry → filter → resolve →
/// read → parse → map) and is exercised end-to-end by the live workspace mode; the risky
/// surface (mapping table + parser) is pinned here.
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

    // Paper cut 1: a discovered agent's marketplace comes from its gateway path, never from its plugin name.
    [Theory]
    [InlineData("/marketplaces/gb-plugins/debugging/agents/x.md", "gb-plugins")]
    [InlineData("/marketplaces/gb-plugins/agents/a.md", "gb-plugins")]
    [InlineData("/marketplaces/My.Market_1+x/p/agents/a.md", "My.Market_1+x")]
    [InlineData(".claude/agents/x.md", null)]
    [InlineData("/workspace/marketplaces/mk/agents/x.md", null)]
    [InlineData("/marketplaces//p/agents/x.md", null)]
    [InlineData("/marketplaces/mk", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MarketplaceOf_ReadsTheAliasFromAGatewayPath_AndNullForAnyOtherPath(string? path, string? expected)
    {
        WorkspaceSubAgentLoader.MarketplaceOf(path).Should().Be(expected);
    }
}
