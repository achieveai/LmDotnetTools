using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.SandboxApps;

namespace LmStreaming.Sample.Tests.SandboxApps;

/// <summary>
/// <see cref="BrowserDebugGateway"/> drives the browser through the SDK's MCP relay
/// (<c>/api/v1/browsers/{id}/mcp/{toolkit}</c>) against a fake relay that speaks streamable-HTTP MCP. The results
/// must keep the shape <see cref="BrowserDebugToolProvider"/> reads: MCP content plus <c>_meta</c> naming the
/// browser and the instance the relay reported.
/// </summary>
public sealed class BrowserDebugGatewayTests
{
    private const string BrowserId = "thread-1.preview-1";
    private const string RelayPath = $"/api/v1/browsers/{BrowserId}/mcp/playwright";

    [Fact]
    public async Task Call_GoesThroughTheRelay_KeepsImages_AndStampsTheBrowserInstance()
    {
        var relay = new FakeRelay();
        await using var gateway = relay.Gateway();

        var first = await gateway.CallAsync(
            BrowserId,
            "playwright",
            "browser_take_screenshot",
            JsonSerializer.SerializeToElement(new { type = "png" }),
            CancellationToken.None
        );
        var second = await gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        first.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("image");
        first.GetProperty("content")[0].GetProperty("data").GetString().Should().Be("iVBORw0KGgo=");
        first.GetProperty("_meta").GetProperty("sbx/browser_id").GetString().Should().Be(BrowserId);
        first.GetProperty("_meta").GetProperty("sbx/browser_instance").GetString().Should().Be("instance-1");
        second.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("called browser_snapshot");
        relay.Calls.Should().Equal("browser_take_screenshot {\"type\":\"png\"}", "browser_snapshot ");
        relay.Initializes.Should().Be(1, "one MCP session serves every call to the same browser and toolkit");
        relay.Paths.Should().OnlyContain(p => p == RelayPath);
    }

    [Fact]
    public async Task Discovery_ListsTheBrowsersTools()
    {
        var relay = new FakeRelay();
        await using var gateway = relay.Gateway();

        var listed = await gateway.CallAsync(BrowserId, "playwright", null, null, CancellationToken.None);

        var tools = JsonDocument.Parse(listed.GetProperty("content")[0].GetProperty("text").GetString()!);
        tools
            .RootElement.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .Should()
            .Equal("browser_navigate");
        listed.GetProperty("_meta").GetProperty("sbx/browser_instance").GetString().Should().Be("instance-1");
    }

    [Fact]
    public async Task McpServerError_IsAToolError_AndTheSessionIsKept()
    {
        var relay = new FakeRelay { RpcErrorFor = "no_such_tool" };
        await using var gateway = relay.Gateway();

        var failed = await gateway.CallAsync(BrowserId, "playwright", "no_such_tool", null, CancellationToken.None);
        _ = await gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        failed.GetProperty("isError").GetBoolean().Should().BeTrue();
        failed
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("Tool no_such_tool not found");
        relay.Initializes.Should().Be(1);
    }

    [Fact]
    public async Task ReplacedBrowser_ReportsTheNewInstance_AndTheNextCallReconnects()
    {
        var relay = new FakeRelay();
        await using var gateway = relay.Gateway();
        _ = await gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        // The replacement answers under a new instance and does not know the old MCP session.
        relay.Instance = "instance-2";
        relay.ForgetSessions = true;
        var replaced = await gateway.CallAsync(
            BrowserId,
            "playwright",
            "browser_snapshot",
            null,
            CancellationToken.None
        );
        relay.ForgetSessions = false;
        var next = await gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        replaced.GetProperty("_meta").GetProperty("sbx/browser_instance").GetString().Should().Be("instance-2");
        replaced.GetProperty("isError").GetBoolean().Should().BeTrue();
        next.GetProperty("_meta").GetProperty("sbx/browser_instance").GetString().Should().Be("instance-2");
        relay.Initializes.Should().Be(2);
    }

    [Fact]
    public async Task RelayRefusesTheConnect_IsATypedSandboxException()
    {
        var relay = new FakeRelay { ConnectError = (503, "browser_capacity") };
        await using var gateway = relay.Gateway();

        var act = () => gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.ErrorCode.Should().Be("browser_capacity");
        thrown.Kind.Should().Be(SandboxErrorKind.Unavailable);
    }

    [Fact]
    public async Task Close_EndsTheMcpSession_DestroysTheBrowser_AndAReopenConnectsAfresh()
    {
        var relay = new FakeRelay();
        await using var gateway = relay.Gateway();
        _ = await gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        await gateway.CloseAsync(BrowserId, CancellationToken.None);
        _ = await gateway.CallAsync(BrowserId, "playwright", "browser_snapshot", null, CancellationToken.None);

        relay.BrowserDeletes.Should().Be(1);
        relay.Initializes.Should().Be(2);
    }

    /// <summary>A browser relay: streamable-HTTP MCP with JSON responses, the instance header on every answer.</summary>
    private sealed class FakeRelay : HttpMessageHandler
    {
        private readonly HashSet<string> _sessions = [];
        private int _nextSession;

        public string Instance { get; set; } = "instance-1";
        public bool ForgetSessions { get; set; }
        public string? RpcErrorFor { get; init; }
        public (int Status, string Code)? ConnectError { get; init; }
        public int Initializes { get; private set; }
        public int BrowserDeletes { get; private set; }
        public List<string> Calls { get; } = [];
        public List<string> Paths { get; } = [];

        public BrowserDebugGateway Gateway()
        {
            var address = new Uri("http://127.0.0.1:3100/");
            return new BrowserDebugGateway(
                new SandboxClient(
                    new SandboxClientOptions(
                        address,
                        "app-1",
                        Convert.ToBase64String(new byte[32]),
                        TimeSpan.FromMinutes(5),
                        TimeSpan.FromSeconds(30)
                    ),
                    new HttpClient(this) { BaseAddress = address }
                )
            );
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete && path == $"/api/v1/browsers/{BrowserId}")
            {
                BrowserDeletes++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Paths.Add(path);
            if (ConnectError is { } error)
            {
                return Json(
                    (HttpStatusCode)error.Status,
                    $$"""{"error":"refused","code":{{error.Status}},"error_code":"{{error.Code}}","retryable":true}"""
                );
            }
            if (request.Method == HttpMethod.Delete)
            {
                return WithInstance(new HttpResponseMessage(HttpStatusCode.OK));
            }
            if (request.Method != HttpMethod.Post)
            {
                return WithInstance(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var method = root.GetProperty("method").GetString();
            var session = request.Headers.TryGetValues("mcp-session-id", out var ids) ? ids.First() : null;
            if (method != "initialize" && (session is null || ForgetSessions || !_sessions.Contains(session)))
            {
                return WithInstance(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (!root.TryGetProperty("id", out var id))
            {
                return WithInstance(new HttpResponseMessage(HttpStatusCode.Accepted));
            }

            object? result = null;
            object? rpcError = null;
            switch (method)
            {
                case "initialize":
                    Initializes++;
                    session = $"mcp-{++_nextSession}";
                    _ = _sessions.Add(session);
                    result = new
                    {
                        protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "fake-playwright", version = "1" },
                    };
                    break;
                case "tools/list":
                    result = new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "browser_navigate",
                                description = "Navigate",
                                inputSchema = new { type = "object" },
                            },
                        },
                    };
                    break;
                case "tools/call":
                    var name = root.GetProperty("params").GetProperty("name").GetString()!;
                    var args = root.GetProperty("params").TryGetProperty("arguments", out var a) ? a.GetRawText() : "";
                    Calls.Add($"{name} {args}");
                    if (name == RpcErrorFor)
                    {
                        rpcError = new { code = -32602, message = $"Tool {name} not found" };
                    }
                    else if (name == "browser_take_screenshot")
                    {
                        result = new
                        {
                            content = new[]
                            {
                                new
                                {
                                    type = "image",
                                    data = "iVBORw0KGgo=",
                                    mimeType = "image/png",
                                },
                            },
                        };
                    }
                    else
                    {
                        result = new { content = new[] { new { type = "text", text = $"called {name}" } } };
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected MCP method {method}");
            }
            var response = Json(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(
                    rpcError is null
                        ? new
                        {
                            jsonrpc = "2.0",
                            id,
                            result,
                        }
                        : (object)
                            new
                            {
                                jsonrpc = "2.0",
                                id,
                                error = rpcError,
                            }
                )
            );
            response.Headers.Add("mcp-session-id", session);
            return WithInstance(response);
        }

        private HttpResponseMessage WithInstance(HttpResponseMessage response)
        {
            response.Headers.Add(SandboxClient.BrowserInstanceHeader, Instance);
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
