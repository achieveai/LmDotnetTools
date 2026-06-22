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
                Plugin("pr-toolkit", Agent("code-reviewer"), Agent("test-analyzer")),
                Plugin("debugging", Agent("logging-review"))),
            Marketplace("community", error: null,
                Plugin("orleans", Agent("orleans-reviewer"))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Keys.Should().BeEquivalentTo(
            "code-reviewer", "test-analyzer", "logging-review", "orleans-reviewer");
    }

    [Fact]
    public void MapCatalog_SkipsAgentsWithBlankName()
    {
        var catalog = Catalog(
            Marketplace("official", error: null,
                Plugin("p", Agent("good"), Agent("   "), Agent(""))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Keys.Should().BeEquivalentTo("good");
    }

    [Fact]
    public void MapCatalog_DuplicateAgentName_KeepsFirstOccurrence()
    {
        var catalog = Catalog(
            Marketplace("official", error: null,
                Plugin("a", Agent("dup", description: "FIRST")),
                Plugin("b", Agent("dup", description: "SECOND"))));

        var result = MarketplaceSubAgentLoader.MapCatalog(catalog, AgentFactory);

        result.Should().ContainKey("dup");
        result["dup"].Description.Should().Be("FIRST");
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

        result.Keys.Should().BeEquivalentTo("ok");
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

        existing.Should().ContainKey("code-reviewer");
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
            ["code-reviewer"] = kept,
        };
        var catalog = MarketplaceSubAgentLoader.MapCatalog(
            Catalog(Marketplace("official", null, Plugin("p", Agent("code-reviewer", description: "catalog stub")))),
            AgentFactory);

        MarketplaceSubAgentLoader.MergeFillGaps(existing, catalog, NullLogger.Instance);

        existing["code-reviewer"].SystemPrompt.Should().Be("REAL-BODY");
        existing["code-reviewer"].Description.Should().Be("REAL workspace file");
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

        result.Keys.Should().BeEquivalentTo("code-reviewer");
        client.Verify(c => c.GetCatalogAsync(selected, It.IsAny<CancellationToken>()), Times.Once);
    }
}
