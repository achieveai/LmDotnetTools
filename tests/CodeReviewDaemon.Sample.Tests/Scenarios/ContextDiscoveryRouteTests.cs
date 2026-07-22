using System.Net;
using System.Net.Http.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The daemon must serve <c>POST /api/discovery/context_discovery</c> and return 2xx for a
/// gateway-authenticated call. Whenever the auth webhook is configured, the sandbox gateway is told to
/// deliver context discoveries to that route (<c>SandboxSessionRegistry.BuildDiscovery</c>); a
/// <b>missing route</b> (404, because the daemon otherwise exposes only the auth webhook) makes the
/// gateway <b>tear down the sandbox session</b> mid-review, killing the review agent's MCP connection
/// (observed live: mcqdb PR #11197 failed 194×). Contract: authenticated ⇒ <b>200 accept-and-ignore</b>
/// (the daemon already consumes discovered context via its host-side pull path, not this push webhook);
/// a bad/missing per-session secret or session id ⇒ 401. Authentication is per-session
/// (<see cref="SessionSecretStore"/>, keyed by the <c>session_id</c> in the body), mirroring
/// <c>AuthWebhookController</c> and LmStreaming's <c>ContextDiscoveryController</c>.
/// </summary>
public sealed class ContextDiscoveryRouteTests
{
    private const string Route = "/api/discovery/context_discovery";
    private const string SessionId = "sess-1";
    private const string Secret = "test-discovery-secret";

    private static object SampleEnvelope(string? sessionId = SessionId) => new
    {
        @event = "context_discovery",
        session_id = sessionId,
        discoveries = new[]
        {
            new { kind = "context_file", path = "CLAUDE.md", content = "repo rules" },
        },
    };

    /// <summary>
    /// Boots the daemon and seeds a per-session secret so a gateway-authenticated call can be validated,
    /// mirroring how <c>SandboxSessionRegistry</c> provisions one secret per live sandbox session.
    /// Accessing the factory's <c>Services</c> builds the host, so the
    /// <see cref="SessionSecretStore"/> singleton (pointing at the factory's isolated token-store dir) is
    /// the same instance the incoming request validates against.
    /// </summary>
    private static async Task<(DaemonWebAppFactory factory, HttpClient client)> NewClientWithSessionAsync(
        string sessionId,
        string secret)
    {
        var factory = new DaemonWebAppFactory();
        await factory.Services.GetRequiredService<SessionSecretStore>().SaveAsync(sessionId, secret);
        return (factory, factory.CreateClient());
    }

    [Fact]
    public async Task Authenticated_discovery_post_returns_2xx()
    {
        var (factory, client) = await NewClientWithSessionAsync(SessionId, Secret);
        using var _ = factory;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(SampleEnvelope()),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Secret);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the gateway tears down the sandbox session when the discovery route is missing (404)");
    }

    [Fact]
    public async Task Wrong_secret_returns_401()
    {
        var (factory, client) = await NewClientWithSessionAsync(SessionId, Secret);
        using var _ = factory;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(SampleEnvelope()),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "not-the-secret");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_secret_returns_401()
    {
        var (factory, client) = await NewClientWithSessionAsync(SessionId, Secret);
        using var _ = factory;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(SampleEnvelope()),
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_session_returns_401()
    {
        // A session with no stored secret can never be validated — even when the presented secret is a
        // real one from another session. This is the per-session isolation guarantee: SessionSecretStore
        // keys strictly on session_id, so one session's secret can't authenticate another's call.
        var (factory, client) = await NewClientWithSessionAsync(SessionId, Secret);
        using var _ = factory;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(SampleEnvelope("sess-never-registered")),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Secret);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_session_id_in_body_returns_401()
    {
        // No session_id ⇒ nothing to key the per-session secret on ⇒ the call cannot be authenticated.
        var (factory, client) = await NewClientWithSessionAsync(SessionId, Secret);
        using var _ = factory;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(SampleEnvelope(sessionId: null)),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Secret);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
