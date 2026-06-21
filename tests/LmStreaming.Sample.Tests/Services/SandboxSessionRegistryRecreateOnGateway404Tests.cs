using System.Net;
using System.Text;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Auth;

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
/// <see cref="SandboxSessionRegistry.GetOrCreateLiveSessionAsync(WorkspaceRef, CancellationToken)"/>
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
            registry.GetOrCreateLiveSessionAsync());

        results[0].SessionId.Should().Be(results[1].SessionId, "both callers must converge on the same session");
        calls.PostCount.Should().Be(2, "the recreate must be single-flighted, not duplicated per caller");
        registry.TryGetSessionById(results[0].SessionId, out _).Should().BeTrue();
    }

    private static IReadOnlyList<string> ReadMarketplaces(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return [.. doc.RootElement.GetProperty("marketplaces").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static (SandboxSessionRegistry Registry, CallLog Calls) CreateRegistry(
        HttpStatusCode? livenessStatusOverride = null)
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
                            "application/json"),
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
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            auth,
            new AuthSharedSecret(auth));

        return (registry, calls);
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
            get { lock (_gate) { return _postBodies.Count; } }
        }

        public IReadOnlyList<string> PostBodies
        {
            get { lock (_gate) { return [.. _postBodies]; } }
        }

        public IReadOnlyList<string> LivenessGets
        {
            get { lock (_gate) { return [.. _livenessGets]; } }
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
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
