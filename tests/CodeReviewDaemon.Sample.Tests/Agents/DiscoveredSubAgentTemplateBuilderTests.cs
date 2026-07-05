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
    public void Build_KeepsCodeReviewerSubAgents_KeyedByQualifiedName()
    {
        var items = new List<SandboxSessionRegistry.DiscoveredItem>
        {
            new("subagent", "architecture-review", "arch", "/marketplaces/gb-plugins/agents/a.md",
                Content: Body, QualifiedName: "code-reviewer:architecture-review"),
            new("subagent", "other", "x", "/marketplaces/other/agents/o.md",
                Content: Body, QualifiedName: "other-plugin:thing"),
            new("skill", "review", null, ".claude/skills/review.md"),
        };
        var builder = new DiscoveredSubAgentTemplateBuilder(NullLogger<DiscoveredSubAgentTemplateBuilder>.Instance);

        var templates = builder.Build(items, "code-reviewer", AgentFactory);

        templates.Should().ContainKey("code-reviewer:architecture-review");
        templates.Should().NotContainKey("other-plugin:thing");
        templates["code-reviewer:architecture-review"].SystemPrompt.Should().Contain("review architecture");
    }
}
