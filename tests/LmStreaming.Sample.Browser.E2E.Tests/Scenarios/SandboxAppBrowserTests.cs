using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>Exercises the real Chromium form-to-iframe launch and same-origin app navigation.</summary>
[Collection(PlaywrightCollection.Name)]
public sealed class SandboxAppBrowserTests(PlaywrightFixture fixture)
{
    [Fact]
    public async Task App_tab_posts_ticket_then_loads_relative_asset_form_fetch_xhr_and_csrf_post()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("ok"))
            .Build();
        await using var session = await fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;
        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("hello");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().WaitForCountAtLeastAsync(1);

        var appRequests = new List<(string Method, string Path)>();
        var requestsLock = new object();
        await page.RouteAsync(
            url =>
                url.Contains("/api/conversations/", StringComparison.Ordinal)
                && url.Contains("/apps", StringComparison.Ordinal),
            async route =>
            {
                if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/launch", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = JsonSerializer.Serialize(
                                new { url = "https://instance.apps.example/_launch", ticket = "one-use-ticket" }
                            ),
                        }
                    );
                }
                else
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/json",
                            Body = JsonSerializer.Serialize(
                                new
                                {
                                    apps = new[]
                                    {
                                        new
                                        {
                                            kind = "mini-web-app",
                                            workspaceId = "workspace-1",
                                            id = "budget",
                                            name = "Budget explorer",
                                            link = "#mini-app?workspace=workspace-1&app=budget",
                                        },
                                    },
                                }
                            ),
                        }
                    );
                }
            }
        );
        await page.RouteAsync(
            url => url.StartsWith("https://instance.apps.example/", StringComparison.Ordinal),
            async route =>
            {
                var uri = new Uri(route.Request.Url);
                lock (requestsLock)
                    appRequests.Add((route.Request.Method, uri.AbsolutePath));
                var options = uri.AbsolutePath switch
                {
                    "/_launch" => new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "text/html",
                        // Playwright route fulfillment cannot reliably follow a synthetic 303 from an
                        // unresolved test hostname. The host's real 303 is covered by HTTP tests.
                        Body = "<script>location.replace('/')</script>",
                    },
                    "/" => new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "text/html",
                        Body = """
                            <!doctype html><html><body>
                            <h1>Budget explorer</h1><p id="asset"></p><p id="fetch"></p><p id="xhr"></p>
                            <form action="/filters" method="post"><input type="hidden" name="_csrf" value="csrf-token"><button>Apply filter</button></form>
                            <button id="save-json" type="button">Save JSON</button><p id="saved"></p>
                            <script src="assets/app.js"></script>
                            </body></html>
                            """,
                    },
                    "/assets/app.js" => new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "text/javascript",
                        Body = """
                            document.querySelector('#asset').textContent = 'Asset loaded';
                            fetch('/data').then(r => r.text()).then(t => document.querySelector('#fetch').textContent = t);
                            const xhr = new XMLHttpRequest(); xhr.open('GET', '/data-xhr');
                            xhr.onload = () => document.querySelector('#xhr').textContent = xhr.responseText; xhr.send();
                            document.querySelector('#save-json').onclick = async () => {
                              const response = await fetch('/save-json', { method: 'POST',
                                headers: { 'X-CSRF-Token': 'csrf-token', 'Content-Type': 'application/json' },
                                body: JSON.stringify({ selection: 'sample' }) });
                              document.querySelector('#saved').textContent = await response.text();
                            };
                            """,
                    },
                    "/data" => new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "text/plain",
                        Body = "Fetch loaded",
                    },
                    "/data-xhr" => new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "text/plain",
                        Body = "XHR loaded",
                    },
                    "/save-json"
                        when route.Request.Headers.TryGetValue("x-csrf-token", out var token)
                            && token == "csrf-token" => new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "text/plain",
                        Body = "JSON saved",
                    },
                    "/filters"
                        when route.Request.PostData?.Contains("_csrf=csrf-token", StringComparison.Ordinal) == true =>
                        new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "text/html",
                            Body = "<h1>Filter applied</h1>",
                        },
                    _ => new RouteFulfillOptions { Status = 404 },
                };
                await route.FulfillAsync(options);
            }
        );

        await page.GetByTestId("conversation-inspector-launcher").ClickAsync();
        await page.Locator("#inspector-tab-apps").ClickAsync();
        await page.GetByTestId("open-sandbox-app-budget").ClickAsync();
        var app = page.FrameLocator("iframe[title='Sandbox app content']");
        await Assertions.Expect(app.GetByRole(AriaRole.Heading, new() { Name = "Budget explorer" })).ToBeVisibleAsync();
        await Assertions.Expect(app.Locator("#asset")).ToHaveTextAsync("Asset loaded");
        await Assertions.Expect(app.Locator("#fetch")).ToHaveTextAsync("Fetch loaded");
        await Assertions.Expect(app.Locator("#xhr")).ToHaveTextAsync("XHR loaded");
        await app.GetByRole(AriaRole.Button, new() { Name = "Save JSON" }).ClickAsync();
        await Assertions.Expect(app.Locator("#saved")).ToHaveTextAsync("JSON saved");
        await session.SaveSuccessScreenshotAsync("SandboxAppBrowser.mocked_app_before_form");
        await app.GetByRole(AriaRole.Button, new() { Name = "Apply filter" }).ClickAsync();
        await Assertions.Expect(app.GetByRole(AriaRole.Heading, new() { Name = "Filter applied" })).ToBeVisibleAsync();
        await session.SaveSuccessScreenshotAsync("SandboxAppBrowser.mocked_app_after_form");

        lock (requestsLock)
        {
            Assert.Contains(("POST", "/_launch"), appRequests);
            Assert.Contains(("GET", "/assets/app.js"), appRequests);
            Assert.Contains(("GET", "/data"), appRequests);
            Assert.Contains(("GET", "/data-xhr"), appRequests);
            Assert.Contains(("POST", "/save-json"), appRequests);
            Assert.Contains(("POST", "/filters"), appRequests);
        }
        Assert.DoesNotContain("one-use-ticket", page.Url, StringComparison.Ordinal);
    }
}
