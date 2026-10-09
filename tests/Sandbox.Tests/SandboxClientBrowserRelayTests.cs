using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// The browser relay surface (gateway <c>browser/relay.rs</c>, SandboxedOstoolsMcpServer origin/master
/// 7f19413): CDP <c>/json/version</c>, the CDP WebSocket upgrade, the bundled MCP servers, and the browser
/// error vocabulary. Everything runs over <see cref="FakeGatewayHandler"/>; WebSocket upgrades are answered
/// with a real <c>101</c> over an in-memory duplex stream.
/// </summary>
public sealed class SandboxClientBrowserRelayTests
{
    private const string Instance = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData(503, "browser_capacity", SandboxErrorKind.Unavailable, true)]
    [InlineData(503, "browser_launch_failed", SandboxErrorKind.Unavailable, true)]
    [InlineData(502, "browser_unreachable", SandboxErrorKind.Unavailable, true)]
    [InlineData(502, "browser_restarted", SandboxErrorKind.BrowserRestarted, true)]
    [InlineData(404, "browser_unsupported", SandboxErrorKind.BrowserUnsupported, false)]
    [InlineData(404, "unknown_browser_mcp", SandboxErrorKind.Protocol, false)]
    [InlineData(400, "invalid_browser_id", SandboxErrorKind.Protocol, false)]
    [InlineData(400, "invalid_cdp_path", SandboxErrorKind.Protocol, false)]
    [InlineData(413, "body_too_large", SandboxErrorKind.Protocol, false)]
    [InlineData(500, "cdp_upgrade_rejected", SandboxErrorKind.NotFound, false)]
    public async Task BrowserVersion_GatewayBrowserErrorCode_ClassifiesByCode(
        int status,
        string errorCode,
        SandboxErrorKind expected,
        bool retryable
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(_ => true, _ => GatewayError(status, errorCode, retryable));

        var act = () => client.GetBrowserVersionAsync("b1");

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(expected);
        thrown.ErrorCode.Should().Be(errorCode);
        thrown.Retryable.Should().Be(retryable);
    }

    [Fact]
    public async Task BrowserVersion_SurfacesInstance_AndReducesTheDebuggerUrlToAPathOnThisGateway()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(
            r => r.RequestUri!.AbsolutePath == "/api/v1/browsers/b1/cdp/json/version",
            _ =>
                WithInstance(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"Browser":"Chrome/153.0.8010.12","Protocol-Version":"1.3","webSocketDebuggerUrl":"ws://elsewhere.invalid:9/api/v1/browsers/b1/cdp/devtools/browser/abc"}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                )
        );

        var version = await client.GetBrowserVersionAsync("b1");

        version.BrowserInstance.Should().Be(Instance);
        version.Browser.Should().Be("Chrome/153.0.8010.12");
        version.ProtocolVersion.Should().Be("1.3");
        version.DebuggerPath.Should().Be("/devtools/browser/abc");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.SbxAppId.Should().Be("app-1");
        request.SbxAppKey.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("/devtools/../json")]
    [InlineData("devtools/page/x")]
    [InlineData("/devtools/page/x?y=1")]
    [InlineData("ws://gw/api/v1/browsers/other/cdp/devtools/page/x")]
    [InlineData("http://gw/api/v1/browsers/b1/cdp/devtools/page/x")]
    public async Task ConnectCdp_RefusesATargetOutsideThisBrowsersCdpEndpoint(string target)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        var act = () => client.ConnectBrowserCdpAsync("b1", target);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectCdp_RejectedUpgrade_IsTheGatewaysTypedError_WithRetryAfter()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(_ => true, _ => GatewayError(503, "browser_capacity", retryable: true, retryAfterSeconds: 5));

        var act = () => client.ConnectBrowserCdpAsync("b1", "/devtools/page/T1");

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Unavailable);
        thrown.ErrorCode.Should().Be("browser_capacity");
        thrown.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectCdp_UpgradesWithAppHeadersOnThisGateway_AndSignalsABrowserRestart()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var server = new FakeWebSocketServer();
        handler.On(_ => true, server.Accept);

        using var cdp = await client.ConnectBrowserCdpAsync(
            "b1",
            "ws://attacker.invalid/api/v1/browsers/b1/cdp/devtools/page/T1"
        );

        var upgrade = handler.Requests.Should().ContainSingle().Subject;
        upgrade.Uri.Host.Should().Be("127.0.0.1");
        upgrade.Uri.AbsolutePath.Should().Be("/api/v1/browsers/b1/cdp/devtools/page/T1");
        upgrade.SbxAppId.Should().Be("app-1");
        upgrade.SbxAppKey.Should().NotBeNullOrEmpty();
        cdp.BrowserInstance.Should().Be(Instance);

        await cdp.SendTextAsync("""{"id":1,"method":"Browser.getVersion"}""");
        (await server.ReceiveTextAsync()).Should().Contain("Browser.getVersion");
        await server.SendTextAsync("""{"id":1,"result":{}}""");
        (await cdp.ReceiveTextAsync()).Should().Be("""{"id":1,"result":{}}""");

        // relay.rs restart_frame(): the browser was destroyed under an open socket.
        await server.CloseAsync(WebSocketCloseStatus.InternalServerError, "browser_restarted");
        var act = () => cdp.ReceiveTextAsync();

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.BrowserRestarted);
        thrown.ErrorCode.Should().Be("browser_restarted");
        thrown.Retryable.Should().Be(true);
    }

    [Fact]
    public async Task ConnectCdp_ANormalCloseIsEndOfStream_NotARestart()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var server = new FakeWebSocketServer();
        handler.On(_ => true, server.Accept);
        using var cdp = await client.ConnectBrowserCdpAsync("b1", "/devtools/page/T1");

        await server.CloseAsync(WebSocketCloseStatus.NormalClosure, "target closed");

        (await cdp.ReceiveTextAsync()).Should().BeNull();
    }

    [Fact]
    public async Task CdpClose_DoesNotWaitForAPeerThatDropsTheConnection()
    {
        // Observed on :3100: on a client close the relay closes upstream and drops the client connection
        // without echoing a close frame, so a full close handshake fails.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var server = new FakeWebSocketServer();
        handler.On(_ => true, server.Accept);
        using var cdp = await client.ConnectBrowserCdpAsync("b1", "/devtools/page/T1");

        var closing = cdp.CloseAsync();
        (await server.ReceiveCloseAsync()).Should().Be(WebSocketCloseStatus.NormalClosure);
        server.Drop();

        await closing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectMcp_ListsToolsThroughTheRelay_WithAppHeaders_AndTracksTheInstance()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var mcp = new FakeMcpServer(handler);
        handler.On(r => r.RequestUri!.AbsolutePath == "/api/v1/browsers/b1/mcp/playwright", mcp.Respond);

        await using var session = await client.ConnectBrowserMcpAsync("b1", BrowserMcpServer.Playwright);
        var tools = await session.Client.ListToolsAsync();

        tools.Select(t => t.Name).Should().Equal("browser_navigate");
        session.BrowserInstance.Should().Be(Instance);
        session.IsBrowserReplaced.Should().BeFalse();
        handler
            .Requests.Where(r => r.Method == HttpMethod.Post)
            .Should()
            .OnlyContain(r => r.SbxAppId == "app-1" && r.SbxAppKey != null);

        mcp.Instance = "replacement-instance";
        _ = await session.Client.ListToolsAsync();
        session.LatestBrowserInstance.Should().Be("replacement-instance");
        session.IsBrowserReplaced.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectMcp_GatewayRelayError_IsTypedNotAnHttpFailure()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(_ => true, _ => GatewayError(503, "browser_capacity", retryable: true, retryAfterSeconds: 5));

        var act = () => client.ConnectBrowserMcpAsync("b1", BrowserMcpServer.DevTools);

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Unavailable);
        thrown.ErrorCode.Should().Be("browser_capacity");
        thrown.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
        handler.Requests.Should().OnlyContain(r => r.Uri.AbsolutePath == "/api/v1/browsers/b1/mcp/devtools");
    }

    private static HttpResponseMessage GatewayError(
        int status,
        string errorCode,
        bool retryable,
        int? retryAfterSeconds = null
    )
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(
                $$"""{"error":"x","code":{{status}},"error_code":"{{errorCode}}","retryable":{{(
                    retryable ? "true" : "false"
                )}}}""",
                Encoding.UTF8,
                "application/json"
            ),
        };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static HttpResponseMessage WithInstance(HttpResponseMessage response, string instance = Instance)
    {
        response.Headers.Add(SandboxClient.BrowserInstanceHeader, instance);
        return response;
    }

    /// <summary>A minimal streamable-HTTP MCP server: JSON responses, a session id, no server stream.</summary>
    private sealed class FakeMcpServer(FakeGatewayHandler handler)
    {
        public string Instance { get; set; } = SandboxClientBrowserRelayTests.Instance;

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.Method != HttpMethod.Post)
            {
                return WithInstance(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed), Instance);
            }

            using var body = JsonDocument.Parse(handler.Requests[^1].Body!);
            var root = body.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return WithInstance(new HttpResponseMessage(HttpStatusCode.Accepted), Instance);
            }

            object result = root.GetProperty("method").GetString() switch
            {
                "initialize" => new
                {
                    protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "fake-playwright", version = "1" },
                },
                "tools/list" => new
                {
                    tools = new[] { new { name = "browser_navigate", inputSchema = new { type = "object" } } },
                },
                var other => throw new InvalidOperationException($"Unexpected MCP method {other}"),
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            jsonrpc = "2.0",
                            id,
                            result,
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
            response.Headers.Add("mcp-session-id", "session-1");
            return WithInstance(response, Instance);
        }
    }

    /// <summary>Answers a WebSocket upgrade with a real <c>101</c> over an in-memory duplex stream.</summary>
    private sealed class FakeWebSocketServer
    {
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private WebSocket? _socket;
        private DuplexStream? _serverStream;

        private WebSocket Socket => _socket ?? throw new InvalidOperationException("Not upgraded.");

        public HttpResponseMessage Accept(HttpRequestMessage request)
        {
            var key = request.Headers.GetValues("Sec-WebSocket-Key").Single();
            var toServer = new Pipe();
            var toClient = new Pipe();
            _serverStream = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
            _socket = WebSocket.CreateFromStream(
                _serverStream,
                new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan }
            );
            var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
            {
                Content = new UpgradedStreamContent(
                    new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream())
                ),
            };
            response.Headers.Connection.Add("Upgrade");
            response.Headers.Upgrade.Add(new("websocket"));
            response.Headers.Add(
                "Sec-WebSocket-Accept",
                Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)))
            );
            return WithInstance(response);
        }

        public Task SendTextAsync(string text) =>
            Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<string> ReceiveTextAsync()
        {
            var buffer = new byte[64 * 1024];
            var result = await Socket.ReceiveAsync(buffer, CancellationToken.None);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }

        public async Task<WebSocketCloseStatus?> ReceiveCloseAsync()
        {
            var result = await Socket.ReceiveAsync(new byte[1024], CancellationToken.None);
            result.MessageType.Should().Be(WebSocketMessageType.Close);
            return result.CloseStatus;
        }

        public Task CloseAsync(WebSocketCloseStatus status, string reason) =>
            Socket.CloseOutputAsync(status, reason, CancellationToken.None);

        /// <summary>Ends the connection without a close frame.</summary>
        public void Drop() => _serverStream!.Dispose();
    }

    /// <summary>Hands the upgraded connection itself to the client, as SocketsHttpHandler does after a 101.</summary>
    private sealed class UpgradedStreamContent(Stream connection) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(connection);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => connection;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            read.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => read.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => write.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            write.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
