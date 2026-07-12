using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Gateway sandbox-create/get/list REST contract (snake_case via SandboxJson.RestOptions) ---
// Mirrors the exact shape LmAgentInfra.Sandbox.SandboxSessionRegistry already speaks against the
// gateway (POST/GET/DELETE /api/v1/sandboxes) so the wire contract this SDK relies on is unchanged.

internal sealed record CreateSandboxRequestDto(
    [property: JsonPropertyName("app")] AppRefDto App,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("auth_providers")] IReadOnlyList<AuthProviderDto>? AuthProviders,
    [property: JsonPropertyName("network")] NetworkDto? Network,
    [property: JsonPropertyName("discovery")] DiscoveryDto? Discovery,
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<string>? Marketplaces
);

internal sealed record AppRefDto([property: JsonPropertyName("id")] string Id);

internal sealed record AuthProviderDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("gateway_auth")] string GatewayAuth,
    [property: JsonPropertyName("cache_ttl_seconds")] int CacheTtlSeconds,
    [property: JsonPropertyName("required_scopes")] IReadOnlyList<string> RequiredScopes
);

internal sealed record NetworkDto([property: JsonPropertyName("rules")] IReadOnlyList<NetworkRuleDto> Rules);

internal sealed record NetworkRuleDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("hosts")] IReadOnlyList<string> Hosts,
    [property: JsonPropertyName("ports")] IReadOnlyList<int> Ports,
    [property: JsonPropertyName("methods")] IReadOnlyList<string> Methods,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("auth_provider")] string AuthProvider,
    [property: JsonPropertyName("required_scopes")] IReadOnlyList<string> RequiredScopes,
    [property: JsonPropertyName("priority")] int Priority
);

internal sealed record DiscoveryDto([property: JsonPropertyName("webhook")] DiscoveryWebhookDto Webhook);

internal sealed record DiscoveryWebhookDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("auth_header")] string Auth
);

internal sealed record CreateSandboxResponseDto(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("container_id")] string? ContainerId,
    [property: JsonPropertyName("volumes")] VolumesDto? Volumes
);

internal sealed record VolumesDto([property: JsonPropertyName("workspace")] WorkspaceVolumeDto? Workspace);

internal sealed record WorkspaceVolumeDto(
    [property: JsonPropertyName("container_path")] string? ContainerPath,
    [property: JsonPropertyName("read_only")] bool ReadOnly
);

/// <summary>
/// Response shape for <c>GET /api/v1/sandboxes</c> (list). No public gateway contract test pins this
/// shape yet (list is not exercised by any existing caller this SDK was extracted from) — it mirrors
/// the create/get response element shape under a <c>sandboxes</c> wrapper, matching the gateway's
/// existing "wrap the collection in a named field" convention (see <see cref="DiscoveredItemsResponseDto"/>).
/// </summary>
internal sealed record ListSandboxesResponseDto(
    [property: JsonPropertyName("sandboxes")] IReadOnlyList<CreateSandboxResponseDto>? Sandboxes
);

// --- Session-discovery REST contract ---

internal sealed record DiscoveredItemDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("qualified_name")] string? QualifiedName
);

internal sealed record DiscoveredItemsResponseDto(
    [property: JsonPropertyName("discovered")] IReadOnlyList<DiscoveredItemDto>? Items
);

// --- Marketplace preview REST contract ---

internal sealed record MarketplaceCatalogDto(
    [property: JsonPropertyName("selected")] IReadOnlyList<string>? Selected,
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<MarketplaceEntryDto>? Marketplaces
);

internal sealed record MarketplaceEntryDto(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("plugins")] IReadOnlyList<MarketplacePluginDto>? Plugins
);

internal sealed record MarketplacePluginDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("skills")] IReadOnlyList<MarketplaceSkillDto>? Skills,
    [property: JsonPropertyName("agents")] IReadOnlyList<MarketplaceAgentDto>? Agents
);

internal sealed record MarketplaceSkillDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("marketplace")] string? Marketplace,
    [property: JsonPropertyName("path")] string? Path
);

internal sealed record MarketplaceAgentDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("marketplace")] string? Marketplace,
    [property: JsonPropertyName("path")] string? Path
);

// --- MCP JSON-RPC contract (verbatim field names via SandboxJson.McpOptions) ---

internal sealed record McpToolCallRequestDto(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] McpToolCallParamsDto Params
);

internal sealed record McpToolCallParamsDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] object Arguments
);

internal sealed record McpRpcErrorDto(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message
);
