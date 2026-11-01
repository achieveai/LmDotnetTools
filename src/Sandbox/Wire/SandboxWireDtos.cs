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
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<string>? Marketplaces,
    // Deliberately camelCase "pluginSelection", NOT "plugins": the gateway's sandbox-create contract
    // already reserves the top-level "plugins" key for volume/plugin MOUNT data.
    [property: JsonPropertyName("pluginSelection")] IReadOnlyList<PluginRefDto>? PluginSelection = null,
    // Per-sandbox environment variables (gateway PR #183 / v0.1.11). Omitted entirely when the
    // caller's request has none, matching this SDK's nulls-omitted REST convention.
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env = null,
    [property: JsonPropertyName("plugins")] IReadOnlyList<PluginMountDto>? Plugins = null,
    // Client image (ADR 0039) and working directory (ADR 0044). Top-level `working_dir` mirrors the
    // top-level `workspace` this DTO already sends (the gateway accepts both it and `mounts.working_dir`).
    // The deprecated `home` alias is never sent. Omitted when unset.
    [property: JsonPropertyName("image")] string? Image = null,
    [property: JsonPropertyName("working_dir")] string? WorkingDir = null,
    // Property bag (ADR 0042), the unified sandbox webhook (gateway PR #205) and skill tiers (ADR 0041).
    // Each is omitted when the caller set none, so the gateway applies its own defaults.
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, string>? Properties = null,
    [property: JsonPropertyName("webhook")] SandboxWebhookDto? Webhook = null,
    [property: JsonPropertyName("skills")] SkillsPolicyDto? Skills = null,
    [property: JsonPropertyName("mcp")] McpPolicyDto? Mcp = null
);

internal sealed record McpPolicyDto(
    [property: JsonPropertyName("egress")] string? Egress,
    [property: JsonPropertyName("servers")] IReadOnlyDictionary<string, McpServerPolicyDto>? Servers
);

internal sealed record McpServerPolicyDto(
    [property: JsonPropertyName("auth")] string? Auth,
    [property: JsonPropertyName("headers")] IReadOnlyList<string>? Headers,
    [property: JsonPropertyName("cache_ttl_seconds")] int? CacheTtlSeconds,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("url")] string? Url
);

/// <summary>
/// The create request's <c>webhook</c> block (gateway <c>webhook/config.rs</c> <c>SandboxWebhook</c>, which
/// denies unknown fields). <c>events</c> absent means every event, including ones added later.
/// </summary>
internal sealed record SandboxWebhookDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("auth_header")] string? AuthHeader,
    [property: JsonPropertyName("events")] IReadOnlyList<string>? Events
);

/// <summary><c>webhook</c> on <c>GET /api/v1/sandboxes/{id}</c>: never the auth header value, only whether one is set.</summary>
internal sealed record WebhookSummaryDto(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("events")] IReadOnlyList<string?>? Events,
    [property: JsonPropertyName("auth_header_set")] bool? AuthHeaderSet
);

/// <summary>
/// The create request's <c>skills</c> block and the resolved policy echoed on the create response (gateway
/// <c>skills/tiers.rs</c>). On the response <c>included</c> holds skill keys, not the refs the caller sent.
/// </summary>
internal sealed record SkillsPolicyDto(
    [property: JsonPropertyName("default_tier")] string? DefaultTier,
    [property: JsonPropertyName("included")] IReadOnlyList<string?>? Included,
    [property: JsonPropertyName("unlisted_hint")] string? UnlistedHint
);

internal sealed record PluginMountDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("origin")] string Origin
);

internal sealed record PluginRefDto(
    [property: JsonPropertyName("marketplace")] string Marketplace,
    [property: JsonPropertyName("plugin")] string Plugin
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

// `default_action` is always "deny". The gateway reads it per sandbox (SandboxNetworkPolicy, serde
// default Deny) and applies it to any request no rule matches. Sending it means a gateway build whose
// default changed cannot open all egress. `rules` is null (omitted) when the caller has none.
internal sealed record NetworkDto(
    [property: JsonPropertyName("default_action")] string DefaultAction,
    [property: JsonPropertyName("rules")] IReadOnlyList<NetworkRuleDto>? Rules
);

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
    [property: JsonPropertyName("volumes")] VolumesDto? Volumes,
    // Both fields below are additive and absent on every gateway built before them. `status` stays
    // null rather than being defaulted to a guess — the SDK reports what the gateway said, and an
    // invented status would be indistinguishable from a reported one.
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("inventory")] SandboxInventoryDto? Inventory = null,
    [property: JsonPropertyName("pluginResolution")] PluginResolutionDto? PluginResolution = null,
    // ADR 0039: present on the CREATE response; the GET detail carries `image`/`image_digest` only on gateways
    // that report them (never `image_report`).
    [property: JsonPropertyName("image")] string? Image = null,
    [property: JsonPropertyName("image_digest")] string? ImageDigest = null,
    [property: JsonPropertyName("image_report")] ImageReportDto? ImageReport = null,
    // ADR 0042: `properties` is always present on a current gateway (`{}` when none); `partition` only for a
    // partitioned app. `webhook` is GET-only; `skills` is create-only (ADR 0041).
    [property: JsonPropertyName("properties")] Dictionary<string, string>? Properties = null,
    [property: JsonPropertyName("partition")] IReadOnlyList<string?>? Partition = null,
    [property: JsonPropertyName("webhook")] WebhookSummaryDto? Webhook = null,
    [property: JsonPropertyName("skills")] SkillsPolicyDto? Skills = null,
    [property: JsonPropertyName("mcp")] System.Text.Json.JsonElement? Mcp = null
);

/// <summary>
/// How the gateway resolved the request's plugin selection. <c>requested</c> is tri-state and must
/// never be collapsed to an empty list — an absent field means "no explicit selection was made".
/// </summary>
internal sealed record PluginResolutionDto(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("requested")] IReadOnlyList<PluginRefDto>? Requested,
    [property: JsonPropertyName("effective")] IReadOnlyList<PluginRefDto>? Effective,
    [property: JsonPropertyName("failed")] IReadOnlyList<PluginRefDto>? Failed
);

/// <summary>
/// What the gateway confirmed it loaded into the session, reported atomically with the create
/// result. Distinct from <see cref="MarketplaceCatalogDto"/> (a browse of what is <i>available</i>)
/// and from the create request's own <c>marketplaces</c> selection (what was <i>asked for</i>) —
/// see <see cref="SandboxInventory"/> for why the three must never be conflated.
/// </summary>
internal sealed record SandboxInventoryDto(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("unavailable_reason")] string? UnavailableReason,
    [property: JsonPropertyName("items")] IReadOnlyList<SandboxInventoryItemDto>? Items
);

/// <summary>
/// One confirmed-loaded item. Only identity and version cross the wire into
/// <see cref="SandboxInventoryItem"/>; a gateway that sends more has the rest ignored by the JSON
/// reader's default unknown-member handling.
/// </summary>
internal sealed record SandboxInventoryItemDto(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("version")] string? Version
);

internal sealed record VolumesDto([property: JsonPropertyName("workspace")] WorkspaceVolumeDto? Workspace);

internal sealed record WorkspaceVolumeDto(
    [property: JsonPropertyName("container_path")] string? ContainerPath,
    [property: JsonPropertyName("read_only")] bool ReadOnly,
    // The persisted session_mounts.id (issue #119) — the integer every direct file/command API is
    // keyed by. Always present on a #119 create/get response (MountSummary.id is non-optional there);
    // nullable here because the list response carries no volumes and a pre-#119 gateway omits it.
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("relative_path")] string? RelativePath = null,
    // The normalized, persisted working directory (ADR 0044); absent when none is set. The legacy `home`
    // echo is not modeled: a gateway that sends only `home` predates the read-only workspace.
    [property: JsonPropertyName("working_dir")] string? WorkingDir = null
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
internal sealed record ListSandboxesResponseDto(
    [property: JsonPropertyName("sandboxes")] IReadOnlyList<ContainerEntryDto>? Sandboxes
);

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
    [property: JsonPropertyName("qualified_name")] string? QualifiedName,
    [property: JsonPropertyName("frontmatter")] System.Text.Json.JsonElement? Frontmatter = null,
    [property: JsonPropertyName("plugin")] string? Plugin = null,
    [property: JsonPropertyName("discovered_at")] DateTimeOffset? DiscoveredAt = null,
    [property: JsonPropertyName("notified")] bool? Notified = null
);

internal sealed record DiscoveredItemsResponseDto(
    [property: JsonPropertyName("discovered")] IReadOnlyList<DiscoveredItemDto>? Items
);

// --- Marketplace preview REST contract ---

internal sealed record MarketplaceCatalogDto(
    [property: JsonPropertyName("selected")] IReadOnlyList<string>? Selected,
    [property: JsonPropertyName("marketplaces")] IReadOnlyList<MarketplaceEntryDto>? Marketplaces,
    [property: JsonPropertyName("capabilities")] CapabilitiesDto? Capabilities = null
);

/// <summary>Gateway capability advertisement. An absent block leaves every flag <see langword="null"/> ("unknown").</summary>
internal sealed record CapabilitiesDto([property: JsonPropertyName("pluginFiltering")] bool? PluginFiltering);

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
/// <c>running|succeeded|failed|timed_out|output_limit_exceeded|cancelled|internal_failure</c>; <c>error</c>
/// carries the gateway's allow-listed, path-free message on the failure states (surfaced, bounded, only
/// when a <c>failed</c> operation has no exit code); <c>error_code</c> is the allow-listed reason code
/// (today only <c>executable_not_runnable</c>, issue #198); <c>artifacts</c> is present once the session
/// has a writable workspace (always true for a submitted operation).
internal sealed record OperationStatusDto(
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("terminal_at")] string? TerminalAt,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("stdout_bytes")] long? StdoutBytes,
    [property: JsonPropertyName("stderr_bytes")] long? StderrBytes,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("artifacts")] OperationArtifactsDto? Artifacts,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null
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

/// The gateway's stable error body: <c>{ error, code, error_code?, retryable? }</c> (direct API, image
/// policy, backup). Only the fixed-string <c>error_code</c> — a closed, gateway-defined vocabulary, not
/// caller free text — and the structured fields below are ever surfaced by the SDK; the human <c>error</c>
/// message is never echoed into a <see cref="SandboxException"/> (it may contain captured
/// output/credential material).
internal sealed record GatewayErrorDto(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("retryable")] bool? Retryable,
    // Present on invalid_env (400): the offending env-map keys. A closed-vocabulary structured field,
    // not caller free text, so it is safe to surface on SandboxException like ErrorCode itself.
    [property: JsonPropertyName("keys")] IReadOnlyList<string>? Keys = null,
    // Present on only_partial_snapshots (409): the gateway-minted partial snapshot ids to restore from.
    [property: JsonPropertyName("snapshots")] IReadOnlyList<string>? Snapshots = null,
    // Present on a 422 image_unsupported conformance-probe rejection: the runtime's report on the image.
    [property: JsonPropertyName("report")] ImageReportDto? Report = null,
    // Present on an unknown marketplace alias/item 400: the alias list the app may select, in gateway order.
    // Raw JSON because the unknown-plugin 400 reuses the name for {marketplace, plugin} objects; a string list
    // here would fail the whole body's parse and drop every other field. See ToAvailableMarketplaces.
    [property: JsonPropertyName("available")] System.Text.Json.JsonElement? Available = null
);

// --- Session env REST contract (gateway PR #183 / v0.1.11) ---

/// <c>GET/PATCH .../env</c> response — the resulting environment map after the request is applied.
internal sealed record SandboxEnvResponseDto([property: JsonPropertyName("env")] Dictionary<string, string>? Env);

// --- Sandbox properties REST contract (ADR 0042, crates/mcp-gateway/src/api/properties.rs) ---

/// <c>GET/PATCH .../properties</c> response — the resulting bag and the partition keys it was created under.
internal sealed record SandboxPropertiesResponseDto(
    [property: JsonPropertyName("properties")] Dictionary<string, string>? Properties,
    [property: JsonPropertyName("partition")] IReadOnlyList<string?>? Partition
);

internal sealed record McpResolvedPolicyDto(
    [property: JsonPropertyName("egress")] string? Egress,
    [property: JsonPropertyName("servers")] IReadOnlyList<McpResolvedServerDto?>? Servers
);

internal sealed record McpResolvedServerDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("marketplace")] string? Marketplace,
    [property: JsonPropertyName("transport")] string? Transport,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("auth")] string? Auth,
    [property: JsonPropertyName("headers")] IReadOnlyList<string?>? Headers,
    [property: JsonPropertyName("provider_id")] string? ProviderId,
    [property: JsonPropertyName("rule_id")] string? RuleId,
    [property: JsonPropertyName("unresolved_vars")] IReadOnlyList<string?>? UnresolvedVars
);

internal sealed record McpManifestDto(
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("servers")] IReadOnlyList<McpManifestServerDto?>? Servers
);

internal sealed record McpManifestServerDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("marketplace")] string? Marketplace,
    [property: JsonPropertyName("transport")] string? Transport,
    [property: JsonPropertyName("supported")] bool? Supported,
    [property: JsonPropertyName("unsupported_reason")] string? UnsupportedReason,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("url_template")] string? UrlTemplate,
    [property: JsonPropertyName("headers")] Dictionary<string, string?>? Headers,
    [property: JsonPropertyName("auth")] McpManifestAuthDto? Auth,
    [property: JsonPropertyName("egress")] string? Egress,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("unresolved_vars")] IReadOnlyList<string?>? UnresolvedVars,
    [property: JsonPropertyName("generated")] McpManifestGeneratedDto? Generated
);

internal sealed record McpManifestAuthDto(
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("headers")] IReadOnlyList<string?>? Headers
);

internal sealed record McpManifestGeneratedDto(
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("marketplace")] string? Marketplace,
    [property: JsonPropertyName("skill")] string? Skill,
    [property: JsonPropertyName("tools_hash")] string? ToolsHash,
    [property: JsonPropertyName("lib_path")] string? LibPath
);
