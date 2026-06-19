using System.Net;
using System.Text;
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

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be($"{GatewayBaseUrl}/api/v1/marketplaces/preview?marketplaces=official%2Cclaude_plugins");
    }

    [Fact]
    public async Task GetCatalogAsync_NoAliases_OmitsQueryString()
    {
        var (client, handler) = CreateClient(_ => Ok(CatalogJson));

        _ = await client.GetCatalogAsync();

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be($"{GatewayBaseUrl}/api/v1/marketplaces/preview");
    }

    [Fact]
    public async Task GetCatalogAsync_NonSuccess_ThrowsUnavailable()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"unknown marketplace alias(es): nope"}""",
                Encoding.UTF8, "application/json"),
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

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (MarketplaceCatalogClient Client, StubHandler Handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var client = new MarketplaceCatalogClient(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            new HttpClient(handler),
            NullLogger<MarketplaceCatalogClient>.Instance);
        return (client, handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
