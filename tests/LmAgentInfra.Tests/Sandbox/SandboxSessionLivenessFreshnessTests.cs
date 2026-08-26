using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Pins the freshness window that keeps the gateway liveness probe off the per-turn hot path (#93).
/// </summary>
/// <remarks>
/// The probe exists to notice idle EVICTION, which takes the gateway minutes. Running it on every
/// workspace-agent session acquisition — once per turn, blocking a thread-pool thread under the
/// sync-over-async call site — asks a question whose answer was already known seconds ago. These tests
/// fix both edges of the window: inside it no round-trip happens at all, past it the probe runs again,
/// and nothing about what a probe DOES when it fails changes.
/// </remarks>
public class SandboxSessionLivenessFreshnessTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    /// <summary>Comfortably longer than the registry's window, so "expired" is not a boundary guess.</summary>
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromSeconds(45);

    /// <summary>Comfortably shorter than it, for the same reason.</summary>
    private static readonly TimeSpan InsideTheWindow = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task GetOrCreateLiveSession_WithinTheFreshnessWindow_DoesNotProbeTheGatewayAgain()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var registry = CreateRegistry(out var gateway, clock);

        // The first acquisition still probes: a create says the gateway minted the session, not that a
        // GET for it under this credential resolves. The window is opened by the probe, nothing else.
        var first = await registry.GetOrCreateLiveSessionAsync();
        first.SessionId.Should().Be("sess-1");
        gateway.LivenessGets.Should().Equal(["sess-1"]);

        // Two more turns, both inside the window that probe opened.
        clock.Advance(InsideTheWindow);
        var second = await registry.GetOrCreateLiveSessionAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var third = await registry.GetOrCreateLiveSessionAsync();

        second.SessionId.Should().Be("sess-1", "the live session is reused, not recreated");
        third.SessionId.Should().Be("sess-1");
        gateway.LivenessGets.Should().Equal(
            ["sess-1"],
            "back-to-back turns inside the freshness window must cost no further gateway round-trip");
        gateway.Creates.Should().Be(1, "skipping the probe must not be confused with skipping the cache");
    }

    [Fact]
    public async Task GetOrCreateLiveSession_OnceTheWindowExpires_ProbesAgainAndReopensIt()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var registry = CreateRegistry(out var gateway, clock);

        _ = await registry.GetOrCreateLiveSessionAsync();
        gateway.LivenessGets.Should().Equal(["sess-1"], "the first acquisition opens the window");

        // Past the window: the session is now old enough that eviction is a real possibility again.
        clock.Advance(PastTheWindow);
        var afterExpiry = await registry.GetOrCreateLiveSessionAsync();

        afterExpiry.SessionId.Should().Be("sess-1");
        gateway.LivenessGets.Should().Equal(
            ["sess-1", "sess-1"],
            "an expired window must put the probe back on the acquisition");

        // That probe's success is itself a confirmation, so it reopens the window rather than leaving
        // every subsequent turn probing forever.
        clock.Advance(InsideTheWindow);
        _ = await registry.GetOrCreateLiveSessionAsync();
        gateway.LivenessGets.Should().Equal(
            ["sess-1", "sess-1"],
            "a successful probe re-verifies the session, so the next turn inside the window skips again");

        clock.Advance(PastTheWindow);
        _ = await registry.GetOrCreateLiveSessionAsync();
        gateway.LivenessGets.Should().Equal(
            ["sess-1", "sess-1", "sess-1"],
            "and the reopened window expires on the same terms as the first");
    }

    [Fact]
    public async Task GetOrCreateLiveSession_WhenTheProbeFinds404_StillRecreatesImmediately()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var registry = CreateRegistry(out var gateway, clock);

        _ = await registry.GetOrCreateLiveSessionAsync();

        // The gateway forgets the session while it sits idle — exactly what the probe is for.
        gateway.Evict("sess-1");
        clock.Advance(PastTheWindow);

        var recreated = await registry.GetOrCreateLiveSessionAsync();

        recreated.SessionId.Should().Be(
            "sess-2",
            "the freshness window may only suppress a probe whose answer is already known; a probe that "
                + "does run and finds the session gone must invalidate it as immediately as before");
        gateway.Creates.Should().Be(2);
        gateway.LivenessGets.Should().Equal(["sess-1", "sess-1"]);

        // The replacement inherits nothing: it is probed on its own first acquisition, and that probe
        // is what opens a window for it.
        var next = await registry.GetOrCreateLiveSessionAsync();
        next.SessionId.Should().Be("sess-2");
        gateway.LivenessGets.Should().Equal(
            ["sess-1", "sess-1", "sess-2"],
            "the recreated session earns its own window rather than inheriting the evicted one's");

        clock.Advance(InsideTheWindow);
        _ = await registry.GetOrCreateLiveSessionAsync();
        gateway.LivenessGets.Should().Equal(
            ["sess-1", "sess-1", "sess-2"],
            "and once opened, that window behaves like any other");
    }

    /// <summary>
    /// Builds a registry over <see cref="FakeGateway"/> with the test's clock behind the freshness
    /// window.
    /// </summary>
    private static SandboxSessionRegistry CreateRegistry(out FakeGateway gateway, TimeProvider clock)
    {
        var fake = new FakeGateway();
        gateway = fake;

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new FakeGateway())
        );

        return new SandboxSessionRegistry(
            lifetime,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(fake),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            ),
            timeProvider: clock
        );
    }

    /// <summary>
    /// Minimal in-memory sandbox gateway: hands out a fresh session id per create and answers a liveness
    /// GET for any session it still holds. Records every liveness GET it is asked, in order — that list
    /// IS the assertion surface here, since the whole change is about which round-trips do not happen.
    /// </summary>
    private sealed class FakeGateway : HttpMessageHandler
    {
        private readonly HashSet<string> _evicted = [];
        private int _creates;

        /// <summary>Session ids the registry probed, in order.</summary>
        public List<string> LivenessGets { get; } = [];

        /// <summary>How many sessions the gateway was asked to create.</summary>
        public int Creates => Volatile.Read(ref _creates);

        /// <summary>Makes the gateway forget <paramref name="sessionId"/>, as an idle eviction does.</summary>
        public void Evict(string sessionId)
        {
            lock (_evicted)
            {
                _ = _evicted.Add(sessionId);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Post)
            {
                var created = Interlocked.Increment(ref _creates);
                return Task.FromResult(SessionResponse($"sess-{created}"));
            }

            // Anything else is a GET: either the gateway health probe (no session segment) or the
            // per-acquisition liveness probe this suite is about.
            var sessionId = path.StartsWith("/api/v1/sandboxes/", StringComparison.Ordinal)
                ? path["/api/v1/sandboxes/".Length..]
                : null;
            if (string.IsNullOrEmpty(sessionId))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            bool evicted;
            lock (_evicted)
            {
                evicted = _evicted.Contains(sessionId);
            }

            lock (LivenessGets)
            {
                LivenessGets.Add(sessionId);
            }

            return Task.FromResult(
                evicted
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{ \"error\": \"not found\" }", Encoding.UTF8, "application/json"),
                    }
                    : SessionResponse(sessionId)
            );
        }

        /// <summary>The gateway's session document, as both a create response and a liveness answer.</summary>
        private static HttpResponseMessage SessionResponse(string sessionId)
        {
            var body = $$"""
                { "session_id": "{{sessionId}}", "container_id": "c-{{sessionId}}",
                  "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
