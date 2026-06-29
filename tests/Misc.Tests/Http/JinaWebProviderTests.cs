using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Web;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Http;

/// <summary>
///     Unit tests for <see cref="JinaWebProvider" /> covering request shape, headers, authorization,
///     tolerant response parsing, and retry behavior. All HTTP traffic is faked via
///     <see cref="FakeHttpMessageHandler" /> so no real network calls are made.
/// </summary>
public class JinaWebProviderTests
{
    private const string ReaderUrl = "https://r.jina.ai/";
    private const string SearchUrl = "https://s.jina.ai/";

    /// <summary>
    ///     Builds a provider wired to <paramref name="handler" /> using fast retry settings so
    ///     retry tests do not sleep for production-length delays.
    /// </summary>
    private static JinaWebProvider CreateProvider(HttpMessageHandler handler, WebToolsOptions? options = null)
    {
        return new JinaWebProvider(
            new HttpClient(handler),
            options ?? new WebToolsOptions(),
            logger: null,
            retryOptions: RetryOptions.FastForTests
        );
    }

    [Fact]
    public async Task FetchAsync_PostsToReader_WithMarkdownHeaders()
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler(
            "{\"data\":{\"content\":\"x\"}}",
            out var captured
        );
        var provider = CreateProvider(handler);

        _ = await provider.FetchAsync("https://example.com/page", new WebFetchOptions(), CancellationToken.None);

        captured.Request.Should().NotBeNull();
        captured.Request!.RequestUri!.ToString().Should().Be(ReaderUrl);
        captured.Request.Method.Should().Be(HttpMethod.Post);
        captured.RequestBody.Should().Contain("https://example.com/page");
        captured.Request.Headers.Accept.ToString().Should().Contain("application/json");
        captured.Request.Headers.GetValues("X-Return-Format").Should().Contain("markdown");
    }

    [Fact]
    public async Task FetchAsync_WithKey_SendsAuthorization()
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler("{\"data\":{\"content\":\"x\"}}", out var captured);
        var provider = CreateProvider(handler, new WebToolsOptions { JinaApiKey = "secret-key-123" });

        _ = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        captured.Request!.Headers.Authorization.Should().NotBeNull();
        captured.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Request.Headers.Authorization.Parameter.Should().Be("secret-key-123");
    }

    [Fact]
    public async Task FetchAsync_WithoutKey_OmitsAuthorization()
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler("{\"data\":{\"content\":\"x\"}}", out var captured);
        var provider = CreateProvider(handler, new WebToolsOptions());

        _ = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        captured.Request!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_TargetSelectorAndNoCache_SetHeaders()
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler("{\"data\":{\"content\":\"x\"}}", out var captured);
        var provider = CreateProvider(handler);

        _ = await provider.FetchAsync(
            "https://example.com",
            new WebFetchOptions { TargetSelector = "#main", NoCache = true },
            CancellationToken.None
        );

        captured.Request!.Headers.GetValues("X-Target-Selector").Should().Contain("#main");
        captured.Request.Headers.GetValues("X-No-Cache").Should().Contain("true");
    }

    [Fact]
    public async Task FetchAsync_NoOptionalOptions_OmitsOptionalHeaders()
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler("{\"data\":{\"content\":\"x\"}}", out var captured);
        var provider = CreateProvider(handler);

        _ = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        captured.Request!.Headers.Contains("X-Target-Selector").Should().BeFalse();
        captured.Request.Headers.Contains("X-No-Cache").Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_ParsesJsonEnvelope()
    {
        const string json =
            "{\"code\":200,\"data\":{\"title\":\"T\",\"url\":\"u\",\"content\":\"# md\",\"usage\":{\"tokens\":42}}}";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(json);
        var provider = CreateProvider(handler);

        var result = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        result.Content.Should().Be("# md");
        result.Title.Should().Be("T");
        result.Url.Should().Be("u");
        result.UsageTokens.Should().Be(42);
    }

    [Fact]
    public async Task FetchAsync_PlainMarkdownBody_UsedAsContent()
    {
        const string body = "# Title\n\nSome **markdown** content.";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(body);
        var provider = CreateProvider(handler);

        var result = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        result.Content.Should().Be(body);
        result.Title.Should().BeNull();
        result.Url.Should().BeNull();
        result.UsageTokens.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_RetriesOn429ThenSucceeds()
    {
        const string successJson = "{\"data\":{\"content\":\"# ok\",\"usage\":{\"tokens\":5}}}";
        var handler = FakeHttpMessageHandler.CreateStatusCodeSequenceHandler(
            [HttpStatusCode.TooManyRequests, HttpStatusCode.OK],
            successJson
        );
        var provider = CreateProvider(handler);

        var result = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        result.Content.Should().Be("# ok");
        result.UsageTokens.Should().Be(5);
    }

    [Fact]
    public async Task FetchAsync_RetriesOn500ThenSucceeds()
    {
        const string successJson = "{\"data\":{\"content\":\"# ok\"}}";
        var handler = FakeHttpMessageHandler.CreateStatusCodeSequenceHandler(
            [HttpStatusCode.InternalServerError, HttpStatusCode.OK],
            successJson
        );
        var provider = CreateProvider(handler);

        var result = await provider.FetchAsync("https://example.com", new WebFetchOptions(), CancellationToken.None);

        result.Content.Should().Be("# ok");
    }

    [Fact]
    public async Task SearchAsync_PostsToSearch_WithBodyAndAuth()
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler("{\"data\":[]}", out var captured);
        var provider = CreateProvider(handler, new WebToolsOptions { JinaApiKey = "k" });

        _ = await provider.SearchAsync(
            "dotnet news",
            new WebSearchOptions
            {
                Count = 5,
                Country = "US",
                Language = "en",
            },
            CancellationToken.None
        );

        captured.Request!.RequestUri!.ToString().Should().Be(SearchUrl);
        captured.Request.Method.Should().Be(HttpMethod.Post);
        captured.RequestBody.Should().Contain("dotnet news");
        captured.RequestBody.Should().Contain("\"num\":5");
        captured.RequestBody.Should().Contain("\"gl\":\"US\"");
        captured.RequestBody.Should().Contain("\"hl\":\"en\"");
        captured.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Request.Headers.Authorization.Parameter.Should().Be("k");
    }

    [Fact]
    public async Task SearchAsync_ParsesItems()
    {
        const string json =
            "{\"data\":[{\"title\":\"A\",\"url\":\"https://a\",\"description\":\"da\"},"
            + "{\"title\":\"B\",\"url\":\"https://b\",\"snippet\":\"sb\"}],\"usage\":{\"tokens\":7}}";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(json);
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync("q", new WebSearchOptions(), CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Title.Should().Be("A");
        result.Items[0].Url.Should().Be("https://a");
        result.Items[0].Snippet.Should().Be("da");
        result.Items[1].Snippet.Should().Be("sb");
        result.UsageTokens.Should().Be(7);
    }

    [Fact]
    public async Task SearchAsync_SkipsPartialItems()
    {
        const string json = "{\"data\":[{\"title\":\"A\",\"url\":\"https://a\"},{\"title\":\"NoUrl\"}]}";
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(json);
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync("q", new WebSearchOptions(), CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].Title.Should().Be("A");
    }

    [Fact]
    public async Task SearchAsync_EmptyResults_ReturnsEmpty()
    {
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{\"data\":[]}");
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync("q", new WebSearchOptions(), CancellationToken.None);

        result.Items.Should().BeEmpty();
    }
}
