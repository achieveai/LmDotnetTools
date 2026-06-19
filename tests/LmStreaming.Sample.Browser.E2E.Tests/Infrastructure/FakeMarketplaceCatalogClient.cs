using LmStreaming.Sample.Models;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="IMarketplaceCatalogClient"/> so browser E2E scenarios can exercise the
/// marketplace UI with NO live sandbox gateway. Returns a fixed catalog, or — via
/// <see cref="Offline"/> — simulates the gateway being down so the UI's offline state can be covered.
/// </summary>
public sealed class FakeMarketplaceCatalogClient : IMarketplaceCatalogClient
{
    private readonly MarketplaceCatalog? _catalog;

    private FakeMarketplaceCatalogClient(MarketplaceCatalog? catalog) => _catalog = catalog;

    /// <summary>A small, representative catalog (one marketplace, one plugin with a skill + agent).</summary>
    public static FakeMarketplaceCatalogClient WithSampleCatalog() =>
        new(new MarketplaceCatalog(
            Selected: ["ClaudePlugins"],
            Marketplaces:
            [
                new CatalogMarketplace(
                    Alias: "ClaudePlugins",
                    Error: null,
                    Plugins:
                    [
                        new CatalogPlugin(
                            Name: "orleans-dev",
                            Version: "1.0.2",
                            Description: "Orleans patterns, best practices, and code review.",
                            Skills:
                            [
                                new CatalogSkill(
                                    "orleans-patterns", "Orleans patterns and rules", "orleans-dev",
                                    "ClaudePlugins", "/marketplaces/ClaudePlugins/orleans-dev/skills/orleans-patterns/")
                            ],
                            Agents:
                            [
                                new CatalogAgent(
                                    "orleans-reviewer", "Senior Orleans code reviewer", "orleans-dev",
                                    "ClaudePlugins", "/marketplaces/ClaudePlugins/orleans-dev/agents/orleans-reviewer.md")
                            ]
                        )
                    ]
                )
            ]));

    /// <summary>Simulates the gateway being unreachable, driving the UI's offline state.</summary>
    public static FakeMarketplaceCatalogClient Offline() => new(catalog: null);

    public Task<MarketplaceCatalog> GetCatalogAsync(
        IReadOnlyList<string>? marketplaces = null,
        CancellationToken ct = default)
    {
        return _catalog is null
            ? Task.FromException<MarketplaceCatalog>(
                new MarketplaceCatalogUnavailableException("gateway offline (E2E fake)"))
            : Task.FromResult(_catalog);
    }
}
