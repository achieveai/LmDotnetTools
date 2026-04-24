using System.Net;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC5 — Error surface. When the upstream provider returns a 5xx response, the UI must
/// render a user-visible error banner rather than silently hanging.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class ErrorHandlingTests
{
    private readonly PlaywrightFixture _fixture;

    public ErrorHandlingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Provider_5xx_renders_error_banner(string providerMode)
    {
        // A handler that always returns 500 simulates an upstream provider outage — the
        // sample's OpenAI/Anthropic client surfaces this through its streaming pipeline
        // and the WebSocket broadcasts it to the client as an error frame.
        var handler = new AlwaysErrorHandler();

        await using var session = await _fixture.OpenAsync(providerMode, handler);
        var page = session.Page;

        await page.SendMessageAsync("trigger an error");

        // The error banner should surface within a reasonable window.
        await page.ErrorBanner()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });

        var bannerText = await page.ErrorBanner().InnerTextAsync();
        bannerText.Should().NotBeNullOrWhiteSpace("error banner must surface a human-readable message");
        await session.SaveSuccessScreenshotAsync($"ErrorHandling.Provider_5xx_renders_error_banner_{providerMode}");
    }

    private sealed class AlwaysErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"simulated provider outage\"}"),
                }
            );
    }
}
