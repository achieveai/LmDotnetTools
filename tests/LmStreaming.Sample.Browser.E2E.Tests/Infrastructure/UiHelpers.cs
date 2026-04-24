namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Thin wrappers around <see cref="IPage"/> that resolve DOM elements by the
/// <c>data-testid</c> attributes added to Vue components. Centralizes selector
/// strings so a rename in the UI only requires one edit here.
/// </summary>
public static class UiHelpers
{
    /// <summary>The chat input textarea. Type into it then call <c>SendButton().ClickAsync()</c>.</summary>
    public static ILocator Textarea(this IPage page) => page.GetByTestId("chat-input-textarea");

    /// <summary>Send button — becomes disabled while the send is in-flight.</summary>
    public static ILocator SendButton(this IPage page) => page.GetByTestId("send-button");

    /// <summary>Stop button — only rendered while the stream is active.</summary>
    public static ILocator StopButton(this IPage page) => page.GetByTestId("stop-button");

    /// <summary>Clear-conversation button in the header.</summary>
    public static ILocator ClearButton(this IPage page) => page.GetByTestId("clear-button");

    /// <summary>Error banner — rendered only when an error is present.</summary>
    public static ILocator ErrorBanner(this IPage page) => page.GetByTestId("error-banner");

    /// <summary>The scrollable message list container.</summary>
    public static ILocator MessageList(this IPage page) => page.GetByTestId("message-list");

    /// <summary>All user message groups in order of appearance.</summary>
    public static ILocator UserMessageGroups(this IPage page) => page.GetByTestId("user-message-group");

    /// <summary>All assistant message groups in order of appearance.</summary>
    public static ILocator AssistantMessageGroups(this IPage page) => page.GetByTestId("assistant-message-group");

    /// <summary>All rendered assistant text bubbles (multi-bubble per group possible).</summary>
    public static ILocator AssistantText(this IPage page) => page.GetByTestId("assistant-text");

    /// <summary>All metadata pills (one per group that produced thinking/tool-call events).</summary>
    public static ILocator MetadataPills(this IPage page) => page.GetByTestId("metadata-pill");

    /// <summary>Thinking pill items inside a metadata pill.</summary>
    public static ILocator ThinkingPills(this IPage page) => page.GetByTestId("thinking-pill");

    /// <summary>Tool-call pill items (use <c>data-tool-name</c> to identify specific tool).</summary>
    public static ILocator ToolCallPills(this IPage page) => page.GetByTestId("tool-call-pill");

    /// <summary>Mode selector button in the header.</summary>
    public static ILocator ModeSelectorButton(this IPage page) => page.GetByTestId("mode-selector-button");

    /// <summary>Mode option menu item. Pass the mode id (e.g., <c>default</c>, <c>medical</c>).</summary>
    public static ILocator ModeOption(this IPage page, string modeId) => page.GetByTestId($"mode-option-{modeId}");

    /// <summary>
    /// Type a message into the chat input and click the send button. Returns after the
    /// click — does not wait for the response.
    /// </summary>
    public static async Task SendMessageAsync(this IPage page, string message)
    {
        await page.Textarea().FillAsync(message);
        await page.SendButton().ClickAsync();
    }
}
