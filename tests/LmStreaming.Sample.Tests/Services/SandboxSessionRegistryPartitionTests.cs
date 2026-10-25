using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Issue #153 M2: the session cache is partitioned by (workspace id, caller app id) — not just
/// workspace id — so two different callers requesting the SAME logical workspace each get their
/// OWN gateway session instead of colliding on one shared session (and one shared credential).
///
/// <para>
/// These tests pin: (1) two credentials for one workspace id produce two distinct create calls,
/// each carrying its own <c>X-Sbx-App-Id</c> header; (2) the same credential still single-flights
/// through the cache exactly like the pre-M2 (workspaceId-only) behavior; (3) a background recreate
/// after a gateway 404 reuses the credential the caller re-supplies — nothing is persisted
/// server-side, so the caller must resend it every turn (design's cross-actor resume matrix); and
/// (4) destroying "a workspace" (the only thing a daemon-style caller knows) tears down every app
/// id's session for it, not just the first one found, and leaves other workspaces untouched.
/// </para>
/// </summary>
public class SandboxSessionRegistryPartitionTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string DefaultAppId = "default-app";

    [Fact]
    public async Task Different_credentials_for_the_same_workspace_create_distinct_sessions()
    {
        var (registry, calls) = CreateRegistry();
        var credentialA = new SandboxCredential("caller-a", "keyA");
        var credentialB = new SandboxCredential("caller-b", "keyB");

        var sessionA = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credentialA
        );
        var sessionB = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credentialB
        );

        sessionA
            .SessionId.Should()
            .NotBe(sessionB.SessionId, "different callers must never collide on one shared session");
        calls.PostCount.Should().Be(2, "each distinct credential must trigger its own create call");
        calls.PostAppIds.Should().Equal("caller-a", "caller-b");
    }

    [Fact]
    public async Task Same_credential_for_the_same_workspace_reuses_the_cached_session()
    {
        // Sanity check for the partition key itself: the SAME credential must still hit the
        // single-flight cache, exactly like the pre-M2 (workspaceId-only) behavior.
        var (registry, calls) = CreateRegistry();
        var credential = new SandboxCredential("caller-a", "keyA");

        var first = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );
        var second = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );

        second.SessionId.Should().Be(first.SessionId);
        calls.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Background_recreate_after_404_reuses_the_credential_resent_by_the_caller()
    {
        var (registry, calls) = CreateRegistry();
        var credential = new SandboxCredential("caller-x", "keyX");

        var first = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );
        calls.PostAppIds.Should().ContainSingle().Which.Should().Be("caller-x");

        calls.Evict(first.SessionId); // the gateway forgets the idle session

        // The caller re-sends the SAME credential on the next turn (design: "re-sent every turn,
        // nothing stored server-side") — the recreate must carry it forward, not silently fall back
        // to the process-wide default.
        var live = await registry.GetOrCreateLiveSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );

        live.SessionId.Should().NotBe(first.SessionId, "the dead session must be replaced, not reused");
        calls.PostCount.Should().Be(2, "exactly one extra create POST should happen for the recreate");
        calls.PostAppIds.Should().Equal("caller-x", "caller-x");
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_removes_every_app_ids_session_for_the_workspace()
    {
        var (registry, calls) = CreateRegistry();
        const string workspaceId = "ws-multi";
        var credentialA = new SandboxCredential("caller-a", "keyA");
        var credentialB = new SandboxCredential("caller-b", "keyB");

        var sessionA = await registry.GetOrCreateSessionAsync(workspaceId, default, credentialA);
        var sessionB = await registry.GetOrCreateSessionAsync(workspaceId, default, credentialB);
        sessionA.SessionId.Should().NotBe(sessionB.SessionId);

        await registry.DestroyWorkspaceSessionAsync(workspaceId);

        calls
            .DeletedSessionIds.Should()
            .BeEquivalentTo(
                [sessionA.SessionId, sessionB.SessionId],
                "every app id's session for the workspace must be torn down, not just the first one found"
            );
        registry.TryGetSessionById(sessionA.SessionId, out _).Should().BeFalse();
        registry.TryGetSessionById(sessionB.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_leaves_other_workspaces_untouched()
    {
        var (registry, calls) = CreateRegistry();
        var credential = new SandboxCredential("caller-a", "keyA");

        var kept = await registry.GetOrCreateSessionAsync("ws-keep", default, credential);
        var removed = await registry.GetOrCreateSessionAsync("ws-remove", default, credential);

        await registry.DestroyWorkspaceSessionAsync("ws-remove");

        calls.DeletedSessionIds.Should().Equal(removed.SessionId);
        registry.TryGetSessionById(kept.SessionId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Read_only_and_normal_refs_for_one_workspace_and_app_get_two_sessions_with_their_own_rules()
    {
        // The judge and the review lanes share the workspace AND the app id, so only the read-only part of
        // the key keeps the judge out of the lane's full-write sandbox (and the lane out of the judge's).
        var auth = new AuthOptions();
        auth.Ado.ClientId = "ado-client";
        var (registry, calls) = CreateRegistry(auth);

        var normal = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var readOnly = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", ReadOnlyEgress: true));

        readOnly.SessionId.Should().NotBe(normal.SessionId);
        normal.ReadOnlyEgress.Should().BeFalse();
        readOnly.ReadOnlyEgress.Should().BeTrue();
        calls.PostAppIds.Should().Equal(DefaultAppId, DefaultAppId);
        AdoMethods(calls.PostBodies[0]).Should().BeEmpty("the lane's ado rule must keep allowing every method");
        AdoMethods(calls.PostBodies[1]).Should().Equal("GET", "HEAD");
    }

    [Fact]
    public async Task The_same_read_only_ref_twice_reuses_one_session()
    {
        var (registry, calls) = CreateRegistry();
        var readOnlyRef = new WorkspaceRef("ws-1", ReadOnlyEgress: true);

        var first = await registry.GetOrCreateSessionAsync(readOnlyRef);
        var second = await registry.GetOrCreateSessionAsync(readOnlyRef);

        second.SessionId.Should().Be(first.SessionId);
        calls.PostCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destroy_and_release_of_a_workspace_stop_its_read_only_session_too(bool release)
    {
        var (registry, calls) = CreateRegistry();
        var normal = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var readOnly = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", ReadOnlyEgress: true));

        if (release)
        {
            await registry.ReleaseWorkspaceSessionsAsync("ws-1", DefaultAppId);
        }
        else
        {
            await registry.DestroyWorkspaceSessionAsync("ws-1");
        }

        calls.DeletedSessionIds.Should().BeEquivalentTo([normal.SessionId, readOnly.SessionId]);
        registry.TryGetSessionById(readOnly.SessionId, out _).Should().BeFalse();
    }

    private static IReadOnlyList<string> AdoMethods(string createBody)
    {
        using var document = JsonDocument.Parse(createBody);
        return
        [
            .. document
                .RootElement.GetProperty("network")
                .GetProperty("rules")
                .EnumerateArray()
                .Single(rule => rule.GetProperty("id").GetString() == "ado")
                .GetProperty("methods")
                .EnumerateArray()
                .Select(method => method.GetString()!),
        ];
    }

    private static (SandboxSessionRegistry Registry, CallLog Calls) CreateRegistry(AuthOptions? auth = null)
    {
        var calls = new CallLog();

        var registryHandler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            // Create: POST /api/v1/sandboxes — records the app id header that came with this
            // particular create call so tests can assert per-credential partitioning.
            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var appId = req.Headers.GetValues("X-Sbx-App-Id").Single();
                var ordinal = calls.RecordPost(appId, req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
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

            // Destroy: DELETE /api/v1/sandboxes/{id}
            if (req.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                calls.RecordDelete(id);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new SandboxGatewayOptions
        {
            BaseUrl = GatewayBaseUrl,
            AppId = DefaultAppId,
            Marketplaces = null,
        };

        // The gateway lifetime client only serves the /health adopt probe; 200 ⇒ adopt an
        // "existing" gateway and proceed straight to create/liveness/destroy calls on the
        // registry's own client (the one `calls` observes).
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            auth ?? new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        return (registry, calls);
    }

    // Thread-safe: mirrors SandboxSessionRegistryRecreateOnGateway404Tests' CallLog.
    private sealed class CallLog
    {
        private readonly object _gate = new();
        private readonly List<string> _postAppIds = [];
        private readonly List<string> _postBodies = [];
        private readonly List<string> _deletedSessionIds = [];
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evicted = new(StringComparer.Ordinal);

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _postAppIds.Count;
                }
            }
        }

        public IReadOnlyList<string> PostAppIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _postAppIds];
                }
            }
        }

        public IReadOnlyList<string> DeletedSessionIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _deletedSessionIds];
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

        /// <summary>Records a create POST's app id header and body, and returns its 1-based ordinal.</summary>
        public int RecordPost(string appId, string body)
        {
            lock (_gate)
            {
                _postAppIds.Add(appId);
                _postBodies.Add(body);
                return _postAppIds.Count;
            }
        }

        public void RecordDelete(string sessionId)
        {
            lock (_gate)
            {
                _deletedSessionIds.Add(sessionId);
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
