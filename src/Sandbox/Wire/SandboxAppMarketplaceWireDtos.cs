using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Per-app git marketplaces (gateway api/app_marketplaces.rs, ADR 0045) ---

/// <summary>Body of <c>PUT /api/v1/apps/self/marketplaces/{alias}</c>. A null ref is omitted: the gateway defaults it to <c>latest</c>.</summary>
internal sealed record AppMarketplaceRegisterDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("ref")] string? Ref
);

/// <summary>Response of <c>GET /api/v1/apps/self/marketplaces</c>.</summary>
internal sealed record AppMarketplaceListDto(
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<AppMarketplaceRowDto?>? Marketplaces
);

/// <summary>One registration row (gateway <c>AppMarketplaceRow</c>). Every field but <c>last_error</c> is required.</summary>
internal sealed record AppMarketplaceRowDto(
    [property: JsonPropertyName("app_id")] string? AppId,
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("repo_key")] string? RepoKey,
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("resolved_sha")] string? ResolvedSha,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("last_error")] string? LastError
);
