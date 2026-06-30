using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     Contract tests mirroring <c>FunctionRegistryTests</c>: both web tools register their contract
///     and handler into a <see cref="FunctionRegistry" />, and the built contracts expose the expected
///     name, description, and parameter schema. A built handler invocation routes to the fake provider.
/// </summary>
public class WebToolContractTests
{
    private static (FunctionRegistry Registry, FakeWebFetchProvider Fetch, FakeWebSearchProvider Search) BuildRegistry()
    {
        var options = new WebToolsOptions { JinaApiKey = "key-1234567890" };
        var fetch = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "routed-content" } };
        var search = new FakeWebSearchProvider { Result = new WebSearchResult { Items = [] } };

        var fetchTool = new WebFetchTool(fetch, options);
        var searchTool = new WebSearchTool(search, options);

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(fetchTool.Contract, fetchTool.Handler, "WebTools");
        _ = registry.AddFunction(searchTool.Contract, searchTool.Handler, "WebTools");

        return (registry, fetch, search);
    }

    private static FunctionParameterContract Param(FunctionContract contract, string name)
    {
        return contract.Parameters!.Single(p => p.Name == name);
    }

    [Fact]
    public void Build_RegistersWebFetchContract_WithExpectedSchema()
    {
        var (registry, _, _) = BuildRegistry();

        var (contracts, _) = registry.Build();
        var contract = contracts.Single(c => c.Name == WebFetchTool.ToolName);

        contract.Description.Should().NotBeNullOrWhiteSpace();

        var url = Param(contract, "url");
        url.IsRequired.Should().BeTrue();
        url.ParameterType.IsTypeString("string").Should().BeTrue();

        var targetSelector = Param(contract, "targetSelector");
        targetSelector.IsRequired.Should().BeFalse();
        targetSelector.ParameterType.IsTypeString("string").Should().BeTrue();

        var noCache = Param(contract, "noCache");
        noCache.IsRequired.Should().BeFalse();
        noCache.ParameterType.IsTypeString("boolean").Should().BeTrue();
    }

    [Fact]
    public void Build_RegistersWebSearchContract_WithExpectedSchema()
    {
        var (registry, _, _) = BuildRegistry();

        var (contracts, _) = registry.Build();
        var contract = contracts.Single(c => c.Name == WebSearchTool.ToolName);

        contract.Description.Should().NotBeNullOrWhiteSpace();

        var query = Param(contract, "query");
        query.IsRequired.Should().BeTrue();
        query.ParameterType.IsTypeString("string").Should().BeTrue();

        var count = Param(contract, "count");
        count.IsRequired.Should().BeFalse();
        count.ParameterType.IsTypeString("integer").Should().BeTrue();

        var country = Param(contract, "country");
        country.IsRequired.Should().BeFalse();
        country.ParameterType.IsTypeString("string").Should().BeTrue();

        var language = Param(contract, "language");
        language.IsRequired.Should().BeFalse();
        language.ParameterType.IsTypeString("string").Should().BeTrue();
    }

    [Fact]
    public async Task Build_WebFetchHandler_RoutesToProvider()
    {
        var (registry, fetch, _) = BuildRegistry();

        var (_, handlers) = registry.Build();
        var result = await handlers[WebFetchTool.ToolName](
            "{\"url\":\"https://e.com\"}",
            new ToolCallContext(),
            CancellationToken.None
        );

        fetch.Called.Should().BeTrue();
        // ValidateUrl normalizes the authority-only URL by appending the root path.
        fetch.ReceivedUrl.Should().Be("https://e.com/");
        result.ResultText.Should().Contain("routed-content");
    }

    [Fact]
    public async Task Build_WebSearchHandler_RoutesToProvider()
    {
        var (registry, _, search) = BuildRegistry();

        var (_, handlers) = registry.Build();
        _ = await handlers[WebSearchTool.ToolName](
            "{\"query\":\"hello\"}",
            new ToolCallContext(),
            CancellationToken.None
        );

        search.Called.Should().BeTrue();
        search.ReceivedQuery.Should().Be("hello");
    }
}
