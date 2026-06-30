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
    public async Task HandleAsync_DoesNotEchoQueryIntoOutput()
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult { Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }] },
        };
        var tool = CreateTool(provider);
        const string query = "john.doe@corp.com SSN 123-45-6789";

        var text = await InvokeAsync(tool, JsonSerializer.Serialize(new { query }));

        // The user's query may carry PII/secrets, so it must never be echoed back into the output.
        text.Should().NotContain(query);
        // The full query still reaches the provider so the search itself is unaffected.
        provider.ReceivedQuery.Should().Be(query);
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
    [InlineData(-5, 1)] // negative clamps up to the minimum
    [InlineData(0, 1)] // zero clamps up to the minimum
    [InlineData(9999, 20)] // huge clamps down to the maximum
    [InlineData(10, 10)] // in-range passes through unchanged
    public async Task HandleAsync_CountOutOfRange_ClampedBeforeReachingProvider(int requested, int expected)
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult { Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }] },
        };
        var tool = CreateTool(provider);

        _ = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats", count = requested }));

        provider.ReceivedOptions!.Count.Should().Be(expected);
    }

    [Theory]
    [InlineData("USA")] // 3 letters, not a 2-letter code
    [InlineData("1!")] // non-alpha
    [InlineData("u")] // too short
    public async Task HandleAsync_MalformedCountry_OmittedFromProviderOptions(string country)
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult { Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }] },
        };
        var tool = CreateTool(provider);

        _ = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats", country }));

        provider.ReceivedOptions!.Country.Should().BeNull();
    }

    [Theory]
    [InlineData("abcdef")] // 6 letters, exceeds the {2,5} primary subtag
    [InlineData("e")] // 1 letter, below the {2,5} primary subtag
    [InlineData("en_US")] // underscore is not an allowed separator
    [InlineData("en-")] // trailing separator with empty subtag
    public async Task HandleAsync_MalformedLanguage_OmittedFromProviderOptions(string language)
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult { Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }] },
        };
        var tool = CreateTool(provider);

        _ = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats", language }));

        provider.ReceivedOptions!.Language.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ValidLocaleWithSubtag_ReachesProviderOptions()
    {
        var provider = new FakeWebSearchProvider
        {
            Result = new WebSearchResult { Items = [new WebSearchItem { Title = "T", Url = "https://t.example" }] },
        };
        var tool = CreateTool(provider);

        _ = await InvokeAsync(tool, JsonSerializer.Serialize(new { query = "cats", language = "en-US" }));

        provider.ReceivedOptions!.Language.Should().Be("en-US");
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
