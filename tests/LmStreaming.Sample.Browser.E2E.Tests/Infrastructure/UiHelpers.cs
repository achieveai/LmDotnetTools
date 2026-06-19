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

    /// <summary>Stop button — only rendered while the stream is active.</summary>
    public static ILocator StopButton(this IPage page)
    {
        return page.GetByTestId("stop-button");
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

    /// <summary>Error banner — rendered only when an error is present.</summary>
    public static ILocator ErrorBanner(this IPage page)
    {
        return page.GetByTestId("error-banner");
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

    /// <summary>
    /// Type a message into the chat input and click the send button. Returns after the
    /// click — does not wait for the response.
    /// </summary>
    public static async Task SendMessageAsync(this IPage page, string message)
    {
        await page.Textarea().FillAsync(message);
        await page.SendButton().ClickAsync();
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
}
