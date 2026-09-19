using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Assistant-message affordances through the real chat client: the per-bubble Copy button puts the RAW
/// markdown on the clipboard, file links open tabbed workspace previews (resolved against the
/// conversation's workspace), and a web link opens in a new tab instead of navigating the chat away.
/// </summary>
/// <remarks>
/// The deterministic suite has no sandbox gateway, so — like <see cref="FileBrowserTests"/> — the file REST
/// surface (<c>files/resolve</c>, <c>files/preview</c>) is stubbed at the HTTP boundary with
/// <c>page.RouteAsync</c>. The resolver's path rules and the controller are proven by the C#
/// <c>WorkspaceLinkResolver</c>/<c>FileBrowserController</c> tests; the viewers by the client vitest suite.
/// What only this test proves is the wiring: rendered link → click → resolve for THIS thread → viewer.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class ChatFileLinkAndCopyTests
{
    private const string Answer =
        "# Results\n\n"
        + "Read the [report](B:\\ws\\docs\\report.md) and the [data](data/items.csv).\n\n"
        + "Source: [example](https://example.com/)";

    private readonly PlaywrightFixture _fixture;

    public ChatFileLinkAndCopyTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Copy_button_copies_raw_markdown_and_file_links_open_the_right_viewer()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text(Answer))
            .Build();
        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;
        await session.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("summarize the files");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().WaitForCountAtLeastAsync(1);

        var resolveRequests = new List<string>();
        await page.RouteAsync(
            url =>
                url.Contains("/api/conversations/", StringComparison.Ordinal)
                && url.Contains("/files/", StringComparison.Ordinal),
            async route =>
            {
                var url = new Uri(route.Request.Url);
                var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                if (url.AbsolutePath.EndsWith("/files/resolve", StringComparison.Ordinal))
                {
                    resolveRequests.Add(route.Request.Url);
                    var path = query["target"] switch
                    {
                        // marked percent-encodes the backslashes; the server decodes once.
                        "B:%5Cws%5Cdocs%5Creport.md" => "docs/report.md",
                        "data/items.csv" => "data/items.csv",
                        _ => null,
                    };
                    await route.FulfillAsync(
                        path is null
                            ? new RouteFulfillOptions
                            {
                                Status = 404,
                                ContentType = "application/json",
                                Body = """{"code":"not_found"}""",
                            }
                            : new RouteFulfillOptions
                            {
                                Status = 200,
                                ContentType = "application/json",
                                Body = $$"""{"path":"{{path}}","type":"file","size":42}""",
                            }
                    );
                    return;
                }

                if (url.AbsolutePath.EndsWith("/files/preview", StringComparison.Ordinal))
                {
                    var text =
                        query["path"] == "docs/report.md"
                            ? "# Quarterly report\\n\\nAll good.\\n\\n```mermaid\\ngraph LR; A-->B\\n```"
                            : "name,qty\\nWidget,3\\nBolt,10";
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = $$"""{"previewable":true,"text":"{{text}}","lineCount":3}""",
                        }
                    );
                    return;
                }

                await route.ContinueAsync();
            }
        );

        var bubble = page.AssistantText().Last;

        // --- Copy: hover and keyboard focus reveal it; touch capability alone does not. ---
        var row = bubble.Locator("xpath=..");
        var copyButton = row.GetByTestId("copy-message-button");
        await page.GetByTestId("chat-input-textarea").ClickAsync();
        await page.EvaluateAsync(
            "() => document.activeElement instanceof HTMLElement && document.activeElement.blur()"
        );
        await Assertions.Expect(copyButton).ToHaveCSSAsync("opacity", "0");
        await Assertions.Expect(copyButton).ToHaveCSSAsync("pointer-events", "none");
        await row.HoverAsync();
        await Assertions.Expect(copyButton).ToHaveCSSAsync("opacity", "1");
        await Assertions.Expect(copyButton).ToHaveCSSAsync("pointer-events", "auto");
        await page.GetByTestId("chat-input-textarea").HoverAsync();
        await Assertions.Expect(copyButton).ToHaveCSSAsync("opacity", "0");
        await Assertions.Expect(copyButton).ToHaveCSSAsync("pointer-events", "none");

        var cdp = await session.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync(
            "Emulation.setTouchEmulationEnabled",
            new Dictionary<string, object> { ["enabled"] = true, ["maxTouchPoints"] = 1 }
        );
        await Assertions.Expect(copyButton).ToHaveCSSAsync("opacity", "0");
        await Assertions.Expect(copyButton).ToHaveCSSAsync("pointer-events", "none");
        await copyButton.FocusAsync();
        await Assertions.Expect(copyButton).ToBeFocusedAsync();
        await Assertions.Expect(copyButton).ToHaveCSSAsync("opacity", "1");
        await Assertions.Expect(copyButton).ToHaveCSSAsync("pointer-events", "auto");
        await copyButton.ClickAsync();
        await Assertions.Expect(copyButton).ToContainTextAsync("Copied");
        var clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        // The Windows system clipboard stores CRLF line endings; the content is otherwise byte-identical.
        clipboard.ReplaceLineEndings("\n").Should().Be(Answer);
        // The button is a sibling of the bubble, so the answer text itself is unchanged.
        (await bubble.InnerTextAsync())
            .Should()
            .NotContain("Copy");

        // --- Web link: a new tab, never the chat pane. ---
        var webLink = bubble.GetByRole(AriaRole.Link, new() { Name = "example" });
        (await webLink.GetAttributeAsync("target")).Should().Be("_blank");
        (await webLink.GetAttributeAsync("rel")).Should().Be("noopener noreferrer");

        // --- Markdown file link: resolved for this thread, rendered as markdown. ---
        var urlBefore = page.Url;
        await bubble.GetByRole(AriaRole.Link, new() { Name = "report" }).ClickAsync();
        var workspace = page.ConversationInspector();
        await Assertions.Expect(workspace).ToBeVisibleAsync();
        var preview = page.GetByTestId("artifact-preview-surface");
        await Assertions
            .Expect(preview.GetByTestId("artifact-preview-markdown").Locator("h1"))
            .ToHaveTextAsync("Quarterly report");
        await Assertions.Expect(preview).ToContainTextAsync("docs/report.md");
        await Assertions.Expect(preview.GetByTestId("artifact-preview-download")).ToBeVisibleAsync();
        await Assertions.Expect(preview.GetByTestId("diagram-image")).ToBeVisibleAsync();
        page.Url.Should().Be(urlBefore, "an intercepted file link must not navigate the page");
        await session.SaveSuccessScreenshotAsync("ChatFileLink.Markdown_preview");

        // Work and Agents are independent disclosures: collapsing one leaves the other open.
        var workDisclosure = workspace.GetByRole(AriaRole.Button, new() { Name = "Work" });
        var agentsDisclosure = workspace.GetByRole(AriaRole.Button, new() { Name = "Agents 0" });
        await Assertions.Expect(workDisclosure).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(agentsDisclosure).ToHaveAttributeAsync("aria-expanded", "true");
        await workDisclosure.ClickAsync();
        await Assertions.Expect(workDisclosure).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(agentsDisclosure).ToHaveAttributeAsync("aria-expanded", "true");

        // A nested diagram dialog consumes the first Escape. The workspace itself remains mounted;
        // a second Escape closes it and restores focus to its stable header launcher.
        await preview.GetByTestId("diagram-expand").ClickAsync();
        await Assertions.Expect(page.GetByTestId("diagram-modal")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("diagram-modal")).ToHaveCountAsync(0);
        await Assertions.Expect(workspace).ToBeVisibleAsync();

        // A second Escape closes the workspace itself and restores focus to its stable header launcher.
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(workspace).ToHaveCountAsync(0);
        await Assertions.Expect(page.ConversationInspectorLauncher()).ToBeFocusedAsync();
        await page.ConversationInspectorLauncher().ClickAsync();
        await Assertions.Expect(workspace).ToBeVisibleAsync();

        // The horizontal separator is keyboard operable and reports the applied size.
        var previewSplitter = page.GetByTestId("workspace-vertical-splitter");
        if (await previewSplitter.CountAsync() > 0)
        {
            var before = int.Parse((await previewSplitter.GetAttributeAsync("aria-valuenow"))!);
            await previewSplitter.FocusAsync();
            await previewSplitter.PressAsync("ArrowUp");
            await Assertions.Expect(previewSplitter).ToHaveAttributeAsync("aria-valuenow", (before - 8).ToString());
        }

        // Expanded reading uses the available drawer width at medium and phone breakpoints, then
        // restores the same mounted monitoring surface instead of rebuilding the workspace.
        foreach (var width in new[] { 1000, 390 })
        {
            await page.SetViewportSizeAsync(width, 800);
            await preview.GetByTestId("artifact-preview-expand").ClickAsync();
            var expandedBox = await workspace.BoundingBoxAsync();
            expandedBox.Should().NotBeNull($"the expanded workspace must be measurable at {width}px");
            expandedBox!
                .X.Should()
                .BeGreaterThanOrEqualTo(0, $"the expanded workspace must stay onscreen at {width}px");
            (expandedBox.X + expandedBox.Width)
                .Should()
                .BeLessThanOrEqualTo(width + 1, $"the expanded workspace must stay inside {width}px");
            var minimumExpandedRatio = width > 768 ? 0.68f : 0.88f;
            expandedBox
                .Width.Should()
                .BeGreaterThan(
                    width * minimumExpandedRatio,
                    $"expanded reading should use the space beside the projects panel at {width}px"
                );
            (await page.EvaluateAsync<double>("() => document.documentElement.scrollHeight - window.innerHeight"))
                .Should()
                .BeLessThanOrEqualTo(1, $"expanded reading must not create page scrolling at {width}px");
            await preview.GetByTestId("artifact-preview-expand").ClickAsync();
            await Assertions.Expect(page.GetByTestId("workspace-monitoring-region")).ToBeVisibleAsync();
        }
        await page.SetViewportSizeAsync(1280, 800);

        // --- CSV file link: a table. ---
        await bubble.GetByRole(AriaRole.Link, new() { Name = "data" }).ClickAsync();
        var table = page.GetByTestId("artifact-preview-table");
        await Assertions.Expect(table.Locator("th")).ToHaveTextAsync(["name", "qty"]);
        await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(2);
        await session.SaveSuccessScreenshotAsync("ChatFileLink.Csv_preview");

        var fileTabs = workspace.GetByRole(AriaRole.Tablist, new() { Name = "Open files" });
        await Assertions.Expect(fileTabs.GetByRole(AriaRole.Tab)).ToHaveCountAsync(2);
        await bubble.GetByRole(AriaRole.Link, new() { Name = "report" }).ClickAsync();
        await Assertions.Expect(fileTabs.GetByRole(AriaRole.Tab)).ToHaveCountAsync(2);
        await Assertions
            .Expect(fileTabs.GetByRole(AriaRole.Tab, new() { Name = "report.md" }))
            .ToHaveAttributeAsync("aria-selected", "true");
        await fileTabs
            .GetByRole(AriaRole.Tab, new() { Name = "report.md" })
            .Locator("xpath=..")
            .Locator(".preview-tab-close")
            .ClickAsync();
        await Assertions
            .Expect(fileTabs.GetByRole(AriaRole.Tab, new() { Name = "items.csv" }))
            .ToHaveAttributeAsync("aria-selected", "true");
        await fileTabs
            .GetByRole(AriaRole.Tab, new() { Name = "items.csv" })
            .Locator("xpath=..")
            .Locator(".preview-tab-close")
            .ClickAsync();
        await Assertions.Expect(page.GetByTestId("workspace-preview-region")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByTestId("workspace-monitoring-region")).ToBeVisibleAsync();

        // F-001 (#784): once a `target` opener resolves, ChatLayout reconciles that tab onto its
        // canonical server-resolved `path` (see ChatLayout.reconcilePreviewResolution), so remounting
        // it — e.g. revealing items.csv again after report.md's tab closes — no longer re-fetches
        // `files/resolve`: the tab already carries its own resolved path. Re-clicking the SAME link a
        // second time still issues one more resolve, though: the click still opens a `target` tab
        // under the raw link string, which briefly duplicates the canonical tab before this same
        // reconciliation merges them back into one (asserted above) — hence 3, not the 2 a fully
        // link-aware cache would need, and not the 4 every activation used to cost pre-#784.
        resolveRequests
            .Should()
            .HaveCount(
                3,
                "a tab already reconciled onto its resolved path is not re-fetched when it becomes active again"
            );
        resolveRequests.Should().OnlyContain(u => u.Contains("/files/resolve?target=", StringComparison.Ordinal));
    }
}
