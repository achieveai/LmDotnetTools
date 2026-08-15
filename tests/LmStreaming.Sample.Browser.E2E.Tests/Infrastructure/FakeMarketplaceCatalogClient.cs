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

    /// <summary>
    /// A catalog whose marketplaces publish named plugins, alongside an explicit gateway capability
    /// advertisement — the two inputs the per-plugin selection UI is driven by.
    /// </summary>
    /// <param name="pluginFiltering">
    /// What the gateway advertises for <c>capabilities.pluginFiltering</c>. Deliberately nullable so a
    /// scenario can model all THREE states the SPA's fail-closed gate must tell apart: advertised
    /// <see langword="true"/>, advertised <see langword="false"/>, and no capability block at all
    /// (<see langword="null"/> — a gateway predating the feature). Only <see langword="true"/> may
    /// enable the per-plugin UI; the other two are equivalent to the UI but not to this fake, which is
    /// why they stay separately expressible.
    /// </param>
    /// <param name="marketplaces">Alias and the plugin names it publishes, in render order.</param>
    public static FakeMarketplaceCatalogClient WithPlugins(
        bool? pluginFiltering,
        params (string Alias, string[] Plugins)[] marketplaces)
    {
        ArgumentNullException.ThrowIfNull(marketplaces);
        var catalog = new MarketplaceCatalog(
            Selected: [.. marketplaces.Select(m => m.Alias)],
            Marketplaces:
            [
                .. marketplaces.Select(m => new CatalogMarketplace(
                    Alias: m.Alias,
                    Error: null,
                    Plugins:
                    [
                        .. m.Plugins.Select(p => new CatalogPlugin(
                            Name: p,
                            Version: "1.0.0",
                            Description: $"{p} (E2E fake)",
                            Skills: [],
                            Agents: []))
                    ]))
            ])
        {
            // Capabilities is init-only with a fail-closed default, so forgetting it here is silent:
            // that is exactly how the per-plugin UI came to render in ZERO E2E tests. A SECOND
            // capability added the same way would default the same way, and nothing would say so —
            // `required` is the only compile-time defense and is ruled out by the same decision that
            // made this init-only (MarketplaceCatalog.cs:17-20: keep existing call sites compiling).
            //
            // The defense is therefore a runtime tripwire, and it is PER ASSERTED REGION, not per
            // capability: the pluginFiltering:true row of the capability-gate theory goes red only
            // because it asserts the per-plugin UI is PRESENT. A capability gating some other region
            // (skill filtering, agent filtering) would default here and leave that row green, because
            // the row never looks there. So adding capability N means adding a positive row that
            // asserts N's own UI appears — extending this factory alone does not cover it.
            Capabilities = new MarketplaceCapabilities(pluginFiltering),
        };
        return new(catalog);
    }

    /// <summary>Returns an alias-only catalog for scenarios that validate workspace selections.</summary>
    public static FakeMarketplaceCatalogClient WithAliases(params string[] aliases) =>
        new(new MarketplaceCatalog(
            Selected: aliases,
            Marketplaces: [.. aliases.Select(alias => new CatalogMarketplace(alias, null, []))]));

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
