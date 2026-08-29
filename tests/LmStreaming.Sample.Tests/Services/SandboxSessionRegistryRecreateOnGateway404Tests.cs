using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Regression coverage for the "stale cached session" failure: the registry caches a created
/// session in-memory per workspace and reuses it without re-validating. When the gateway evicts
/// that session (e.g. after an idle period) every later gateway call for it returns
/// <c>404 "Session not found"</c>, and — before this fix — the app kept handing back the dead
/// handle, so the agent silently lost its marketplace-provided tools (e.g. <c>sandbox-Skill</c>)
/// until a process restart cleared the cache.
///
/// <para>
/// <see cref="SandboxSessionRegistry.GetOrCreateLiveSessionAsync(WorkspaceRef, CancellationToken, SandboxCredential?)"/>
/// closes the gap: it probes the gateway for the cached session and, on a definitive 404, evicts
/// the cache and re-creates the session (re-mounting the workspace's marketplaces).
/// </para>
///
/// <para>
/// The stub gateway models reality: a freshly-created session is alive until a test explicitly
/// <see cref="CallLog.Evict"/>s it (simulating the gateway forgetting an idle session).
/// </para>
/// </summary>
public class SandboxSessionRegistryRecreateOnGateway404Tests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task Live_session_check_recreates_session_when_gateway_returns_404()
    {
        var (registry, calls) = CreateRegistry();

        var first = await registry.GetOrCreateSessionAsync();
        first.SessionId.Should().Be("sess-1");
        calls.PostCount.Should().Be(1);

        calls.Evict("sess-1"); // the gateway forgets the idle session

        var live = await registry.GetOrCreateLiveSessionAsync();

        live.SessionId.Should().Be("sess-2", "the dead session must be replaced, not reused");
        calls.PostCount.Should().Be(2, "exactly one extra create POST should happen for the recreate");
        calls.LivenessGets.Should().Contain("sess-1");
    }

    [Fact]
    public async Task Live_session_check_reuses_session_when_gateway_still_knows_it()
    {
        var (registry, calls) = CreateRegistry();

        var first = await registry.GetOrCreateSessionAsync(); // stays alive — never evicted
        var live = await registry.GetOrCreateLiveSessionAsync();

        live.SessionId.Should().Be("sess-1");
        first.SessionId.Should().Be("sess-1");
        calls.PostCount.Should().Be(1, "a live session must be reused, never recreated");
        calls.LivenessGets.Should().Contain("sess-1");
    }

    [Fact]
    public async Task Live_session_check_does_not_recreate_on_transient_gateway_error()
    {
        // A non-404 failure (gateway flapping) must NOT trigger churn — recreating wouldn't help and
        // would tear down a possibly-healthy session.
        var (registry, calls) = CreateRegistry(livenessStatusOverride: HttpStatusCode.ServiceUnavailable);

        _ = await registry.GetOrCreateSessionAsync();
        var live = await registry.GetOrCreateLiveSessionAsync();

        live.SessionId.Should().Be("sess-1", "a 503 is not a definitive 'session gone' signal");
        calls.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Recreate_preserves_the_workspace_marketplace_selection()
    {
        // The headline promise of the fix: when a session is recreated after eviction, the workspace's
        // marketplaces must be re-sent so its marketplace-provided tools (e.g. sandbox-Skill) come back.
        var (registry, calls) = CreateRegistry();
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers", "official"]);

        _ = await registry.GetOrCreateSessionAsync(workspaceRef);
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync(workspaceRef);

        live.SessionId.Should().Be("sess-2");
        calls.PostBodies.Should().HaveCount(2);
        ReadMarketplaces(calls.PostBodies[1]).Should().Equal("superpowers", "official");
    }

    [Fact]
    public async Task Recreate_updates_the_reverse_session_id_map()
    {
        // The context-discovery webhook resolves sessions via TryGetSessionById; after a recreate it
        // must point at the live session, not the dead one.
        var (registry, calls) = CreateRegistry();

        _ = await registry.GetOrCreateSessionAsync();
        calls.Evict("sess-1");
        _ = await registry.GetOrCreateLiveSessionAsync();

        registry.TryGetSessionById("sess-2", out var live).Should().BeTrue();
        live!.SessionId.Should().Be("sess-2");
        registry.TryGetSessionById("sess-1", out _).Should().BeFalse("the dead session id must be dropped");
    }

    [Fact]
    public async Task Concurrent_live_checks_converge_on_a_single_recreated_session()
    {
        // Two callers racing the recreate must converge on ONE new session (the cache is not clobbered)
        // — guards the InvalidateSession "only evict the entry we own" invariant.
        var (registry, calls) = CreateRegistry();

        _ = await registry.GetOrCreateSessionAsync();
        calls.Evict("sess-1");

        var results = await Task.WhenAll(
            registry.GetOrCreateLiveSessionAsync(),
            registry.GetOrCreateLiveSessionAsync()
        );

        results[0].SessionId.Should().Be(results[1].SessionId, "both callers must converge on the same session");
        calls.PostCount.Should().Be(2, "the recreate must be single-flighted, not duplicated per caller");
        registry.TryGetSessionById(results[0].SessionId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_SessionGone_ReloadsWorkspaceRef_BeforeRecreating()
    {
        // The WorkspaceRef a long-lived agent holds was captured when the agent was built. If the
        // workspace's marketplaces/plugins changed since, recreating from that captured ref silently
        // resurrects the OLD configuration — the user's edit appears to have been thrown away. The
        // recreate must therefore re-read current workspace config first.
        var reloadCalls = 0;
        var (registry, calls) = CreateRegistry(
            reloadWorkspaceRef: (workspaceId, _) =>
            {
                reloadCalls++;
                return Task.FromResult<WorkspaceRef?>(
                    new WorkspaceRef(
                        workspaceId,
                        DirectoryRelPath: null,
                        Marketplaces: ["official"],
                        PluginSelection: [new SandboxPluginRef("official", "code-review")]
                    )
                );
            }
        );
        var staleRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        _ = await registry.GetOrCreateSessionAsync(staleRef);
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync(staleRef);

        live.SessionId.Should().Be("sess-2");
        reloadCalls.Should().Be(1);
        calls.PostBodies.Should().HaveCount(2);
        // NOTE: `Equal` on a string collection is `params string[]` — a trailing "because" argument
        // would be read as an extra EXPECTED ELEMENT, not a reason. Rationale stays in comments.
        // First create used the captured ref; the recreate must use CURRENT workspace config.
        ReadMarketplaces(calls.PostBodies[0]).Should().Equal("superpowers");
        ReadMarketplaces(calls.PostBodies[1]).Should().Equal("official");
        using var recreate = JsonDocument.Parse(calls.PostBodies[1]);
        recreate
            .RootElement.GetProperty("pluginSelection")[0]
            .GetProperty("plugin")
            .GetString()
            .Should()
            .Be("code-review");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_NoReloadCallbackConfigured_FallsBackToOriginalRef()
    {
        // Every existing construction site passes no callback. Those must keep the prior behaviour
        // exactly — recreate from the ref the caller handed in — rather than losing the selection.
        var (registry, calls) = CreateRegistry();
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        _ = await registry.GetOrCreateSessionAsync(workspaceRef);
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync(workspaceRef);

        live.SessionId.Should().Be("sess-2");
        ReadMarketplaces(calls.PostBodies[1]).Should().Equal("superpowers");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_SessionStillAlive_DoesNotReloadWorkspaceRef()
    {
        // NON-VACUITY GUARD for the reload fix. The reload belongs to the RECREATE path only. If it
        // leaked into the first resolve, this registry would hit the workspace store on every single
        // turn — and the two tests above would still pass, because both of them recreate. Only this
        // test can tell the scoped fix from the unscoped one.
        var reloadCalls = 0;
        var (registry, calls) = CreateRegistry(
            reloadWorkspaceRef: (workspaceId, _) =>
            {
                reloadCalls++;
                return Task.FromResult<WorkspaceRef?>(new WorkspaceRef(workspaceId));
            }
        );
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        _ = await registry.GetOrCreateSessionAsync(workspaceRef);
        var live = await registry.GetOrCreateLiveSessionAsync(workspaceRef); // never evicted ⇒ still alive

        live.SessionId.Should().Be("sess-1");
        calls.PostCount.Should().Be(1);
        reloadCalls.Should().Be(0, "a live session is never recreated, so nothing needs reloading");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_ReloadReturnsNull_FallsBackToOriginalRef()
    {
        // A workspace deleted between capture and recreate returns null. Falling back to the captured
        // ref keeps the session recoverable; treating null as "no marketplaces" would strip the
        // agent's tools on a race that is not the user's doing.
        var (registry, calls) = CreateRegistry(reloadWorkspaceRef: (_, _) => Task.FromResult<WorkspaceRef?>(null));
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        _ = await registry.GetOrCreateSessionAsync(workspaceRef);
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync(workspaceRef);

        live.SessionId.Should().Be("sess-2");
        ReadMarketplaces(calls.PostBodies[1]).Should().Equal("superpowers");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_ReloadThrows_StillRecreatesFromTheCapturedRef()
    {
        // The reload reads the workspace store, which reads a FILE — corrupt JSON or a concurrent
        // atomic-replace makes it throw for real. By the time it runs, the dead session has ALREADY
        // been invalidated, so letting the failure escape would leave the caller with no session at
        // all: strictly worse than the stale-config outcome the reload exists to avoid. Degrade to
        // the captured ref instead.
        var (registry, calls) = CreateRegistry(
            reloadWorkspaceRef: (_, _) => throw new InvalidOperationException("workspace store is corrupt")
        );
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        _ = await registry.GetOrCreateSessionAsync(workspaceRef);
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync(workspaceRef);

        live.SessionId.Should().Be("sess-2");
        ReadMarketplaces(calls.PostBodies[1]).Should().Equal("superpowers");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_ReloadCancelled_PropagatesInsteadOfRecreating()
    {
        // NON-VACUITY GUARD for the test above. A blanket `catch (Exception)` would also swallow a
        // cancellation and press on with a gateway create that was already abandoned. The exception
        // filter excluding OperationCanceledException is what separates "the store failed, degrade"
        // from "this work was cancelled, stop" — and only this test can tell the two apart.
        var (registry, calls) = CreateRegistry(reloadWorkspaceRef: (_, _) => throw new OperationCanceledException());
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        _ = await registry.GetOrCreateSessionAsync(workspaceRef);
        calls.Evict("sess-1");
        var act = async () => await registry.GetOrCreateLiveSessionAsync(workspaceRef);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.PostCount.Should().Be(1, "an abandoned recreate must not create a second gateway session");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_ReloadCancelled_LeavesTheExistingSessionInPlace()
    {
        // Companion to the test above, which stays green whichever side of the invalidate the reload
        // sits on and therefore cannot see this. Because the reload now runs FIRST, an abandoned
        // recreate tears nothing down: the caller keeps the session it had, instead of being left
        // mid-teardown with its session already evicted and no replacement built. That is an
        // improvement the reorder happens to buy, and an unasserted improvement is one tidy-up away
        // from being silently undone.
        var (registry, calls) = CreateRegistry(reloadWorkspaceRef: (_, _) => throw new OperationCanceledException());
        var workspaceRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        var original = await registry.GetOrCreateSessionAsync(workspaceRef);
        calls.Evict("sess-1");
        var act = async () => await registry.GetOrCreateLiveSessionAsync(workspaceRef);

        await act.Should().ThrowAsync<OperationCanceledException>();
        registry
            .TryGetSessionById(original.SessionId, out _)
            .Should()
            .BeTrue("a cancelled reload must not leave the per-session state of a session it never replaced evicted");
        (await registry.GetOrCreateSessionAsync(workspaceRef))
            .SessionId.Should()
            .Be(original.SessionId, "the cache entry survives, so a later resolve reuses it rather than creating");
        calls.PostCount.Should().Be(1, "nothing was recreated, so nothing new was created either");
    }

    [Fact]
    public async Task GetOrCreateLiveSessionAsync_ConcurrentCreateDuringReload_DoesNotDiscardTheReloadedRef()
    {
        // The reload is I/O, so it is a WINDOW. Run while the (workspaceId, appId) slot is empty, a
        // concurrent caller still holding the OLD ref publishes its own session into that slot, and
        // the recreate then resolves THAT session instead of building one from what was just
        // reloaded — the user's edit silently undone by the very path that exists to apply it.
        // Reading the store before vacating the slot is what prevents it: while the slot is still
        // occupied the concurrent caller gets a cache hit and publishes nothing.
        SandboxSession? observedInsideReload = null;
        SandboxSessionRegistry? registry = null;
        var staleRef = new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["superpowers"]);

        // A second caller arrives mid-reload carrying the ref it captured long ago.
        Func<string, CancellationToken, Task<WorkspaceRef?>> reload = async (workspaceId, _) =>
        {
            observedInsideReload = await registry!.GetOrCreateSessionAsync(staleRef);
            return new WorkspaceRef(workspaceId, DirectoryRelPath: null, Marketplaces: ["official"]);
        };

        var (built, calls) = CreateRegistry(reloadWorkspaceRef: reload);
        registry = built;

        _ = await registry.GetOrCreateSessionAsync(staleRef);
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync(staleRef);

        // Asserted out HERE, never inside the lambda: the reload's catch swallows every
        // non-cancellation exception, so an assertion that failed in there would be swallowed whole
        // and this test would pass having proved nothing.
        observedInsideReload.Should().NotBeNull("the concurrent caller must actually have run");
        observedInsideReload!
            .SessionId.Should()
            .Be(
                "sess-1",
                "the slot must still hold the session being replaced while the store is read, so a "
                    + "concurrent caller gets a cache hit instead of publishing a stale-ref session into "
                    + "an empty slot"
            );

        // The count below is the SAME under the broken ordering — there the second POST is the
        // concurrent caller's stale-ref create and the recreate becomes the cache hit — so it is a
        // supporting assertion that no THIRD session appeared, not a witness for the fix. Verified by
        // hoisting it above the session-id assertion under the reverted ordering: it stayed green.
        // The session-id assertion above is the one that discriminates, which is why it comes first.
        calls
            .PostCount.Should()
            .Be(2, "the arrange-phase create and the recreate — the concurrent caller added none");
        live.SessionId.Should().Be("sess-2");
        ReadMarketplaces(calls.PostBodies[^1]).Should().Equal("official");
    }

    private static IReadOnlyList<string> ReadMarketplaces(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return [.. doc.RootElement.GetProperty("marketplaces").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static (SandboxSessionRegistry Registry, CallLog Calls) CreateRegistry(
        HttpStatusCode? livenessStatusOverride = null,
        Func<string, CancellationToken, Task<WorkspaceRef?>>? reloadWorkspaceRef = null
    )
    {
        var calls = new CallLog();

        var registryHandler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            // Create: POST /api/v1/sandboxes — a freshly-created session is alive.
            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var requestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var ordinal = calls.RecordPost(requestBody);
                var sessionId = $"sess-{ordinal}";
                calls.MarkCreated(sessionId);
                var responseBody = $$"""
                    { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
            }

            // Liveness: GET /api/v1/sandboxes/{id}
            if (req.Method == HttpMethod.Get && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                calls.RecordLiveness(id);
                if (livenessStatusOverride is { } forced)
                {
                    return new HttpResponseMessage(forced);
                }

                return calls.IsAlive(id)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            $$"""{ "code": 404, "error": "Session not found: {{id}}" }""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };

        // The gateway lifetime client only serves the /health probe; 200 ⇒ adopt an "existing"
        // gateway and proceed straight to create/liveness calls on the registry's own client.
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            auth,
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            ),
            reloadWorkspaceRef: reloadWorkspaceRef
        );

        return (registry, calls);
    }

    /// <summary>
    /// Regression: when the gateway is reachable for the /health adopt probe but then REFUSES the
    /// create POST (down / restarting — <c>SocketException 10061</c> surfaced as
    /// <see cref="HttpRequestException"/>), the registry must surface a handled
    /// <see cref="SandboxSessionUnavailableException"/> so the WebSocket / mode-switch layers answer a
    /// clean error — not let the raw exception crash the request with an unhandled 500.
    /// </summary>
    [Fact]
    public async Task GetOrCreateSessionAsync_GatewayConnectionRefusedOnCreate_ThrowsSandboxUnavailable()
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };
        // Health probe answers 200 (adopt), but the create POST connection is refused.
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );
        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(
                new StubHandler(_ =>
                    throw new HttpRequestException(
                        "No connection could be made because the target machine actively refused it."
                    )
                )
            ),
            auth,
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        var act = async () => await registry.GetOrCreateSessionAsync();

        await act.Should().ThrowAsync<SandboxSessionUnavailableException>();
    }

    // Thread-safe: the concurrency test fires parallel liveness GETs (and a recreate POST) through
    // the stub handler from multiple threads at once.
    private sealed class CallLog
    {
        private readonly object _gate = new();
        private readonly List<string> _postBodies = [];
        private readonly List<string> _livenessGets = [];
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evicted = new(StringComparer.Ordinal);

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _postBodies.Count;
                }
            }
        }

        public IReadOnlyList<string> PostBodies
        {
            get
            {
                lock (_gate)
                {
                    return [.. _postBodies];
                }
            }
        }

        public IReadOnlyList<string> LivenessGets
        {
            get
            {
                lock (_gate)
                {
                    return [.. _livenessGets];
                }
            }
        }

        /// <summary>Records a create POST body and returns its 1-based ordinal.</summary>
        public int RecordPost(string body)
        {
            lock (_gate)
            {
                _postBodies.Add(body);
                return _postBodies.Count;
            }
        }

        public void RecordLiveness(string sessionId)
        {
            lock (_gate)
            {
                _livenessGets.Add(sessionId);
            }
        }

        public void MarkCreated(string sessionId)
        {
            lock (_gate)
            {
                _ = _created.Add(sessionId);
            }
        }

        /// <summary>Simulates the gateway forgetting an idle session.</summary>
        public void Evict(string sessionId)
        {
            lock (_gate)
            {
                _ = _evicted.Add(sessionId);
            }
        }

        public bool IsAlive(string sessionId)
        {
            lock (_gate)
            {
                return _created.Contains(sessionId) && !_evicted.Contains(sessionId);
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
