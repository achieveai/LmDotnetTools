using System.Text.Json;
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

    /// <summary>
    /// Fixed-height list (WI #214): the browser's file panel must occupy a STABLE height whether it holds
    /// few or many files (no "jumping"), scrolling internally when the list overflows. The deterministic
    /// suite has no sandbox gateway, so this stubs the file REST API at the HTTP boundary (via
    /// <c>page.RouteAsync</c>) to render real populated listings through the real Vue component — the
    /// controller/sandbox path is proven separately by the FileBrowserController unit tests. Also captures
    /// the New-folder dialog. Screenshots land in <c>.logs/e2e-screenshots/</c> as reviewer-facing proof.
    /// </summary>
    [Fact]
    public async Task File_browser_list_keeps_stable_height_across_file_counts_and_shows_new_folder_dialog()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("ok"))
            .Build();
        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;

        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("hello");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().WaitForCountAtLeastAsync(1);

        // Stub ONLY the file-browser REST surface; every other API call falls through untouched. `fileCount`
        // is captured (not a value) so bumping it below changes what the next listing returns.
        var fileCount = 4;
        await page.RouteAsync(
            url => url.Contains("/api/conversations/", StringComparison.Ordinal) && url.Contains("/files", StringComparison.Ordinal),
            async route =>
            {
                var request = route.Request;
                var url = request.Url;
                var method = request.Method;

                if (method == "GET" && !url.Contains("/download", StringComparison.Ordinal) && !url.Contains("/preview", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = ListingJson(fileCount) });
                }
                else if (method == "POST" && url.Contains("/files/directory", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = "{\"path\":\"reports\"}" });
                }
                else if (method == "POST")
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = "{\"name\":\"note.txt\",\"size\":12}" });
                }
                else if (method == "DELETE")
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
                }
                else
                {
                    await route.ContinueAsync();
                }
            }
        );

        // --- Few files: the panel renders without an internal scrollbar ---
        await page.GetByTestId("file-browser-button").ClickAsync();
        await page.GetByTestId("file-browser-modal").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var list = page.GetByTestId("file-browser-list");
        await list.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        var fewClientHeight = await list.EvaluateAsync<double>("el => el.clientHeight");
        var fewScrollHeight = await list.EvaluateAsync<double>("el => el.scrollHeight");
        Assert.False(fewScrollHeight > fewClientHeight + 4, "A short listing must NOT overflow — the panel should size to its fixed height, not shrink to the content.");
        await session.SaveSuccessScreenshotAsync("FileBrowser.FixedHeight_FewFiles");

        // --- New-folder dialog ---
        await page.GetByTestId("file-browser-new-folder").ClickAsync();
        await page.GetByTestId("file-browser-new-folder-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-browser-new-folder-input").FillAsync("reports");
        await session.SaveSuccessScreenshotAsync("FileBrowser.NewFolderDialog");
        await page.GetByTestId("file-browser-new-folder-cancel").ClickAsync();

        // --- Many files: reopen with a large listing; the panel scrolls internally at the SAME height ---
        await page.GetByTestId("file-browser-modal-close").ClickAsync();
        await page.GetByTestId("file-browser-modal").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        fileCount = 200;
        await page.GetByTestId("file-browser-button").ClickAsync();
        await page.GetByTestId("file-browser-modal").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await list.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        var manyClientHeight = await list.EvaluateAsync<double>("el => el.clientHeight");
        var manyScrollHeight = await list.EvaluateAsync<double>("el => el.scrollHeight");
        Assert.True(manyScrollHeight > manyClientHeight + 4, "A long listing must overflow and scroll INSIDE the panel (no virtualization), proving the content grew but the panel did not.");
        await session.SaveSuccessScreenshotAsync("FileBrowser.FixedHeight_ManyFiles");

        // The visible panel height must not "jump" between few and many files — that is the whole point of #214.
        Assert.True(
            Math.Abs(manyClientHeight - fewClientHeight) <= 2,
            $"File list panel height jumped between few ({fewClientHeight}px) and many ({manyClientHeight}px) files — it must stay fixed."
        );
    }

    /// <summary>Builds a camelCase <c>DirectoryListing</c> JSON with two directories and <paramref name="fileCount"/> files.</summary>
    private static string ListingJson(int fileCount)
    {
        var entries = new List<object>
        {
            new { name = "docs", type = "directory", size = (long?)null, nameLossy = false },
            new { name = "src", type = "directory", size = (long?)null, nameLossy = false },
        };
        for (var i = 1; i <= fileCount; i++)
        {
            entries.Add(new { name = $"file-{i:D3}.txt", type = "file", size = (long?)(1024 + i), nameLossy = false });
        }

        return JsonSerializer.Serialize(new { workspaceId = "demo", path = "", entries, moreCount = 0 });
    }
}
