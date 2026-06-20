using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Models;

/// <summary>
/// App-side mirror of the sandbox gateway's <c>GET /api/v1/marketplaces/preview</c> response
/// (<c>CatalogResponse</c>): the marketplace aliases that were resolved plus the nested
/// plugin/skill/agent catalog for each. Surfaced by <c>MarketplacesController</c> so the UI can
/// browse available plugins without standing up a sandbox session.
/// </summary>
public sealed record MarketplaceCatalog(
    [property: JsonPropertyName("selected")] IReadOnlyList<string> Selected,
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<CatalogMarketplace> Marketplaces
);

/// <summary>One marketplace alias and the plugins it exposes (or an <see cref="Error"/> if it
/// failed to load, in which case <see cref="Plugins"/> is empty).</summary>
public sealed record CatalogMarketplace(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("plugins")] IReadOnlyList<CatalogPlugin> Plugins
);

/// <summary>A plugin within a marketplace and the skills/agents it contributes.</summary>
public sealed record CatalogPlugin(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("skills")] IReadOnlyList<CatalogSkill> Skills,
    [property: JsonPropertyName("agents")] IReadOnlyList<CatalogAgent> Agents
);

/// <summary>A skill the gateway discovered in a plugin.</summary>
public sealed record CatalogSkill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("marketplace")] string Marketplace,
    [property: JsonPropertyName("path")] string Path
);

/// <summary>A sub-agent the gateway discovered in a plugin.</summary>
public sealed record CatalogAgent(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("marketplace")] string Marketplace,
    [property: JsonPropertyName("path")] string Path
);
