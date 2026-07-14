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

// `auth_provider` is `Option<String>` on the gateway's NetworkRule (proxy_policy/models.rs,
// SandboxedOsToolsMcpServer@c0dc9cfe) and, when present, is looked up by id (proxy.rs) — a
// present-but-empty string is `Some("")`, a lookup the gateway fails, not "no provider". Kept
// nullable here (and omitted from the wire via SandboxJson.RestOptions) so "no auth provider"
// serializes as field-absent, matching the gateway's `None`.
internal sealed record NetworkRuleDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("hosts")] IReadOnlyList<string> Hosts,
    [property: JsonPropertyName("ports")] IReadOnlyList<int> Ports,
    [property: JsonPropertyName("methods")] IReadOnlyList<string> Methods,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("auth_provider")] string? AuthProvider,
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
    [property: JsonPropertyName("read_only")] bool ReadOnly,
    // The persisted session_mounts.id (issue #119) — the integer every direct file/command API is
    // keyed by. Always present on a #119 create/get response (MountSummary.id is non-optional there);
    // nullable here because the list response carries no volumes and a pre-#119 gateway omits it.
    [property: JsonPropertyName("id")] long? Id
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

// "name" is `Option<String>` on the gateway's `DiscoveredFile` (crates/mcp-gateway/src/api/
// sandboxes.rs) and is omitted from the wire entirely for a "context_file" item (and any other
// kind the gateway has no name for) — only "kind" and "path" are non-optional there.
internal sealed record DiscoveredItemDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string? Name,
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

// --- Direct file/command API REST contract (ADR 0031 / issue #119, snake_case via RestOptions) ---
// Every direct route is keyed by the integer mount id (session_mounts.id), surfaced as
// volumes.workspace.id on the create/get response (see WorkspaceVolumeDto.Id). Field names were
// verified against the gateway's serde structs (crates/mcp-gateway/src/api/{operations,files,
// directories}.rs and src/direct/error.rs, SandboxedOsToolsMcpServer@f4df670), not assumed.

/// The working directory of an operation: a mount id plus a mount-relative path (empty/<c>.</c> = mount root).
internal sealed record OperationCwdDto(
    [property: JsonPropertyName("mount_id")] long MountId,
    [property: JsonPropertyName("path")] string Path
);

/// <c>POST .../operations</c> body — a native <c>executable</c> + argv (no shell), with optional env
/// overlay, mount-relative cwd, wall-clock timeout, and combined stdout+stderr byte cap. Empty
/// <c>args</c>/<c>env</c> serialize as field-absent (the gateway defaults them), matching this SDK's
/// nulls-omitted REST convention.
internal sealed record CreateOperationRequestDto(
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env,
    [property: JsonPropertyName("cwd")] OperationCwdDto? Cwd,
    [property: JsonPropertyName("timeout_secs")] long? TimeoutSecs,
    [property: JsonPropertyName("max_output_bytes")] long? MaxOutputBytes
);

/// Workspace mount-relative references to an operation's captured stdout/stderr, downloadable through
/// the files API once the operation is terminal.
internal sealed record OperationArtifactsDto(
    [property: JsonPropertyName("mount_id")] long MountId,
    [property: JsonPropertyName("stdout_path")] string StdoutPath,
    [property: JsonPropertyName("stderr_path")] string StderrPath
);

/// <c>POST</c>/<c>GET .../operations</c> status snapshot. <c>status</c> is one of
/// <c>running|succeeded|failed|timed_out|output_limit_exceeded|internal_failure</c>; <c>error</c>
/// carries a sanitized message on the failure states (never surfaced by the SDK); <c>artifacts</c> is
/// present once the session has a writable workspace (always true for a submitted operation).
internal sealed record OperationStatusDto(
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("terminal_at")] string? TerminalAt,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("stdout_bytes")] long? StdoutBytes,
    [property: JsonPropertyName("stderr_bytes")] long? StderrBytes,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("artifacts")] OperationArtifactsDto? Artifacts
);

/// One entry of a directory listing. <c>type</c> is <c>file|directory|symlink</c> (symlinks reported,
/// never followed); <c>size</c> is present only for files; <c>name_lossy</c> marks a non-UTF-8 name
/// rendered lossily (absent implies <c>false</c>).
internal sealed record DirectoryEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("name_lossy")] bool? NameLossy
);

/// <c>GET .../directories/{mount_id}</c> response — one byte-sorted, non-recursive page.
/// <c>next_cursor</c> is present only when more entries remain (an opaque, self-contained token).
internal sealed record ListDirectoryResponseDto(
    [property: JsonPropertyName("entries")] IReadOnlyList<DirectoryEntryDto>? Entries,
    [property: JsonPropertyName("next_cursor")] string? NextCursor
);

/// <c>PUT .../files/{mount_id}</c> response.
internal sealed record WriteFileResponseDto([property: JsonPropertyName("bytes_written")] long BytesWritten);

/// The gateway's stable direct-API error body: <c>{ error, code, error_code, retryable }</c>. Only
/// the fixed-string <c>error_code</c> — a closed, gateway-defined vocabulary, not caller free text —
/// is ever surfaced by the SDK; the human <c>error</c> message is never echoed into a
/// <see cref="SandboxException"/> (it may contain captured output/credential material).
internal sealed record GatewayErrorDto(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("retryable")] bool? Retryable
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
