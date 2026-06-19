using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Drives the marketplace browser end-to-end through the real chat client: the header button opens
/// the modal, which fetches <c>GET /api/marketplaces</c> and renders the catalog. A fake
/// <see cref="LmStreaming.Sample.Services.IMarketplaceCatalogClient"/> stands in for the sandbox
/// gateway so the scenario is deterministic with NO gateway running — and a second case forces the
/// gateway-offline path to confirm the UI degrades gracefully instead of hanging.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class MarketplaceBrowserTests
{
    private readonly PlaywrightFixture _fixture;

    public MarketplaceBrowserTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Marketplace_button_opens_modal_and_renders_catalog()
    {
        // No LLM traffic is needed (we never send a message); a minimal responder satisfies OpenAsync.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("ok"))
            .Build();
        await using var session = await _fixture.OpenAsync(
            "test",
            responder.HandlerFor("test"),
            catalogClient: FakeMarketplaceCatalogClient.WithSampleCatalog());
        var page = session.Page;

        await page.MarketplaceButton().ClickAsync();

        await page.MarketplaceModal()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // The fake catalog's marketplace, plugin and its skill + agent chips all render.
        await page.GetByTestId("marketplace-item-ClaudePlugins")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("marketplace-plugin-orleans-dev").IsVisibleAsync();
        (await page.GetByTestId("marketplace-skill-orleans-patterns").IsVisibleAsync()).Should().BeTrue();
        (await page.GetByTestId("marketplace-agent-orleans-reviewer").IsVisibleAsync()).Should().BeTrue();

        // Closing the modal removes it from the DOM.
        await page.MarketplaceModalClose().ClickAsync();
        await page.MarketplaceModal()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await session.SaveSuccessScreenshotAsync("MarketplaceBrowser.Renders_catalog");
    }

    [Fact]
    public async Task Marketplace_modal_shows_offline_state_when_gateway_unavailable()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("ok"))
            .Build();
        await using var session = await _fixture.OpenAsync(
            "test",
            responder.HandlerFor("test"),
            catalogClient: FakeMarketplaceCatalogClient.Offline());
        var page = session.Page;

        await page.MarketplaceButton().ClickAsync();

        await page.GetByTestId("marketplace-browser-offline")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        await session.SaveSuccessScreenshotAsync("MarketplaceBrowser.Offline_state");
    }
}
