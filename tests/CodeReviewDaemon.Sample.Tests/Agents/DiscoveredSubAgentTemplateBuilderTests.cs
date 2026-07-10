using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public class DiscoveredSubAgentTemplateBuilderTests
{
    private static readonly Func<IStreamingAgent> AgentFactory = () => new Mock<IStreamingAgent>().Object;

    private const string Body = """
        ---
        name: architecture-review
        description: Reviews architecture.
        ---
        You review architecture across the connected repos.
        """;

    [Fact]
    public void Build_KeepsEveryPluginInAllowedMarketplace_KeyedByQualifiedName()
    {
        var items = new List<SandboxSessionRegistry.DiscoveredItem>
        {
            // code-reviewer plugin in gb-plugins
            new("subagent", "architecture-review", "arch", "/marketplaces/gb-plugins/code-reviewer/agents/a.md",
                Content: Body, QualifiedName: "code-reviewer:architecture-review"),
            // a DIFFERENT plugin in the SAME gb-plugins marketplace — must also be kept (all plugins, not just code-reviewer)
            new("subagent", "blind-spot-detector", "bsd", "/marketplaces/gb-plugins/development/agents/b.md",
                Content: Body, QualifiedName: "development:blind-spot-detector"),
            // an agent from a marketplace NOT in the allow-list — must be dropped
            new("subagent", "other", "x", "/marketplaces/superpowers/agents/o.md",
                Content: Body, QualifiedName: "superpowers:thing"),
            new("skill", "review", null, ".claude/skills/review.md"),
        };
        var builder = new DiscoveredSubAgentTemplateBuilder(NullLogger<DiscoveredSubAgentTemplateBuilder>.Instance);

        var templates = builder.Build(items, ["gb-plugins"], AgentFactory);

        templates.Should().ContainKey("code-reviewer:architecture-review");
        templates.Should().ContainKey("development:blind-spot-detector");
        templates.Should().NotContainKey("superpowers:thing");
        templates["code-reviewer:architecture-review"].SystemPrompt.Should().Contain("review architecture");
    }

    [Fact]
    public void Build_EmptyFilter_KeepsEverySubAgentRegardlessOfMarketplace()
    {
        var items = new List<SandboxSessionRegistry.DiscoveredItem>
        {
            new("subagent", "architecture-review", "arch", "/marketplaces/gb-plugins/agents/a.md",
                Content: Body, QualifiedName: "code-reviewer:architecture-review"),
            new("subagent", "thing", "x", "/marketplaces/superpowers/agents/o.md",
                Content: Body, QualifiedName: "superpowers:thing"),
        };
        var builder = new DiscoveredSubAgentTemplateBuilder(NullLogger<DiscoveredSubAgentTemplateBuilder>.Instance);

        var templates = builder.Build(items, [], AgentFactory);

        templates.Should().ContainKey("code-reviewer:architecture-review");
        templates.Should().ContainKey("superpowers:thing");
    }
}
