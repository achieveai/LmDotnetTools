using System.Net;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the read-only members the context-discovery diagnostics endpoint reads off the registry:
/// <see cref="SandboxSessionRegistry.DiscoveryWebhookUrl"/> (so an operator can see — and recognise
/// a loopback/unreachable — callback host), <see cref="SandboxSessionRegistry.DiscoveryEnabled"/>
/// (false when no callback base is configured), and
/// <see cref="SandboxSessionRegistry.GetActiveSessionIds"/>.
/// </summary>
public sealed class SandboxSessionRegistryDiscoveryDiagnosticsTests
{
    [Fact]
    public async Task DiscoveryWebhookUrl_AppendsRouteToCallbackBase_AndEnabledIsTrue()
    {
        await using var registry = CreateRegistry(publicBaseUrl: "http://example.test:5000/");

        // Trailing slash is normalised away by CallbackBaseUrl, so the route concatenation is clean.
        registry.DiscoveryWebhookUrl.Should().Be("http://example.test:5000/api/discovery/context_discovery");
        registry.DiscoveryEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task DiscoveryEnabled_IsFalse_WhenCallbackBaseIsBlank()
    {
        await using var registry = CreateRegistry(publicBaseUrl: string.Empty);

        registry.DiscoveryEnabled.Should().BeFalse("no callback base means the gateway has nowhere to deliver discoveries");
        registry.DiscoveryWebhookUrl.Should().Be("/api/discovery/context_discovery");
    }

    [Fact]
    public async Task GetActiveSessionIds_IsEmpty_BeforeAnySessionCreated()
    {
        await using var registry = CreateRegistry();

        registry.GetActiveSessionIds().Should().BeEmpty();
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
