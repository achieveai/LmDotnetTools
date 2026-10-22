using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Skill tiers REST contract (gateway ADR 0041, crates/mcp-gateway/src/api/skills.rs) ---
// GET/PATCH /api/v1/sandboxes/{session_id}/skills?agent_id= → SkillCatalog. Every field the gateway always
// sends is nullable here so a missing one is reported as a Protocol error by the mapper, not defaulted.

internal sealed record SkillCatalogDto(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("default_tier")] string? DefaultTier,
    [property: JsonPropertyName("counts")] SkillCountsDto? Counts,
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<SkillMarketplaceDto?>? Marketplaces,
    [property: JsonPropertyName("workspace")] SkillWorkspaceDto? Workspace,
    // PATCH only.
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

internal sealed record SkillPluginDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("skills")] IReadOnlyList<SkillRowDto?>? Skills
);

internal sealed record SkillWorkspaceDto([property: JsonPropertyName("skills")] IReadOnlyList<SkillRowDto?>? Skills);

internal sealed record SkillRowDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("tier_origin")] string? TierOrigin,
    [property: JsonPropertyName("loaded")] bool? Loaded,
    [property: JsonPropertyName("loaded_at")] DateTimeOffset? LoadedAt,
    // Workspace skills only; marketplace rows omit it.
    [property: JsonPropertyName("notified")] bool? Notified = null,
    // Marketplace skills only; omitted when false.
    [property: JsonPropertyName("shadowed")] bool? Shadowed = null
);

/// <summary><c>PATCH …/skills</c> body (the gateway denies unknown fields).</summary>
internal sealed record PatchSkillsRequestDto(
    [property: JsonPropertyName("set")] IReadOnlyDictionary<string, string> Set,
    [property: JsonPropertyName("reset")] bool Reset
);
