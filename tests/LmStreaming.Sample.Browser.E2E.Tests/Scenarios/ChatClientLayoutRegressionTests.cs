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
        var role = ScriptedSseResponder.New().ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"));

        for (var i = 0; i < ToolCallCount; i++)
        {
            var n = i;
            role = role.Turn(t =>
                t.ToolCall(
                    "calculate",
                    new
                    {
                        a = n,
                        operation = "add",
                        b = 1,
                    }
                )
            );
        }

        var responder = role.Turn(t => t.TextLen(6_000)).Build();

        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;
        await page.SelectDeveloperViewAsync();

        // Pin the viewport so both regressions are deterministic across machines (see field docs).
        await page.SetViewportSizeAsync(ViewportWidth, ViewportHeight);

        // Before a conversation exists, the menu remains keyboard-operable while conversation-bound
        // actions are truthfully disabled. ArrowDown opens at the first enabled item; Escape restores
        // focus to the trigger.
        await page.HeaderActionsMenuButton().FocusAsync();
        await page.HeaderActionsMenuButton().PressAsync("ArrowDown");
        await Assertions.Expect(page.HeaderActionsMenu()).ToBeVisibleAsync();
        await Assertions.Expect(page.MarketplaceButton()).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByTestId("file-browser-button")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("share-button")).ToBeDisabledAsync();
        await page.MarketplaceButton().PressAsync("ArrowDown");
        await Assertions.Expect(page.GetByTestId("egress-auth-button")).ToBeFocusedAsync();
        await page.GetByTestId("egress-auth-button").PressAsync("Escape");
        await Assertions.Expect(page.HeaderActionsMenuButton()).ToBeFocusedAsync();

        // Menu items are roving-focus targets rather than independent tab stops. Tab and Shift+Tab
        // close the menu and continue from More to the natural controls on either side.
        await page.HeaderActionsMenuButton().PressAsync("ArrowDown");
        await page.MarketplaceButton().PressAsync("Tab");
        await Assertions.Expect(page.HeaderActionsMenu()).ToHaveCountAsync(0);
        await Assertions.Expect(page.Textarea()).ToBeFocusedAsync();

        await page.HeaderActionsMenuButton().FocusAsync();
        await page.HeaderActionsMenuButton().PressAsync("ArrowDown");
        await page.MarketplaceButton().PressAsync("Shift+Tab");
        await Assertions.Expect(page.HeaderActionsMenu()).ToHaveCountAsync(0);
        await Assertions.Expect(page.ModeSelectorButton()).ToBeFocusedAsync();

        // Open a fresh conversation, then run the scripted plan to completion so every pill and the
        // long final text are rendered before we measure layout.
        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("run twelve calculations, then write a long summary");
        await page.WaitForStreamActiveAsync();
        await page.OpenHeaderActionsMenuAsync();
        await Assertions.Expect(page.ClearButton()).ToBeDisabledAsync();
        await page.MarketplaceButton().PressAsync("Escape");
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
                "the conversation must overflow and scroll INSIDE .message-list (otherwise 'the page does not scroll' is vacuous)"
            );

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
        (await pillHeaders.CountAsync())
            .Should()
            .Be(ToolCallCount, "every collapsed tool pill exposes exactly one header toggle to expand");
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

        // More belongs to the centered header's context row, while the inspector toggle belongs to
        // the app chrome at the top-right. Prove both positions in a real renderer from phone width
        // through the user's 1597px desktop viewport. Opening the inspector replaces its launcher
        // with an equal-size close control at the same screen coordinates, so the affordance does
        // not jump as the panel changes the available transcript width.
        var geometryCases = new (int Width, bool SidebarCollapsed)[]
        {
            (1597, false),
            (ViewportWidth, false),
            (1261, false),
            (1261, true),
            (1259, false),
            (1101, false),
            (946, false),
            (768, true),
            (390, true),
        };
        foreach (var (width, sidebarCollapsed) in geometryCases)
        {
            await page.SetViewportSizeAsync(width, ViewportHeight);
            var sidebar = page.Locator(".conversation-sidebar");
            if (width <= 768)
            {
                await Assertions
                    .Expect(sidebar)
                    .ToHaveAttributeAsync(
                        "class",
                        new System.Text.RegularExpressions.Regex("(?:^|\\s)collapsed(?:\\s|$)")
                    );
                await Assertions.Expect(sidebar).ToHaveCSSAsync("width", "0px");
            }
            else
            {
                var sidebarIsCollapsed = await sidebar.EvaluateAsync<bool>("el => el.classList.contains('collapsed')");
                if (sidebarIsCollapsed != sidebarCollapsed)
                {
                    var sidebarToggle = sidebarIsCollapsed
                        ? page.Locator(".chat-header .menu-btn")
                        : sidebar.Locator(".toggle-btn");
                    await sidebarToggle.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }
                await Assertions.Expect(sidebar).ToHaveCSSAsync("width", sidebarCollapsed ? "48px" : "280px");
            }
            var geometryLabel = $"{width}px with sidebar {(sidebarCollapsed ? "collapsed" : "expanded")}";

            var headerBox = await page.Locator(".chat-header").BoundingBoxAsync();
            var moreBox = await page.HeaderActionsMenuButton().BoundingBoxAsync();
            var launcherBox = await page.ConversationInspectorLauncher().BoundingBoxAsync();
            var titleBox = await page.Locator(".chat-header h1").BoundingBoxAsync();
            var viewPreference = page.Locator(".view-preference");
            var viewPreferenceBox = await viewPreference.BoundingBoxAsync();
            var headerPaddingRight = await page.Locator(".chat-header")
                .EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).paddingRight)");
            var moreFontSize = await page.HeaderActionsMenuButton()
                .EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)");
            var viewFontSize = await viewPreference
                .Locator("span")
                .First.EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)");

            headerBox.Should().NotBeNull("the centered transcript header must have a measurable layout box");
            moreBox.Should().NotBeNull("More must remain visible in the header context row");
            launcherBox.Should().NotBeNull("the closed inspector launcher must remain visible at app top-right");
            titleBox.Should().NotBeNull("the conversation title must have a measurable layout box");
            viewPreferenceBox.Should().NotBeNull("the view switch must have a measurable layout box");

            Math.Abs(moreBox!.X + moreBox.Width - (headerBox!.X + headerBox.Width - headerPaddingRight))
                .Should()
                .BeLessThanOrEqualTo(1, $"More must align with the header's inner right edge at {geometryLabel}");
            Math.Abs(viewPreferenceBox!.X + viewPreferenceBox.Width - (moreBox.X + moreBox.Width))
                .Should()
                .BeLessThanOrEqualTo(1, $"the view switch and More must share a right edge at {geometryLabel}");
            Math.Abs(viewPreferenceBox.Height - moreBox.Height)
                .Should()
                .BeLessThanOrEqualTo(1, $"the view switch and More must have equal height at {geometryLabel}");
            Math.Abs(viewFontSize - moreFontSize)
                .Should()
                .BeLessThanOrEqualTo(0.1, $"the view switch and More must use equal type size at {geometryLabel}");
            (width - (launcherBox!.X + launcherBox.Width))
                .Should()
                .BeInRange(0, 20, $"the inspector launcher must stay at the app's top-right edge at {geometryLabel}");
            launcherBox
                .Y.Should()
                .BeInRange(0, 20, $"the inspector launcher must stay at the app top at {geometryLabel}");
            AssertRectanglesDoNotOverlap(launcherBox, titleBox!, $"launcher and title at {geometryLabel}");
            AssertRectanglesDoNotOverlap(
                launcherBox,
                viewPreferenceBox,
                $"launcher and view switch at {geometryLabel}"
            );

            await page.ConversationInspectorLauncher().ClickAsync();
            await Assertions.Expect(page.ConversationInspector()).ToBeVisibleAsync();
            await Assertions.Expect(page.ConversationInspectorLauncher()).ToBeHiddenAsync();
            var closeButton = page.ConversationInspector()
                .GetByRole(AriaRole.Button, new() { Name = "Close Work and agents" });
            var closeBox = await closeButton.BoundingBoxAsync();
            var inspectorTabsBox = await page.ConversationInspector().GetByRole(AriaRole.Tablist).BoundingBoxAsync();
            closeBox.Should().NotBeNull("the open inspector must expose a measurable close control");
            inspectorTabsBox.Should().NotBeNull("the inspector tabs must have a measurable layout box");
            Math.Abs(closeBox!.X - launcherBox.X)
                .Should()
                .BeLessThanOrEqualTo(
                    1,
                    $"opening the inspector must not move the top-right control at {geometryLabel}"
                );
            Math.Abs(closeBox.Y - launcherBox.Y)
                .Should()
                .BeLessThanOrEqualTo(
                    1,
                    $"opening the inspector must not move the top-right control at {geometryLabel}"
                );
            Math.Abs(closeBox.Width - launcherBox.Width)
                .Should()
                .BeLessThanOrEqualTo(1, $"open and closed inspector controls must have equal width at {geometryLabel}");
            Math.Abs(closeBox.Height - launcherBox.Height)
                .Should()
                .BeLessThanOrEqualTo(
                    1,
                    $"open and closed inspector controls must have equal height at {geometryLabel}"
                );
            (closeBox.Y + closeBox.Height)
                .Should()
                .BeLessThanOrEqualTo(
                    inspectorTabsBox!.Y,
                    $"the fixed-size inspector close control must not overlap its tabs at {geometryLabel}"
                );
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.ConversationInspector()).ToHaveCountAsync(0);
            await Assertions.Expect(page.ConversationInspectorLauncher()).ToBeFocusedAsync();

            await page.HeaderActionsMenuButton().FocusAsync();
            await page.HeaderActionsMenuButton().PressAsync("ArrowDown");
            await Assertions.Expect(page.MarketplaceButton()).ToBeFocusedAsync();
            await page.MarketplaceButton().PressAsync("End");
            await Assertions.Expect(page.ClearButton()).ToBeFocusedAsync();

            var menuBox = await page.HeaderActionsMenu().BoundingBoxAsync();
            menuBox.Should().NotBeNull("the open More menu must have a measurable layout box");
            menuBox!.X.Should().BeGreaterThanOrEqualTo(0, $"the More menu must stay inside {geometryLabel}");
            (menuBox.X + menuBox.Width)
                .Should()
                .BeLessThanOrEqualTo(width, $"the More menu must stay inside {geometryLabel}");
            await AssertPageDoesNotScrollAsync(page, $"with the More menu open at {geometryLabel}");

            await page.ClearButton().PressAsync("Escape");
            await Assertions.Expect(page.HeaderActionsMenuButton()).ToBeFocusedAsync();
        }

        // A real menu action closes the menu and preserves the existing modal behavior.
        await page.OpenHeaderActionsMenuAsync();
        await page.MarketplaceButton().ClickAsync();
        await Assertions.Expect(page.MarketplaceModal()).ToBeVisibleAsync();
        await page.MarketplaceModalClose().ClickAsync();
        await Assertions.Expect(page.MarketplaceModal()).ToHaveCountAsync(0);

        await session.SaveSuccessScreenshotAsync("ChatClientLayout.message_list_scrolls_not_page_and_Clear_on_screen");
    }

    private static void AssertRectanglesDoNotOverlap(
        LocatorBoundingBoxResult first,
        LocatorBoundingBoxResult second,
        string because
    )
    {
        var overlapWidth = Math.Max(
            0,
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X)
        );
        var overlapHeight = Math.Max(
            0,
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y)
        );
        (overlapWidth * overlapHeight).Should().Be(0, $"{because} must not overlap in two dimensions");
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
            "() => { const se = document.scrollingElement; return se.scrollHeight - se.clientHeight; }"
        );
        overflow
            .Should()
            .BeLessThanOrEqualTo(
                1,
                $"the page must not have a spurious scrollbar ({because}) — the shell clips to 100vh and the conversation scrolls internally"
            );

        var clampedScrollTop = await page.EvaluateAsync<double>(
            "() => { const se = document.scrollingElement; se.scrollTop = 9999; return se.scrollTop; }"
        );
        clampedScrollTop
            .Should()
            .Be(0, $"driving the page scroll offset must be clamped to 0 — the document cannot scroll ({because})");
    }
}
