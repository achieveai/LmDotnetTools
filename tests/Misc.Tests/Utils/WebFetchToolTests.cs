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
    public async Task HandleAsync_MinimizesEchoedUrl_DropsQueryAndFragmentButFetchesFullUrl()
    {
        var provider = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "ok" } };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(
            tool,
            Args("https://example.com/reset?email=user@example.com&token=sek-ret#frag")
        );

        // The displayed source label is minimized: query, fragment, and any secrets are dropped.
        text.Should().NotContain("email=user@example.com");
        text.Should().NotContain("token=sek-ret");
        text.Should().NotContain("frag");
        text.Should().Contain("example.com/reset");

        // Fetching still uses the full URL (only the fragment is stripped by validation), so the
        // minimization is display-only and does not break the request.
        provider.ReceivedUrl.Should().Be("https://example.com/reset?email=user@example.com&token=sek-ret");
    }

    [Fact]
    public async Task HandleAsync_ProviderUrlWithQuery_NotEchoedVerbatim()
    {
        var provider = new FakeWebFetchProvider
        {
            Result = new WebFetchResult { Content = "ok", Url = "https://x.com/p?secret=abc" },
        };
        var tool = CreateTool(provider);

        var text = await InvokeAsync(tool, Args(SampleUrl));

        // A provider-returned URL carrying a query must not be echoed verbatim into the Source line.
        text.Should().NotContain("secret=abc");
        text.Should().Contain("x.com/p");
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
    public async Task HandleAsync_TargetSelectorWithControlChar_OmittedFromProviderOptions()
    {
        var provider = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "ok" } };
        var tool = CreateTool(provider);
        // (char)10 is LF: a CR/LF in the selector could enable header injection via X-Target-Selector,
        // which is added with TryAddWithoutValidation. Built at runtime, never typed into the file.
        var selector = "a" + (char)10 + "b";
        var argsJson = JsonSerializer.Serialize(new { url = SampleUrl, targetSelector = selector });

        _ = await InvokeAsync(tool, argsJson);

        provider.ReceivedOptions!.TargetSelector.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_OverlongTargetSelector_OmittedFromProviderOptions()
    {
        var provider = new FakeWebFetchProvider { Result = new WebFetchResult { Content = "ok" } };
        var tool = CreateTool(provider);
        var selector = new string('a', 257); // exceeds the 256-character cap

        var argsJson = JsonSerializer.Serialize(new { url = SampleUrl, targetSelector = selector });

        _ = await InvokeAsync(tool, argsJson);

        provider.ReceivedOptions!.TargetSelector.Should().BeNull();
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
        // OutputCap is the FINAL cap: the truncation marker counts toward it.
        text.Length.Should().BeLessThanOrEqualTo(50);
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
