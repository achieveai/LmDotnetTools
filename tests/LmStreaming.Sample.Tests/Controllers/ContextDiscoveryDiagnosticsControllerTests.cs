using System.Net;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests for <c>GET /api/diagnostics/context-discovery</c>: it must surface the discovery webhook
/// URL + enabled flag (so an operator can spot a loopback/unreachable callback host) and the
/// per-session received counts the app has actually recorded.
/// </summary>
public sealed class ContextDiscoveryDiagnosticsControllerTests
{
    [Fact]
    public async Task Get_ReportsEnabledAndWebhookUrl_WithNoSessions()
    {
        await using var registry = CreateRegistry(publicBaseUrl: "http://gateway.test:5000");
        var controller = new ContextDiscoveryDiagnosticsController(registry, new ContextDiscoveryDiagnostics());

        var response = GetResponse(controller);

        response.DiscoveryEnabled.Should().BeTrue();
        response.WebhookUrl.Should().Be("http://gateway.test:5000/api/discovery/context_discovery");
        response.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_LoopbackCallbackHost_IsVisibleInWebhookUrl()
    {
        // The whole point of the endpoint: the default callback base is a loopback host the gateway
        // (in a container) often can't reach. Surfacing it lets an operator diagnose the silent
        // failure at a glance.
        await using var registry = CreateRegistry(publicBaseUrl: null); // default 127.0.0.1:5000
        var controller = new ContextDiscoveryDiagnosticsController(registry, new ContextDiscoveryDiagnostics());

        GetResponse(controller).WebhookUrl.Should().Be("http://127.0.0.1:5000/api/discovery/context_discovery");
    }

    [Fact]
    public async Task Get_ReportsDisabled_WhenCallbackBaseBlank()
    {
        await using var registry = CreateRegistry(publicBaseUrl: string.Empty);
        var controller = new ContextDiscoveryDiagnosticsController(registry, new ContextDiscoveryDiagnostics());

        GetResponse(controller).DiscoveryEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Get_ReportsReceivedCountsPerSession()
    {
        await using var registry = CreateRegistry();
        var diagnostics = new ContextDiscoveryDiagnostics();
        diagnostics.RecordReceived("sess-a", "context_file", "CLAUDE.md");
        diagnostics.RecordReceived("sess-a", "context_file", "AGENTS.md");
        diagnostics.RecordReceived("sess-b", "subagent", "echo");
        var controller = new ContextDiscoveryDiagnosticsController(registry, diagnostics);

        var response = GetResponse(controller);

        response.Sessions.Should().HaveCount(2);
        var a = response.Sessions.Single(s => s.SessionId == "sess-a");
        a.ReceivedCount.Should().Be(2);
        a.LastPath.Should().Be("AGENTS.md");
        a.LastReceivedAt.Should().NotBeNull();
        a.Active.Should().BeFalse("the session was never created on the registry — only discoveries arrived");

        response.Sessions.Single(s => s.SessionId == "sess-b").ReceivedCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_SurfacesRoutingOutcomeCounts()
    {
        // The controller must expose the per-outcome sub-agent routing tally: a routed delivery renders
        // no "Context loaded" pill in the primary view, so these counters are an operator's only signal
        // that routing (vs. today's fan-out or an unresolved drop) is actually happening.
        await using var registry = CreateRegistry();
        var diagnostics = new ContextDiscoveryDiagnostics();
        diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Routed);
        diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Routed);
        diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
        diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Fallback);
        diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Fallback);
        diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Fallback);
        var controller = new ContextDiscoveryDiagnosticsController(registry, diagnostics);

        var response = GetResponse(controller);

        response.Routing.Should().NotBeNull();
        response.Routing.Routed.Should().Be(2);
        response.Routing.Dropped.Should().Be(1);
        response.Routing.Fallback.Should().Be(3);
    }

    private static ContextDiscoveryDiagnosticsResponse GetResponse(ContextDiscoveryDiagnosticsController controller)
    {
        var result = controller.Get();
        return result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ContextDiscoveryDiagnosticsResponse>().Subject;
    }

    private static SandboxSessionRegistry CreateRegistry(string? publicBaseUrl = null)
    {
        static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

        var authOptions = new AuthOptions();
        if (publicBaseUrl is not null)
        {
            authOptions.Webhook.PublicBaseUrl = publicBaseUrl;
        }

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Unused)));

        return new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Unused)),
            authOptions,
            new AuthSharedSecret(authOptions));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
