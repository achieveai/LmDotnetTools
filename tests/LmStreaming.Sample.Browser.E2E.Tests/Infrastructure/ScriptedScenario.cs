using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Encapsulates the boilerplate that every scenario test repeats: pick the right
/// provider-mode HTTP handler, build a <see cref="BrowserWebAppFactory.ScriptedBuilder"/>,
/// boot the Kestrel factory, open a fresh Playwright context + page, navigate to the SPA,
/// and wait for the chat textarea. Tests can then focus on the behaviour under test.
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
        var builder = new BrowserWebAppFactory.ScriptedBuilder(handler, subAgentFactory);
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

    public async ValueTask DisposeAsync()
    {
        await Context.CloseAsync();
        await Factory.DisposeAsync();
    }
}
