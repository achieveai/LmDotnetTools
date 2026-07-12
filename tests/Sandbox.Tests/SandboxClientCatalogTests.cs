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

    [Fact]
    public async Task PreviewMarketplacesAsync_MarketplaceWithNullAlias_ThrowsProtocol_NotArgumentException()
    {
        // "alias" is typed as non-nullable `string` on MarketplaceEntryDto, but System.Text.Json
        // does not enforce that at runtime — a semantically-invalid 2xx body can still supply
        // `null`. SandboxMarketplaceEntry's constructor then rejects it via
        // ArgumentException.ThrowIfNullOrWhiteSpace, which must surface as SandboxException(Protocol)
        // rather than a raw ArgumentException escaping this SDK's exception contract.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/marketplaces/preview",
            """{"selected":["official"],"marketplaces":[{"alias":null,"error":null,"plugins":[]}]}"""
        );

        var exception = await Record.ExceptionAsync(() => client.PreviewMarketplacesAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PreviewMarketplacesAsync_PluginWithMissingName_ThrowsProtocol_NotArgumentException()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/marketplaces/preview",
            """
            {
              "selected": ["official"],
              "marketplaces": [
                {"alias": "official", "error": null, "plugins": [{"version": "1.0.0", "skills": [], "agents": []}]}
              ]
            }
            """
        );

        var exception = await Record.ExceptionAsync(() => client.PreviewMarketplacesAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PreviewMarketplacesAsync_SkillWithBlankName_ThrowsProtocol_NotArgumentException()
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
                    {"name": "code-reviewer", "skills": [{"name": "   ", "path": "/skills/x"}], "agents": []}
                  ]
                }
              ]
            }
            """
        );

        var exception = await Record.ExceptionAsync(() => client.PreviewMarketplacesAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PreviewMarketplacesAsync_CallerCancellation_PropagatesAsOperationCanceledException()
    {
        // A malformed-payload mapping failure must not swallow caller cancellation: cancelling
        // before the call still surfaces as a plain OperationCanceledException, never wrapped as a
        // SandboxException.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/marketplaces/preview", """{"selected":[],"marketplaces":[]}""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.PreviewMarketplacesAsync(ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListDiscoveredAsync_ItemWithMissingKind_ThrowsProtocol_NotArgumentException()
    {
        // "kind"/"name"/"path" are typed as non-nullable `string` on DiscoveredItemDto, but a
        // semantically-invalid 2xx body can still omit or null one out — SandboxDiscoveredItem's
        // constructor validation must map to SandboxException(Protocol), not a raw ArgumentException.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1/discovered",
            """{"discovered":[{"name":"reviewer","path":"/workspace/x"}]}"""
        );

        var exception = await Record.ExceptionAsync(() => client.ListDiscoveredAsync("sess-1"));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDiscoveredAsync_ItemWithNullPath_ThrowsProtocol_NotArgumentException()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1/discovered",
            """{"discovered":[{"kind":"subagent","name":"reviewer","path":null}]}"""
        );

        var exception = await Record.ExceptionAsync(() => client.ListDiscoveredAsync("sess-1"));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDiscoveredAsync_CallerCancellation_PropagatesAsOperationCanceledException()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1/discovered", "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.ListDiscoveredAsync("sess-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    // A null array element deserializes to a null reference for a non-nullable wire DTO; projecting it
    // (or handing it to a model constructor) would otherwise throw a raw NullReferenceException. Every
    // collection level in the catalog tree must reject a null element as Protocol.
    [InlineData("""{"selected":["official",null],"marketplaces":[]}""")]
    [InlineData("""{"selected":[],"marketplaces":[null]}""")]
    [InlineData("""{"selected":[],"marketplaces":[{"alias":"official","plugins":[null]}]}""")]
    [InlineData("""{"selected":[],"marketplaces":[{"alias":"official","plugins":[{"name":"p","skills":[null],"agents":[]}]}]}""")]
    [InlineData("""{"selected":[],"marketplaces":[{"alias":"official","plugins":[{"name":"p","skills":[],"agents":[null]}]}]}""")]
    public async Task PreviewMarketplacesAsync_NullCollectionElement_ThrowsProtocol_NotNullReference(string body)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/marketplaces/preview", body);

        var exception = await Record.ExceptionAsync(() => client.PreviewMarketplacesAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDiscoveredAsync_NullItemElement_ThrowsProtocol_NotNullReference()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1/discovered",
            """{"discovered":[{"kind":"subagent","name":"reviewer","path":"/workspace/x"},null]}"""
        );

        var exception = await Record.ExceptionAsync(() => client.ListDiscoveredAsync("sess-1"));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }
}
