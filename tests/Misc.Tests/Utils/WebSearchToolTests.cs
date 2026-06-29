using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     Unit tests for <see cref="WebSearchTool" /> driven by an in-memory fake provider. They cover the
///     happy path, the up-front API-key gate, the 401 mapping, empty results, and option forwarding.
/// </summary>
public class WebSearchToolTests
{
    private const string ApiKey = "key-1234567890";

    private static WebSearchTool CreateTool(FakeWebSearchProvider provider, WebToolsOptions? options = null)
    {
        return new WebSearchTool(provider, options ?? new WebToolsOptions { JinaApiKey = ApiKey });
    }

    private static async Task<string> InvokeAsync(WebSearchTool tool, string argsJson)
    {
        var result = await tool.Handler(argsJson, new ToolCallContext(), CancellationToken.None);
        return result.ResultText;
    }

    [Fact]
    public async Task HandleAsync_HappyPath_ReturnsMarkdownListWithTitlesUrlsAndSnippets()
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult
            {
                Items =
                [
                    new WebSearchItem
                    {
                        Title = "First Result",
                        Url = "https://a.example",
                        Snippet = "snippet one",
                    },
                    new WebSearchItem
                    {
                        Title = "Second Result",
                        Url = "https://b.example",
                        Snippet = "snippet two",
                    },
                ],
            },
        };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats" }));

        provider.Called.Should().BeTrue();
        text.Should().Contain("First Result").And.Contain("https://a.example").And.Contain("snippet one");
        text.Should().Contain("Second Result").And.Contain("https://b.example").And.Contain("snippet two");
        text.Should().Contain("BEGIN UNTRUSTED WEB CONTENT");
    }

    [Fact]
    public async Task HandleAsync_MissingApiKey_ReturnsUnavailableAndDoesNotCallProvider()
    {
        var provider = new FakeWebSearchProvider();
        var tool = new WebSearchTool(provider, new WebToolsOptions { JinaApiKey = null });

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats" }));

        text.Should().Be("WebSearch unavailable: set JINA_API_KEY.");
        provider.Called.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_Unauthorized401_ReturnsInvalidKeyMessage()
    {
        var provider = new FakeWebSearchProvider
        {
            Exception = new HttpRequestException(
                "unauthorized: UPSTREAM_SECRET_BODY",
                null,
                HttpStatusCode.Unauthorized
            ),
        };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats" }));

        text.Should().Contain("401");
        text.Should().Contain("invalid");
        text.Should().NotContain("UPSTREAM_SECRET_BODY");
    }

    [Fact]
    public async Task HandleAsync_EmptyItems_ReturnsNoResultsFound()
    {
        var provider = new FakeWebSearchProvider { Result = new WebSearchResult { Items = [] } };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats" }));

        text.Should().Be("No results found.");
    }

    [Fact]
    public async Task HandleAsync_CountCountryLanguage_ReachProviderOptions()
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult
            {
                Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }],
            },
        };
        var tool = CreateTool(provider);

        var argsJson = JsonSerializer.Serialize(
            new
            {
                query = "cats",
                count = 3,
                country = "US",
                language = "en",
            }
        );
        _ = await InvokeAsync(tool, argsJson);

        provider.ReceivedOptions!.Count.Should().Be(3);
        provider.ReceivedOptions.Country.Should().Be("US");
        provider.ReceivedOptions.Language.Should().Be("en");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_InvalidQuery_ReturnsErrorAndDoesNotCallProvider(string query)
    {
        var provider = new FakeWebSearchProvider();
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query }));

        text.Should().Contain("WebSearch error");
        provider.Called.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ControlCharQuery_ReturnsErrorAndDoesNotCallProvider()
    {
        var provider = new FakeWebSearchProvider();
        var tool = CreateTool(provider);
        // (char)1 is SOH, a control character built at runtime (never typed into the file).
        var query = "bad" + (char)1 + "query";

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query }));

        text.Should().Contain("WebSearch error");
        provider.Called.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_TimeoutWithoutCallerCancellation_ReturnsTimedOutMessage()
    {
        var provider = new FakeWebSearchProvider { Exception = new OperationCanceledException() };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats" }));

        text.Should().Contain("timed out");
    }

    [Fact]
    public async Task HandleAsync_ThreadsCallerTokenToProvider()
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult { Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }] },
        };
        var tool = CreateTool(provider);
        using var cts = new CancellationTokenSource();

        _ = await tool.Handler(JsonSerializer.Serialize(new { query = "cats" }), new ToolCallContext(), cts.Token);

        // Proves the handler threads the caller's token straight through to the provider call.
        provider.ReceivedToken.Should().Be(cts.Token);
    }
}
