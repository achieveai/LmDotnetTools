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
}
