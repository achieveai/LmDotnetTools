using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Regression coverage for the invalidation-leak: when the gateway evicts a session and the registry
/// recreates it via <see cref="SandboxSessionRegistry.GetOrCreateLiveSessionAsync(WorkspaceRef, CancellationToken, SandboxCredential?)"/>,
/// invalidation must clear EVERY per-session-id-indexed collection — sub-agent bindings, thread routing,
/// and the discovery dedup ledger — not just the main session/credential/secret maps. Before the fix,
/// those three retained session-id-indexed state until registry disposal.
/// </summary>
public class SandboxSessionRegistryInvalidateStateTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task Invalidation_on_recreate_clears_all_per_session_state()
    {
        var (registry, calls) = CreateRegistry();
        await using var ownedRegistry = registry;

        var first = await registry.GetOrCreateSessionAsync();
        first.SessionId.Should().Be("sess-1");

        // Populate every per-session collection for sess-1.
        registry.RegisterThread("sess-1", "thread-1");
        _ = registry.GetOrAddSubAgentBinding(
            "sess-1",
            "conv-1",
            new Dictionary<string, SubAgentTemplate>(),
            () => Mock.Of<IStreamingAgent>());
        registry.TryMarkDiscoverySeen("sess-1", "skill", "SKILL.md").Should().BeTrue();

        // The gateway forgets the idle session, forcing a live-check recreate (invalidate + create).
        calls.Evict("sess-1");
        var live = await registry.GetOrCreateLiveSessionAsync();
        live.SessionId.Should().Be("sess-2");

        // Every per-session collection keyed by the dead sess-1 must now be empty.
        registry.GetThreads("sess-1").Should().BeEmpty("thread routing must be cleared on invalidation");
        registry.TryGetSubAgentBinding("sess-1", "conv-1", out _).Should()
            .BeFalse("sub-agent bindings must be cleared on invalidation");
        registry.TryMarkDiscoverySeen("sess-1", "skill", "SKILL.md").Should()
            .BeTrue("the discovery dedup ledger must be cleared on invalidation (mark is 'first' again)");
        registry.TryGetSessionById("sess-1", out _).Should().BeFalse();
    }

    private static (SandboxSessionRegistry Registry, CallLog Calls) CreateRegistry()
    {
        var calls = new CallLog();
        var registryHandler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var ordinal = calls.RecordPost();
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
                            "application/json"),
                    };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));

        return (registry, calls);
    }

    private sealed class CallLog
    {
        private readonly object _gate = new();
        private int _postCount;
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evicted = new(StringComparer.Ordinal);

        public int RecordPost()
        {
            lock (_gate) { return ++_postCount; }
        }

        public void MarkCreated(string sessionId)
        {
            lock (_gate) { _ = _created.Add(sessionId); }
        }

        public void Evict(string sessionId)
        {
            lock (_gate) { _ = _evicted.Add(sessionId); }
        }

        public bool IsAlive(string sessionId)
        {
            lock (_gate) { return _created.Contains(sessionId) && !_evicted.Contains(sessionId); }
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
