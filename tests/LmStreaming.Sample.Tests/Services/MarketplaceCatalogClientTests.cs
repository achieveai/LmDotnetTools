using System.Diagnostics;
using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// HTTP-level tests for <see cref="MarketplaceCatalogClient"/>: the happy path (gateway JSON →
/// typed <see cref="LmStreaming.Sample.Models.MarketplaceCatalog"/>), the alias query-string
/// construction, and the two unavailable paths (non-success status, connection failure) that the
/// controller maps to a 503. A subset of a real gateway response is used as the body.
/// </summary>
public class MarketplaceCatalogClientTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    // Trimmed, real-shaped gateway response (one marketplace, one plugin, one skill + one agent).
    private const string CatalogJson = """
        {
          "selected": ["ClaudePlugins"],
          "marketplaces": [
            {
              "alias": "ClaudePlugins",
              "error": null,
              "plugins": [
                {
                  "name": "orleans-dev",
                  "version": "1.0.2",
                  "description": "Orleans patterns and review.",
                  "skills": [
                    { "name": "orleans-patterns", "description": "patterns", "plugin": "orleans-dev",
                      "marketplace": "ClaudePlugins", "path": "/marketplaces/ClaudePlugins/orleans-dev/skills/orleans-patterns/" }
                  ],
                  "agents": [
                    { "name": "orleans-reviewer", "description": "reviewer", "plugin": "orleans-dev",
                      "marketplace": "ClaudePlugins", "path": "/marketplaces/ClaudePlugins/orleans-dev/agents/orleans-reviewer.md" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetCatalogAsync_Success_MapsToTypedCatalog()
    {
        var (client, _) = CreateClient(_ => Ok(CatalogJson));

        var catalog = await client.GetCatalogAsync();

        catalog.Selected.Should().Equal("ClaudePlugins");
        catalog.Marketplaces.Should().HaveCount(1);
        var mk = catalog.Marketplaces[0];
        mk.Alias.Should().Be("ClaudePlugins");
        mk.Error.Should().BeNull();
        var plugin = mk.Plugins.Should().ContainSingle().Subject;
        plugin.Version.Should().Be("1.0.2");
        plugin.Skills.Should().ContainSingle(s => s.Name == "orleans-patterns");
        plugin.Agents.Should().ContainSingle(a => a.Path.EndsWith("orleans-reviewer.md"));
    }

    [Fact]
    public async Task GetCatalogAsync_NullVersion_Preserved()
    {
        var json = CatalogJson.Replace("\"version\": \"1.0.2\"", "\"version\": null");
        var (client, _) = CreateClient(_ => Ok(json));

        var catalog = await client.GetCatalogAsync();

        catalog.Marketplaces[0].Plugins[0].Version.Should().BeNull();
    }

    [Fact]
    public async Task GetCatalogAsync_PassesMarketplacesAsCommaSeparatedQuery()
    {
        var (client, handler) = CreateClient(_ => Ok(CatalogJson));

        _ = await client.GetCatalogAsync(["official", "claude_plugins"]);

        handler
            .LastRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{GatewayBaseUrl}/api/v1/marketplaces/preview?marketplaces=official%2Cclaude_plugins");
    }

    [Fact]
    public async Task GetCatalogAsync_NoAliases_OmitsQueryString()
    {
        var (client, handler) = CreateClient(_ => Ok(CatalogJson));

        _ = await client.GetCatalogAsync();

        handler.LastRequest!.RequestUri!.ToString().Should().Be($"{GatewayBaseUrl}/api/v1/marketplaces/preview");
    }

    [Fact]
    public async Task GetCatalogAsync_NonSuccess_ThrowsUnavailable()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":"unknown marketplace alias(es): nope"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });

        var act = async () => await client.GetCatalogAsync(["nope"]);

        var ex = await act.Should().ThrowAsync<MarketplaceCatalogUnavailableException>();
        ex.Which.Message.Should().Contain("400");
    }

    [Fact]
    public async Task GetCatalogAsync_ConnectionRefused_ThrowsUnavailable()
    {
        // Simulates the gateway being offline: the handler throws a connection error.
        var (client, _) = CreateClient(_ => throw new HttpRequestException("Connection refused"));

        var act = async () => await client.GetCatalogAsync();

        await act.Should().ThrowAsync<MarketplaceCatalogUnavailableException>();
    }

    /// <summary>
    /// The wrapper must not be the end of the story. Everything that distinguishes one unavailability
    /// from another — unreachable vs. timed out vs. rejected — lives on the <see cref="SandboxException"/>
    /// underneath, and the compatibility service re-wraps this exception again before an operator sees it.
    /// A cause dropped here is a cause no caller can recover.
    /// </summary>
    [Fact]
    public async Task GetCatalogAsync_ConnectionRefused_KeepsTheSandboxCause()
    {
        var (client, _) = CreateClient(_ => throw new HttpRequestException("Connection refused"));

        var act = async () => await client.GetCatalogAsync();

        var thrown = await act.Should().ThrowAsync<MarketplaceCatalogUnavailableException>();
        var cause = thrown.Which.InnerException.Should().BeOfType<SandboxException>().Subject;
        cause.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        cause.InnerException.Should().BeOfType<HttpRequestException>();
    }

    /// <summary>
    /// The COLD-gateway shape, and the reason session validation needs a transport budget of its own: a
    /// gateway that is coming up answers nothing until it is ready, so the only thing that decides whether
    /// the catalog is "unavailable" is the budget on the handed-in <see cref="HttpClient"/>. Pinning that
    /// here is what makes "give the fail-closed path a bigger client" a fix rather than a hope.
    /// </summary>
    [Fact]
    public async Task GetCatalogAsync_GatewaySlowerThanTheClientBudget_IsUnavailableWithATimeoutCause()
    {
        using var handler = new NeverAnsweringHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var client = new MarketplaceCatalogClient(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            http,
            NullLogger<MarketplaceCatalogClient>.Instance
        );

        var act = async () => await client.GetCatalogAsync();

        var thrown = await act.Should().ThrowAsync<MarketplaceCatalogUnavailableException>();
        thrown
            .Which.InnerException.Should()
            .BeOfType<SandboxException>()
            .Which.Kind.Should()
            .Be(SandboxErrorKind.TransportTimeout);
    }

    [Fact]
    public async Task GetCatalogAsync_CallerCancellation_PropagatesWithoutWrapping()
    {
        // The catch filter intentionally lets CALLER cancellation propagate (only gateway-unreachable
        // becomes MarketplaceCatalogUnavailableException). Pin that: a pre-canceled token must surface
        // as OperationCanceledException, never masqueraded as "gateway offline".
        var (client, _) = CreateClient(_ => Ok(CatalogJson));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await client.GetCatalogAsync(ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await act.Should().NotThrowAsync<MarketplaceCatalogUnavailableException>();
    }

    [Fact]
    public async Task GetCatalogAsync_NullBody_NormalizesToEmptyCatalog()
    {
        // A 200 with a literal JSON `null` body must become an empty catalog, not a null reference.
        var (client, _) = CreateClient(_ => Ok("null"));

        var catalog = await client.GetCatalogAsync();

        catalog.Should().NotBeNull();
        catalog.Selected.Should().BeEmpty();
        catalog.Marketplaces.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCatalogAsync_MalformedBody_ThrowsUnavailable()
    {
        // A reachable gateway that returns a 200 with unparseable JSON is still "unavailable" — it
        // must fold into the 503 contract, not bubble a raw JsonException to a 500.
        var (client, _) = CreateClient(_ => Ok("{ not valid json"));

        var act = async () => await client.GetCatalogAsync();

        await act.Should().ThrowAsync<MarketplaceCatalogUnavailableException>();
    }

    [Fact]
    public void Map_PropagatesPluginFilteringSupported_IntoCapabilities()
    {
        var sdkCatalog = new SandboxMarketplaceCatalog(["official"], [], pluginFilteringSupported: true);

        var mapped = MarketplaceCatalogClient.Map(sdkCatalog);

        mapped.Capabilities.PluginFiltering.Should().BeTrue();
    }

    [Fact]
    public void Map_NullPluginFilteringSupported_PropagatesAsNull()
    {
        var sdkCatalog = new SandboxMarketplaceCatalog(["official"], []);

        var mapped = MarketplaceCatalogClient.Map(sdkCatalog);

        mapped.Capabilities.PluginFiltering.Should().BeNull();
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (MarketplaceCatalogClient Client, StubHandler Handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    )
    {
        var handler = new StubHandler(respond);
        var client = new MarketplaceCatalogClient(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            new HttpClient(handler),
            NullLogger<MarketplaceCatalogClient>.Instance
        );
        return (client, handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// Never answers — a gateway that is still starting up. It ends only when the caller's token is
    /// cancelled, which is precisely the token <see cref="HttpClient"/> cancels when its own
    /// <see cref="HttpClient.Timeout"/> elapses, so the budget is enforced by the real mechanism rather
    /// than by a test-only shortcut and the test never waits on a wall clock it did not bound.
    /// </summary>
    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }
}
