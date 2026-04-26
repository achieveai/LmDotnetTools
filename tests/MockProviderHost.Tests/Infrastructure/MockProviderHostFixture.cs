using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.AspNetCore.Builder;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests.Infrastructure;

/// <summary>
/// Boots a real <see cref="MockProviderHostBuilder"/> on an ephemeral port and exposes
/// the assigned <see cref="BaseUrl"/>. Disposed at the end of each test (callers create
/// per-test fixtures so the wrapped <see cref="ScriptedSseResponder"/> queues stay isolated).
/// </summary>
internal sealed class MockProviderHostFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MockProviderHostFixture(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<MockProviderHostFixture> StartAsync(ScriptedSseResponder responder)
    {
        var app = MockProviderHostBuilder.Build(responder, urls: ["http://127.0.0.1:0"]);
        return await StartCoreAsync(app).ConfigureAwait(false);
    }

    public static async Task<MockProviderHostFixture> StartAsync(
        HttpMessageHandler openAiHandler,
        HttpMessageHandler anthropicHandler)
    {
        var app = MockProviderHostBuilder.BuildFromHandlers(
            openAiHandler,
            anthropicHandler,
            urls: ["http://127.0.0.1:0"]);
        return await StartCoreAsync(app).ConfigureAwait(false);
    }

    private static async Task<MockProviderHostFixture> StartCoreAsync(WebApplication app)
    {
        await app.StartAsync().ConfigureAwait(false);
        var url = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("Mock host failed to bind to a URL.");
        return new MockProviderHostFixture(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
