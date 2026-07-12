using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Gateway sandbox-create/get/list REST contract (snake_case via SandboxJson.RestOptions) ---
// POST/GET/DELETE /api/v1/sandboxes mirror the exact shape LmAgentInfra.Sandbox.SandboxSessionRegistry
// already speaks against the gateway, so that wire contract this SDK relies on is unchanged. The list
// endpoint (GET /api/v1/sandboxes, no path segment) is DIFFERENT — see ContainerEntryDto below — and
// was verified directly against the gateway's handler source rather than assumed.

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
/// One entry of <c>GET /api/v1/sandboxes</c> (list) — verified against the gateway's actual handler
/// (<c>list_sandboxes</c> in <c>crates/mcp-gateway/src/api/sandboxes.rs</c>,
/// SandboxedOsToolsMcpServer@c0dc9cfe). This is the gateway's Docker-level container inventory
/// (<c>ContainerEntry</c> = its <c>docker::ContainerInfo</c> flattened — <c>id</c>/<c>names</c>/
/// <c>state</c>/<c>status</c>/<c>created</c>/<c>running</c>/<c>finished_at</c>/<c>started_at</c> —
/// plus <c>session_id</c>), a DIFFERENT shape from the create/get response
/// (<see cref="CreateSandboxResponseDto"/>): the container id field is <c>id</c>, not
/// <c>container_id</c>, there is no <c>volumes</c> field at all, and <c>session_id</c> is nullable (a
/// live container the gateway hasn't attributed to any session, or — for the dormant tail the
/// handler appends — a persisted session whose container is gone). Only the fields this SDK's
/// <see cref="SandboxInfo"/> surfaces are modeled; the rest are left for the JSON reader's default
/// unknown-member handling to ignore.
/// </summary>
internal sealed record ContainerEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("session_id")] string? SessionId
);

/// <summary>Response shape for <c>GET /api/v1/sandboxes</c> (list). See <see cref="ContainerEntryDto"/>.</summary>
internal sealed record ListSandboxesResponseDto([property: JsonPropertyName("sandboxes")] IReadOnlyList<ContainerEntryDto>? Sandboxes);

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
