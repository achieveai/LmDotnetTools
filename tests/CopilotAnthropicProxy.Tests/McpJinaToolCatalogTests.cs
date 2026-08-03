using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class McpJinaToolCatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankKey_DisablesBothTools(string? key)
    {
        var catalog = CreateCatalog(new WebToolsOptions { JinaApiKey = key });

        catalog.IsEnabled.Should().BeFalse();
        catalog.Tools.Should().BeEmpty();
    }

    [Fact]
    public void InvalidOptions_DisableBothTools()
    {
        var catalog = CreateCatalog(new WebToolsOptions { JinaApiKey = "secret", OutputCap = 0 });

        catalog.IsEnabled.Should().BeFalse();
        catalog.Tools.Should().BeEmpty();
    }

    [Fact]
    public void ValidKey_ExposesSnakeCaseToolsWithExistingSchemas()
    {
        var catalog = CreateCatalog(new WebToolsOptions { JinaApiKey = "secret" });

        catalog.Tools.Keys.Should().BeEquivalentTo("web_search", "web_fetch");
        var search = catalog.Tools["web_search"].Definition;
        var fetch = catalog.Tools["web_fetch"].Definition;
        search["name"]!.GetValue<string>().Should().Be("web_search");
        fetch["name"]!.GetValue<string>().Should().Be("web_fetch");
        (search["inputSchema"]?["properties"] as JsonObject)!.Should().ContainKey("query");
        (fetch["inputSchema"]?["properties"] as JsonObject)!.Should().ContainKey("url");
        search.ToJsonString().Should().NotContain("secret");
        fetch.ToJsonString().Should().NotContain("secret");
    }

    [Fact]
    public void Selection_RespectsAllowlistExcludeAndLockdown()
    {
        var catalog = CreateCatalog(new WebToolsOptions { JinaApiKey = "secret" });
        var headers = new HeaderDictionary
        {
            ["X-MCP-Tools"] = "web_search, web_fetch",
            ["X-MCP-Exclude-Tools"] = "web_fetch",
        };

        catalog.SelectInjectable(headers, []).Select(t => t.Name).Should().ContainSingle().Which.Should().Be("web_search");

        headers["X-MCP-Lockdown"] = "true";
        catalog.SelectInjectable(headers, []).Should().BeEmpty();
    }

    [Fact]
    public void Selection_DoesNotDuplicateGithubNames()
    {
        var catalog = CreateCatalog(new WebToolsOptions { JinaApiKey = "secret" });

        catalog.SelectInjectable(new HeaderDictionary(), ["web_search"])
            .Select(t => t.Name)
            .Should().ContainSingle().Which.Should().Be("web_fetch");
    }

    private static McpJinaToolCatalog CreateCatalog(WebToolsOptions options)
    {
        var provider = new JinaWebProvider(new HttpClient(new StubHandler()), options);
        return new McpJinaToolCatalog(provider, options, NullLoggerFactory.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
