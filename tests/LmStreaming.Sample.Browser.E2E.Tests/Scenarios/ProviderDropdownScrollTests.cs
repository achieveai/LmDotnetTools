using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Regression for the provider-dropdown scroll fix (issue #133, originally shipped in #129): when the
/// dynamically discovered Copilot model list overflows the menu's capped height
/// (<c>max-height: min(60vh, 320px)</c>), the menu must actually scroll so every option — including the
/// last — stays reachable. PR #129 guarded this only with a source-level CSS assertion; this drives a
/// real browser.
///
/// The Copilot list is token-gated and empty in CI, so a stub <see cref="CopilotModelInfo"/> list is
/// injected through <see cref="BrowserWebAppFactory"/> with the Copilot token gate forced on, making the
/// discovered models render as <em>available</em> providers without a real gh/Copilot login. The test
/// proves the overflowed last option is present, enabled, and reachable by scrolling — it deliberately
/// does NOT select it (the selection flow is covered elsewhere; changing the provider is out of scope
/// for a scroll regression).
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class ProviderDropdownScrollTests
{
    private readonly PlaywrightFixture _fixture;

    public ProviderDropdownScrollTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // Enough entries that the two Copilot partitions alone dwarf the 320px cap under CI fonts —
    // ~20 items × ~35px each + the base providers is well over 320px, so overflow is deterministic
    // rather than dependent on incidental viewport/font sizing.
    private static IReadOnlyList<CopilotModelInfo> StubCopilotModels(int count = 20)
    {
        return
        [
            .. Enumerable.Range(0, count)
                .Select(i => new CopilotModelInfo(
                    $"copilot-model-{i:D2}",
                    $"Copilot Model {i:D2}",
                    i % 2 == 0 ? CopilotModelVendor.Anthropic : CopilotModelVendor.OpenAI,
                    i % 2 == 0 ? CopilotModelTransport.Anthropic : CopilotModelTransport.Responses))
        ];
    }

    [Fact]
    public async Task Overflowing_provider_dropdown_scrolls_and_reaches_the_last_option()
    {
        // A no-op responder: the test never sends a message, it only inspects the provider dropdown.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("assistant"))
            .Turn(t => t.Text("noop"))
            .Build();

        var copilotModels = StubCopilotModels();

        await using var session = await _fixture.OpenAsync("test", responder, copilotModels: copilotModels);
        var page = session.Page;

        // Pin the viewport so the 60vh/320px cap and thus the overflow are deterministic across machines.
        await page.SetViewportSizeAsync(1280, 720);

        // Open the dropdown and wait for the menu to actually render before measuring it.
        await page.ProviderSelectorButton().ClickAsync();
        var menu = page.Locator(".dropdown-menu");
        await menu.WaitForAsync();

        // Setup assertion: the injected Copilot options are present and enabled. This fails loudly if the
        // DI replacement (or the token-gate seam) regressed, before we ever measure scroll behavior.
        var options = page.Locator("[data-testid^='provider-option-']");
        (await options.CountAsync()).Should().BeGreaterThan(
            copilotModels.Count,
            "the base providers plus every injected Copilot model should be rendered");
        await page.ProviderOption("copilot-model-00").WaitForAsync();
        (await page.ProviderOption("copilot-model-00").IsEnabledAsync()).Should().BeTrue(
            "injected Copilot models render available when the token gate is forced on");

        // The menu must actually be scrollable — its content exceeds the capped visible height.
        (await menu.EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight")).Should().BeTrue(
            "the overflowing Copilot list must scroll inside the capped-height menu, not run off-screen");

        // The last option starts below the fold; scrolling must bring it fully into view, and it must be
        // enabled/reachable. We stop here — selecting it is intentionally out of scope.
        var lastOption = options.Last;
        (await lastOption.IsEnabledAsync()).Should().BeTrue();
        await lastOption.ScrollIntoViewIfNeededAsync();
        (await lastOption.IsVisibleAsync()).Should().BeTrue(
            "the last option must be reachable by scrolling the menu");

        await session.SaveSuccessScreenshotAsync("ProviderDropdownScroll.overflow_reaches_last_option");
    }
}
