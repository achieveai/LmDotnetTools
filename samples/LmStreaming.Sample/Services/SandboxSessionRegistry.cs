using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Auth;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Per-session binding the context-discovery webhook needs to convert a discovered subagent into
/// a live template: the catalog the loop reads (<see cref="Source"/>) plus the agent factory the
/// template's spawn-time wiring will reuse (<see cref="AgentFactory"/>). The factory is provider-
/// specific and only known at agent-creation time, so it must travel with the source rather than
/// be re-derived inside the webhook handler.
/// </summary>
public sealed record SubAgentSessionBinding(
    MutableSubAgentTemplateSource Source,
    Func<IStreamingAgent> AgentFactory);

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
/// Identifies the workspace a sandbox session is being requested for: the logical
/// <paramref name="Id"/> (the session-cache key) plus the optional directory leaf
/// (<paramref name="DirectoryRelPath"/>) the session mounts. A null/blank
/// <paramref name="DirectoryRelPath"/> falls back to the configured default workspace path.
/// </summary>
public sealed record WorkspaceRef(string Id, string? DirectoryRelPath = null);

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
    /// <summary>
    /// Logical id of the default workspace, which maps to the configured
    /// <see cref="SandboxGatewayOptions.Workspace"/>. Also the seeded id used by the workspace store.
    /// </summary>
    public const string DefaultWorkspaceId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<SandboxSession>>> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Sub-agent binding (template source + agent factory) the loop uses to populate the Agent
    /// tool's <c>subagent_type</c> enum and that the context-discovery webhook mutates when the
    /// gateway reports a newly discovered subagent.
    /// </summary>
    /// <remarks>
    /// Keyed by gateway session id and THEN by conversation (agent-pool thread) id. All
    /// conversations share ONE sandbox session (v1 always uses the <c>"default"</c> workspace),
    /// but each conversation owns its OWN catalog source so a sub-agent discovered/registered
    /// while one conversation is live does not bleed into a NEW conversation that starts later.
    /// The outer key is the session id (what the webhook carries) so it can fan a discovery out to
    /// every conversation currently live on that session.
    /// </remarks>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SubAgentSessionBinding>> _subAgentBindings =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Reverse map from gateway session id to the <see cref="SandboxSession"/> it represents.
    /// Populated by <see cref="CreateSessionAsync"/> after a session is created so the
    /// context-discovery webhook (which only knows the session id) can resolve back to the
    /// session's <c>HostPath</c> for path-containment checks.
    /// </summary>
    private readonly ConcurrentDictionary<string, SandboxSession> _sessionsById =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Session id → set of agent-pool thread ids currently routed to that session. The
    /// context-discovery webhook uses this to fan an injected message out to every live thread
    /// when the gateway delivers a context_file. Membership is added by <see cref="RegisterThread"/>
    /// (called after the session/binding are wired) and removed by <see cref="UnregisterThread"/>
    /// (called from the pool's thread-removed notifier — NOT from a mode switch, which preserves
    /// the same threadId and therefore must continue routing).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionThreads =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Per-session dedup ledger of <c>(kind, path)</c> tuples the gateway has already delivered.
    /// Gateways may retry the same discovery on transient failure or when the workspace mount
    /// re-emits an event; without this gate the injector would inject the same context file
    /// twice into the same thread. No TTL — entries are scoped to the registry lifetime, cleared
    /// on <see cref="DisposeAsync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _discoverySeen =
        new(StringComparer.Ordinal);

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
    /// Back-compat overload that creates (or returns) the session for <paramref name="workspaceId"/>
    /// using the configured default workspace directory. Equivalent to passing a
    /// <see cref="WorkspaceRef"/> with a null directory.
    /// </summary>
    /// <param name="workspaceId">Logical workspace key.</param>
    /// <param name="ct">Cancellation token observed by the creating call.</param>
    public Task<SandboxSession> GetOrCreateSessionAsync(
        string workspaceId = DefaultWorkspaceId,
        CancellationToken ct = default
    )
    {
        return GetOrCreateSessionAsync(new WorkspaceRef(workspaceId), ct);
    }

    /// <summary>
    /// Returns the sandbox session for <paramref name="workspaceRef"/>, creating it on first use.
    /// The cache is keyed by <see cref="WorkspaceRef.Id"/>; on first creation the mounted directory
    /// is resolved from <see cref="WorkspaceRef.DirectoryRelPath"/> (null/blank → configured
    /// default). Concurrent first-callers share a single creation; subsequent callers get the
    /// cached session (subsequent directory values are ignored — the first creation wins).
    /// </summary>
    /// <param name="workspaceRef">The workspace identity + directory to mount.</param>
    /// <param name="ct">Cancellation token observed by the creating call.</param>
    public Task<SandboxSession> GetOrCreateSessionAsync(
        WorkspaceRef workspaceRef,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workspaceRef);

        var workspaceId = string.IsNullOrWhiteSpace(workspaceRef.Id) ? DefaultWorkspaceId : workspaceRef.Id;
        var effectiveRef = workspaceRef with { Id = workspaceId };

        // Use CancellationToken.None inside the single-flight Lazy factory: the shared creation task
        // must not be poisoned by the first caller's request-scoped token being cancelled (e.g. that
        // caller disconnecting), which would otherwise fail it for all concurrent waiters.
        var lazy = _sessions.GetOrAdd(
            workspaceId,
            _ => new Lazy<Task<SandboxSession>>(
                () => CreateSessionAsync(effectiveRef, CancellationToken.None),
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
    private async Task<SandboxSession> CreateSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct)
    {
        var workspaceId = workspaceRef.Id;

        // Surface the clear, actionable gateway error here (not at app startup) — this is the
        // first point at which Workspace Agent mode actually requires a healthy gateway.
        await _gateway.EnsureReadyAsync(ct).ConfigureAwait(false);

        var (_, workspaceLeaf, workspaceFullPath) = _options.ResolveWorkspace(workspaceRef.DirectoryRelPath);
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
        var marketplaces = MarketplaceAliases.Parse(_options.Marketplaces);
        var request = new CreateSandboxRequest(
            new AppRef(_options.AppId),
            workspaceRelPath,
            authProviders,
            network,
            discovery,
            marketplaces
        );

        _logger.LogInformation(
            "Sandbox session marketplace selection: {Marketplaces}",
            marketplaces is { Count: > 0 } ? string.Join(", ", marketplaces) : "(gateway default)"
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

        // Register the reverse session-id → session mapping so the context-discovery webhook can
        // resolve back to the session. Last-write-wins is acceptable because session ids are
        // gateway-allocated and unique per creation.
        _sessionsById[session.SessionId] = session;

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
        _subAgentBindings.Clear();
        _sessionsById.Clear();
        _sessionThreads.Clear();
        _discoverySeen.Clear();
        _httpClient.Dispose();
    }

    /// <summary>
    /// Lists items the gateway has discovered for the session's workspace (the result of its
    /// background sweep of context files — sub-agents, skills, etc). Callers filter by
    /// <see cref="DiscoveredItem.Kind"/> for the kinds they care about.
    /// </summary>
    /// <param name="sessionId">Gateway session id returned by <see cref="GetOrCreateSessionAsync(WorkspaceRef, CancellationToken)"/>.</param>
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

    /// <summary>
    /// Returns the <see cref="SubAgentSessionBinding"/> for the
    /// (<paramref name="sessionId"/>, <paramref name="conversationId"/>) pair, creating one on
    /// first access — seeded with <paramref name="seed"/> and remembering
    /// <paramref name="agentFactory"/> for the discovery webhook's spawn-time wiring. Subsequent
    /// calls for the same pair return the existing binding unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bindings are scoped per CONVERSATION, not per session: all conversations share one sandbox
    /// session, but each gets its own catalog source seeded only with the static built-ins, so a
    /// sub-agent discovered/registered while one conversation is live does NOT leak into a new
    /// conversation that starts later. The webhook fans live discoveries out via
    /// <see cref="GetSubAgentBindingsForSession"/>.
    /// </para>
    /// <para>
    /// The seed lets the pool factory plant the built-in templates as the initial entries before
    /// the loop wires the source into <see cref="SubAgentToolProvider"/>/<see cref="SubAgentManager"/>.
    /// </para>
    /// </remarks>
    public SubAgentSessionBinding GetOrAddSubAgentBinding(
        string sessionId,
        string conversationId,
        IReadOnlyDictionary<string, SubAgentTemplate> seed,
        Func<IStreamingAgent> agentFactory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(agentFactory);

        var conversations = _subAgentBindings.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, SubAgentSessionBinding>(StringComparer.Ordinal));

        var binding = conversations.GetOrAdd(
            conversationId,
            _ => new SubAgentSessionBinding(new MutableSubAgentTemplateSource(seed), agentFactory));

        // Reconcile the seed every call: a later pool factory invocation for the same conversation
        // may present additional built-in templates that weren't present on the first call.
        // TryRegister is first-wins, so previously seeded entries (and any discovered templates
        // registered by the webhook in between) are preserved — the trust boundary holds.
        foreach (var kvp in seed)
        {
            _ = binding.Source.TryRegister(kvp.Key, kvp.Value);
        }

        return binding;
    }

    /// <summary>
    /// Tries to look up the sub-agent binding previously registered for the
    /// (<paramref name="sessionId"/>, <paramref name="conversationId"/>) pair via
    /// <see cref="GetOrAddSubAgentBinding"/>. Returns false (with <paramref name="binding"/> set to
    /// null) when no binding exists — the discovery webhook treats that as a best-effort no-op
    /// rather than an error, since the conversation may not have routed through the agent path yet.
    /// </summary>
    public bool TryGetSubAgentBinding(string sessionId, string conversationId, out SubAgentSessionBinding? binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(conversationId))
        {
            binding = null;
            return false;
        }

        if (_subAgentBindings.TryGetValue(sessionId, out var conversations))
        {
            return conversations.TryGetValue(conversationId, out binding);
        }

        binding = null;
        return false;
    }

    /// <summary>
    /// Returns every conversation's sub-agent binding currently registered for
    /// <paramref name="sessionId"/>. Used by the context-discovery webhook to fan a discovered
    /// sub-agent out to every conversation that is live on the shared session. Empty when no
    /// conversation has initialised the agent path for the session yet.
    /// </summary>
    public IReadOnlyList<SubAgentSessionBinding> GetSubAgentBindingsForSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        return _subAgentBindings.TryGetValue(sessionId, out var conversations)
            ? [.. conversations.Values]
            : [];
    }

    /// <summary>
    /// Tries to look up a created <see cref="SandboxSession"/> by its gateway-allocated session id.
    /// Returns false (with <paramref name="session"/> set to null) when no session has been created
    /// with that id yet — discovery callbacks that race ahead of session creation are treated as
    /// best-effort no-ops by the caller.
    /// </summary>
    public bool TryGetSessionById(string sessionId, out SandboxSession? session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            session = null;
            return false;
        }

        return _sessionsById.TryGetValue(sessionId, out session);
    }

    /// <summary>
    /// Registers an agent-pool thread as belonging to <paramref name="sessionId"/> so the
    /// context-discovery injector can find every thread that should receive an injection event.
    /// Idempotent: re-registering the same thread is a no-op.
    /// </summary>
    public void RegisterThread(string sessionId, string threadId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var set = _sessionThreads.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        _ = set.TryAdd(threadId, 0);
    }

    /// <summary>
    /// Removes an agent-pool thread's membership from <paramref name="sessionId"/>. Called by the
    /// pool's thread-removed notifier on <c>RemoveAgentAsync</c> — NOT on a mode-switch
    /// recreation (which preserves the same threadId and therefore must continue routing).
    /// Idempotent: removing an absent thread is a no-op.
    /// </summary>
    public void UnregisterThread(string sessionId, string threadId)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        if (_sessionThreads.TryGetValue(sessionId, out var set))
        {
            _ = set.TryRemove(threadId, out _);
        }
    }

    /// <summary>
    /// Removes <paramref name="threadId"/> from every session's membership set. Used by the
    /// pool's <c>ThreadRemoved</c> notifier, which only knows the threadId (the pool has no
    /// session context). Walks the registry's per-session sets — small enough that an O(sessions)
    /// scan on thread teardown is preferable to maintaining a reverse map that must be kept in
    /// sync with every register/unregister call.
    /// </summary>
    public void UnregisterThreadFromAllSessions(string threadId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        foreach (var set in _sessionThreads.Values)
        {
            _ = set.TryRemove(threadId, out _);
        }

        // Release the conversation's per-conversation sub-agent binding (keyed by conversation ==
        // thread id). One is created per chat, so without this they accumulate unbounded for the
        // process lifetime.
        foreach (var conversations in _subAgentBindings.Values)
        {
            _ = conversations.TryRemove(threadId, out _);
        }
    }

    /// <summary>
    /// Returns a snapshot of the thread ids currently registered against
    /// <paramref name="sessionId"/>. Empty when nothing is registered (no exception). The
    /// returned list is a copy — callers can safely iterate it without holding a registry lock.
    /// </summary>
    public IReadOnlyList<string> GetThreads(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        return _sessionThreads.TryGetValue(sessionId, out var set)
            ? [.. set.Keys]
            : [];
    }

    /// <summary>
    /// Atomically marks <c>(kind, path)</c> as seen for <paramref name="sessionId"/>. Returns
    /// true on the first call (caller proceeds with injection) and false on every subsequent
    /// call for the same tuple (caller drops the duplicate). Used to defend against gateway
    /// retry storms re-firing the same discovery event.
    /// </summary>
    public bool TryMarkDiscoverySeen(string sessionId, string kind, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var set = _discoverySeen.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        return set.TryAdd($"{kind}\0{path}", 0);
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
    /// <summary>
    /// Test seam: returns the gateway auth-provider ids the registry would attach to a
    /// sandbox-create request given the current <see cref="AuthOptions"/>. Mirrors the inputs that
    /// drive the optional gateway rule + webhook entry — useful for asserting the gating logic for
    /// each provider (e.g. m365 needs both ClientId + ClientSecret) without standing up the gateway.
    /// </summary>
    internal IReadOnlyList<string> GetAuthProviderIdsForTest() =>
        BuildAuthProviders().Providers?.Select(p => p.Id).ToArray() ?? [];

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

        // M365 (Microsoft Graph). Gate on both ClientId and ClientSecret: the provider stays
        // disabled when the secret is missing, so emitting the gateway rule + webhook entry in that
        // case would point to a webhook that always denies — confusing rather than helpful.
        if (!string.IsNullOrWhiteSpace(_authOptions.M365.ClientId)
            && !string.IsNullOrWhiteSpace(_authOptions.M365.ClientSecret))
        {
            providers.Add(
                new AuthProviderDto(
                    Id: "m365-auth",
                    Type: "webhook",
                    Endpoint: $"{baseUrl}/api/auth/webhook/m365",
                    GatewayAuth: _sharedSecret.Value,
                    CacheTtlSeconds: 300,
                    RequiredScopes: []
                )
            );
            rules.Add(
                new NetworkRuleDto(
                    Id: "m365",
                    Action: "allow",
                    Hosts: OAuthProviderHosts.For("m365"),
                    Ports: [443],
                    Methods: [],
                    Paths: [],
                    AuthProvider: "m365-auth",
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
        [property: JsonPropertyName("discovery")] DiscoveryDto? Discovery = null,
        // Omitted when null (JsonOptions ignores nulls) so the gateway keeps its default-set
        // behaviour; an empty/blank config must therefore parse to null, never an empty array.
        [property: JsonPropertyName("marketplaces")] IReadOnlyList<string>? Marketplaces = null
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
