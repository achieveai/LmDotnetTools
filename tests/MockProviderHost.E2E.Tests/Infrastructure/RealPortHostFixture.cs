using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.AspNetCore.Builder;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Boots <see cref="MockProviderHostBuilder"/> on a real ephemeral 127.0.0.1 port and exposes
/// the bound URL. External processes (the Claude Agent SDK CLI) need a real TCP socket; an
/// in-process <c>HttpMessageHandler</c> seam is not enough.
/// </summary>
internal sealed class RealPortHostFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RealPortHostFixture(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<RealPortHostFixture> StartAsync(ScriptedSseResponder responder)
    {
        var app = MockProviderHostBuilder.Build(responder, urls: ["http://127.0.0.1:0"]);
        await app.StartAsync().ConfigureAwait(false);
        var url = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("Mock host failed to bind to a URL.");
        return new RealPortHostFixture(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
