using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Drives the workspace file browser (WI #195) end-to-end through the real chat client against the real
/// backend controller. With no sandbox gateway running (the deterministic browser suite has none) a plain
/// conversation has no established sandbox binding, so the browser renders its structured "no session yet"
/// state — this scenario proves the whole wiring: the gated header button, the modal open, the real
/// <c>GET /api/conversations/{threadId}/files</c> call, the no-session render, and modal close. The
/// listing/preview/upload/delete internals are covered exhaustively by the client vitest suite and the C#
/// FileBrowserController tests; the real-gateway happy path belongs to the gated sandbox E2E family.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class FileBrowserTests
{
    private readonly PlaywrightFixture _fixture;

    public FileBrowserTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task File_browser_button_opens_modal_and_shows_no_session_state()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("ok"))
            .Build();
        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;

        // Start a conversation (New Chat sets the active thread id the Files button is gated on) and send a
        // message so the thread is persisted. Wait for the assistant bubble so the full turn has completed.
        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("hello");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().WaitForCountAtLeastAsync(1);

        // ClickAsync auto-waits for the button to become actionable (enabled once a conversation is active).
        await page.GetByTestId("file-browser-button").ClickAsync();

        await page.GetByTestId("file-browser-modal")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // A non-workspace conversation has no established sandbox binding, so the browser shows the
        // structured no-session state rather than an error or a hang.
        await page.GetByTestId("file-browser-no-session")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // Closing the modal removes it from the DOM.
        await page.GetByTestId("file-browser-modal-close").ClickAsync();
        await page.GetByTestId("file-browser-modal")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await session.SaveSuccessScreenshotAsync("FileBrowser.No_session_state");
    }
}
