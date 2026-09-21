using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Drives the workspace file browser (WI #195) end-to-end through the real chat client against the real
/// backend controller. The browser is no longer a modal: it is the "Files" disclosure of the right-hand
/// workspace panel (<c>conversation-inspector</c>), and opening a file from it renders in that panel's
/// shared preview region rather than inline in the row. With no sandbox gateway running (the
/// deterministic browser suite has none) a plain conversation has no established sandbox binding, so the
/// browser renders its structured "no session yet" state — this scenario proves the whole wiring: the
/// gated header item, the panel section opening, the real
/// <c>GET /api/conversations/{threadId}/files</c> call, the no-session render, and closing the panel. The
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

        // Start a conversation (New Chat sets the active thread id the Files item is gated on) and send a
        // message so the thread is persisted. Wait for the assistant bubble so the full turn has completed.
        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("hello");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().WaitForCountAtLeastAsync(1);

        // ClickAsync auto-waits for the item to become actionable (enabled once a conversation is active).
        await page.OpenHeaderActionsMenuAsync();
        await page.GetByTestId("file-browser-button").ClickAsync();

        // Files opens the right-hand workspace panel and reveals its Files section — no second panel
        // system, no modal.
        await page.GetByTestId("conversation-inspector")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var browser = page.GetByTestId("conversation-inspector").GetByTestId("file-browser");
        await browser.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // A non-workspace conversation has no established sandbox binding, so the browser shows the
        // structured no-session state rather than an error or a hang.
        await page.GetByTestId("file-browser-no-session")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        await session.SaveSuccessScreenshotAsync("FileBrowser.No_session_state");

        // Escape from inside the panel closes the whole panel, taking the browser with it.
        await page.Keyboard.PressAsync("Escape");
        Assert.Equal(0, await page.GetByTestId("conversation-inspector").CountAsync());
    }

    /// <summary>
    /// Fixed-height list (WI #214): the browser's file panel must occupy a STABLE height whether it holds
    /// few or many files (no "jumping"), scrolling internally when the list overflows. The deterministic
    /// suite has no sandbox gateway, so this stubs the file REST API at the HTTP boundary (via
    /// <c>page.RouteAsync</c>) to render real populated listings through the real Vue component — the
    /// controller/sandbox path is proven separately by the FileBrowserController unit tests. Also captures
    /// the New-folder dialog, and exercises the panel's Refresh button (which replaced the modal
    /// close/reopen this test used to rely on for a second listing). Screenshots land in
    /// <c>.logs/e2e-screenshots/</c> as reviewer-facing proof.
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
            url =>
                url.Contains("/api/conversations/", StringComparison.Ordinal)
                && url.Contains("/files", StringComparison.Ordinal),
            async route =>
            {
                var request = route.Request;
                var url = request.Url;
                var method = request.Method;

                if (
                    method == "GET"
                    && !url.Contains("/download", StringComparison.Ordinal)
                    && !url.Contains("/preview", StringComparison.Ordinal)
                )
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = ListingJson(fileCount),
                        }
                    );
                }
                else if (method == "POST" && url.Contains("/files/directory", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = "{\"path\":\"reports\"}",
                        }
                    );
                }
                else if (method == "POST")
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = "{\"name\":\"note.txt\",\"size\":12}",
                        }
                    );
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
        await page.OpenHeaderActionsMenuAsync();
        await page.GetByTestId("file-browser-button").ClickAsync();
        await page.GetByTestId("file-browser")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var list = page.GetByTestId("file-browser-list");
        await list.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-entry-file-001.txt")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        var fewClientHeight = await list.EvaluateAsync<double>("el => el.clientHeight");
        var fewScrollHeight = await list.EvaluateAsync<double>("el => el.scrollHeight");
        Assert.False(
            fewScrollHeight > fewClientHeight + 4,
            "A short listing must NOT overflow — the panel should size to its fixed height, not shrink to the content."
        );
        await session.SaveSuccessScreenshotAsync("FileBrowser.FixedHeight_FewFiles");

        // --- New-folder dialog ---
        await page.GetByTestId("file-browser-new-folder").ClickAsync();
        await page.GetByTestId("file-browser-new-folder-dialog")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-browser-new-folder-input").FillAsync("reports");
        await session.SaveSuccessScreenshotAsync("FileBrowser.NewFolderDialog");
        await page.GetByTestId("file-browser-new-folder-cancel").ClickAsync();

        // --- Many files: REFRESH with a large listing; the panel scrolls internally at the SAME height.
        // In a persistent panel the listing is re-read in place rather than by closing and reopening a
        // modal, so Refresh is what this exercises now.
        fileCount = 200;
        await page.GetByTestId("file-browser-refresh").ClickAsync();
        await page.GetByTestId("file-entry-file-200.txt")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

        var manyClientHeight = await list.EvaluateAsync<double>("el => el.clientHeight");
        var manyScrollHeight = await list.EvaluateAsync<double>("el => el.scrollHeight");
        Assert.True(
            manyScrollHeight > manyClientHeight + 4,
            "A long listing must overflow and scroll INSIDE the panel (no virtualization), proving the content grew but the panel did not."
        );
        await session.SaveSuccessScreenshotAsync("FileBrowser.FixedHeight_ManyFiles");

        // The visible panel height must not "jump" between few and many files — that is the whole point of #214.
        Assert.True(
            Math.Abs(manyClientHeight - fewClientHeight) <= 2,
            $"File list panel height jumped between few ({fewClientHeight}px) and many ({manyClientHeight}px) files — it must stay fixed."
        );
    }

    /// <summary>
    /// Fixed-height regression (WI #214 review T8): the list container must stay MOUNTED at a stable height while
    /// a listing is loading — the loading indicator renders INSIDE the fixed-height panel, so initial load and
    /// every refresh no longer collapse then re-expand it (the layout jump this change removes). Gates the first
    /// listing response so the loading state is deterministically observable.
    /// </summary>
    [Fact]
    public async Task File_browser_list_container_stays_mounted_at_stable_height_while_loading()
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

        // Hold the FIRST listing response open so the loading state is observable; later calls resolve normally.
        var listGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstList = true;
        await page.RouteAsync(
            url =>
                url.Contains("/api/conversations/", StringComparison.Ordinal)
                && url.Contains("/files", StringComparison.Ordinal),
            async route =>
            {
                var url = route.Request.Url;
                if (
                    route.Request.Method == "GET"
                    && !url.Contains("/download", StringComparison.Ordinal)
                    && !url.Contains("/preview", StringComparison.Ordinal)
                )
                {
                    if (firstList)
                    {
                        firstList = false;
                        await listGate.Task;
                    }

                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = ListingJson(6),
                        }
                    );
                }
                else
                {
                    await route.ContinueAsync();
                }
            }
        );

        await page.OpenHeaderActionsMenuAsync();
        await page.GetByTestId("file-browser-button").ClickAsync();
        await page.GetByTestId("file-browser")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // While loading: the fixed-height list container is mounted with the loading indicator INSIDE it.
        var list = page.GetByTestId("file-browser-list");
        await list.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-browser-loading")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var loadingHeight = await list.EvaluateAsync<double>("el => el.clientHeight");
        await session.SaveSuccessScreenshotAsync("FileBrowser.Loading_State");

        // Release the listing; the rows replace the loading indicator inside the SAME container.
        listGate.SetResult();
        await page.GetByTestId("file-browser-loading")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        var loadedHeight = await list.EvaluateAsync<double>("el => el.clientHeight");

        Assert.True(
            Math.Abs(loadedHeight - loadingHeight) <= 2,
            $"File list panel height changed between loading ({loadingHeight}px) and loaded ({loadedHeight}px) — the container must stay mounted at a stable height."
        );
    }

    /// <summary>
    /// The third ask of the bug: previewing a file opens it in the RIGHT PANEL, not inline in the row.
    /// Clicking a row's preview action must open the file as a tab in the panel's shared preview region
    /// (the same surface a board artifact chip and a chat file link use), with the file list still
    /// visible below it — a split, not a replacement. This also proves Playwright can activate the
    /// hover-revealed row action (the actions are opacity-0 until hover/focus-within but never leave the
    /// DOM), which is why the row buttons were NOT moved behind a kebab menu.
    /// </summary>
    [Fact]
    public async Task File_browser_preview_opens_the_file_in_the_workspace_preview_region()
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

        // Same HTTP-boundary stub as the fixed-height scenario, plus a /preview branch: the preview is
        // fetched by the PANEL's preview component now, not by the browser row.
        await page.RouteAsync(
            url =>
                url.Contains("/api/conversations/", StringComparison.Ordinal)
                && url.Contains("/files", StringComparison.Ordinal),
            async route =>
            {
                var url = route.Request.Url;
                if (route.Request.Method != "GET")
                {
                    await route.ContinueAsync();
                    return;
                }

                if (url.Contains("/preview", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = "{\"previewable\":true,\"text\":\"hello\",\"lineCount\":1}",
                        }
                    );
                }
                else if (!url.Contains("/download", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = ListingJson(3),
                        }
                    );
                }
                else
                {
                    await route.ContinueAsync();
                }
            }
        );

        await page.OpenHeaderActionsMenuAsync();
        await page.GetByTestId("file-browser-button").ClickAsync();
        await page.GetByTestId("file-browser")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-entry-file-001.txt")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // The row action is revealed on hover / focus-within and is opacity-0 otherwise — which
        // Playwright still treats as visible and actionable. If that ever stops being true, THIS is the
        // test that says so.
        await page.GetByTestId("file-entry-preview-file-001.txt").ClickAsync();

        var inspector = page.GetByTestId("conversation-inspector");
        await inspector
            .GetByTestId("artifact-preview-surface")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await Assertions.Expect(page.GetByTestId("artifact-preview-path")).ToHaveTextAsync("file-001.txt");
        await Assertions.Expect(page.GetByTestId("artifact-preview-text")).ToHaveTextAsync("hello");

        // Split, not replacement: the file list is still on screen under the preview.
        await Assertions.Expect(page.GetByTestId("file-browser-list")).ToBeVisibleAsync();

        await session.SaveSuccessScreenshotAsync("FileBrowser.Preview_In_Panel");
    }

    /// <summary>Builds a camelCase <c>DirectoryListing</c> JSON with two directories and <paramref name="fileCount"/> files.</summary>
    private static string ListingJson(int fileCount)
    {
        var entries = new List<object>
        {
            new
            {
                name = "docs",
                type = "directory",
                size = (long?)null,
                nameLossy = false,
            },
            new
            {
                name = "src",
                type = "directory",
                size = (long?)null,
                nameLossy = false,
            },
        };
        for (var i = 1; i <= fileCount; i++)
        {
            entries.Add(
                new
                {
                    name = $"file-{i:D3}.txt",
                    type = "file",
                    size = (long?)(1024 + i),
                    nameLossy = false,
                }
            );
        }

        return JsonSerializer.Serialize(
            new
            {
                workspaceId = "demo",
                path = "",
                entries,
                moreCount = 0,
            }
        );
    }
}
