using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using static LmStreaming.Sample.Tests.Services.Discovery.MarketplaceCatalogFixture;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Pins the marketplace → sub-agent bridge: the agents the UI's marketplace browser lists
/// (<see cref="MarketplaceCatalog"/>) must become spawnable <see cref="SubAgentTemplate"/>s, while
/// never shadowing a built-in or a real workspace-discovered file (<see cref="MarketplaceSubAgentLoader.MergeFillGaps"/>).
/// This is the regression guard for the "Agent tool omits marketplace agents" bug.
/// </summary>
public class MarketplaceSubAgentLoaderTests
{
    private static readonly Mock<IStreamingAgent> AgentStub = new();
    private static readonly Func<IStreamingAgent> AgentFactory = () => AgentStub.Object;

    // Catalog builders (Agent/Plugin/Marketplace/Catalog) come from MarketplaceCatalogFixture via the
    // `using static` above, shared with MarketplaceSubAgentCatalogVisibilityTests.

    [Fact]
    public void MapToTemplate_MapsCatalogFieldsIntoSpawnableTemplate()
    {
        var template = MarketplaceSubAgentLoader.MapToTemplate(
            Agent("code-reviewer", "Reviews code for bugs", plugin: "pr-toolkit", marketplace: "official"),
            AgentFactory);

        template.Name.Should().Be("code-reviewer");
        template.Description.Should().Be("Reviews code for bugs");
        template.WhenToUse.Should().Be("Reviews code for bugs");
        template.MaxTurnsPerRun.Should().Be(WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);
        template.EnabledTools.Should().BeNull("a catalog agent inherits every parent tool");
        template.AgentFactory.Should().BeSameAs(AgentFactory);

        // Best-effort persona prompt grounds the agent in its name + provenance + description.
        template.SystemPrompt.Should().Contain("code-reviewer");
        template.SystemPrompt.Should().Contain("pr-toolkit");
        template.SystemPrompt.Should().Contain("official");
        template.SystemPrompt.Should().Contain("Reviews code for bugs");
    }

    [Fact]
    public void MapToTemplate_BlankDescription_LeavesDescriptionNull()
    {
        var template = MarketplaceSubAgentLoader.MapToTemplate(Agent("planner", description: "   "), AgentFactory);

        template.Description.Should().BeNull();
        template.WhenToUse.Should().BeNull();
        template.SystemPrompt.Should().Contain("planner");
    }

    [Fact]
    public void MapCatalog_FlattensAgentsAcrossMarketplacesAndPlugins()
    {
        var catalog = Catalog(
            Marketplace("official", error: null,
                Plugin("pr-toolkit",
                    Agent("code-reviewer", plugin: "pr-toolkit"),
                    Agent("test-analyzer", plugin: "pr-toolkit")),
                Plugin("debugging", Agent("logging-review", plugin: "debugging"))),
            Marketplace("community", error: null,
                Plugin("orleans", Agent("orleans-reviewer", plugin: "orleans"))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        // Keys are the spawnable subagent_type values, and they are QUALIFIED by contributing plugin —
        // the same shape the gateway's QualifiedName gives WorkspaceSubAgentLoader for the same agent.
        result.Keys.Should().BeEquivalentTo(
            "pr-toolkit:code-reviewer",
            "pr-toolkit:test-analyzer",
            "debugging:logging-review",
            "orleans:orleans-reviewer");
    }

    [Fact]
    public void MapCatalog_KeysByQualifiedName_SoWorkspaceDiscoveryOfTheSameAgentDeduplicates()
    {
        // Live regression: workspace discovery keys by the gateway's QualifiedName ("code-reviewer:pr-review")
        // while this loader used the bare name ("pr-review"). MergeFillGaps can only suppress a duplicate it
        // can SEE, so every marketplace agent that was also discovered landed TWICE in the merged catalog —
        // the real template plus a description-only stub — and both were offered as spawnable subagent_types.
        var discovered = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["code-reviewer:pr-review"] = new SubAgentTemplate
            {
                Name = "pr-review",
                SystemPrompt = "REAL-BODY",
                AgentFactory = AgentFactory,
                MaxTurnsPerRun = WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun,
            },
        };
        var catalog = MarketplaceSubAgentLoader.MapCatalog(
            Catalog(Marketplace("gb-plugins", null, Plugin("code-reviewer", Agent("pr-review", plugin: "code-reviewer")))),
            AgentFactory);

        MarketplaceSubAgentLoader.MergeFillGaps(discovered, catalog, NullLogger.Instance);

        discovered.Keys.Should().BeEquivalentTo("code-reviewer:pr-review");
        discovered["code-reviewer:pr-review"].SystemPrompt.Should().Be("REAL-BODY");
    }

    [Fact]
    public void MapCatalog_AgentMissingItsPluginField_FallsBackToTheEnclosingPlugin()
    {
        // The gateway derives QualifiedName from the contributing plugin, and the enclosing
        // CatalogPlugin names that same plugin — so a catalog that omits the per-agent field must
        // still produce the qualified key, not silently reintroduce the duplicate.
        var catalog = Catalog(
            Marketplace("official", error: null, Plugin("p", Agent("loose", plugin: "  "))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Keys.Should().BeEquivalentTo("p:loose");
    }

    [Fact]
    public void MapCatalog_NothingNamesAContributingPlugin_KeepsTheBareName()
    {
        var catalog = Catalog(
            Marketplace("official", error: null, Plugin("  ", Agent("loose", plugin: "  "))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Keys.Should().BeEquivalentTo("loose");
    }

    [Fact]
    public void MapCatalog_SkipsAgentsWithBlankName()
    {
        var catalog = Catalog(
            Marketplace("official", error: null,
                Plugin("p", Agent("good"), Agent("   "), Agent(""))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Keys.Should().BeEquivalentTo("rev:good");
    }

    [Fact]
    public void MapCatalog_SameAgentNameUnderDifferentPlugins_StaysDistinct()
    {
        // Qualifying by plugin also un-collides two genuinely different agents that happen to share a
        // name — under bare keying the second was silently dropped.
        var catalog = Catalog(
            Marketplace("official", error: null,
                Plugin("a", Agent("dup", description: "FIRST", plugin: "a")),
                Plugin("b", Agent("dup", description: "SECOND", plugin: "b"))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result["a:dup"].Description.Should().Be("FIRST");
        result["b:dup"].Description.Should().Be("SECOND");
    }

    [Fact]
    public void MapCatalog_DuplicateQualifiedName_KeepsFirstOccurrence()
    {
        var catalog = Catalog(
            Marketplace("official", error: null,
                Plugin("a", Agent("dup", description: "FIRST", plugin: "shared")),
                Plugin("b", Agent("dup", description: "SECOND", plugin: "shared"))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Should().ContainKey("shared:dup");
        result["shared:dup"].Description.Should().Be("FIRST");
    }

    [Fact]
    public void MapCatalog_MarketplaceThatFailedToLoad_ContributesNothing()
    {
        // A marketplace the gateway couldn't read reports an Error and an empty plugin list — it must
        // not blow up the mapping nor contribute phantom agents.
        var catalog = Catalog(
            Marketplace("broken", error: "could not read marketplace.json"),
            Marketplace("official", error: null, Plugin("p", Agent("ok"))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Keys.Should().BeEquivalentTo("rev:ok");
    }

    [Fact]
    public void MergeFillGaps_AddsAgentsForKeysNotAlreadyPresent()
    {
        var existing = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = MarketplaceSubAgentLoader.MapToTemplate(Agent("general-purpose"), AgentFactory),
        };
        var catalog = MarketplaceSubAgentLoader.MapCatalog(
            Catalog(Marketplace("official", null, Plugin("p", Agent("code-reviewer")))),
            AgentFactory);

        MarketplaceSubAgentLoader.MergeFillGaps(existing, catalog, NullLogger.Instance);

        existing.Should().ContainKey("rev:code-reviewer");
    }

    [Fact]
    public void MergeFillGaps_DoesNotOverrideExistingTemplate()
    {
        // A built-in or a real workspace-discovered file (merged before the catalog) must keep its
        // place: the richer template wins, the catalog stub is dropped.
        var kept = new SubAgentTemplate
        {
            Name = "code-reviewer",
            Description = "REAL workspace file",
            SystemPrompt = "REAL-BODY",
            AgentFactory = AgentFactory,
            MaxTurnsPerRun = WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun,
        };
        var existing = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["rev:code-reviewer"] = kept,
        };
        var catalog = MarketplaceSubAgentLoader.MapCatalog(
            Catalog(Marketplace("official", null, Plugin("p", Agent("code-reviewer", description: "catalog stub")))),
            AgentFactory);

        MarketplaceSubAgentLoader.MergeFillGaps(existing, catalog, NullLogger.Instance);

        existing["rev:code-reviewer"].SystemPrompt.Should().Be("REAL-BODY");
        existing["rev:code-reviewer"].Description.Should().Be("REAL workspace file");
    }

    [Fact]
    public async Task LoadAsync_GatewayUnavailable_ReturnsEmptyWithoutThrowing()
    {
        var client = new Mock<IMarketplaceCatalogClient>();
        client
            .Setup(c => c.GetCatalogAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MarketplaceCatalogUnavailableException("gateway offline"));
        var loader = new MarketplaceSubAgentLoader(client.Object, NullLogger<MarketplaceSubAgentLoader>.Instance);

        var result = await loader.LoadAsync(marketplaces: null, AgentFactory);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_MapsCatalogReturnedByClient_AndPassesMarketplaceFilter()
    {
        var selected = new[] { "official" };
        var client = new Mock<IMarketplaceCatalogClient>();
        client
            .Setup(c => c.GetCatalogAsync(selected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Catalog(Marketplace("official", null, Plugin("p", Agent("code-reviewer")))));
        var loader = new MarketplaceSubAgentLoader(client.Object, NullLogger<MarketplaceSubAgentLoader>.Instance);

        var result = await loader.LoadAsync(selected, AgentFactory);

        result.Keys.Should().BeEquivalentTo("rev:code-reviewer");
        client.Verify(c => c.GetCatalogAsync(selected, It.IsAny<CancellationToken>()), Times.Once);
    }
}
