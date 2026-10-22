using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Per-agent skill tiers REST contract (gateway ADR 0041, v0.1.17 @5c4eeefc) ---
// Verified against crates/mcp-gateway/src/api/skills.rs (SkillCatalog, SkillRow, PluginNode,
// MarketplaceNode, WorkspaceNode, TierCounts, PatchSkillsRequest) and src/skills/tiers.rs
// (SkillTier "lowercase", TierOrigin "snake_case", SkillsRequest/SkillsPolicy). Response fields the
// gateway always sends are still nullable here so a missing one is reported as a Protocol error by the
// mapper instead of silently defaulting.

/// <summary>
/// The create request's <c>skills</c> block (<c>deny_unknown_fields</c> upstream) and, on the create
/// response, the resolved policy the gateway echoes. Nulls are omitted on write, so a
/// <c>null</c> <c>unlisted_hint</c> means "gateway default trailer" and <c>""</c> survives as "no trailer".
/// </summary>
internal sealed record SkillsPolicyDto(
    [property: JsonPropertyName("default_tier")] string? DefaultTier,
    [property: JsonPropertyName("included")] IReadOnlyList<string>? Included,
    [property: JsonPropertyName("unlisted_hint")] string? UnlistedHint
);

/// <summary><c>PATCH …/skills</c> body: <c>{ "set": { ref: tier }, "reset": bool }</c>.</summary>
internal sealed record PatchSkillsRequestDto(
    [property: JsonPropertyName("set")] IReadOnlyDictionary<string, string> Set,
    [property: JsonPropertyName("reset")] bool Reset
);

/// <summary><c>GET</c>/<c>PATCH …/skills</c> response. <c>tools_list_changed</c> is sent by <c>PATCH</c> only.</summary>
internal sealed record SkillCatalogDto(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("default_tier")] string? DefaultTier,
    [property: JsonPropertyName("counts")] SkillCountsDto? Counts,
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<SkillMarketplaceDto?>? Marketplaces,
    [property: JsonPropertyName("workspace")] SkillWorkspaceDto? Workspace,
    [property: JsonPropertyName("tools_list_changed")] bool? ToolsListChanged = null
);

internal sealed record SkillCountsDto(
    [property: JsonPropertyName("included")] int? Included,
    [property: JsonPropertyName("available")] int? Available,
    [property: JsonPropertyName("unavailable")] int? Unavailable,
    [property: JsonPropertyName("loaded")] int? Loaded
);

internal sealed record SkillMarketplaceDto(
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("plugins")] IReadOnlyList<SkillPluginDto?>? Plugins
);

// `version` is Option<String> upstream (absent from plugin.json ⇒ null); `description` is a String.
internal sealed record SkillPluginDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("skills")] IReadOnlyList<SkillRowDto?>? Skills
);

internal sealed record SkillWorkspaceDto([property: JsonPropertyName("skills")] IReadOnlyList<SkillRowDto?>? Skills);

// `notified` is skipped upstream unless the row is a workspace skill; `shadowed` is skipped unless true.
internal sealed record SkillRowDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("tier_origin")] string? TierOrigin,
    [property: JsonPropertyName("loaded")] bool? Loaded,
    [property: JsonPropertyName("loaded_at")] DateTimeOffset? LoadedAt,
    [property: JsonPropertyName("notified")] bool? Notified = null,
    [property: JsonPropertyName("shadowed")] bool? Shadowed = null
);
