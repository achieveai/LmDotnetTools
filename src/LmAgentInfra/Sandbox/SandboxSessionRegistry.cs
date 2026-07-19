using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Per-session binding the context-discovery webhook needs to convert a discovered subagent into
/// a live template: the catalog the loop reads (<see cref="Source"/>) plus the agent factory the
/// template's spawn-time wiring will reuse (<see cref="AgentFactory"/> and
/// <see cref="CharacteristicsAgentFactory"/>). The factories are provider-specific and only known
/// at agent-creation time, so they must travel with the source rather than be re-derived inside the
/// webhook handler.
/// </summary>
public sealed record SubAgentSessionBinding(
    MutableSubAgentTemplateSource Source,
    Func<IStreamingAgent> AgentFactory)
{
    /// <summary>
    /// Optional factory that creates the provider agent from resolved spawn characteristics.
    /// </summary>
    public Func<SubAgentCharacteristics, SubAgentProviderAgent>? CharacteristicsAgentFactory { get; init; }
}

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
/// <paramref name="Marketplaces"/> are the plugin-marketplace aliases this workspace enables;
/// when non-empty they drive the sandbox-create selection, otherwise the global
/// <see cref="SandboxGatewayOptions.Marketplaces"/> default applies.
/// </summary>
public sealed record WorkspaceRef(
    string Id,
    string? DirectoryRelPath = null,
    IReadOnlyList<string>? Marketplaces = null);

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
/// Creation is single-flight: concurrent first-callers for the same (workspace id, caller app id)
/// pair share one in-flight POST via a cached <see cref="Lazy{T}"/>. A failed creation is evicted
/// so a later call can retry. Partitioning the cache by caller app id (not just workspace id)
/// ensures two different callers requesting the same logical workspace (e.g. two S2S identities,
/// or a caller and the interactive UI default) never collide on one shared session.
/// </para>
/// </remarks>
public sealed class SandboxSessionRegistry : IAsyncDisposable
{
    /// <summary>
    /// Logical id of the default workspace, which maps to the configured
    /// <see cref="SandboxGatewayOptions.Workspace"/>. Also the seeded id used by the workspace store.
    /// </summary>
    public const string DefaultWorkspaceId = "default";

    /// <summary>
    /// Dedup-ledger target sentinel for a discovery that is NOT routed to a specific sub-agent —
    /// i.e. the fallback session fan-out and the boot-time root-file seed. Kept distinct from any
    /// real <c>agent_id</c> so the primary conversation and a sub-agent that enter the SAME
    /// directory each dedup independently (see <see cref="TryMarkDiscoverySeen(string, string, string, string)"/>).
    /// </summary>
    public const string SessionDiscoveryTarget = "__session__";

    /// <summary>
    /// Upper bound on the gateway session-liveness probe so a wedged gateway degrades to "assume
    /// alive" quickly rather than blocking the turn for the full shared-client timeout.
    /// </summary>
    private static readonly TimeSpan SessionLivenessProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-credential control-plane clients (issue #191): one <see cref="SandboxClient"/> per
    /// distinct app credential, resolved and cached by <see cref="ClientFor"/>. Each client is
    /// KEYLESS — it stamps only the app id — because a per-caller app KEY is an opaque, gateway-
    /// validated string that the SDK's base64 <c>ClientSecret</c> validation would reject; the key
    /// is instead stamped verbatim downstream by a per-credential <see cref="GatewayAuthHandler"/>.
    /// All clients BORROW the one shared <see cref="_httpClient"/> transport through a cloning
    /// forwarding handler, so they share its connection pool (and, in tests, its recording handler).
    /// Keyed by the full (AppId, AppKey) credential so a rotated key yields a fresh client.
    /// </summary>
    private readonly ConcurrentDictionary<(string AppId, string AppKey), SandboxClientEntry> _clients =
        new();

    /// <summary>
    /// Serializes every <see cref="_clients"/> get-or-create, per-credential session refcount mutation,
    /// and eviction, so a concurrent create can never resurrect (or keep using) an entry that a last-
    /// session destroy is evicting/disposing. The (rare, brief) client construction happens under it too;
    /// the actual <see cref="IDisposable.Dispose"/> is always performed OUTSIDE the lock.
    /// </summary>
    private readonly object _clientsLock = new();

    /// <summary>
    /// A cached per-credential client plus the dedicated transport it borrows through, REF-COUNTED by the
    /// number of live sessions created under that credential. When the last session under an (AppId, AppKey)
    /// is destroyed the entry is evicted and its transport disposed, so credential rotation / high-cardinality
    /// credentials cannot grow <see cref="_clients"/> monotonically. <see cref="SessionRefCount"/> is guarded
    /// by <see cref="_clientsLock"/>.
    /// </summary>
    private sealed class SandboxClientEntry(SandboxClient client, HttpClient transport)
    {
        public SandboxClient Client { get; } = client;

        public HttpClient Transport { get; } = transport;

        public int SessionRefCount { get; set; }
    }

    /// <summary>
    /// Sandbox sessions, partitioned by the (workspace id, caller app id) pair — see the class
    /// remarks — so two different callers requesting the SAME logical workspace never collide on
    /// one shared session. Keyed by the <c>AppId</c> only, NEVER the secret <c>AppKey</c>. The
    /// default equality comparer for a <c>(string, string)</c> value tuple already compares each
    /// element with ordinal string equality, so no explicit comparer is needed.
    /// </summary>
    private readonly ConcurrentDictionary<
        (string WorkspaceId, string AppId),
        Lazy<Task<SandboxSession>>
    > _sessions = new();

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
    private readonly PredefinedKeyRegistry? _predefinedKeys;
    private readonly SandboxCredential _defaultCredential;
    private bool _disposed;

    /// <summary>
    /// Credential the session with a given gateway session id was CREATED with, so contextless
    /// paths (liveness probe, destroy, discovery) resolve the same credential the session was
    /// opened under instead of the process-wide default. Populated by <see cref="CreateSessionAsync"/>
    /// and cleared wherever <see cref="_sessionsById"/> is cleared.
    /// </summary>
    private readonly ConcurrentDictionary<string, SandboxCredential> _sessionCredentials =
        new(StringComparer.Ordinal);

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
    /// <param name="predefinedKeys">Optional predefined-egress-key registry; when supplied, each
    /// configured entry contributes a webhook auth-provider + host-scoped allow rule. Null (the
    /// headless daemon and every test that does not exercise egress keys) emits none — fail-closed.</param>
    public SandboxSessionRegistry(
        SandboxGatewayLifetime gateway,
        SandboxGatewayOptions options,
        ILogger<SandboxSessionRegistry> logger,
        HttpClient httpClient,
        AuthOptions authOptions,
        AuthSharedSecret sharedSecret,
        PredefinedKeyRegistry? predefinedKeys = null
    )
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authOptions = authOptions ?? throw new ArgumentNullException(nameof(authOptions));
        _sharedSecret = sharedSecret ?? throw new ArgumentNullException(nameof(sharedSecret));
        _predefinedKeys = predefinedKeys;
        // Non-null default even when no key is configured: contextless paths (liveness/destroy on
        // a session with no side-table entry) always resolve to a credential, never null. An empty
        // AppKey is exactly the keyless AUTH_ENFORCE=off dev path.
        _defaultCredential = new SandboxCredential(_options.AppId, _options.AppKey ?? string.Empty);
    }

    /// <summary>
    /// Resolves the credential a gateway request for <paramref name="sessionId"/> should use: the
    /// credential the session was created with, when known, otherwise the process-wide default.
    /// Background/contextless call sites (liveness probe, destroy, discovery) have no ambient
    /// caller identity, so they always resolve through this lookup rather than reading
    /// <see cref="_options"/> directly.
    /// </summary>
    private SandboxCredential CredentialFor(string sessionId) =>
        _sessionCredentials.TryGetValue(sessionId, out var credential) ? credential : _defaultCredential;

    /// <summary>
    /// Resolves (and caches) the credential-scoped <see cref="SandboxClient"/> for
    /// <paramref name="credential"/>. Every gateway control-plane call routes through here so a
    /// single seam owns the per-credential client lifetime. The client is KEYLESS: it stamps only
    /// <c>X-Sbx-App-Id</c> (from the credential's app id, also carried as the wire <c>app.id</c>);
    /// the opaque per-app KEY is stamped verbatim downstream by a per-credential
    /// <see cref="GatewayAuthHandler"/>. Routing the key through the SDK's <c>ClientSecret</c> would
    /// fail-fast on a non-base64 dev key that the gateway itself would accept, so the SDK never sees
    /// it. All clients borrow the one shared <see cref="_httpClient"/> transport via a cloning
    /// forwarding handler, so the connection pool (and, in tests, the recording handler) is shared
    /// and no caller's key ever lands on a shared client's default headers.
    /// </summary>
    private SandboxClient ClientFor(SandboxCredential credential)
    {
        lock (_clientsLock)
        {
            return GetOrCreateClientEntryLocked(credential).Client;
        }
    }

    /// <summary>Resolves (creating on first use) the per-credential client entry. Caller MUST hold <see cref="_clientsLock"/>.</summary>
    private SandboxClientEntry GetOrCreateClientEntryLocked(SandboxCredential credential)
    {
        var key = (credential.AppId, credential.AppKey);
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var serverAddress = new Uri(_gateway.GatewayBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        // TransportTimeout is enforced per-call by the SDK via a linked token; borrow the shared client's
        // timeout (default 100s) so behaviour matches the pre-SDK path, and fall back to 100s when the
        // shared client's timeout is infinite/unset.
        var transportTimeout =
            _httpClient.Timeout > TimeSpan.Zero ? _httpClient.Timeout : TimeSpan.FromSeconds(100);
        var options = new SandboxClientOptions(
            serverAddress,
            credential.AppId,
            clientSecret: string.Empty,
            executionTimeout: TimeSpan.FromMinutes(5),
            transportTimeout: transportTimeout,
            allowInsecureDevelopmentTransport: true
        );
        // Pipeline: GatewayAuthHandler stamps the per-caller app key verbatim (idempotent, gated on a
        // configured key), then the forwarding handler clones the request onto the shared borrowed
        // transport. The per-credential HttpClient owns ONLY this pipeline — disposing it never disposes
        // the shared transport.
        var pipeline = new GatewayAuthHandler(credential.AppId, credential.AppKey)
        {
            InnerHandler = new SharedTransportForwardingHandler(_httpClient),
        };
        var transport = new HttpClient(pipeline, disposeHandler: true);
        var client = new SandboxClient(options, transport);
        var entry = new SandboxClientEntry(client, transport);
        _clients[key] = entry;
        return entry;
    }

    /// <summary>
    /// Resolves the per-credential client AND pins it by RESERVING a session refcount, atomically under
    /// <see cref="_clientsLock"/> — so a concurrent last-session destroy cannot decrement to zero and
    /// dispose the transport while a create is still using it. The caller MUST either commit the
    /// reservation (on success it becomes the new session's refcount) or release it via
    /// <see cref="DecrementSessionRefAndMaybeDispose"/> on failure.
    /// </summary>
    private SandboxClient AcquireClientForSession(SandboxCredential credential)
    {
        lock (_clientsLock)
        {
            var entry = GetOrCreateClientEntryLocked(credential);
            entry.SessionRefCount++;
            return entry.Client;
        }
    }

    /// <summary>
    /// Decrements the session refcount of <paramref name="credential"/>'s client entry; when it reaches
    /// zero the entry is evicted from <see cref="_clients"/> and its client + transport disposed (OUTSIDE
    /// the lock). Called once per session that goes away (explicit destroy or gateway eviction).
    /// </summary>
    private void DecrementSessionRefAndMaybeDispose(SandboxCredential credential)
    {
        SandboxClientEntry? toDispose = null;
        lock (_clientsLock)
        {
            var key = (credential.AppId, credential.AppKey);
            if (_clients.TryGetValue(key, out var entry))
            {
                entry.SessionRefCount--;
                if (entry.SessionRefCount <= 0)
                {
                    _ = _clients.TryRemove(key, out _);
                    toDispose = entry;
                }
            }
        }

        if (toDispose is not null)
        {
            toDispose.Client.Dispose();
            toDispose.Transport.Dispose();
        }
    }

    /// <summary>Test-only: the number of live per-credential client entries currently cached. Used to assert refcount eviction.</summary>
    internal int PerCredentialClientCount
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>
    /// Forwards each per-credential SDK request onto the ONE shared borrowed <see cref="HttpClient"/>
    /// transport. The request is CLONED first because a single <see cref="HttpRequestMessage"/> can
    /// not be sent through two <see cref="HttpClient"/> instances (the outer per-credential client
    /// and this shared one) — the second send throws "request already sent". Cloning lets every
    /// per-credential client share the shared client's connection pool (and, in tests, its recording
    /// handler) while the per-credential pipeline still stamps the caller's opaque app key. Never
    /// disposes the shared transport: it is borrowed and the registry owns its lifetime.
    /// </summary>
    private sealed class SharedTransportForwardingHandler(HttpClient shared) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var header in request.Headers)
            {
                _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var bytes = await request
                    .Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                var content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                {
                    _ = content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = content;
            }

            // Forward on ResponseHeadersRead (NOT the default ResponseContentRead): buffering must defer to
            // the OUTER per-credential HttpClient, which already picks the right completion per call — the
            // SDK streams file/artifact downloads under its 64 MiB cap via ResponseHeadersRead, and fully
            // buffers only the small JSON control-plane bodies. Buffering here would fully read the body
            // before the SDK's cap could bound it, defeating the cap on the daemon's borrowed-transport
            // path. The outer caller owns the returned response's lifetime (its `using` disposes it,
            // releasing this shared client's pooled connection); the clone is never disposed early.
            return await shared
                .SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Back-compat overload that creates (or returns) the session for <paramref name="workspaceId"/>
    /// using the configured default workspace directory. Equivalent to passing a
    /// <see cref="WorkspaceRef"/> with a null directory.
    /// </summary>
    /// <param name="workspaceId">Logical workspace key.</param>
    /// <param name="ct">Cancellation token observed by the creating call.</param>
    /// <param name="credential">Caller credential to create the session under; <c>null</c>
    /// (interactive/daemon callers) resolves to the process-wide default. See the
    /// <see cref="GetOrCreateSessionAsync(WorkspaceRef, CancellationToken, SandboxCredential?)"/>
    /// overload for how this drives the session-cache partition.</param>
    public Task<SandboxSession> GetOrCreateSessionAsync(
        string workspaceId = DefaultWorkspaceId,
        CancellationToken ct = default,
        SandboxCredential? credential = null
    )
    {
        return GetOrCreateSessionAsync(new WorkspaceRef(workspaceId), ct, credential);
    }

    /// <summary>
    /// Returns the sandbox session for <paramref name="workspaceRef"/>, creating it on first use.
    /// The cache is keyed by (<see cref="WorkspaceRef.Id"/>, the effective caller app id); on first
    /// creation the mounted directory is resolved from <see cref="WorkspaceRef.DirectoryRelPath"/>
    /// (null/blank → configured default). Concurrent first-callers for the SAME (workspace id, app
    /// id) pair share a single creation; subsequent callers for that pair get the cached session
    /// (subsequent directory values are ignored — the first creation wins). A different
    /// <paramref name="credential"/> for the same <see cref="WorkspaceRef.Id"/> is a DIFFERENT
    /// cache entry — it creates (and owns) its own gateway session rather than sharing one.
    /// </summary>
    /// <param name="workspaceRef">The workspace identity + directory to mount.</param>
    /// <param name="ct">Cancellation token observed by the creating call.</param>
    /// <param name="credential">Caller credential to create the session under, and the credential
    /// whose <c>AppId</c> partitions the session cache. <c>null</c> resolves to the process-wide
    /// default credential (unchanged behavior for interactive/daemon callers, which never pass
    /// one).</param>
    public Task<SandboxSession> GetOrCreateSessionAsync(
        WorkspaceRef workspaceRef,
        CancellationToken ct = default,
        SandboxCredential? credential = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workspaceRef);

        var workspaceId = string.IsNullOrWhiteSpace(workspaceRef.Id) ? DefaultWorkspaceId : workspaceRef.Id;
        var effectiveRef = workspaceRef with { Id = workspaceId };
        var effectiveCredential = credential ?? _defaultCredential;
        var key = (workspaceId, effectiveCredential.AppId);

        // Use CancellationToken.None inside the single-flight Lazy factory: the shared creation task
        // must not be poisoned by the first caller's request-scoped token being cancelled (e.g. that
        // caller disconnecting), which would otherwise fail it for all concurrent waiters.
        var lazy = _sessions.GetOrAdd(
            key,
            _ => new Lazy<Task<SandboxSession>>(
                () => CreateSessionAsync(effectiveRef, CancellationToken.None, effectiveCredential),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );

        return AwaitAndEvictOnFailureAsync(key, lazy);
    }

    /// <summary>
    /// Awaits the cached creation task. On failure the cached <see cref="Lazy{T}"/> is evicted so a
    /// later call retries instead of replaying the cached fault.
    /// </summary>
    private async Task<SandboxSession> AwaitAndEvictOnFailureAsync(
        (string WorkspaceId, string AppId) key,
        Lazy<Task<SandboxSession>> lazy
    )
    {
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Only evict the entry we own, in case another creation has since replaced it.
            _ = ((ICollection<KeyValuePair<(string WorkspaceId, string AppId), Lazy<Task<SandboxSession>>>>)_sessions).Remove(
                new KeyValuePair<(string WorkspaceId, string AppId), Lazy<Task<SandboxSession>>>(key, lazy)
            );
            throw;
        }
    }

    /// <summary>
    /// Like <see cref="GetOrCreateSessionAsync(string, CancellationToken, SandboxCredential?)"/>,
    /// but additionally verifies the cached session still exists on the gateway and transparently
    /// recreates it when it does not. See the <see cref="WorkspaceRef"/> overload for the full
    /// rationale.
    /// </summary>
    /// <param name="workspaceId">Logical workspace key.</param>
    /// <param name="ct">Cancellation token observed by the call.</param>
    /// <param name="credential">Caller credential; see the <see cref="WorkspaceRef"/> overload.</param>
    public Task<SandboxSession> GetOrCreateLiveSessionAsync(
        string workspaceId = DefaultWorkspaceId,
        CancellationToken ct = default,
        SandboxCredential? credential = null
    )
    {
        return GetOrCreateLiveSessionAsync(new WorkspaceRef(workspaceId), ct, credential);
    }

    /// <summary>
    /// Returns a sandbox session for <paramref name="workspaceRef"/> that is guaranteed to still be
    /// known to the gateway. The in-memory cache (<see cref="GetOrCreateSessionAsync(WorkspaceRef, CancellationToken, SandboxCredential?)"/>)
    /// is reused as-is while the session is healthy, but the gateway evicts idle sessions on its own
    /// schedule — after which every call for that id returns <c>404 "Session not found"</c>. Reusing
    /// the dead handle silently strips the session's marketplace-provided tools (e.g.
    /// <c>sandbox-Skill</c>). On a definitive 404 this method drops the stale cache entry and
    /// recreates the session with the same workspace + marketplace selection, so callers always get a
    /// live session with the full tool set. Non-404 probe failures are treated as "still ours" to
    /// avoid churning a healthy session when the gateway is briefly unreachable.
    /// </summary>
    /// <param name="workspaceRef">The workspace identity + directory to mount.</param>
    /// <param name="ct">Cancellation token observed by the call.</param>
    /// <param name="credential">Caller credential to (re)create the session under; <c>null</c>
    /// resolves to the process-wide default. Passing the SAME credential on every call for a given
    /// conversation (the caller's responsibility — see <c>MultiTurnAgentPool</c>'s frozen
    /// <c>CallerCredential</c>) is what makes a background recreate after eviction reuse the
    /// original creator's identity instead of silently falling back to the default.</param>
    public async Task<SandboxSession> GetOrCreateLiveSessionAsync(
        WorkspaceRef workspaceRef,
        CancellationToken ct = default,
        SandboxCredential? credential = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workspaceRef);

        var workspaceId = string.IsNullOrWhiteSpace(workspaceRef.Id) ? DefaultWorkspaceId : workspaceRef.Id;
        var effectiveRef = workspaceRef with { Id = workspaceId };
        var effectiveCredential = credential ?? _defaultCredential;

        var session = await GetOrCreateSessionAsync(effectiveRef, ct, effectiveCredential).ConfigureAwait(false);
        if (await IsSessionAliveAsync(session.SessionId, ct).ConfigureAwait(false))
        {
            return session;
        }

        _logger.LogWarning(
            "Sandbox gateway no longer recognizes session {SessionId} for workspace {WorkspaceId} "
                + "(likely evicted after idle); recreating it so marketplace tools are restored.",
            session.SessionId,
            workspaceId
        );

        InvalidateSession((workspaceId, effectiveCredential.AppId), session);
        return await GetOrCreateSessionAsync(effectiveRef, ct, effectiveCredential).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes the gateway for <paramref name="sessionId"/>. Returns <c>false</c> only on a definitive
    /// <c>404</c> (the gateway has forgotten the session); any other status — success OR a transient
    /// error — is reported as alive so a flaky gateway never triggers needless recreation.
    /// </summary>
    private async Task<bool> IsSessionAliveAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        // Cap the probe well under the shared client timeout: a wedged (non-404) gateway must not
        // stall the turn — degrade quickly to "assume alive" instead. The linked token still honours
        // a genuine caller cancellation.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(SessionLivenessProbeTimeout);
        try
        {
            _ = await ClientFor(CredentialFor(sessionId))
                .GetAsync(sessionId, probeCts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.NotFound)
        {
            // Auth-scoping signal: a KNOWN session id (we hold a handle to it) 404ing while valid
            // auth headers were sent may indicate the session belongs to a different app id rather
            // than being genuinely evicted. Does not change control flow — the caller still treats
            // this as "not alive" and recreates.
            _logger.LogWarning(
                "Sandbox gateway returned 404 for known session {SessionId} with auth headers "
                    + "present ({is_scoped_404}); may indicate app-id scoping/ownership drift "
                    + "rather than a genuinely evicted session.",
                sessionId,
                true
            );
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine caller cancellation — propagate
        }
        catch (Exception ex)
        {
            // Probe timeout or transient error — including a malformed/empty 2xx body, which the SDK
            // surfaces as Protocol — → assume alive so a flaky/slow gateway never churns a healthy
            // session (recreating wouldn't help if the gateway is unreachable anyway). Only a
            // definitive 404 (handled above) reports "not alive", matching the pre-SDK contract.
            _logger.LogInformation(
                ex,
                "Liveness probe for sandbox session {SessionId} failed or timed out; assuming the session is still alive.",
                sessionId
            );
            return true;
        }
    }

    /// <summary>
    /// Drops a dead session from both caches so the next request recreates it. Removes the
    /// per-(workspace id, caller app id) creation entry and the reverse id mapping (the latter only
    /// when it still points at <paramref name="session"/>, so a concurrent recreation is never
    /// clobbered).
    /// </summary>
    private void InvalidateSession((string WorkspaceId, string AppId) key, SandboxSession session)
    {
        // Remove the cached creation ONLY when it still holds the exact dead session — mirroring
        // AwaitAndEvictOnFailureAsync's "only evict the entry we own" discipline. A plain key-removal
        // would clobber a fresh session a concurrent caller may have just installed, orphaning it on
        // the gateway and causing churn.
        if (
            _sessions.TryGetValue(key, out var lazy)
            && lazy.IsValueCreated
            && lazy.Value.IsCompletedSuccessfully
            && ReferenceEquals(lazy.Value.Result, session)
        )
        {
            _ = (
                (ICollection<KeyValuePair<(string WorkspaceId, string AppId), Lazy<Task<SandboxSession>>>>)_sessions
            ).Remove(
                new KeyValuePair<(string WorkspaceId, string AppId), Lazy<Task<SandboxSession>>>(key, lazy)
            );
        }

        // Reverse map removal is already exact: only drops the entry when it still points at this
        // dead session, so a concurrent recreation's mapping is never clobbered.
        _ = ((ICollection<KeyValuePair<string, SandboxSession>>)_sessionsById).Remove(
            new KeyValuePair<string, SandboxSession>(session.SessionId, session)
        );
        // Drop the session's credential and release its client refcount (evicting+disposing the per-
        // credential client when this was its last session). Guarded on the TryRemove so an already-
        // invalidated session never double-decrements.
        if (_sessionCredentials.TryRemove(session.SessionId, out var credential))
        {
            DecrementSessionRefAndMaybeDispose(credential);
        }
    }

    /// <summary>
    /// POSTs a sandbox-create request to the gateway and maps the response to a
    /// <see cref="SandboxSession"/>.
    /// </summary>
    /// <param name="workspaceRef">The workspace identity + directory to mount.</param>
    /// <param name="ct">Cancellation token observed by the call.</param>
    /// <param name="credential">Caller credential to create the session under; <c>null</c> resolves
    /// to the process-wide default (M1 behavior, still the only path for the daemon and every
    /// contextless caller).</param>
    private async Task<SandboxSession> CreateSessionAsync(
        WorkspaceRef workspaceRef,
        CancellationToken ct,
        SandboxCredential? credential = null
    )
    {
        var workspaceId = workspaceRef.Id;

        // Surface the clear, actionable gateway error here (not at app startup) — this is the
        // first point at which Workspace Agent mode actually requires a healthy gateway. Wrap the
        // gateway-unreachable failure as SandboxSessionUnavailableException so the WebSocket layer
        // surfaces a clean client error instead of crashing the connection with an unhandled 500.
        try
        {
            await _gateway.EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not SandboxSessionUnavailableException)
        {
            throw new SandboxSessionUnavailableException(workspaceId, statusCode: null, ex.Message, ex);
        }

        // Resolve only the workspace LEAF — the identifier sent to the gateway as the `workspace`
        // field. The client deliberately does NOT touch the workspace filesystem here: the sandbox
        // gateway may be on a remote machine, and it owns workspace directory creation
        // (create-if-missing) and mounting. A local WorkspaceBasePath/WorkspacePath is used only by
        // the spawn path to provision a LOCAL gateway (SandboxGatewayLifetime.EnsureWorkspaceDirectory)
        // — it is neither required nor resolved to a local path when adopting a (possibly remote) gateway.
        var (_, workspaceLeaf, _) = _options.ResolveWorkspace(workspaceRef.DirectoryRelPath);
        var workspaceRelPath = workspaceLeaf ?? string.Empty;

        var (authProviders, network) = BuildAuthProviders();
        var discovery = BuildDiscovery();
        // Per-workspace marketplace selection wins; fall back to the global config default when the
        // workspace enables none. Either way `null` means "omit the field, gateway picks its default".
        var marketplaces = workspaceRef.Marketplaces is { Count: > 0 }
            ? workspaceRef.Marketplaces
            : MarketplaceAliases.Parse(_options.Marketplaces);
        // The effective credential for this creation: caller-supplied (M2 per-caller passthrough) or
        // the process-wide default (M1 behavior, still the only path for the daemon and any other
        // contextless caller).
        var effectiveCredential = credential ?? _defaultCredential;
        // The SDK owns protocol only: it maps this fully-formed request to the wire
        // `app.id` (from the credential-scoped client) / `workspace` / `auth_providers` / `network` /
        // `discovery` / `marketplaces` shape the gateway expects — identical bytes to the hand-rolled
        // DTOs this migration removed.
        var createRequest = new SandboxCreateRequest(
            workspaceRelPath,
            marketplaces,
            authProviders,
            network,
            discovery
        );

        _logger.LogInformation(
            "Sandbox session marketplace selection: {Marketplaces}",
            marketplaces is { Count: > 0 } ? string.Join(", ", marketplaces) : "(gateway default)"
        );

        _logger.LogInformation(
            "Creating sandbox session for workspace {WorkspaceId} (app {AppId}, path '{WorkspaceRelPath}')",
            workspaceId,
            effectiveCredential.AppId,
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

        // Reserve the per-credential client's session refcount BEFORE using it for the create, so a
        // concurrent last-session destroy under the SAME credential cannot decrement to zero and dispose
        // the transport while this create is still in flight (two workspaces under one credential — the
        // daemon's normal multi-workspace case). The reservation IS this session's refcount on success; on
        // ANY failure the finally below rolls it back (evicting+disposing the client if it was the last).
        var client = AcquireClientForSession(effectiveCredential);
        var reservationCommitted = false;
        try
        {
            SandboxInfo info;
            try
            {
                info = await client.CreateAsync(createRequest, ct).ConfigureAwait(false);
            }
            catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.TransportTimeout)
            {
                // The gateway was unreachable (down / restarting / connection refused) or the request
                // timed out. Surface it as the same clean, handled "sandbox unavailable" signal the
                // WebSocket and mode-switch layers already catch — not a raw exception that crashes the
                // request with an unhandled 500. (A genuine CALLER cancellation is re-thrown by the SDK as
                // OperationCanceledException, not a SandboxException, so it bypasses this catch.)
                _logger.LogWarning(
                    ex,
                    "Sandbox create could not reach the gateway at {GatewayBaseUrl} for workspace {WorkspaceId}",
                    _gateway.GatewayBaseUrl,
                    workspaceId
                );
                throw new SandboxSessionUnavailableException(
                    workspaceId,
                    statusCode: null,
                    $"Could not reach the sandbox gateway at {_gateway.GatewayBaseUrl} to create a session "
                        + $"for workspace '{workspaceId}'.",
                    ex
                );
            }
            catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.Authorization)
            {
                // Distinct marker from the connectivity-failure path above: this is the gateway actively
                // rejecting the presented credential (misconfigured/rotated/wrong app key), not an
                // unreachable gateway. The SDK never reads the gateway auth-rejection body (the response
                // most likely to echo submitted credential material), so only the app id + status code are
                // logged — preserving the never-log-the-key invariant.
                _logger.LogError(
                    "Sandbox create failed for workspace {WorkspaceId}: sandbox_auth_failed "
                        + "({StatusCode}) for app {AppId}",
                    workspaceId,
                    ex.StatusCode,
                    effectiveCredential.AppId
                );
                throw new SandboxSessionUnavailableException(
                    workspaceId,
                    ex.StatusCode,
                    // Client-safe: names only the app id + status code, never any upstream auth output,
                    // so the controller-surfaced `detail` field can never echo secret-bearing material.
                    $"sandbox_auth_failed: sandbox gateway rejected the credential for app "
                        + $"'{effectiveCredential.AppId}' creating a session for workspace '{workspaceId}' "
                        + $"({ex.StatusCode})."
                );
            }
            catch (SandboxException ex) when (ex.StatusCode is >= 200 and < 300)
            {
                // A success status with an unusable body (missing session id or malformed JSON), which the
                // SDK classifies as Protocol. Mirrors the pre-SDK "success but no session id"
                // InvalidOperationException.
                throw new InvalidOperationException(
                    $"Sandbox gateway returned a success status but no session id for workspace '{workspaceId}'.",
                    ex
                );
            }
            catch (SandboxException ex)
            {
                // Any other non-success status (e.g. a rejected network policy 4xx/5xx). The SDK never
                // surfaces the gateway body, so the previous body-in-log/message diagnostic is dropped in
                // favour of the status code; the observable outcome (SandboxSessionUnavailableException
                // carrying the gateway status) is unchanged.
                _logger.LogError(
                    "Sandbox create failed for workspace {WorkspaceId}: {StatusCode}",
                    workspaceId,
                    ex.StatusCode
                );
                throw new SandboxSessionUnavailableException(
                    workspaceId,
                    ex.StatusCode,
                    $"Sandbox gateway returned {ex.StatusCode} creating a session for "
                        + $"workspace '{workspaceId}'."
                );
            }

            var hostPath = StripLongPathPrefix(info.WorkspaceContainerPath ?? string.Empty);
            var session = new SandboxSession(workspaceId, info.SessionId, workspaceRelPath, hostPath);

            // Register the reverse session-id → session mapping so the context-discovery webhook can
            // resolve back to the session. Last-write-wins is acceptable because session ids are
            // gateway-allocated and unique per creation.
            _sessionsById[session.SessionId] = session;
            // Capture the credential this session was created with so contextless paths (liveness,
            // destroy, discovery) resolve the SAME credential via CredentialFor, not the process
            // default (relevant once M2 lets callers create under a per-caller credential).
            _sessionCredentials[session.SessionId] = effectiveCredential;
            // The reservation acquired above IS this session's refcount — commit it (no extra increment),
            // so the finally does not roll it back.
            reservationCommitted = true;

            _logger.LogInformation(
                "Created sandbox session {SessionId} for workspace {WorkspaceId} at host path {HostPath}",
                session.SessionId,
                workspaceId,
                session.HostPath
            );

            return session;
        }
        finally
        {
            if (!reservationCommitted)
            {
                // Create failed (or was cancelled) — release the reservation, evicting+disposing the client
                // if this was its last hold. A successful create keeps the reservation as the session's ref.
                DecrementSessionRefAndMaybeDispose(effectiveCredential);
            }
        }
    }

    /// <summary>
    /// Destroys every session cached for <paramref name="workspaceId"/> — across ALL caller app ids,
    /// since the cache is now partitioned by (workspace id, app id) (M2): a workspace id alone no
    /// longer identifies a single cache entry. Callers that only know the workspace id (e.g. the
    /// review daemon's per-run cleanup) still get complete teardown this way, rather than leaking
    /// every non-default-credential session forever. Evicts each entry's creation slot + reverse
    /// maps, then issues the gateway DELETE. Idempotent — a no-op when nothing is cached for the id.
    /// Best-effort: gateway failures are logged inside <see cref="DestroySessionAsync"/> and
    /// swallowed so run teardown never throws.
    /// </summary>
    public async Task DestroyWorkspaceSessionAsync(string workspaceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var matchingKeys = _sessions.Keys.Where(key => key.WorkspaceId == workspaceId).ToList();

        foreach (var key in matchingKeys)
        {
            if (
                !_sessions.TryRemove(key, out var lazy)
                || !lazy.IsValueCreated
                || !lazy.Value.IsCompletedSuccessfully
            )
            {
                continue;
            }

            var session = lazy.Value.Result;
            _ = ((ICollection<KeyValuePair<string, SandboxSession>>)_sessionsById).Remove(
                new KeyValuePair<string, SandboxSession>(session.SessionId, session));
            _ = _subAgentBindings.TryRemove(session.SessionId, out _);
            _ = _sessionThreads.TryRemove(session.SessionId, out _);
            _ = _discoverySeen.TryRemove(session.SessionId, out _);

            // Destroy BEFORE dropping the credential: DestroySessionAsync resolves the session's
            // creating credential via CredentialFor(sessionId), so the DELETE must carry the owner's
            // X-Sbx-App-Id (a foreign/default id would be rejected 404, leaking the gateway session).
            await DestroySessionAsync(session, ct).ConfigureAwait(false);
            // Drop the credential and release its client refcount (evicting+disposing the per-credential
            // client when this was its last session). Guarded so a concurrent invalidate never double-drops.
            if (_sessionCredentials.TryRemove(session.SessionId, out var credential))
            {
                DecrementSessionRefAndMaybeDispose(credential);
            }
        }
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

            await DestroySessionAsync(entry.Value.Result, CancellationToken.None).ConfigureAwait(false);
        }

        _sessions.Clear();
        _subAgentBindings.Clear();
        _sessionsById.Clear();
        _sessionThreads.Clear();
        _discoverySeen.Clear();
        _sessionCredentials.Clear();

        // Catch-all: dispose every remaining per-credential transport (refcount eviction handles the
        // steady state; this covers any sessions still live at shutdown). Snapshot + clear under the lock,
        // then dispose OUTSIDE it. Each entry owns only its GatewayAuthHandler → forwarding pipeline; the
        // SandboxClient over it is borrowed, so disposing the client is a no-op and the forwarding handler
        // never disposes the shared transport. The shared transport is disposed last, below.
        List<SandboxClientEntry> remainingClients;
        lock (_clientsLock)
        {
            remainingClients = [.. _clients.Values];
            _clients.Clear();
        }

        foreach (var entry in remainingClients)
        {
            entry.Client.Dispose();
            entry.Transport.Dispose();
        }

        _httpClient.Dispose();
    }

    /// <summary>
    /// Lists items the gateway has discovered for the session's workspace (the result of its
    /// background sweep of context files — sub-agents, skills, etc). Callers filter by
    /// <see cref="DiscoveredItem.Kind"/> for the kinds they care about.
    /// </summary>
    /// <param name="sessionId">Gateway session id returned by <see cref="GetOrCreateSessionAsync(WorkspaceRef, CancellationToken, SandboxCredential?)"/>.</param>
    /// <param name="ct">Cancellation token observed by the HTTP call.</param>
    /// <returns>The discovered items. Empty when the gateway reports no discoveries; never null.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the gateway returns a non-success status. The gateway status code is included so
    /// callers can correlate against gateway logs. Caller is expected to catch + log + degrade.
    /// </exception>
    public async Task<IReadOnlyList<DiscoveredItem>> ListDiscoveredAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            var items = await ClientFor(CredentialFor(sessionId))
                .ListDiscoveredAsync(sessionId, ct)
                .ConfigureAwait(false);
            return
            [
                .. items.Select(i => new DiscoveredItem(
                    i.Kind,
                    i.Name!,
                    i.Description,
                    i.Path,
                    i.Content,
                    i.QualifiedName
                )),
            ];
        }
        catch (SandboxException ex)
        {
            // Log + throw, matching CreateSessionAsync's policy: the caller is expected to catch and
            // degrade, but we still want a server-log breadcrumb correlating the failure with gateway
            // logs by session id. The SDK never surfaces the raw gateway body (it may echo submitted
            // material), so only the status code is available as the correlation signal.
            _logger.LogError(
                "Sandbox discovered-items list failed for session {SessionId}: {StatusCode}",
                sessionId,
                ex.StatusCode
            );
            throw new InvalidOperationException(
                $"Sandbox gateway returned {ex.StatusCode} listing discovered items for "
                    + $"session '{sessionId}'.",
                ex
            );
        }
    }

    /// <summary>
    /// Reads the text content of a file inside the session's sandbox through the gateway via the
    /// typed <see cref="SandboxClient"/> SDK's direct files API (ADR 0031 / issue #119): the SDK
    /// resolves the session's workspace mount, then issues a single
    /// <c>GET /api/v1/sandboxes/{session_id}/files/{mount_id}?path=...</c> that returns the file's exact
    /// bytes (scoped by the <c>X-Session-ID</c> header). This is the ONLY way the (local-host) backend
    /// can obtain a workspace file's content in the Docker topology — it cannot read the container's
    /// <c>/workspace</c> filesystem directly, and the discovery query API is metadata-only.
    /// </summary>
    /// <param name="sessionId">Gateway session id whose sandbox the file lives in.</param>
    /// <param name="absolutePath">Path INSIDE the sandbox — accepts a rooted
    /// <c>/workspace/CLAUDE.md</c>, the session's host path, or an already-relative
    /// <c>CLAUDE.md</c>; it is normalised to a workspace-relative path for the SDK.</param>
    /// <param name="ct">Cancellation token observed by the HTTP call.</param>
    /// <returns>
    /// The file's raw text, or <c>null</c> when the file is missing, the tool reports an error, or the
    /// call fails. Best-effort by contract: callers treat <c>null</c> as "no content" and degrade.
    /// </returns>
    public async Task<string?> ReadWorkspaceFileAsync(string sessionId, string absolutePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        // The SDK's transfer protocol addresses files by WORKSPACE-RELATIVE path and rejects rooted /
        // backslash paths; the callers here pass an in-sandbox absolute path (/workspace/CLAUDE.md) or
        // the session host path, so convert before delegating.
        var relativePath = ToWorkspaceRelativePath(sessionId, absolutePath);

        try
        {
            return await ClientFor(CredentialFor(sessionId))
                .ReadTextFileAsync(sessionId, relativePath, ct)
                .ConfigureAwait(false);
        }
        catch (SandboxException)
        {
            // Best-effort: a missing file, an evicted session, a tool-level error, or any transport
            // failure is "no content", never a thrown error — the caller (boot-time system-prompt
            // seed) degrades to no workspace instructions. A genuine caller cancellation surfaces as
            // OperationCanceledException (not a SandboxException) and is left to propagate.
            return null;
        }
    }

    /// <summary>
    /// Normalises an in-sandbox path (a rooted <c>/workspace/…</c> path, the session's host path, or
    /// an already-relative path) to the workspace-relative form the SDK's transfer protocol requires.
    /// Strips a leading host-path prefix (when the session is known), then a leading
    /// <c>/workspace/</c> segment, then any residual leading slash; backslashes are normalised to
    /// forward slashes for the container filesystem.
    /// </summary>
    private string ToWorkspaceRelativePath(string sessionId, string absolutePath)
    {
        var path = absolutePath.Replace('\\', '/');

        if (_sessionsById.TryGetValue(sessionId, out var session)
            && !string.IsNullOrEmpty(session.HostPath))
        {
            var hostPath = session.HostPath.Replace('\\', '/').TrimEnd('/');
            if (path.StartsWith(hostPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(hostPath.Length + 1)..];
            }
            else if (string.Equals(path, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                path = string.Empty;
            }
        }

        const string WorkspacePrefix = "/workspace/";
        if (path.StartsWith(WorkspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[WorkspacePrefix.Length..];
        }

        return path.TrimStart('/');
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
            _ => new SubAgentSessionBinding(
                new MutableSubAgentTemplateSource(seed),
                agentFactory));

        return ReconcileSubAgentBindingSeed(binding, seed);
    }

    /// <summary>
    /// Creates a conversation binding or atomically refreshes both provider factories on its
    /// existing binding while preserving the conversation's template source.
    /// </summary>
    /// <remarks>
    /// Use this when recreating an agent for the same conversation. Existing
    /// <see cref="GetOrAddSubAgentBinding(string, string, IReadOnlyDictionary{string, SubAgentTemplate}, Func{IStreamingAgent})"/>
    /// overloads intentionally retain their unchanged-on-hit behavior.
    /// </remarks>
    public SubAgentSessionBinding AddOrUpdateSubAgentBinding(
        string sessionId,
        string conversationId,
        IReadOnlyDictionary<string, SubAgentTemplate> seed,
        Func<IStreamingAgent> agentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent>? characteristicsAgentFactory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(agentFactory);

        var conversations = _subAgentBindings.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, SubAgentSessionBinding>(StringComparer.Ordinal));

        var binding = conversations.AddOrUpdate(
            conversationId,
            _ =>
            {
                var source = new MutableSubAgentTemplateSource(seed);
                source.RebindFactories(agentFactory, characteristicsAgentFactory);
                return new SubAgentSessionBinding(source, agentFactory)
                {
                    CharacteristicsAgentFactory = characteristicsAgentFactory,
                };
            },
            (_, existing) =>
            {
                existing.Source.RebindFactories(agentFactory, characteristicsAgentFactory);
                return existing with
                {
                    AgentFactory = agentFactory,
                    CharacteristicsAgentFactory = characteristicsAgentFactory,
                };
            });

        return ReconcileSubAgentBindingSeed(binding, seed);
    }

    private static SubAgentSessionBinding ReconcileSubAgentBindingSeed(
        SubAgentSessionBinding binding,
        IReadOnlyDictionary<string, SubAgentTemplate> seed)
    {
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
    /// <see cref="GetOrAddSubAgentBinding(string, string, IReadOnlyDictionary{string, SubAgentTemplate}, Func{IStreamingAgent})"/>.
    /// Returns false (with <paramref name="binding"/> set to
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

        // Drop this thread's membership from every session it was routed to. The discovery dedup
        // ledger is intentionally NOT cleared here: as before #198 it is cleared only on session
        // destroy/dispose (DestroyWorkspaceSessionAsync / DisposeAsync). Removing the old
        // clear-on-last-thread step also removes its check-then-act race against a concurrent
        // RegisterThread. Per-target growth within a long-lived session is bounded by the session's
        // own lifetime; per-sub-agent reclamation is deferred until the gateway can signal sub-agent
        // teardown (tracked with the gateway dependency #187).
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
    /// Atomically marks <c>(target, kind, path)</c> as seen for <paramref name="sessionId"/>.
    /// Returns true on the first call (caller proceeds with injection) and false on every subsequent
    /// call for the same tuple (caller drops the duplicate). Used to defend against gateway retry
    /// storms re-firing the same discovery event.
    /// </summary>
    /// <remarks>
    /// <paramref name="target"/> discriminates WHO the discovery was delivered to: a sub-agent's
    /// <c>agent_id</c> for a routed delivery, or <see cref="SessionDiscoveryTarget"/> for the session
    /// fan-out / root-file seed. Keying on it lets the primary and a sub-agent (or two sub-agents)
    /// entering the same directory each receive that directory's context exactly once.
    /// </remarks>
    public bool TryMarkDiscoverySeen(string sessionId, string target, string kind, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var set = _discoverySeen.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        return set.TryAdd(DiscoverySeenKey(target, kind, path), 0);
    }

    /// <summary>
    /// Back-compat overload that marks <c>(kind, path)</c> under the session-level
    /// <see cref="SessionDiscoveryTarget"/> sentinel — the pre-#198 signature. Preserves the
    /// session-scoped dedup behaviour for existing/external callers that do not route to a sub-agent.
    /// </summary>
    public bool TryMarkDiscoverySeen(string sessionId, string kind, string path) =>
        TryMarkDiscoverySeen(sessionId, SessionDiscoveryTarget, kind, path);

    /// <summary>
    /// Removes exactly the one <c>(target, kind, path)</c> dedup entry for <paramref name="sessionId"/>,
    /// undoing a single <see cref="TryMarkDiscoverySeen(string, string, string, string)"/> mark. Used by
    /// the router to roll back a routed discovery it optimistically marked but could NOT deliver to a live
    /// sub-agent (all sinks returned <c>NotOwned</c>), so a later gateway redelivery can retry once the
    /// sub-agent registers — healing the pre-registration race. Precise by design: it must NOT disturb a
    /// mark a concurrent delivery placed for another path (or another target) of the same session.
    /// Thread-safe; best-effort no-op for an unknown session/key or after disposal.
    /// </summary>
    public void UnmarkDiscoverySeen(string sessionId, string target, string kind, string path)
    {
        if (_disposed
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_discoverySeen.TryGetValue(sessionId, out var set))
        {
            _ = set.TryRemove(DiscoverySeenKey(target, kind, path), out _);
        }
    }

    /// <summary>
    /// Builds the <see cref="_discoverySeen"/> inner-set key for a <c>(target, kind, path)</c> tuple. A
    /// single builder keeps <see cref="TryMarkDiscoverySeen(string, string, string, string)"/> and
    /// <see cref="UnmarkDiscoverySeen"/> on the exact same key shape.
    /// </summary>
    private static string DiscoverySeenKey(string target, string kind, string path) =>
        $"{target}\0{kind}\0{path}";

    /// <summary>
    /// The exact URL the gateway is told to deliver context-discovery webhooks to (the auth-callback
    /// base plus the discovery route). Surfaced by the diagnostics endpoint so an operator can spot
    /// an unreachable/loopback callback host — the silent-failure mode where discoveries are fired
    /// by the gateway but never arrive at the app.
    /// </summary>
    public string DiscoveryWebhookUrl =>
        $"{_authOptions.Webhook.CallbackBaseUrl}/api/discovery/context_discovery";

    /// <summary>
    /// True when a callback base URL is configured. The default (a loopback host) is non-empty, so
    /// this is normally true; it only reports false if an operator explicitly blanks the webhook
    /// PublicBaseUrl. The more actionable "why is nothing arriving?" signal is the host visible in
    /// <see cref="DiscoveryWebhookUrl"/> — a loopback callback the gateway (in a container) often
    /// cannot reach.
    /// </summary>
    public bool DiscoveryEnabled => !string.IsNullOrWhiteSpace(_authOptions.Webhook.CallbackBaseUrl);

    /// <summary>
    /// Snapshot of the ids of every gateway session currently known to the registry. Used by the
    /// diagnostics endpoint to pair live sessions against their received-discovery counts.
    /// </summary>
    public IReadOnlyCollection<string> GetActiveSessionIds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return [.. _sessionsById.Keys];
    }

    private async Task DestroySessionAsync(SandboxSession session, CancellationToken ct = default)
    {
        try
        {
            await ClientFor(CredentialFor(session.SessionId))
                .DeleteAsync(session.SessionId, ct)
                .ConfigureAwait(false);
            _logger.LogInformation("Destroyed sandbox session {SessionId}", session.SessionId);
        }
        catch (SandboxException ex)
        {
            // Best-effort teardown: a non-success gateway status (incl. a 404 for an already-evicted
            // session) is logged and swallowed so shutdown/run-cleanup never throws.
            _logger.LogWarning(
                "Sandbox destroy returned {StatusCode} for session {SessionId}",
                ex.StatusCode,
                session.SessionId
            );
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

    private (IReadOnlyList<SandboxAuthProvider>? Providers, IReadOnlyList<SandboxNetworkRule>? Network) BuildAuthProviders()
    {
        var providers = new List<SandboxAuthProvider>();
        var rules = new List<SandboxNetworkRule>();
        var baseUrl = _authOptions.Webhook.CallbackBaseUrl;

        if (!string.IsNullOrWhiteSpace(_authOptions.Github.ClientId))
        {
            providers.Add(
                new SandboxAuthProvider(
                    id: "github-auth",
                    type: "webhook",
                    endpoint: $"{baseUrl}/api/auth/webhook/github",
                    gatewayAuth: _sharedSecret.Value,
                    cacheTtlSeconds: 300,
                    requiredScopes: []
                )
            );
            rules.Add(
                new SandboxNetworkRule(
                    id: "github",
                    action: "allow",
                    hosts: OAuthProviderHosts.For("github"),
                    ports: [443],
                    methods: [],
                    paths: [],
                    authProvider: "github-auth",
                    requiredScopes: [],
                    priority: 100
                )
            );
        }

        if (!string.IsNullOrWhiteSpace(_authOptions.Ado.ClientId))
        {
            providers.Add(
                new SandboxAuthProvider(
                    id: "ado-auth",
                    type: "webhook",
                    endpoint: $"{baseUrl}/api/auth/webhook/ado",
                    gatewayAuth: _sharedSecret.Value,
                    cacheTtlSeconds: 300,
                    requiredScopes: []
                )
            );
            rules.Add(
                new SandboxNetworkRule(
                    id: "ado",
                    action: "allow",
                    hosts: OAuthProviderHosts.For("ado"),
                    ports: [443],
                    methods: [],
                    paths: [],
                    authProvider: "ado-auth",
                    requiredScopes: [],
                    priority: 100
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
                new SandboxAuthProvider(
                    id: "m365-auth",
                    type: "webhook",
                    endpoint: $"{baseUrl}/api/auth/webhook/m365",
                    gatewayAuth: _sharedSecret.Value,
                    cacheTtlSeconds: 300,
                    requiredScopes: []
                )
            );
            rules.Add(
                new SandboxNetworkRule(
                    id: "m365",
                    action: "allow",
                    hosts: OAuthProviderHosts.For("m365"),
                    ports: [443],
                    methods: [],
                    paths: [],
                    authProvider: "m365-auth",
                    requiredScopes: [],
                    priority: 100
                )
            );
        }

        // Predefined egress keys (issue #210): one webhook provider + host-scoped allow rule per
        // configured entry. Custom-header entries carry no token expiry, so the gateway falls back to
        // the provider cache TTL — keep it short so an edited/rotated key takes effect promptly; the
        // token-minting kinds carry the minted token's real expiry regardless.
        if (_predefinedKeys is not null)
        {
            foreach (var entry in _predefinedKeys.Entries)
            {
                var id = $"{PredefinedKeyRegistry.ProviderIdPrefix}{entry.Id}";
                var cacheTtlSeconds = entry.Kind == PredefinedKeyKind.CustomHeaders ? 30 : 300;
                providers.Add(
                    new SandboxAuthProvider(
                        id: id,
                        type: "webhook",
                        endpoint: $"{baseUrl}/api/auth/webhook/{id}",
                        gatewayAuth: _sharedSecret.Value,
                        cacheTtlSeconds: cacheTtlSeconds,
                        requiredScopes: []
                    )
                );
                rules.Add(
                    new SandboxNetworkRule(
                        id: id,
                        action: "allow",
                        hosts: [entry.Host],
                        ports: [443],
                        methods: [],
                        paths: [],
                        authProvider: id,
                        requiredScopes: [],
                        priority: 100
                    )
                );
            }
        }

        return providers.Count > 0 ? (providers, rules) : (null, null);
    }

    /// <summary>
    /// Builds the outbound <c>discovery</c> block telling the gateway where to deliver
    /// context-discovery events. The URL is the public webhook base (the same one the gateway
    /// already uses for auth callbacks) plus the discovery route; the auth value is the
    /// gateway↔webhook shared secret. SECRET — never logged.
    /// </summary>
    private SandboxDiscoverySettings BuildDiscovery()
    {
        var url = DiscoveryWebhookUrl;
        // Registration breadcrumb: logs WHERE the gateway will deliver discoveries so an operator
        // can correlate it against the diagnostics endpoint's received counts. NEVER logs the auth
        // value (the gateway↔webhook shared secret) — only the URL and the enabled flag.
        _logger.LogInformation(
            "ContextDiscovery: gateway will deliver discoveries to {WebhookUrl} (enabled={Enabled}).",
            url,
            DiscoveryEnabled);
        return new SandboxDiscoverySettings(url, _sharedSecret.Value);
    }


    /// <summary>One item the gateway has discovered for the workspace (a sub-agent file,
    /// a skill descriptor, …). <see cref="Kind"/> is the discriminator the caller filters by;
    /// <see cref="Path"/> is workspace-relative. <see cref="Content"/> carries the item's inline
    /// body when the gateway includes it (e.g. a marketplace sub-agent's full markdown source);
    /// <see cref="QualifiedName"/> is the plugin-qualified id (e.g.
    /// <c>code-reviewer:architecture-review</c>). Mapped from the SDK's
    /// <see cref="SandboxDiscoveredItem"/>; the <c>JsonPropertyName</c> attributes are retained only
    /// so any host that still serializes this public type keeps its snake_case wire shape.</summary>
    public sealed record DiscoveredItem(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("content")] string? Content = null,
        [property: JsonPropertyName("qualified_name")] string? QualifiedName = null
    );
}
