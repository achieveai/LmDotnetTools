using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Assistant-message affordances through the real chat client: the per-bubble Copy button puts the RAW
/// markdown on the clipboard, a file link opens the preview modal (resolved against the conversation's
/// workspace), and a web link opens in a new tab instead of navigating the chat away.
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
                            ? "# Quarterly report\\n\\nAll good."
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

        // --- Copy: hover reveals the button; the clipboard gets the model's markdown byte for byte. ---
        var row = bubble.Locator("xpath=..");
        await row.HoverAsync();
        await row.GetByTestId("copy-message-button").ClickAsync();
        await Assertions.Expect(row.GetByTestId("copy-message-button")).ToContainTextAsync("Copied");
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
        var modal = page.GetByTestId("artifact-preview-modal");
        await Assertions
            .Expect(modal.GetByTestId("artifact-preview-markdown").Locator("h1"))
            .ToHaveTextAsync("Quarterly report");
        await Assertions.Expect(modal).ToContainTextAsync("docs/report.md");
        await Assertions.Expect(modal.GetByTestId("artifact-preview-download")).ToBeVisibleAsync();
        page.Url.Should().Be(urlBefore, "an intercepted file link must not navigate the page");
        await session.SaveSuccessScreenshotAsync("ChatFileLink.Markdown_preview");
        await page.GetByTestId("artifact-preview-modal-close").ClickAsync();
        await Assertions.Expect(modal).ToBeHiddenAsync();

        // --- CSV file link: a table. ---
        await bubble.GetByRole(AriaRole.Link, new() { Name = "data" }).ClickAsync();
        var table = page.GetByTestId("artifact-preview-table");
        await Assertions.Expect(table.Locator("th")).ToHaveTextAsync(["name", "qty"]);
        await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(2);
        await session.SaveSuccessScreenshotAsync("ChatFileLink.Csv_preview");

        resolveRequests.Should().HaveCount(2);
        resolveRequests.Should().OnlyContain(u => u.Contains("/files/resolve?target=", StringComparison.Ordinal));
    }
}
