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
///     Unit tests for <see cref="WebFetchTool" /> driven by an in-memory fake provider. They cover the
///     happy path, input validation (no provider call on invalid input), fragment stripping, bounded
///     error mapping, cancellation semantics, and output truncation.
/// </summary>
public class WebFetchToolTests
{
    private const string SampleUrl = "https://example.com/page";

    private static WebFetchTool CreateTool(FakeWebFetchProvider provider, WebToolsOptions? options = null)
    {
        return new WebFetchTool(provider, options ?? new WebToolsOptions());
    }

    private static string Args(string url)
    {
        return JsonSerializer.Serialize(new { url });
    }

    private static async Task<string> InvokeAsync(WebFetchTool tool, string argsJson, CancellationToken token = default)
    {
        var result = await tool.Handler(argsJson, new ToolCallContext(), token);
        return result.ResultText;
    }

    [Fact]
    public async Task HandleAsync_HappyPath_ReturnsFramedMarkdownContent()
    {
        var provider = new FakeWebFetchProvider
        {
            Result = new WebFetchResult { Content = "PAGE CONTENT HERE", Title = "A Title" },
        };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, Args(SampleUrl));

        provider.Called.Should().BeTrue();
        text.Should().Contain("PAGE CONTENT HERE");
        text.Should().Contain("BEGIN UNTRUSTED WEB CONTENT");
        text.Should().Contain("END UNTRUSTED WEB CONTENT");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://x")]
    [InlineData("")]
    [InlineData("http://localhost/")]
    public async Task HandleAsync_InvalidUrl_ReturnsErrorAndDoesNotCallProvider(string url)
    {
        var provider = new FakeWebFetchProvider();
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, Args(url));

        text.Should().Contain("WebFetch error");
        provider.Called.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_UrlWithQueryAndFragment_StripsFragmentAndKeepsQuery()
    {
        var provider = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "ok" } };
        var tool = CreateTool(provider);

        _ = await InvokeAsync(tool, Args("https://e.com/p?a=1&b=2#frag"));

        provider.ReceivedUrl.Should().Be("https://e.com/p?a=1&b=2");
        provider.ReceivedUrl.Should().NotContain("#");
        provider.ReceivedUrl.Should().Contain("a=1&b=2");
    }

    [Fact]
    public async Task HandleAsync_TargetSelectorAndNoCache_ReachProvider()
    {
        var provider = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "ok" } };
        var tool = CreateTool(provider);

        var argsJson = JsonSerializer.Serialize(
            new
            {
                url = SampleUrl,
                targetSelector = "#main",
                noCache = true,
            }
        );
        _ = await InvokeAsync(tool, argsJson);

        provider.ReceivedOptions!.TargetSelector.Should().Be("#main");
        provider.ReceivedOptions.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_HttpRequestException404_ReturnsFriendlyMessageWithoutBody()
    {
        var provider = new FakeWebFetchProvider
        {
            Exception = new HttpRequestException(
                "boom: UPSTREAM_SECRET_BODY with lots of detail",
                null,
                HttpStatusCode.NotFound
            ),
        };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, Args(SampleUrl));

        text.Should().Contain("404");
        text.Should().Contain("page not found");
        text.Should().NotContain("UPSTREAM_SECRET_BODY");
    }

    [Fact]
    public async Task HandleAsync_HttpRequestExceptionNoStatus_ReturnsBoundedGenericError()
    {
        var provider = new FakeWebFetchProvider
        {
            Exception = new HttpRequestException("network down: connection reset by peer"),
        };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, Args(SampleUrl));

        text.Should().StartWith("WebFetch error");
        text.Should().NotContain("connection reset by peer");
        text.Length.Should().BeLessThan(120);
    }

    [Fact]
    public async Task HandleAsync_TimeoutWithoutCallerCancellation_ReturnsTimedOutMessage()
    {
        var provider = new FakeWebFetchProvider { Exception = new OperationCanceledException() };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, Args(SampleUrl), CancellationToken.None);

        text.Should().Contain("timed out");
    }

    [Fact]
    public async Task HandleAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        var provider = new FakeWebFetchProvider { Exception = new OperationCanceledException() };
        var tool = CreateTool(provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await tool.Handler(Args(SampleUrl), new ToolCallContext(), cts.Token);

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HandleAsync_ContentExceedsCap_TruncatesWithMarker()
    {
        var provider = new FakeWebFetchProvider
        {
            Result = new WebFetchResult { Content = new string('a', 500) },
        };
        var tool = CreateTool(provider, new WebToolsOptions { OutputCap = 50 });

        var text = await InvokeAsync(tool, Args(SampleUrl));

        text.Should().EndWith(WebToolOutput.TruncationMarker);
        text.Length.Should().BeLessThanOrEqualTo(50 + WebToolOutput.TruncationMarker.Length);
    }

    [Fact]
    public async Task HandleAsync_ThreadsCallerTokenToProvider()
    {
        var provider = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "ok" } };
        var tool = CreateTool(provider);
        using var cts = new CancellationTokenSource();

        _ = await InvokeAsync(tool, Args(SampleUrl), cts.Token);

        // Proves the handler threads the caller's token straight through to the provider call.
        provider.ReceivedToken.Should().Be(cts.Token);
    }
}
