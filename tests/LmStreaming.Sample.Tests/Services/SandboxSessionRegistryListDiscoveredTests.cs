using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// HTTP-level tests for <see cref="SandboxSessionRegistry.ListDiscoveredAsync"/>: the success path
/// (well-formed JSON → strongly-typed items) and the failure path (non-2xx → thrown
/// <see cref="InvalidOperationException"/> carrying the truncated body). Both were promised in
/// plan v2 and remained ungated until this pin.
/// </summary>
public class SandboxSessionRegistryListDiscoveredTests
{
    private const string SessionId = "session-abc";
    private const string GatewayBaseUrl = "http://localhost:3000";

    private static (SandboxSessionRegistry Registry, StubHandler Handler) CreateRegistry(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        // SandboxGatewayLifetime needs an HttpClient too; give it the same stub since this test
        // only exercises ListDiscoveredAsync and never calls EnsureReadyAsync.
        var gatewayLifetimeClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));
        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            gatewayLifetimeClient);

        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions()));

        return (registry, handler);
    }

    [Fact]
    public async Task ListDiscoveredAsync_Success_ReturnsItems()
    {
        var json = """
            {
              "session_id": "session-abc",
              "discovered": [
                { "kind": "subagent", "name": "architecture-review", "qualified_name": "code-reviewer:architecture-review",
                  "description": "arch review", "path": "/marketplaces/gb-plugins/agents/architecture-review.md",
                  "content": "---\nname: architecture-review\n---\nYou review architecture." },
                { "kind": "skill", "name": "review", "description": null, "path": ".claude/skills/review.md" }
              ]
            }
            """;
        var (registry, handler) = CreateRegistry(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        var items = await registry.ListDiscoveredAsync(SessionId);

        items.Should().HaveCount(2);
        items[0].Kind.Should().Be("subagent");
        items[0].QualifiedName.Should().Be("code-reviewer:architecture-review");
        items[0].Content.Should().Contain("You review architecture.");
        items[1].Kind.Should().Be("skill");
        items[1].Content.Should().BeNull();
        items[1].QualifiedName.Should().BeNull();

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be($"{GatewayBaseUrl}/api/v1/sandboxes/{SessionId}/discovered");
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task ListDiscoveredAsync_EmptyItems_ReturnsEmptyList()
    {
        var (registry, _) = CreateRegistry(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"discovered\":[]}", Encoding.UTF8, "application/json"),
        });

        var items = await registry.ListDiscoveredAsync(SessionId);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDiscoveredAsync_NonSuccess_ThrowsWithStatusCode()
    {
        // The SDK never surfaces the raw gateway body (it can echo submitted material), so the
        // registry correlates failures by the gateway status code only — no body / truncation marker.
        var largeBody = new string('x', 1200);
        var (registry, _) = CreateRegistry(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(largeBody, Encoding.UTF8, "text/html"),
        });

        var act = async () => await registry.ListDiscoveredAsync(SessionId);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("502");
        ex.Which.Message.Should().NotContain(largeBody);
    }

    [Fact]
    public async Task ListDiscoveredAsync_NotFound_Throws()
    {
        var (registry, _) = CreateRegistry(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("session not found"),
        });

        var act = async () => await registry.ListDiscoveredAsync(SessionId);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("404");
    }

    [Fact]
    public async Task ListDiscoveredAsync_NullPayload_ReturnsEmptyList()
    {
        // Gateway returning a JSON null body is structurally allowed by the wire contract;
        // map that to an empty list rather than NRE.
        var (registry, _) = CreateRegistry(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        });

        var items = await registry.ListDiscoveredAsync(SessionId);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDiscoveredAsync_NullOrWhitespaceSessionId_Throws()
    {
        var (registry, _) = CreateRegistry(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAsync<ArgumentException>(() => registry.ListDiscoveredAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.ListDiscoveredAsync("   "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => registry.ListDiscoveredAsync(null!));
    }

    /// <summary>
    /// Minimal HttpMessageHandler that delegates the response to a lambda and records the last
    /// request. Captures the request for URL/method assertions.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
