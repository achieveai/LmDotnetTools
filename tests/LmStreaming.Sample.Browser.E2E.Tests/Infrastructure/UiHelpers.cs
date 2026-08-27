namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Thin wrappers around <see cref="IPage"/> that resolve DOM elements by the
/// <c>data-testid</c> attributes added to Vue components. Centralizes selector
/// strings so a rename in the UI only requires one edit here.
/// </summary>
public static class UiHelpers
{
    /// <summary>The chat input textarea. Type into it then call <c>SendButton().ClickAsync()</c>.</summary>
    public static ILocator Textarea(this IPage page)
    {
        return page.GetByTestId("chat-input-textarea");
    }

    /// <summary>Send button — becomes disabled while the send is in-flight.</summary>
    public static ILocator SendButton(this IPage page)
    {
        return page.GetByTestId("send-button");
    }

    /// <summary>Stop button — only rendered while the stream is active AND the input box is empty.</summary>
    public static ILocator StopButton(this IPage page)
    {
        return page.GetByTestId("stop-button");
    }

    /// <summary>Queue button — rendered while the stream is active AND the input box has text; queues the typed message.</summary>
    public static ILocator QueueButton(this IPage page)
    {
        return page.GetByTestId("queue-button");
    }

    /// <summary>Clear-conversation button in the header.</summary>
    public static ILocator ClearButton(this IPage page)
    {
        return page.GetByTestId("clear-button");
    }

    /// <summary>New-chat button in the sidebar.</summary>
    public static ILocator NewChatButton(this IPage page)
    {
        return page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ New Chat" });
    }

    /// <summary>All conversation list items in the sidebar (one per started conversation).</summary>
    public static ILocator ConversationItems(this IPage page)
    {
        return page.GetByTestId("conversation-item");
    }

    /// <summary>Error banner — rendered only when an error is present.</summary>
    public static ILocator ErrorBanner(this IPage page)
    {
        return page.GetByTestId("error-banner");
    }

    /// <summary>
    /// Conversation-wide token-usage banner (#196) — rendered only once cumulative total tokens &gt; 0.
    /// Text shape: <c>Total: N | In: N | Out: N [| Cached: N] [| Cache created: N]</c>.
    /// </summary>
    public static ILocator UsageBanner(this IPage page)
    {
        return page.GetByTestId("usage-banner");
    }

    /// <summary>Deferred-auth banner — rendered while the backend holds a webhook call awaiting sign-in (one per provider, see <c>data-provider-id</c>).</summary>
    public static ILocator AuthRequiredBanner(this IPage page)
    {
        return page.GetByTestId("auth-required-banner");
    }

    /// <summary>Sign-in button inside the deferred-auth banner — opens the same-origin sign-in popup.</summary>
    public static ILocator AuthSigninButton(this IPage page)
    {
        return page.GetByTestId("auth-signin-button");
    }

    /// <summary>Dismiss (✕) button inside the deferred-auth banner.</summary>
    public static ILocator AuthDismissButton(this IPage page)
    {
        return page.GetByTestId("auth-dismiss-button");
    }

    /// <summary>The scrollable message list container.</summary>
    public static ILocator MessageList(this IPage page)
    {
        return page.GetByTestId("message-list");
    }

    /// <summary>All user message groups in order of appearance.</summary>
    public static ILocator UserMessageGroups(this IPage page)
    {
        return page.GetByTestId("user-message-group");
    }

    /// <summary>All assistant message groups in order of appearance.</summary>
    public static ILocator AssistantMessageGroups(this IPage page)
    {
        return page.GetByTestId("assistant-message-group");
    }

    /// <summary>All rendered assistant text bubbles (multi-bubble per group possible).</summary>
    public static ILocator AssistantText(this IPage page)
    {
        return page.GetByTestId("assistant-text");
    }

    /// <summary>All metadata pills (one per group that produced thinking/tool-call events).</summary>
    public static ILocator MetadataPills(this IPage page)
    {
        return page.GetByTestId("metadata-pill");
    }

    /// <summary>Thinking pill items inside a metadata pill.</summary>
    public static ILocator ThinkingPills(this IPage page)
    {
        return page.GetByTestId("thinking-pill");
    }

    /// <summary>Tool-call pill items (use <c>data-tool-name</c> to identify specific tool).</summary>
    public static ILocator ToolCallPills(this IPage page)
    {
        return page.GetByTestId("tool-call-pill");
    }

    /// <summary>
    /// Out-of-band notification pills (async sub-agent completion, context discovery, ...). Distinct
    /// from a user bubble; use <c>data-notify-kind</c> to identify the specific kind.
    /// </summary>
    public static ILocator NotificationPills(this IPage page)
    {
        return page.GetByTestId("notification-pill");
    }

    /// <summary>Returns rendered tool-call names from metadata pills.</summary>
    public static Task<string[]> ToolCallNamesAsync(this IPage page)
    {
        return page.ToolCallPills()
            .EvaluateAllAsync<string[]>(
                "nodes => nodes.map(n => (n.getAttribute('data-tool-name') || n.textContent || '').trim())"
            );
    }

    /// <summary>Mode selector button in the header.</summary>
    public static ILocator ModeSelectorButton(this IPage page)
    {
        return page.GetByTestId("mode-selector-button");
    }

    /// <summary>Mode option menu item. Pass the mode id (e.g., <c>default</c>, <c>medical</c>).</summary>
    public static ILocator ModeOption(this IPage page, string modeId)
    {
        return page.GetByTestId($"mode-option-{modeId}");
    }

    /// <summary>Provider selector button in the header (a dropdown when idle; disabled while streaming).</summary>
    public static ILocator ProviderSelectorButton(this IPage page)
    {
        return page.GetByTestId("provider-selector-button");
    }

    /// <summary>The opened provider-selector dropdown menu (the scrollable, capped-height container).</summary>
    public static ILocator ProviderSelectorMenu(this IPage page)
    {
        return page.GetByTestId("provider-selector-menu");
    }

    /// <summary>Provider option menu item. Pass the provider id (e.g., <c>test</c>, <c>test-anthropic</c>).</summary>
    public static ILocator ProviderOption(this IPage page, string providerId)
    {
        return page.GetByTestId($"provider-option-{providerId}");
    }

    /// <summary>
    /// Type a message into the chat input and click the send button. Returns after the
    /// click — does not wait for the response.
    /// </summary>
    public static async Task SendMessageAsync(this IPage page, string message)
    {
        await page.Textarea().FillAsync(message);
        await page.SendButton().ClickAsync();
    }

    /// <summary>The center-pane conversation tab strip (rendered only when ≥1 sub-agent exists).</summary>
    public static ILocator ConversationTabs(this IPage page)
    {
        return page.GetByTestId("conversation-tabs");
    }

    /// <summary>A single conversation tab — pass the tab id (<c>main</c> or an agentId via <c>data-tab-id</c>).</summary>
    public static ILocator ConversationTab(this IPage page, string tabId)
    {
        return page.Locator($"[data-testid=\"conversation-tab\"][data-tab-id=\"{tabId}\"]");
    }

    /// <summary>All sub-agent tabs (every conversation tab except the always-present <c>main</c> tab).</summary>
    public static ILocator SubAgentTabs(this IPage page)
    {
        return page.Locator("[data-testid=\"conversation-tab\"]:not([data-tab-id=\"main\"])");
    }

    /// <summary>The center-pane sub-agent view (mounted only while a sub-agent tab is active).</summary>
    public static ILocator SubAgentView(this IPage page)
    {
        return page.GetByTestId("subagent-view");
    }

    /// <summary>Header button that opens the marketplace browser modal.</summary>
    public static ILocator MarketplaceButton(this IPage page)
    {
        return page.GetByTestId("marketplace-button");
    }

    /// <summary>The marketplace browser modal backdrop (present only while open).</summary>
    public static ILocator MarketplaceModal(this IPage page)
    {
        return page.GetByTestId("marketplace-modal");
    }

    /// <summary>Close (×) button inside the marketplace modal.</summary>
    public static ILocator MarketplaceModalClose(this IPage page)
    {
        return page.GetByTestId("marketplace-modal-close");
    }

    // -------------------------------------------------------------------------------------------
    // Browser-hosted client tools (#246): AskUserQuestion (QuestionRich) / NotifyClient.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A single tool-call pill matched by its raw <c>data-tool-name</c> (e.g. <c>AskUserQuestion</c>,
    /// <c>NotifyClient</c> — exact casing, not the lowercased registry key). Rich content (including
    /// <c>QuestionRich</c>) only renders once the pill is expanded — click it first.
    /// </summary>
    public static ILocator ToolCallPillByName(this IPage page, string toolName)
    {
        return page.Locator(ToolCallPillSelector(toolName));
    }

    /// <summary>
    /// The same pill, scoped to <paramref name="within"/> — use it when a view's own pills must be
    /// told apart from another's (the sub-agent focus transcript vs the parent conversation, say),
    /// since the page-wide overload matches both and so cannot prove where content rendered.
    /// </summary>
    public static ILocator ToolCallPillByName(this ILocator within, string toolName)
    {
        return within.Locator(ToolCallPillSelector(toolName));
    }

    private static string ToolCallPillSelector(string toolName)
    {
        return $"[data-testid=\"tool-call-pill\"][data-tool-name=\"{toolName}\"]";
    }

    /// <summary>The AskUserQuestion rich body root (only meaningful once its pill is expanded).</summary>
    public static ILocator QuestionRich(this IPage page)
    {
        return page.GetByTestId("question-rich");
    }

    /// <summary>The interactive, awaiting-answer question form — absent once resolved.</summary>
    public static ILocator QuestionForm(this IPage page)
    {
        return page.GetByTestId("question-form");
    }

    /// <summary>The read-only resolved-answer view — absent while the question is still pending.</summary>
    public static ILocator QuestionResolved(this IPage page)
    {
        return page.GetByTestId("question-resolved");
    }

    /// <summary>A selectable option for the currently-shown question, keyed by its (effective) <c>value</c>.</summary>
    public static ILocator QuestionOption(this IPage page, string value)
    {
        return page.GetByTestId($"question-option-{value}");
    }

    /// <summary>The "Other" toggle for the currently-shown question (only rendered when <c>allowOther</c>).</summary>
    public static ILocator QuestionOtherToggle(this IPage page)
    {
        return page.GetByTestId("question-other-toggle");
    }

    /// <summary>The free-text input for an "Other" answer (only rendered once the Other toggle is active).</summary>
    public static ILocator QuestionOtherText(this IPage page)
    {
        return page.GetByTestId("question-other-text");
    }

    /// <summary>Skip button for the currently-shown question.</summary>
    public static ILocator QuestionSkipButton(this IPage page)
    {
        return page.GetByTestId("question-skip");
    }

    /// <summary>Submit button — only rendered on the last question of the batch.</summary>
    public static ILocator QuestionSubmitButton(this IPage page)
    {
        return page.GetByTestId("question-submit");
    }

    /// <summary>Next button — only rendered while earlier questions remain in a multi-question batch.</summary>
    public static ILocator QuestionNextButton(this IPage page)
    {
        return page.GetByTestId("question-next");
    }

    /// <summary>Error banner shown when a <c>client_tool_result</c> submission is rejected/fails.</summary>
    public static ILocator QuestionSubmitError(this IPage page)
    {
        return page.GetByTestId("question-submit-error");
    }

    // -------------------------------------------------------------------------------------------
    // Conversation sidebar paging + sort. The sidebar pages by infinite scroll: `.sidebar-content`
    // is the scroll container whose `scroll` handler asks for the next page, so a test that wants a
    // further page must drive THAT element rather than the window.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The sidebar's scrollable list container — the element whose <c>scroll</c> event asks for the
    /// next conversation page. Located by class because it carries no testid of its own; the row
    /// testids below are the stable surface.
    /// </summary>
    public static ILocator SidebarContent(this IPage page)
    {
        return page.Locator(".sidebar-content");
    }

    /// <summary>Sort-mode dropdown button in the sidebar.</summary>
    public static ILocator SortModeButton(this IPage page)
    {
        return page.GetByTestId("sort-mode-button");
    }

    /// <summary>
    /// A sort-mode option in the opened dropdown. Pass the wire sort id (<c>lastUsed</c> /
    /// <c>created</c>) — the same value that travels as the <c>sort</c> query parameter.
    /// </summary>
    public static ILocator SortModeOption(this IPage page, string sortMode)
    {
        return page.GetByTestId($"sort-mode-option-{sortMode}");
    }

    /// <summary>
    /// The "Loading more..." row. Rendered ONLY while a further page is in flight — once the list is
    /// exhausted the loader stops firing, so its absence after a scroll is the observable form of
    /// "there is nothing left to fetch".
    /// </summary>
    public static ILocator ConversationsLoadingMore(this IPage page)
    {
        return page.GetByTestId("conversations-loading-more");
    }

    /// <summary>Rendered sidebar thread ids, in display order (from <c>data-thread-id</c>).</summary>
    public static Task<string[]> ConversationThreadIdsAsync(this IPage page)
    {
        return page.ConversationItems()
            .EvaluateAllAsync<string[]>(
                "nodes => nodes.map(n => n.getAttribute('data-thread-id') || '')"
            );
    }

    /// <summary>Rendered sidebar titles, in display order.</summary>
    public static Task<string[]> ConversationTitlesAsync(this IPage page)
    {
        return page.ConversationItems()
            .EvaluateAllAsync<string[]>(
                "nodes => nodes.map(n => (n.querySelector('.conversation-title')?.textContent || '').trim())"
            );
    }

    /// <summary>
    /// Scrolls the sidebar list to its end, which is what the infinite-scroll handler reads as
    /// "ask for the next page".
    /// </summary>
    public static Task ScrollSidebarToEndAsync(this IPage page)
    {
        return page.SidebarContent().EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
    }

    /// <summary>
    /// Scrolls the sidebar to its end and waits until the list has grown to at least
    /// <paramref name="expectedCount"/> rows. Returns the row count actually reached.
    /// </summary>
    public static async Task<int> LoadMoreConversationsAsync(
        this IPage page,
        int expectedCount,
        float timeoutMs = 15_000
    )
    {
        await page.ScrollSidebarToEndAsync();
        return await page.ConversationItems().WaitForCountAtLeastAsync(expectedCount, timeoutMs);
    }

    /// <summary>
    /// Waits until the sidebar holds EXACTLY <paramref name="expectedCount"/> rows. Exactness is the
    /// point at a page boundary: "at least 30" cannot tell one page from two concatenated ones.
    /// </summary>
    public static Task WaitForConversationCountAsync(
        this IPage page,
        int expectedCount,
        float timeoutMs = 15_000
    )
    {
        return Assertions
            .Expect(page.ConversationItems())
            .ToHaveCountAsync(expectedCount, new LocatorAssertionsToHaveCountOptions { Timeout = timeoutMs });
    }

    /// <summary>
    /// Chooses a sidebar sort mode through the real dropdown (open, then click the option) rather
    /// than by poking the composable, so the assertion covers the wiring the user actually drives.
    /// </summary>
    public static async Task SelectSortModeAsync(this IPage page, string sortMode)
    {
        await page.SortModeButton().ClickAsync();
        await page.SortModeOption(sortMode).ClickAsync();
    }
}
