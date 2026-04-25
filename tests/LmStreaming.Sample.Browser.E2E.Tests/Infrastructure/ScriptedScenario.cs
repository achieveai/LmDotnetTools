using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Encapsulates the boilerplate that every scenario test repeats: pick the right
/// provider-mode HTTP handler, build a <see cref="ScriptedBuilder"/>, boot the Kestrel
/// factory, open a fresh Playwright context + page, navigate to the SPA, and wait for
/// the chat textarea. Tests can then focus on the behaviour under test.
/// </summary>
public static class ScriptedScenario
{
    /// <summary>
    /// Selects the provider-appropriate handler for <paramref name="providerMode"/>.
    /// </summary>
    public static HttpMessageHandler HandlerFor(this ScriptedSseResponder responder, string providerMode) =>
        providerMode == "test-anthropic" ? responder.AsAnthropicHandler() : responder.AsOpenAiHandler();

    /// <summary>
    /// Boots a <see cref="BrowserWebAppFactory"/> around <paramref name="handler"/>, opens
    /// a new Playwright context + page, navigates to the bound Kestrel URL, and waits for
    /// the chat textarea to render.
    /// </summary>
    /// <returns>
    /// A <see cref="ScenarioSession"/> that owns the factory, context, and page. Callers
    /// dispose it with <c>await using</c>.
    /// </returns>
    public static async Task<ScenarioSession> OpenAsync(
        this PlaywrightFixture fixture,
        string providerMode,
        HttpMessageHandler handler,
        Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? subAgentFactory = null
    )
    {
        var builder = new ScriptedBuilder(handler, subAgentFactory);
        var factory = new BrowserWebAppFactory(providerMode, builder);
        IBrowserContext? context = null;
        try
        {
            context = await fixture.Browser.NewContextAsync();
            var page = await context.NewPageAsync();

            await page.GotoAsync(factory.ServerAddress);
            await page.Textarea().WaitForAsync();

            return new ScenarioSession(factory, context, page);
        }
        catch
        {
            // Partial construction leaks Kestrel port + browser context without this —
            // dispose in reverse order (context first, then factory) before rethrowing.
            if (context is not null)
            {
                await context.CloseAsync();
            }
            await factory.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Owns a booted factory + browser context + page and disposes them in reverse order
/// when the scope ends.
/// </summary>
public sealed class ScenarioSession : IAsyncDisposable
{
    public BrowserWebAppFactory Factory { get; }
    public IBrowserContext Context { get; }
    public IPage Page { get; }

    internal ScenarioSession(BrowserWebAppFactory factory, IBrowserContext context, IPage page)
    {
        Factory = factory;
        Context = context;
        Page = page;
    }

    /// <summary>
    /// Saves a full-page PNG of the current chat surface under
    /// <c>&lt;repoRoot&gt;/.logs/e2e-screenshots/&lt;name&gt;.png</c>. Used by tests at the
    /// end of a successful run so reviewers can scan a per-test-case visual proof
    /// without re-running the suite.
    /// </summary>
    public async Task SaveSuccessScreenshotAsync(string name)
    {
        // Screenshot capture is a diagnostic convenience, not a test assertion.
        // If disk/permissions/browser-state fails (notably after a cancellation
        // test where the page is mid-teardown), fail soft and log — don't mark
        // an otherwise-passing test as failed. Mirrors the
        // "don't block tests" pattern in TestLoggingConfiguration.
        try
        {
            var dir = ScreenshotDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{name}.png");
            await Page.ScreenshotAsync(new() { Path = path, FullPage = true });
        }
#pragma warning disable CA1031 // Do not catch general exception types — screenshot must never fail a passing test.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await Console.Error.WriteLineAsync(
                $"[Screenshot] Warning: failed to save '{name}': {ex.Message}");
        }
    }

    private static string ScreenshotDirectory()
    {
        // Walk up from the test bin dir to the repo root (.sln / .git / .env.test markers —
        // EnvironmentHelper.FindWorkspaceRoot is worktree-aware, handling ".git" as either
        // directory or file). Drop the PNGs in .logs/e2e-screenshots/ so they live
        // alongside the existing test logs.
        var root = EnvironmentHelper.FindWorkspaceRoot(AppContext.BaseDirectory);
        return Path.Combine(root, ".logs", "e2e-screenshots");
    }

    public async ValueTask DisposeAsync()
    {
        // If Context.CloseAsync throws (e.g., browser crashed mid-test) we must
        // still tear down the factory — otherwise the Kestrel port and the
        // LM_PROVIDER_MODE env var leak for the rest of the test run.
        try
        {
            await Context.CloseAsync();
        }
        finally
        {
            await Factory.DisposeAsync();
        }
    }
}
