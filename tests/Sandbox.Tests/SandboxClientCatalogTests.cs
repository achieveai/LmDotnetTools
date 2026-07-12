using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxClientCatalogTests
{
    [Fact]
    public async Task PreviewMarketplacesAsync_HappyPath_ParsesNestedCatalog()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/marketplaces/preview",
            """
            {
              "selected": ["official"],
              "marketplaces": [
                {
                  "alias": "official",
                  "error": null,
                  "plugins": [
                    {
                      "name": "code-reviewer",
                      "version": "1.0.0",
                      "description": "Review helpers",
                      "skills": [{"name": "pr-review", "description": "desc", "plugin": "code-reviewer", "marketplace": "official", "path": "/skills/pr-review"}],
                      "agents": [{"name": "reviewer", "description": "desc", "plugin": "code-reviewer", "marketplace": "official", "path": "/agents/reviewer"}]
                    }
                  ]
                }
              ]
            }
            """
        );

        var catalog = await client.PreviewMarketplacesAsync();

        catalog.Selected.Should().BeEquivalentTo(["official"]);
        catalog.Marketplaces.Should().ContainSingle();
        var marketplace = catalog.Marketplaces[0];
        marketplace.Alias.Should().Be("official");
        marketplace.Error.Should().BeNull();
        marketplace.Plugins.Should().ContainSingle();
        var plugin = marketplace.Plugins[0];
        plugin.Name.Should().Be("code-reviewer");
        plugin.Skills.Should().ContainSingle().Which.Name.Should().Be("pr-review");
        plugin.Agents.Should().ContainSingle().Which.Name.Should().Be("reviewer");
    }

    [Fact]
    public async Task PreviewMarketplacesAsync_WithAliasFilter_EncodesQueryString()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/marketplaces/preview", """{"selected":[],"marketplaces":[]}""");

        _ = await client.PreviewMarketplacesAsync(["official", "claude_plugins"]);

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        sent.Uri.Query.Should().Contain("marketplaces=official%2Cclaude_plugins");
    }

    [Fact]
    public async Task PreviewMarketplacesAsync_MarketplaceWithError_KeepsEmptyPlugins()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/marketplaces/preview",
            """{"selected":["broken"],"marketplaces":[{"alias":"broken","error":"failed to load","plugins":[]}]}"""
        );

        var catalog = await client.PreviewMarketplacesAsync();

        catalog.Marketplaces[0].Error.Should().Be("failed to load");
        catalog.Marketplaces[0].Plugins.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDiscoveredAsync_HappyPath_ParsesItems()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1/discovered",
            """{"discovered":[{"kind":"subagent","name":"reviewer","description":"desc","path":"/workspace/.claude/agents/reviewer.md","qualified_name":"code-reviewer:reviewer"}]}"""
        );

        var items = await client.ListDiscoveredAsync("sess-1");

        items.Should().ContainSingle();
        items[0].Kind.Should().Be("subagent");
        items[0].Name.Should().Be("reviewer");
        items[0].QualifiedName.Should().Be("code-reviewer:reviewer");
    }

    [Fact]
    public async Task ListDiscoveredAsync_EmptyResponse_ReturnsEmptyList()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1/discovered", "{}");

        var items = await client.ListDiscoveredAsync("sess-1");

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDiscoveredAsync_UsesRestEndpoint_NotSessionMcpHeader()
    {
        // ListDiscoveredAsync is the existing session-discovery REST endpoint, not an MCP call — it
        // must not stamp X-Session-ID (that header is reserved for session-scoped MCP tool calls).
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1/discovered", "{}");

        _ = await client.ListDiscoveredAsync("sess-1");

        handler.Requests.Single().SessionId.Should().BeNull();
    }
}
