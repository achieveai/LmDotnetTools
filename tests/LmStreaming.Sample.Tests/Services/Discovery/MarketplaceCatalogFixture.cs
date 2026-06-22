namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Shared builders for <see cref="MarketplaceCatalog"/> test fixtures. Centralises the knowledge of
/// the positional <c>Catalog*</c> records (reordering a record parameter would otherwise break every
/// hand-built literal across the marketplace sub-agent tests) and the representative sample catalog.
/// </summary>
internal static class MarketplaceCatalogFixture
{
    internal static CatalogAgent Agent(
        string name, string? description = "does things", string plugin = "rev", string marketplace = "official")
        => new(name, description!, plugin, marketplace, $"agents/{name}.md");

    internal static CatalogPlugin Plugin(string name, params CatalogAgent[] agents)
        => new(name, "1.0.0", $"{name} plugin", Skills: [], Agents: agents);

    internal static CatalogMarketplace Marketplace(string alias, string? error, params CatalogPlugin[] plugins)
        => new(alias, error, plugins);

    internal static MarketplaceCatalog Catalog(params CatalogMarketplace[] marketplaces)
        => new(Selected: [.. marketplaces.Select(m => m.Alias)], Marketplaces: marketplaces);

    /// <summary>
    /// A representative catalog with two agent-bearing plugins (<c>orleans-reviewer</c>,
    /// <c>silent-failure-hunter</c>) under one marketplace — enough to prove flatten + visibility.
    /// </summary>
    internal static MarketplaceCatalog Sample()
        => Catalog(
            Marketplace("ClaudePlugins", error: null,
                Plugin("orleans-dev",
                    Agent("orleans-reviewer", "Senior Orleans code reviewer", "orleans-dev", "ClaudePlugins")),
                Plugin("pr-toolkit",
                    Agent("silent-failure-hunter", "Finds swallowed errors and silent failures", "pr-toolkit", "ClaudePlugins"))));
}
