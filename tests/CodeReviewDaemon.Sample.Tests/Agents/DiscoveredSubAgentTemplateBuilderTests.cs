using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
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

    [Fact]
    public void Build_LogsEverySharedParserDiagnostic()
    {
        var logger = new CapturingLogger<DiscoveredSubAgentTemplateBuilder>();
        var builder = new DiscoveredSubAgentTemplateBuilder(logger);
        var items = ItemsWithFrontmatter(
            """
            modelintelligence: 7
            effort: maximum
            """);

        var templates = builder.Build(items, "code-reviewer", AgentFactory);

        templates.Should().ContainKey("code-reviewer:architecture-review");
        logger.WarningCount("modelintelligence must be an integer").Should().Be(1);
        logger.WarningCount("effort must be one of").Should().Be(1);
    }

    [Theory]
    [InlineData("modelintelligence: 4\neffort: high", "")]
    [InlineData("modelintelligence: invalid\neffort: maximum", "")]
    [InlineData(
        "model: claude-sonnet-5\nmodelintelligence: 4\neffort: extra-high",
        "model: claude-sonnet-5")]
    public void Build_CharacteristicsFrontmatter_DoesNotChangeDaemonRequestOptions(
        string characteristicsFrontmatter,
        string baselineModelFrontmatter)
    {
        var builder = new DiscoveredSubAgentTemplateBuilder(
            NullLogger<DiscoveredSubAgentTemplateBuilder>.Instance);
        var baseline = builder.Build(
            ItemsWithFrontmatter(baselineModelFrontmatter),
            "code-reviewer",
            AgentFactory)["code-reviewer:architecture-review"];

        var actual = builder.Build(
            ItemsWithFrontmatter(characteristicsFrontmatter),
            "code-reviewer",
            AgentFactory)["code-reviewer:architecture-review"];

        JsonSerializer.SerializeToUtf8Bytes(actual.DefaultOptions)
            .Should().Equal(JsonSerializer.SerializeToUtf8Bytes(baseline.DefaultOptions));
        actual.CharacteristicsAgentFactory.Should().BeNull();
        actual.AgentFactory.Should().BeSameAs(AgentFactory);
    }

    private static IReadOnlyList<SandboxSessionRegistry.DiscoveredItem> ItemsWithFrontmatter(
        string additionalFrontmatter)
    {
        var content = $"""
            ---
            name: architecture-review
            description: Reviews architecture.
            {additionalFrontmatter}
            ---
            You review architecture across the connected repos.
            """;
        return
        [
            new(
                "subagent",
                "architecture-review",
                "arch",
                "/marketplaces/gb-plugins/agents/a.md",
                Content: content,
                QualifiedName: "code-reviewer:architecture-review"),
        ];
    }
}
