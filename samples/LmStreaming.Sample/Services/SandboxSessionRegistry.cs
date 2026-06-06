using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LmStreaming.Sample.Services.Auth;

namespace LmStreaming.Sample.Services;

/// <summary>
/// A sandbox session created against the gateway and bound to a workspace directory.
/// </summary>
/// <param name="WorkspaceId">Logical workspace key the session was created for (v1 always
/// <c>"default"</c>).</param>
/// <param name="SessionId">Gateway session id; sent as the <c>X-Session-ID</c> header on tool
/// calls.</param>
/// <param name="WorkspaceRelPath">Workspace path relative to the gateway's workspace base, as
/// requested at creation time.</param>
/// <param name="HostPath">Absolute host path of the mounted workspace (long-path <c>\\?\</c>
/// prefix stripped) — the path the model uses for absolute-path file tools.</param>
public sealed record SandboxSession(string WorkspaceId, string SessionId, string WorkspaceRelPath, string HostPath);

/// <summary>
/// Owns sandbox sessions for the sample app. Sessions are created lazily and exactly once per
/// workspace id, then destroyed on application shutdown when the DI container disposes this
/// singleton.
/// </summary>
/// <remarks>
/// <para>
/// v1 only ever requests the <c>"default"</c> workspace, which maps to the configured
/// <see cref="SandboxGatewayOptions.Workspace"/>. The <c>workspaceId</c> parameter is kept as the
/// seam for future per-request workspace switching.
/// </para>
/// <para>
/// Creation is single-flight: concurrent first-callers for the same workspace id share one
/// in-flight POST via a cached <see cref="Lazy{T}"/>. A failed creation is evicted so a later
/// call can retry.
/// </para>
/// </remarks>
public sealed class SandboxSessionRegistry : IAsyncDisposable
{
    private const string DefaultWorkspaceId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<SandboxSession>>> _sessions = new(StringComparer.Ordinal);
    private readonly SandboxGatewayLifetime _gateway;
    private readonly SandboxGatewayOptions _options;
    private readonly ILogger<SandboxSessionRegistry> _logger;
    private readonly HttpClient _httpClient;
    private readonly AuthOptions _authOptions;
    private readonly AuthSharedSecret _sharedSecret;
    private bool _disposed;

    /// <summary>
    /// Initialises the registry.
    /// </summary>
    /// <param name="gateway">Gateway lifetime, used for the base URL.</param>
    /// <param name="options">Strongly-typed gateway configuration.</param>
    /// <param name="logger">Logger for session diagnostics.</param>
    /// <param name="httpClient">Client used to create and destroy sandbox sessions.</param>
    /// <param name="authOptions">OAuth auth-provider configuration; drives the optional
    /// <c>auth_providers</c>/<c>network</c> blocks sent on sandbox creation.</param>
    /// <param name="sharedSecret">Gateway↔webhook shared secret attached to each auth provider.
    /// SECRET — never logged.</param>
    public SandboxSessionRegistry(
        SandboxGatewayLifetime gateway,
        SandboxGatewayOptions options,
        ILogger<SandboxSessionRegistry> logger,
        HttpClient httpClient,
        AuthOptions authOptions,
        AuthSharedSecret sharedSecret
    )
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authOptions = authOptions ?? throw new ArgumentNullException(nameof(authOptions));
        _sharedSecret = sharedSecret ?? throw new ArgumentNullException(nameof(sharedSecret));
    }

    /// <summary>
    /// Returns the sandbox session for <paramref name="workspaceId"/>, creating it on first use.
    /// Concurrent first-callers share a single creation; subsequent callers get the cached session.
    /// </summary>
    /// <param name="workspaceId">Logical workspace key. v1 only passes <c>"default"</c>.</param>
    /// <param name="ct">Cancellation token observed by the creating call.</param>
    public Task<SandboxSession> GetOrCreateSessionAsync(
        string workspaceId = DefaultWorkspaceId,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            workspaceId = DefaultWorkspaceId;
        }

        // Use CancellationToken.None inside the single-flight Lazy factory: the shared creation task
        // must not be poisoned by the first caller's request-scoped token being cancelled (e.g. that
        // caller disconnecting), which would otherwise fail it for all concurrent waiters.
        var lazy = _sessions.GetOrAdd(
            workspaceId,
            key => new Lazy<Task<SandboxSession>>(
                () => CreateSessionAsync(key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );

        return AwaitAndEvictOnFailureAsync(workspaceId, lazy);
    }

    /// <summary>
    /// Awaits the cached creation task. On failure the cached <see cref="Lazy{T}"/> is evicted so a
    /// later call retries instead of replaying the cached fault.
    /// </summary>
    private async Task<SandboxSession> AwaitAndEvictOnFailureAsync(string workspaceId, Lazy<Task<SandboxSession>> lazy)
    {
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Only evict the entry we own, in case another creation has since replaced it.
            _ = ((ICollection<KeyValuePair<string, Lazy<Task<SandboxSession>>>>)_sessions).Remove(
                new KeyValuePair<string, Lazy<Task<SandboxSession>>>(workspaceId, lazy)
            );
            throw;
        }
    }

    /// <summary>
    /// POSTs a sandbox-create request to the gateway and maps the response to a
    /// <see cref="SandboxSession"/>.
    /// </summary>
    private async Task<SandboxSession> CreateSessionAsync(string workspaceId, CancellationToken ct)
    {
        // Surface the clear, actionable gateway error here (not at app startup) — this is the
        // first point at which Workspace Agent mode actually requires a healthy gateway.
        await _gateway.EnsureReadyAsync(ct).ConfigureAwait(false);

        var (_, workspaceLeaf, workspaceFullPath) = _options.ResolveWorkspace();
        var workspaceRelPath = workspaceLeaf ?? string.Empty;

        // Ensure the workspace directory exists before the gateway mounts it — the gateway rejects a
        // leaf that doesn't exist under WORKSPACE_BASE_PATH. Idempotent with the spawn-time creation;
        // this also covers the adopt-an-existing-gateway path (no spawn ran).
        if (!string.IsNullOrWhiteSpace(workspaceFullPath))
        {
            try
            {
                _ = Directory.CreateDirectory(workspaceFullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create workspace directory '{WorkspacePath}'", workspaceFullPath);
            }
        }

        var requestUri = $"{_gateway.GatewayBaseUrl}/api/v1/sandboxes";
        var (authProviders, network) = BuildAuthProviders();
        var discovery = BuildDiscovery();
        var request = new CreateSandboxRequest(
            new AppRef(_options.AppId),
            workspaceRelPath,
            authProviders,
            network,
            discovery
        );

        _logger.LogInformation(
            "Creating sandbox session for workspace {WorkspaceId} (app {AppId}, path '{WorkspaceRelPath}')",
            workspaceId,
            _options.AppId,
            workspaceRelPath
        );

        if (authProviders is { Count: > 0 })
        {
            // Log only which providers were attached — never the shared secret.
            _logger.LogInformation(
                "Sandbox session created with auth providers: {Providers}",
                string.Join(", ", authProviders.Select(p => p.Id))
            );
        }

        using var response = await _httpClient
            .PostAsJsonAsync(requestUri, request, JsonOptions, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError(
                "Sandbox create failed for workspace {WorkspaceId}: {StatusCode} {Body}",
                workspaceId,
                (int)response.StatusCode,
                body
            );
            throw new InvalidOperationException(
                $"Sandbox gateway returned {(int)response.StatusCode} creating a session for "
                    + $"workspace '{workspaceId}': {body}"
            );
        }

        var payload = await response
            .Content.ReadFromJsonAsync<CreateSandboxResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            throw new InvalidOperationException(
                $"Sandbox gateway returned a success status but no session id for workspace '{workspaceId}'."
            );
        }

        var hostPath = StripLongPathPrefix(payload.Volumes?.Workspace?.ContainerPath ?? string.Empty);
        var session = new SandboxSession(workspaceId, payload.SessionId, workspaceRelPath, hostPath);

        _logger.LogInformation(
            "Created sandbox session {SessionId} for workspace {WorkspaceId} at host path {HostPath}",
            session.SessionId,
            workspaceId,
            session.HostPath
        );

        return session;
    }

    /// <summary>
    /// Best-effort destroys every successfully-created session on shutdown. Errors are logged and
    /// swallowed so disposal never throws.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var entry in _sessions.Values)
        {
            // Only destroy sessions that were actually created and completed successfully.
            if (!entry.IsValueCreated || !entry.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            await DestroySessionAsync(entry.Value.Result).ConfigureAwait(false);
        }

        _sessions.Clear();
        _httpClient.Dispose();
    }

    /// <summary>
    /// Lists items the gateway has discovered for the session's workspace (the result of its
    /// background sweep of context files — sub-agents, skills, etc). Callers filter by
    /// <see cref="DiscoveredItem.Kind"/> for the kinds they care about.
    /// </summary>
    /// <param name="sessionId">Gateway session id returned by <see cref="GetOrCreateSessionAsync"/>.</param>
    /// <param name="ct">Cancellation token observed by the HTTP call.</param>
    /// <returns>The discovered items. Empty when the gateway reports no discoveries; never null.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the gateway returns a non-success status. The body (truncated) is included so
    /// callers can correlate against gateway logs. Caller is expected to catch + log + degrade.
    /// </exception>
    public async Task<IReadOnlyList<DiscoveredItem>> ListDiscoveredAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var requestUri = $"{_gateway.GatewayBaseUrl}/api/v1/sandboxes/{sessionId}/discovered";
        using var response = await _httpClient.GetAsync(requestUri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var bodySnippet = TruncateBody(body);
            // Log + throw, matching CreateSessionAsync's policy: the caller is expected to catch
            // and degrade, but we still want a server-log breadcrumb correlating the failure with
            // gateway logs by session id.
            _logger.LogError(
                "Sandbox discovered-items list failed for session {SessionId}: {StatusCode} {Body}",
                sessionId,
                (int)response.StatusCode,
                bodySnippet
            );
            throw new InvalidOperationException(
                $"Sandbox gateway returned {(int)response.StatusCode} listing discovered items for "
                    + $"session '{sessionId}': {bodySnippet}"
            );
        }

        var payload = await response
            .Content.ReadFromJsonAsync<DiscoveredItemsResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        return payload?.Items ?? [];
    }

    private const int ErrorBodyMaxLength = 500;

    /// <summary>
    /// Caps a gateway error body so a large HTML error page (e.g. a 502 from a reverse proxy) can
    /// not blow up server logs or the exception message. Mirrors the doc-claim on
    /// <see cref="ListDiscoveredAsync"/>.
    /// </summary>
    private static string TruncateBody(string body) =>
        string.IsNullOrEmpty(body) || body.Length <= ErrorBodyMaxLength
            ? body
            : body[..ErrorBodyMaxLength] + "...(truncated)";

    private async Task DestroySessionAsync(SandboxSession session)
    {
        try
        {
            using var response = await _httpClient
                .DeleteAsync($"{_gateway.GatewayBaseUrl}/api/v1/sandboxes/{session.SessionId}")
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Destroyed sandbox session {SessionId}", session.SessionId);
            }
            else
            {
                _logger.LogWarning(
                    "Sandbox destroy returned {StatusCode} for session {SessionId}",
                    (int)response.StatusCode,
                    session.SessionId
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to destroy sandbox session {SessionId}", session.SessionId);
        }
    }

    /// <summary>
    /// Strips a leading Windows long-path prefix (<c>\\?\</c>, including the <c>\\?\UNC\</c> form)
    /// so the stored host path reads as a normal absolute path the model can pass to file tools.
    /// </summary>
    internal static string StripLongPathPrefix(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        const string UncPrefix = @"\\?\UNC\";
        const string Prefix = @"\\?\";

        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            // \\?\UNC\server\share -> \\server\share
            return @"\\" + path[UncPrefix.Length..];
        }

        return path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;
    }

    /// <summary>
    /// Builds the optional <c>auth_providers</c> + <c>network</c> blocks for the sandbox-create
    /// request from the configured OAuth providers. Each configured provider (one with a non-empty
    /// client id) contributes a webhook auth-provider plus an allow rule scoping it to that
    /// provider's hosts. Returns <c>(null, null)</c> when no provider is configured so both blocks
    /// are omitted from the JSON.
    /// </summary>
    private (IReadOnlyList<AuthProviderDto>? Providers, NetworkDto? Network) BuildAuthProviders()
    {
        var providers = new List<AuthProviderDto>();
        var rules = new List<NetworkRuleDto>();
        var baseUrl = _authOptions.Webhook.CallbackBaseUrl;

        if (!string.IsNullOrWhiteSpace(_authOptions.Github.ClientId))
        {
            providers.Add(
                new AuthProviderDto(
                    Id: "github-auth",
                    Type: "webhook",
                    Endpoint: $"{baseUrl}/api/auth/webhook/github",
                    GatewayAuth: _sharedSecret.Value,
                    CacheTtlSeconds: 300,
                    RequiredScopes: []
                )
            );
            rules.Add(
                new NetworkRuleDto(
                    Id: "github",
                    Action: "allow",
                    Hosts: OAuthProviderHosts.For("github"),
                    Ports: [443],
                    Methods: [],
                    Paths: [],
                    AuthProvider: "github-auth",
                    RequiredScopes: [],
                    Priority: 100
                )
            );
        }

        if (!string.IsNullOrWhiteSpace(_authOptions.Ado.ClientId))
        {
            providers.Add(
                new AuthProviderDto(
                    Id: "ado-auth",
                    Type: "webhook",
                    Endpoint: $"{baseUrl}/api/auth/webhook/ado",
                    GatewayAuth: _sharedSecret.Value,
                    CacheTtlSeconds: 300,
                    RequiredScopes: []
                )
            );
            rules.Add(
                new NetworkRuleDto(
                    Id: "ado",
                    Action: "allow",
                    Hosts: OAuthProviderHosts.For("ado"),
                    Ports: [443],
                    Methods: [],
                    Paths: [],
                    AuthProvider: "ado-auth",
                    RequiredScopes: [],
                    Priority: 100
                )
            );
        }

        return providers.Count > 0 ? (providers, new NetworkDto(rules)) : (null, null);
    }

    /// <summary>
    /// Builds the outbound <c>discovery</c> block telling the gateway where to deliver
    /// context-discovery events. The URL is the public webhook base (the same one the gateway
    /// already uses for auth callbacks) plus the discovery route; the auth value is the
    /// gateway↔webhook shared secret. SECRET — never logged.
    /// </summary>
    private DiscoveryDto BuildDiscovery()
    {
        var baseUrl = _authOptions.Webhook.CallbackBaseUrl;
        return new DiscoveryDto(
            new DiscoveryWebhookDto(
                Url: $"{baseUrl}/api/discovery/context_discovery",
                Auth: _sharedSecret.Value
            )
        );
    }

    // --- Gateway JSON contract (snake_case via JsonOptions) ---

    private sealed record CreateSandboxRequest(
        [property: JsonPropertyName("app")] AppRef App,
        [property: JsonPropertyName("workspace")] string Workspace,
        [property: JsonPropertyName("auth_providers")] IReadOnlyList<AuthProviderDto>? AuthProviders,
        [property: JsonPropertyName("network")] NetworkDto? Network,
        [property: JsonPropertyName("discovery")] DiscoveryDto? Discovery = null
    );

    /// <summary>Outbound <c>discovery</c> block telling the gateway where to deliver
    /// context-discovery events. Omitted from the request JSON when null.</summary>
    internal sealed record DiscoveryDto(
        [property: JsonPropertyName("webhook")] DiscoveryWebhookDto Webhook
    );

    /// <summary>Webhook descriptor inside the outbound <c>discovery</c> block. <c>auth</c>
    /// matches the value the gateway later presents in the <c>Authorization</c> header on its
    /// callbacks — same convention as the auth-provider <c>gateway_auth</c> field.</summary>
    internal sealed record DiscoveryWebhookDto(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("auth")] string Auth
    );

    /// <summary>One item the gateway has discovered for the workspace (a sub-agent file,
    /// a skill descriptor, …). <see cref="Kind"/> is the discriminator the caller filters by;
    /// <see cref="Path"/> is workspace-relative.</summary>
    public sealed record DiscoveredItem(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("path")] string Path
    );

    private sealed record DiscoveredItemsResponse(
        [property: JsonPropertyName("items")] IReadOnlyList<DiscoveredItem> Items
    );

    private sealed record AppRef([property: JsonPropertyName("id")] string Id);

    private sealed record AuthProviderDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("endpoint")] string Endpoint,
        [property: JsonPropertyName("gateway_auth")] string GatewayAuth,
        [property: JsonPropertyName("cache_ttl_seconds")] int CacheTtlSeconds,
        [property: JsonPropertyName("required_scopes")] IReadOnlyList<string> RequiredScopes
    );

    private sealed record NetworkDto([property: JsonPropertyName("rules")] IReadOnlyList<NetworkRuleDto> Rules);

    private sealed record NetworkRuleDto(
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

    private sealed record CreateSandboxResponse(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("container_id")] string? ContainerId,
        [property: JsonPropertyName("volumes")] VolumesDto? Volumes
    );

    private sealed record VolumesDto([property: JsonPropertyName("workspace")] WorkspaceVolumeDto? Workspace);

    private sealed record WorkspaceVolumeDto(
        [property: JsonPropertyName("container_path")] string? ContainerPath,
        [property: JsonPropertyName("read_only")] bool ReadOnly
    );
}
