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

        var prose = string.Join(
            " ",
            Enumerable.Repeat(
                "A production answer should use the available transcript width while keeping each prose line comfortable to read.",
                160
            )
        );
        var wideCodeLine = $"const payload = '{new string('x', 320)}';";
        var tableHeaders = string.Join(" | ", Enumerable.Range(1, 10).Select(i => $"Column {i} heading"));
        var tableDivider = string.Join(" | ", Enumerable.Repeat("---", 10));
        var tableValues = string.Join(" | ", Enumerable.Range(1, 10).Select(i => $"value-{i}-{new string('y', 24)}"));
        var finalAnswer =
            $"{prose}\n\n```text\n{wideCodeLine}\n```\n\n| {tableHeaders} |\n| {tableDivider} |\n| {tableValues} |";
        var responder = role.Turn(t => t.Text(finalAnswer)).Build();

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

        // Menu items are roving-focus targets rather than independent tab stops. Tab closes the
        // menu and continues from More to the composer textarea.
        await page.HeaderActionsMenuButton().PressAsync("ArrowDown");
        await page.MarketplaceButton().PressAsync("Tab");
        await Assertions.Expect(page.HeaderActionsMenu()).ToHaveCountAsync(0);
        await Assertions.Expect(page.Textarea()).ToBeFocusedAsync();

        await page.HeaderActionsMenuButton().FocusAsync();
        await page.HeaderActionsMenuButton().PressAsync("ArrowDown");
        await page.MarketplaceButton().PressAsync("Shift+Tab");
        await Assertions.Expect(page.HeaderActionsMenu()).ToHaveCountAsync(0);

        // Mode now lives at the bottom-left of the root composer. Exercise the real menu option,
        // rather than checking only its DOM bounds: an overflow-clipped upward menu can report as
        // visible while still being impossible for a user to click.
        await page.ModeSelectorButton().FocusAsync();
        await page.ModeSelectorButton().PressAsync("Enter");
        await page.ModeOption("default").ClickAsync();
        await Assertions.Expect(page.ModeOption("default")).ToHaveCountAsync(0);

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
            (520, true),
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
                    var sidebarToggle = page.GetByTestId("sidebar-toggle");
                    await sidebarToggle.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }
                await Assertions.Expect(sidebar).ToHaveCSSAsync("width", sidebarCollapsed ? "0px" : "280px");
            }
            var geometryLabel = $"{width}px with sidebar {(sidebarCollapsed ? "collapsed" : "expanded")}";

            var headerBox = await page.GetByTestId("app-header").BoundingBoxAsync();
            var moreBox = await page.HeaderActionsMenuButton().BoundingBoxAsync();
            var launcherBox = await page.ConversationInspectorLauncher().BoundingBoxAsync();
            var sidebarToggleBox = await page.GetByTestId("sidebar-toggle").BoundingBoxAsync();
            var titleBox = await page.Locator(".app-header h1").BoundingBoxAsync();
            var viewPreference = page.Locator(".view-preference");
            var viewPreferenceBox = await viewPreference.BoundingBoxAsync();
            headerBox.Should().NotBeNull("the full-width app header must have a measurable layout box");
            moreBox.Should().NotBeNull("More must remain visible in the header context row");
            launcherBox.Should().NotBeNull("the closed inspector launcher must remain visible in the app header");
            sidebarToggleBox.Should().NotBeNull("the app header must expose the sidebar control");
            titleBox.Should().NotBeNull("the conversation title must have a measurable layout box");
            viewPreferenceBox.Should().NotBeNull("the view switch must have a measurable layout box");

            headerBox!
                .X.Should()
                .BeApproximately(0, 1, $"the app header must start at the viewport edge at {geometryLabel}");
            headerBox
                .Width.Should()
                .BeApproximately(width, 1, $"the app header must span the viewport at {geometryLabel}");
            headerBox
                .Y.Should()
                .BeApproximately(0, 1, $"the app header must stay at the viewport top at {geometryLabel}");
            (launcherBox!.X + launcherBox.Width)
                .Should()
                .BeLessThanOrEqualTo(
                    headerBox.X + headerBox.Width,
                    $"the inspector control must stay in the header at {geometryLabel}"
                );
            AssertRectanglesDoNotOverlap(sidebarToggleBox!, titleBox!, $"sidebar control and title at {geometryLabel}");
            AssertRectanglesDoNotOverlap(launcherBox, titleBox!, $"launcher and title at {geometryLabel}");
            AssertRectanglesDoNotOverlap(
                launcherBox,
                viewPreferenceBox!,
                $"launcher and view switch at {geometryLabel}"
            );
            AssertRectanglesDoNotOverlap(
                sidebarToggleBox!,
                viewPreferenceBox!,
                $"sidebar control and view switch at {geometryLabel}"
            );

            var shellBodyBox = await page.GetByTestId("shell-body").BoundingBoxAsync();
            var mainBox = await page.Locator(".chat-main").BoundingBoxAsync();
            shellBodyBox.Should().NotBeNull("the body below the app header must be measurable");
            mainBox.Should().NotBeNull("the transcript panel must be measurable");
            Math.Abs(shellBodyBox!.Y - (headerBox.Y + headerBox.Height))
                .Should()
                .BeLessThanOrEqualTo(1, $"the shell body must begin below the header at {geometryLabel}");
            if (sidebarCollapsed)
            {
                Math.Abs(mainBox!.X - shellBodyBox.X)
                    .Should()
                    .BeLessThanOrEqualTo(
                        1,
                        $"a collapsed sidebar must release its horizontal space at {geometryLabel}"
                    );
            }

            if (width is 1597 or ViewportWidth or 768 or 390)
            {
                var messageList = page.MessageList();
                var messageListBox = await messageList.BoundingBoxAsync();
                var messageInnerWidth = await messageList.EvaluateAsync<double>(
                    "el => el.clientWidth - parseFloat(getComputedStyle(el).paddingLeft) - parseFloat(getComputedStyle(el).paddingRight)"
                );
                var messagePaddingRight = await messageList.EvaluateAsync<double>(
                    "el => parseFloat(getComputedStyle(el).paddingRight)"
                );
                var assistantWrapperBox = await page.Locator(".assistant-message-wrapper").Last.BoundingBoxAsync();
                var assistantContentBox = await page.Locator(".assistant-content").Last.BoundingBoxAsync();
                var userWrapperBox = await page.Locator(".user-message-wrapper").Last.BoundingBoxAsync();
                var textRow = page.Locator(".text-bubble-row").Last;
                var textRowBox = await textRow.BoundingBoxAsync();
                var assistantText = page.AssistantText().Last;
                var responseContentWidth = await assistantText.EvaluateAsync<double>(
                    "el => el.clientWidth - parseFloat(getComputedStyle(el).paddingLeft) - parseFloat(getComputedStyle(el).paddingRight)"
                );
                var proseBlock = assistantText.Locator("p").First;
                var proseBox = await proseBlock.BoundingBoxAsync();
                var renderedTable = assistantText.Locator("table").First;
                var renderedColumns = renderedTable.Locator("thead th");
                var tableOverflow = await renderedTable.EvaluateAsync<double>("el => el.scrollWidth - el.clientWidth");

                messageListBox.Should().NotBeNull("the message list must have a measurable content box");
                assistantWrapperBox.Should().NotBeNull("the assistant turn must have a measurable wrapper");
                assistantContentBox.Should().NotBeNull("the assistant turn must have a measurable content column");
                userWrapperBox.Should().NotBeNull("the human turn must have a measurable wrapper");
                textRowBox.Should().NotBeNull("the assistant prose row must have a measurable box");
                proseBox.Should().NotBeNull("the assistant answer must render a measurable prose block");
                (await renderedColumns.CountAsync())
                    .Should()
                    .Be(10, $"the wide markdown table must render all ten actual columns at {geometryLabel}");

                assistantWrapperBox!
                    .Width.Should()
                    .BeGreaterThanOrEqualTo(
                        (float)(messageInnerWidth * 0.95),
                        $"assistant turns should use nearly all transcript width at {geometryLabel}"
                    );
                textRowBox!
                    .Width.Should()
                    .BeGreaterThanOrEqualTo(
                        assistantContentBox!.Width * 0.95f,
                        $"the assistant text row should carry the full turn width at {geometryLabel}"
                    );
                proseBox!
                    .Width.Should()
                    .BeGreaterThanOrEqualTo(
                        (float)(responseContentWidth * 0.99),
                        $"assistant prose should use the full response-content width at {geometryLabel}"
                    );
                proseBox
                    .Width.Should()
                    .BeLessThanOrEqualTo(
                        (float)(responseContentWidth + 1),
                        $"assistant prose must not overflow the response-content box at {geometryLabel}"
                    );
                tableOverflow
                    .Should()
                    .BeGreaterThan(
                        0,
                        $"the ten rendered table columns should scroll inside the response instead of widening it at {geometryLabel}"
                    );

                var messageInnerRight = messageListBox!.X + messageListBox.Width - messagePaddingRight;
                Math.Abs(userWrapperBox!.X + userWrapperBox.Width - messageInnerRight)
                    .Should()
                    .BeLessThanOrEqualTo(1, $"the human turn must stay right-aligned at {geometryLabel}");
                var userMaxRatio = width <= 600 ? 0.92 : 0.70;
                userWrapperBox
                    .Width.Should()
                    .BeLessThanOrEqualTo(
                        (float)((messageInnerWidth * userMaxRatio) + 1),
                        $"the human turn must keep its compact treatment at {geometryLabel}"
                    );

                foreach (
                    var (overflowingContent, contentName) in new[]
                    {
                        (page.AssistantText().Last.Locator("pre"), "code block"),
                        (page.AssistantText().Last.Locator("table"), "table"),
                    }
                )
                {
                    var contentBox = await overflowingContent.BoundingBoxAsync();
                    contentBox.Should().NotBeNull($"the scripted answer must render its {contentName}");
                    contentBox!
                        .X.Should()
                        .BeGreaterThanOrEqualTo(textRowBox.X, $"the {contentName} must stay inside the prose row");
                    (contentBox.X + contentBox.Width)
                        .Should()
                        .BeLessThanOrEqualTo(
                            textRowBox.X + textRowBox.Width + 1,
                            $"the {contentName} must not widen the assistant turn at {geometryLabel}"
                        );
                    var horizontalOverflow = await overflowingContent.EvaluateAsync<double>(
                        "el => el.scrollWidth - el.clientWidth"
                    );
                    horizontalOverflow
                        .Should()
                        .BeGreaterThan(
                            0,
                            $"the deliberately wide {contentName} must scroll locally at {geometryLabel}"
                        );
                }
            }

            await page.ConversationInspectorLauncher().ClickAsync();
            await Assertions.Expect(page.ConversationInspector()).ToBeVisibleAsync();
            await Assertions.Expect(page.ConversationInspectorLauncher()).ToBeHiddenAsync();
            var closeButton = page.ConversationInspector()
                .GetByRole(AriaRole.Button, new() { Name = "Close Work and agents" });
            var inspectorBox = await page.ConversationInspector().BoundingBoxAsync();
            var closeBox = await closeButton.BoundingBoxAsync();
            var inspectorTabsBox = await page.ConversationInspector().GetByRole(AriaRole.Tablist).BoundingBoxAsync();
            inspectorBox.Should().NotBeNull("the open inspector must have a measurable panel");
            closeBox.Should().NotBeNull("the open inspector must expose a measurable close control");
            inspectorTabsBox.Should().NotBeNull("the inspector tabs must have a measurable layout box");
            if (width > 1100)
            {
                Math.Abs(inspectorBox!.Y - (headerBox.Y + headerBox.Height))
                    .Should()
                    .BeLessThanOrEqualTo(1, $"the docked inspector must begin below the app header at {geometryLabel}");
                (inspectorBox.Y + inspectorBox.Height)
                    .Should()
                    .BeLessThanOrEqualTo(
                        ViewportHeight + 1,
                        $"the docked inspector must end inside the viewport at {geometryLabel}"
                    );
            }
            else
            {
                inspectorBox!
                    .X.Should()
                    .BeGreaterThanOrEqualTo(0, $"the drawer must stay in the viewport at {geometryLabel}");
                inspectorBox
                    .Y.Should()
                    .BeApproximately(0, 1, $"the narrow drawer must cover from the viewport top at {geometryLabel}");
                (inspectorBox.X + inspectorBox.Width)
                    .Should()
                    .BeLessThanOrEqualTo(width + 1, $"the drawer must stay in the viewport at {geometryLabel}");
            }
            Math.Abs(closeBox!.X - launcherBox.X)
                .Should()
                .BeLessThanOrEqualTo(
                    1,
                    $"opening the inspector must preserve the top-right control x at {geometryLabel}"
                );
            Math.Abs(closeBox.Y - launcherBox.Y)
                .Should()
                .BeLessThanOrEqualTo(
                    1,
                    $"opening the inspector must preserve the top-right control y at {geometryLabel}"
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
            if (width <= 1100)
            {
                (closeBox.Y + closeBox.Height)
                    .Should()
                    .BeLessThanOrEqualTo(
                        inspectorTabsBox!.Y,
                        $"the narrow drawer close control must not overlap its tabs at {geometryLabel}"
                    );
            }
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
