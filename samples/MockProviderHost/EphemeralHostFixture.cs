using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace AchieveAi.LmDotnetTools.MockProviderHost;

/// <summary>
/// Boots <see cref="MockProviderHostBuilder"/> on a real ephemeral 127.0.0.1 port and exposes
/// the bound URL. Used by both unit tests (in-process <c>HttpClient</c>) and E2E tests (external
/// CLI subprocess) — external processes need a real TCP socket, an in-process
/// <see cref="HttpMessageHandler"/> seam is not enough.
/// </summary>
internal sealed class EphemeralHostFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EphemeralHostFixture(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<EphemeralHostFixture> StartAsync(ScriptedSseResponder responder)
    {
        var app = MockProviderHostBuilder.Build(responder, urls: ["http://127.0.0.1:0"]);
        return await StartCoreAsync(app).ConfigureAwait(false);
    }

    public static async Task<EphemeralHostFixture> StartAsync(
        HttpMessageHandler openAiHandler,
        HttpMessageHandler anthropicHandler)
    {
        var app = MockProviderHostBuilder.BuildFromHandlers(
            openAiHandler,
            anthropicHandler,
            urls: ["http://127.0.0.1:0"]);
        return await StartCoreAsync(app).ConfigureAwait(false);
    }

    private static async Task<EphemeralHostFixture> StartCoreAsync(WebApplication app)
    {
        await app.StartAsync().ConfigureAwait(false);
        var url = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("Mock host failed to bind to a URL.");
        return new EphemeralHostFixture(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
