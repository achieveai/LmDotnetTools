using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The daemon must serve <c>POST /api/discovery/context_discovery</c> and return 2xx for a
/// gateway-authenticated call. Whenever the auth webhook is configured, the sandbox gateway is told to
/// deliver context discoveries to that route (<c>SandboxSessionRegistry.BuildDiscovery</c>); a non-2xx
/// (today a 404, because the daemon exposes only the auth webhook) makes the gateway <b>tear down the
/// sandbox session</b> mid-review, killing the review agent's MCP connection (observed live: mcqdb PR
/// #11197 failed 194×). Contract: authenticated ⇒ <b>200 accept-and-ignore</b> (the daemon already
/// consumes discovered context via its host-side pull path, not this push webhook), even for an
/// unactionable/malformed body; a bad or missing shared secret ⇒ 401.
/// </summary>
public sealed class ContextDiscoveryRouteTests
{
    private const string Secret = "test-discovery-secret";
    private const string Route = "/api/discovery/context_discovery";

    private static object SampleEnvelope() => new
    {
        @event = "context_discovery",
        session_id = "sess-1",
        discoveries = new[]
        {
            new { kind = "context_file", path = "CLAUDE.md", content = "repo rules" },
        },
    };

    private static (DaemonWebAppFactory factory, HttpClient client) NewClient()
    {
        var factory = new DaemonWebAppFactory();
        var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Auth:Webhook:GatewaySharedSecret", Secret));
        return (factory, configured.CreateClient());
    }

    [Fact]
    public async Task Authenticated_discovery_post_returns_2xx()
    {
        var (factory, client) = NewClient();
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
            "the gateway tears down the sandbox session on any non-2xx to the discovery webhook");
    }

    [Fact]
    public async Task Wrong_secret_returns_401()
    {
        var (factory, client) = NewClient();
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
        var (factory, client) = NewClient();
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
    public async Task Authenticated_but_malformed_body_still_returns_2xx()
    {
        var (factory, client) = NewClient();
        using var _ = factory;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent("{ not valid json ", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Secret);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an authenticated-but-unactionable payload must not 400 — that would tear down the session too");
    }
}
