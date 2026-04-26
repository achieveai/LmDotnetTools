namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// xUnit collection fixture that owns a single Playwright driver and a single headless
/// Chromium <see cref="IBrowser"/> for the duration of the test assembly. Each test
/// should create its own <see cref="IBrowserContext"/> so state (cookies, storage)
/// never leaks across tests.
/// </summary>
/// <remarks>
/// Launching Chromium is the most expensive step in the browser E2E suite
/// (seconds per launch on CI). Sharing one browser across tests avoids paying that
/// cost repeatedly; isolating contexts avoids cross-test interference.
/// </remarks>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        Browser = await Playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = true,
                // --no-sandbox required for containerized CI runners (GitHub Actions, etc).
                Args = ["--no-sandbox", "--disable-dev-shm-usage"],
            }
        );
    }

    public async Task DisposeAsync()
    {
        if (Browser != null)
        {
            await Browser.CloseAsync();
        }

        Playwright?.Dispose();
    }
}

/// <summary>
/// Collection definition so every browser E2E test class can share one Chromium launch
/// via <c>[Collection(PlaywrightCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "Playwright";
}
