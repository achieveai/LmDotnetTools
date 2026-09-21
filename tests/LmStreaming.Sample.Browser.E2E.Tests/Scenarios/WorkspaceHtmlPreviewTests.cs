using System.Globalization;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// The real-browser proof for Bug#15: a workspace HTML file, opened from the file browser, RENDERS in the
/// sandboxed iframe with its OWN relative links resolving to sibling workspace files.
///
/// <para>
/// This is the one claim a unit test cannot make. <c>WorkspaceRawEndpointTests</c> proves what the
/// controller emits; <c>workspaceGrant.test.ts</c> proves the URL this client builds. Neither can prove the
/// part that lives entirely inside a browser engine: that a real Chromium, given a document served from
/// <c>/api/conversations/{id}/workspace/{grant}/report/index.html</c> under the production
/// <see cref="WorkspaceContentTypes.SandboxPolicy"/>, resolves <c>img/dot.png</c> and
/// <c>../shared/style.css</c> back onto that same grant-bearing path prefix (RFC 3986 §5.3 — the reason the
/// grant is a PATH segment and not a query parameter), and then ACTUALLY LOADS them despite the document
/// having an opaque origin from the <c>sandbox</c> directive. The open question that motivated this test was
/// whether <c>'self'</c> in that CSP still matches for an opaque-origin document; the image and stylesheet
/// assertions below are the empirical answer.
/// </para>
///
/// <para>
/// The deterministic browser suite has no sandbox gateway, so — exactly as
/// <see cref="FileBrowserTests"/> does — the file REST surface is stubbed at the HTTP boundary with
/// <c>page.RouteAsync</c>. What is NOT hand-written here are the headers: the stub labels and protects every
/// response through the production <see cref="WorkspaceContentTypes"/> members, so weakening the real policy
/// weakens this test's fixture too and the browser gets the same treatment the controller would give it.
/// </para>
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class WorkspaceHtmlPreviewTests
{
    private readonly PlaywrightFixture _fixture;

    public WorkspaceHtmlPreviewTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>The token the stubbed mint hands out; it occupies the same path segment a real grant would.</summary>
    private const string Grant = "e2e-grant-token";

    private const string RawPrefix = "/workspace/" + Grant + "/";

    /// <summary>
    /// The document under test. Both subresources are RELATIVE and deliberately of different shapes: one
    /// descends (<c>img/dot.png</c>), one climbs out of the file's own directory (<c>../shared/style.css</c>).
    /// The climbing one also demonstrates why the controller's refusal of <c>..</c> segments is not a
    /// contradiction: the browser resolves and REMOVES the <c>..</c> before the request is sent, which this
    /// test asserts by recording the paths that actually reached the wire.
    /// </summary>
    private const string IndexHtml = """
        <!doctype html>
        <html>
          <head>
            <meta charset="utf-8" />
            <title>Workspace report</title>
            <link rel="stylesheet" href="../shared/style.css" />
          </head>
          <body>
            <h1 id="heading">Quarterly report</h1>
            <img id="chart" src="img/dot.png" alt="chart" />
          </body>
        </html>
        """;

    /// <summary>A background colour no browser default would produce, so asserting it proves the sheet applied.</summary>
    private const string StyleCss = "body { background-color: rgb(1, 2, 3); }";

    /// <summary>A 1x1 PNG. Decoded width is what <c>naturalWidth &gt; 0</c> reports.</summary>
    private const string DotPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task Workspace_html_renders_in_the_sandboxed_iframe_with_its_relative_links_resolved()
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

        // Every path the browser asked the raw endpoint for, in arrival order. This is the evidence for the
        // relative-resolution claim: nothing here may contain ".." or lose the grant segment.
        var rawRequests = new List<string>();
        var rawLock = new object();

        // The grant mint and the listing/preview JSON. `POST .../files/grant` matches this filter too.
        await page.RouteAsync(
            url =>
                url.Contains("/api/conversations/", StringComparison.Ordinal)
                && url.Contains("/files", StringComparison.Ordinal),
            async route =>
            {
                var request = route.Request;
                var url = request.Url;

                if (request.Method == "POST" && url.Contains("/files/grant", StringComparison.Ordinal))
                {
                    var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = JsonSerializer.Serialize(new { grant = Grant, expiresAt }),
                        }
                    );
                    return;
                }

                if (request.Method != "GET")
                {
                    await route.ContinueAsync();
                    return;
                }

                if (url.Contains("/preview", StringComparison.Ordinal))
                {
                    // The Source view of the very same document the iframe renders.
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = JsonSerializer.Serialize(
                                new
                                {
                                    previewable = true,
                                    text = IndexHtml,
                                    lineCount = 12,
                                }
                            ),
                        }
                    );
                    return;
                }

                if (url.Contains("/download", StringComparison.Ordinal))
                {
                    await route.ContinueAsync();
                    return;
                }

                await route.FulfillAsync(
                    new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "application/json",
                        Body = ListingJson(ListedPath(url)),
                    }
                );
            }
        );

        // The raw path-addressed endpoint. Headers come from the PRODUCTION constants, not from strings
        // typed here, so this fixture cannot drift into being kinder than the controller.
        await page.RouteAsync(
            url => url.Contains(RawPrefix, StringComparison.Ordinal),
            async route =>
            {
                var url = route.Request.Url;
                var start = url.IndexOf(RawPrefix, StringComparison.Ordinal) + RawPrefix.Length;
                var relative = url[start..];
                var query = relative.IndexOf('?', StringComparison.Ordinal);
                if (query >= 0)
                {
                    relative = relative[..query];
                }

                lock (rawLock)
                {
                    rawRequests.Add(relative);
                }

                var body = WorkspaceBytes(relative);
                if (body is null)
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 404,
                            ContentType = "application/json",
                            Body = "{\"error\":\"not_found\"}",
                        }
                    );
                    return;
                }

                var contentType = WorkspaceContentTypes.ForFileName(relative);
                var headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["X-Content-Type-Options"] = "nosniff",
                    ["Cache-Control"] = "private, no-store",
                    ["Referrer-Policy"] = "no-referrer",
                };
                if (WorkspaceContentTypes.IsActiveDocument(contentType))
                {
                    headers["Content-Security-Policy"] = WorkspaceContentTypes.SandboxPolicy;
                }

                await route.FulfillAsync(
                    new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = contentType,
                        Headers = headers,
                        BodyBytes = body,
                    }
                );
            }
        );

        // --- Open report/index.html from the file browser ---
        await page.OpenHeaderActionsMenuAsync();
        await page.GetByTestId("file-browser-button").ClickAsync();
        await page.GetByTestId("file-browser")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-entry-name-report").ClickAsync();
        await page.GetByTestId("file-entry-preview-index.html")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("file-entry-preview-index.html").ClickAsync();

        var frameLocator = page.GetByTestId("artifact-preview-html-frame");
        await frameLocator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // The iframe is addressed by PATH, carries the grant in that path, and is sandboxed without
        // same-origin — the one token that would undo the whole isolation story.
        var src = await frameLocator.GetAttributeAsync("src");
        Assert.NotNull(src);
        Assert.Contains(RawPrefix + "report/index.html", src, StringComparison.Ordinal);
        Assert.DoesNotContain("?grant=", src, StringComparison.Ordinal);
        var sandbox = await frameLocator.GetAttributeAsync("sandbox");
        Assert.NotNull(sandbox);
        Assert.DoesNotContain("allow-same-origin", sandbox, StringComparison.Ordinal);

        var element = await frameLocator.ElementHandleAsync();
        var frame = await element.ContentFrameAsync();
        Assert.NotNull(frame);

        // --- The claim: the document's OWN relative links loaded, inside the sandbox ---
        // naturalWidth is only non-zero once the bytes decoded, so this is the image actually loading, not
        // merely an <img> existing. Asserted from INSIDE the frame, which is the only place it is visible.
        await frame!.WaitForFunctionAsync(
            """
            () => {
              const img = document.getElementById('chart');
              return !!img && img.complete && img.naturalWidth > 0;
            }
            """
        );
        var naturalWidth = await frame.EvaluateAsync<int>("() => document.getElementById('chart').naturalWidth");
        Assert.True(naturalWidth > 0, "The relative <img> must decode inside the sandboxed iframe.");

        // The stylesheet is proven by its EFFECT, not by the <link> being present: a sheet that 404s or is
        // blocked by CSP leaves the default background behind.
        await frame.WaitForFunctionAsync("() => getComputedStyle(document.body).backgroundColor === 'rgb(1, 2, 3)'");

        // The rendered heading is the document itself, not the app chrome around it.
        var heading = await frame.EvaluateAsync<string>("() => document.getElementById('heading').textContent");
        Assert.Equal("Quarterly report", heading);

        // The sandbox really is on: an opaque-origin document cannot reach the app's storage. (Chrome
        // throws SecurityError on localStorage access from an opaque origin.)
        var storageReachable = await frame.EvaluateAsync<bool>(
            """
            () => {
              try { window.localStorage.getItem('x'); return true; }
              catch { return false; }
            }
            """
        );
        Assert.False(
            storageReachable,
            "A sandboxed workspace document must NOT be able to touch this app's localStorage."
        );

        // --- What reached the wire ---
        List<string> observed;
        lock (rawLock)
        {
            observed = [.. rawRequests];
        }

        Assert.Contains("report/index.html", observed);
        Assert.Contains("report/img/dot.png", observed);
        // `../shared/style.css` was RESOLVED by the browser against the grant-bearing base path before it
        // was sent: the grant survived, and the `..` never travelled.
        Assert.Contains("shared/style.css", observed);
        Assert.DoesNotContain(observed, p => p.Contains("..", StringComparison.Ordinal));

        await session.SaveSuccessScreenshotAsync("WorkspaceHtmlPreview.Rendered_In_Sandboxed_Iframe");

        // --- Source view still shows the markup, and back again ---
        await page.GetByTestId("artifact-preview-mode-source").ClickAsync();
        await Assertions.Expect(page.GetByTestId("artifact-preview-text")).ToContainTextAsync("Quarterly report");
        await page.GetByTestId("artifact-preview-mode-rendered").ClickAsync();
        await Assertions.Expect(page.GetByTestId("artifact-preview-html-frame")).ToBeVisibleAsync();
    }

    /// <summary>The bytes of the three-file fixture workspace, or null for anything else (404).</summary>
    private static byte[]? WorkspaceBytes(string relativePath) =>
        relativePath switch
        {
            "report/index.html" => Encoding.UTF8.GetBytes(IndexHtml),
            "shared/style.css" => Encoding.UTF8.GetBytes(StyleCss),
            "report/img/dot.png" => Convert.FromBase64String(DotPngBase64),
            _ => null,
        };

    /// <summary>Reads the <c>path</c> query the file browser listed, defaulting to the workspace root.</summary>
    private static string ListedPath(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && pair[..eq] == "path")
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return string.Empty;
    }

    /// <summary>The fixture workspace as the browser sees it: a <c>report</c> folder holding the document.</summary>
    private static string ListingJson(string path)
    {
        object[] entries = path switch
        {
            "report" =>
            [
                new
                {
                    name = "img",
                    type = "directory",
                    size = (long?)null,
                    nameLossy = false,
                },
                new
                {
                    name = "index.html",
                    type = "file",
                    size = (long?)IndexHtml.Length,
                    nameLossy = false,
                },
            ],
            _ =>
            [
                new
                {
                    name = "report",
                    type = "directory",
                    size = (long?)null,
                    nameLossy = false,
                },
                new
                {
                    name = "shared",
                    type = "directory",
                    size = (long?)null,
                    nameLossy = false,
                },
            ],
        };

        return JsonSerializer.Serialize(
            new
            {
                workspaceId = "demo",
                path,
                entries,
                moreCount = 0,
            }
        );
    }
}
