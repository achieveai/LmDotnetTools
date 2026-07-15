using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Behavioral regression for the two chat-client layout fixes shipped in PR #203 (issue #199):
/// <list type="number">
///   <item>
///     <b>Spurious whole-page scrollbar.</b> The tool pills render <c>.sr-only</c> accessibility
///     labels with <c>position: absolute</c>. With every ancestor <c>position: static</c>, those
///     labels' containing block resolved to the document, so <c>.chat-layout { overflow: hidden }</c>
///     could not clip them — they lengthened <c>documentElement</c> into a page scrollbar beside the
///     intended conversation scrollbar. Fix: <c>.tool-pill { position: relative }</c> makes each pill
///     the containing block for its own labels, so they stay clipped inside <c>.message-list</c>.
///   </item>
///   <item>
///     <b>Header "Clear" button clipped off the right edge.</b> The header control row (workspace +
///     provider + mode selectors, "Marketplaces", "Clear") is collectively wider than the 900px
///     content column, so a single non-wrapping flex row pushed the trailing "Clear" button past the
///     viewport. Fix: <c>flex-wrap: wrap</c> on <c>.chat-header</c> and <c>.header-actions</c> lets
///     the controls reflow so "Clear" stays on screen.
///   </item>
/// </list>
///
/// The vitest suite (<c>AppShellLayout.test.ts</c>) guards these fixes only at the source-text level
/// (the CSS declarations exist). happy-dom does not compute real layout — <c>scrollHeight</c> /
/// <c>getBoundingClientRect</c> are stubbed — so a cascade / specificity / flex / containing-block
/// regression could restore either bug while every source-text check stays green. This test therefore
/// drives the REAL chat client under headless Chromium (precedent: <c>ProviderDropdownScrollTests</c>,
/// added because "PR #129 guarded this only with a source-level CSS assertion; this drives a real
/// browser") and asserts the OBSERVABLE outcome: the document does not scroll while
/// <c>.message-list</c> does, and the "Clear" button lands inside the viewport.
///
/// RED without the production fixes / GREEN with them: reverting <c>.tool-pill { position: relative }</c>
/// re-introduces the page scrollbar (assertion (a), strongest when every pill is expanded); reverting
/// the header <c>flex-wrap</c> pushes "Clear" back to right ≈ 1346px at a 1280px viewport (assertion (c)).
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class ChatClientLayoutRegressionTests
{
    /// <summary>
    /// Number of scripted <c>calculate</c> tool-call turns. Twelve pills (each rendering two
    /// absolutely-positioned <c>.sr-only</c> labels) plus the long final text turn guarantees the
    /// conversation overflows an 800px-tall viewport, so the "page must not scroll while the message
    /// list does" assertion is not vacuous — there is genuinely more content than fits.
    /// </summary>
    private const int ToolCallCount = 12;

    /// <summary>
    /// Pinned viewport. At 1280px the header control row is wider than the 900px content column, so the
    /// pre-fix (non-wrapping) header clipped "Clear" at right ≈ 1346px — making the header regression
    /// deterministic. 800px tall keeps the conversation overflowing so the internal scroll region is
    /// exercised.
    /// </summary>
    private const int ViewportWidth = 1280;
    private const int ViewportHeight = 800;

    private readonly PlaywrightFixture _fixture;

    public ChatClientLayoutRegressionTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Overflowing_conversation_scrolls_the_message_list_not_the_page_and_keeps_Clear_on_screen()
    {
        // A single default-mode parent role serves the whole run: twelve calculate tool-call turns
        // (each executed by the real local `calculate` tool so the multi-turn loop advances) followed
        // by a long final text turn. The tool pills + the long tail overflow the pinned viewport.
        var role = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"));

        for (var i = 0; i < ToolCallCount; i++)
        {
            var n = i;
            role = role.Turn(t => t.ToolCall("calculate", new { a = n, operation = "add", b = 1 }));
        }

        var responder = role
            .Turn(t => t.TextLen(6_000))
            .Build();

        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;

        // Pin the viewport so both regressions are deterministic across machines (see field docs).
        await page.SetViewportSizeAsync(ViewportWidth, ViewportHeight);

        // Open a fresh conversation, then run the scripted plan to completion so every pill and the
        // long final text are rendered before we measure layout.
        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("run twelve calculations, then write a long summary");
        await page.WaitForStreamActiveAsync();
        await page.ToolCallPills().WaitForCountAtLeastAsync(ToolCallCount, timeoutMs: 30_000);
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

        (await page.ToolCallPills().CountAsync())
            .Should()
            .Be(ToolCallCount, "the scripted run emits exactly one pill per tool call");

        // Assertion (b) — sanity that the page-scroll assertions are not vacuous: the conversation
        // genuinely overflows and that overflow is absorbed INTERNALLY by `.message-list`. If nothing
        // overflowed, "the page does not scroll" would pass trivially. This must hold before we assert
        // the page itself does not scroll.
        (await page.MessageList().EvaluateAsync<double>("el => el.scrollHeight - el.clientHeight"))
            .Should()
            .BeGreaterThan(
                0,
                "the conversation must overflow and scroll INSIDE .message-list (otherwise 'the page does not scroll' is vacuous)");

        // Assertion (a), collapsed — the document must not scroll: the fixed full-height shell caps at
        // 100vh and clips its overflow, so `.sr-only` labels (and everything else) stay inside the
        // conversation's internal scroll region rather than lengthening the page.
        await AssertPageDoesNotScrollAsync(page, "with tool pills collapsed");

        // Expand EVERY pill: this is where the containing-block bug was largest — an expanded pill
        // renders the most content and its two `.sr-only` labels per pill escaped furthest without the
        // `position: relative` fix. Target the pill HEADER toggle explicitly (scoped so a future
        // collapsed control isn't swept in) and click through Playwright's actionability checks rather
        // than a raw DOM click, asserting one header per pill so the intended interaction is explicit
        // and fails clearly if a header is missing or not actionable.
        var pillHeaders = page.Locator("[data-testid='tool-call-pill'] .tool-pill__header");
        (await pillHeaders.CountAsync()).Should().Be(
            ToolCallCount,
            "every collapsed tool pill exposes exactly one header toggle to expand");
        for (var i = 0; i < ToolCallCount; i++)
        {
            await pillHeaders.Nth(i).ClickAsync();
        }

        // Wait for the expanded bodies to actually render before re-measuring (Vue renders on the next
        // tick — never assert against an un-applied state change with a fixed sleep).
        await page.Locator("[data-testid='tool-call-pill'] .tool-pill__body")
            .WaitForCountAtLeastAsync(ToolCallCount, timeoutMs: 10_000);

        // Assertion (a), expanded — the strongest guard on the sr-only-leak fix. Without
        // `.tool-pill { position: relative }`, the now-numerous absolutely-positioned labels escape the
        // shell clip and re-introduce the whole-page scrollbar.
        await AssertPageDoesNotScrollAsync(page, "with every tool pill expanded (largest sr-only leak)");

        // Assertion (c) — the header "Clear" button must sit fully within the viewport. Without the
        // `flex-wrap` fix the control row does not reflow and "Clear" is pushed to right ≈ 1346px at a
        // 1280px viewport (clipped off-screen). BoundingBox reports the true layout position regardless
        // of any ancestor clip, so this catches the clip that IsVisible alone would not.
        var clearBox = await page.ClearButton().BoundingBoxAsync();
        clearBox.Should().NotBeNull("the Clear button must be laid out to assert its on-screen position");
        (clearBox!.X + clearBox.Width)
            .Should()
            .BeLessThanOrEqualTo(
                ViewportWidth,
                "the header must wrap so the trailing 'Clear' button stays within the viewport, not clipped off the right edge");
        (await page.ClearButton().IsVisibleAsync())
            .Should()
            .BeTrue("the Clear button must remain visible after the header wraps");

        await session.SaveSuccessScreenshotAsync(
            "ChatClientLayout.message_list_scrolls_not_page_and_Clear_on_screen");
    }

    /// <summary>
    /// Asserts the document itself cannot scroll: (1) its scroll overflow
    /// (<c>scrollHeight - clientHeight</c>) is ≤ 1px (sub-pixel tolerance), and (2) a real scroll probe
    /// — driving <c>scrollTop</c> to a large value — is clamped back to 0 because there is nowhere to
    /// scroll. Checking both the static measurement and the live probe guards against a regression
    /// where the document is scrollable even if it currently sits at the top.
    /// </summary>
    private static async Task AssertPageDoesNotScrollAsync(IPage page, string because)
    {
        var overflow = await page.EvaluateAsync<double>(
            "() => { const se = document.scrollingElement; return se.scrollHeight - se.clientHeight; }");
        overflow.Should().BeLessThanOrEqualTo(
            1,
            $"the page must not have a spurious scrollbar ({because}) — the shell clips to 100vh and the conversation scrolls internally");

        var clampedScrollTop = await page.EvaluateAsync<double>(
            "() => { const se = document.scrollingElement; se.scrollTop = 9999; return se.scrollTop; }");
        clampedScrollTop.Should().Be(
            0,
            $"driving the page scroll offset must be clamped to 0 — the document cannot scroll ({because})");
    }
}
