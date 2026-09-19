using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     Upgrade-failure behaviour of the REAL <see cref="ClientWebSocketResponsesSocket"/> against a local
///     loopback server. The retry tests in <see cref="CopilotResponsesWebSocketClientRetryTests"/> inject
///     the failure shape through a fake socket; these prove the real adapter actually produces that shape
///     (<see cref="WebSocketException"/> wrapping <see cref="HttpRequestException.StatusCode"/>), which a
///     bare <see cref="ClientWebSocket"/> does not — it throws <c>NotAWebSocket</c> with no inner exception.
/// </summary>
public sealed class CopilotResponsesWebSocketRealSocketTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("gho_test");
    }

    [Fact]
    public async Task Real_adapter_surfaces_a_404_upgrade_as_an_inner_HttpRequestException_with_the_status()
    {
        await using var server = new LoopbackUpgradeServer(rejectFirst: int.MaxValue);
        await using var socket = new ClientWebSocketResponsesSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var act = async () =>
            await socket.ConnectAsync(server.Endpoint, new Dictionary<string, string>(), timeout.Token);

        var thrown = (await act.Should().ThrowAsync<WebSocketException>()).Which;
        thrown.WebSocketErrorCode.Should().Be(WebSocketError.NotAWebSocket);
        thrown
            .InnerException.Should()
            .BeOfType<HttpRequestException>("the retry classifier reads the upgrade status from this inner exception")
            .Which.StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        server.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Real_socket_404_upgrade_is_retried_and_succeeds_when_NotFound_is_opted_in()
    {
        await using var server = new LoopbackUpgradeServer(rejectFirst: 1);
        await using var client = NewClient(
            server.Endpoint,
            RetryOptions.FastForTests with
            {
                AdditionalRetryableStatusCodes = [HttpStatusCode.NotFound],
            }
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var events = await CollectAsync(client.StreamResponseAsync(Request(), timeout.Token));

        events
            .Select(e => e.Type)
            .Should()
            .ContainInOrder(ResponseEventTypes.ResponseCreated, ResponseEventTypes.ResponseCompleted);
        server.Attempts.Should().Be(2, "the first upgrade is rejected with 404 and the retry upgrades to 101");
    }

    [Fact]
    public async Task Real_socket_404_upgrade_fails_after_exactly_one_attempt_under_default_options()
    {
        await using var server = new LoopbackUpgradeServer(rejectFirst: int.MaxValue);
        await using var client = NewClient(server.Endpoint, RetryOptions.FastForTests);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request(), timeout.Token));

        var thrown = (await act.Should().ThrowAsync<WebSocketException>()).Which;
        thrown
            .InnerException.Should()
            .BeOfType<HttpRequestException>()
            .Which.StatusCode.Should()
            .Be(HttpStatusCode.NotFound, "the surfaced failure names the upgrade status");
        server.Attempts.Should().Be(1, "404 is not retryable unless the transport opts it in");
    }

    private static CopilotResponsesWebSocketClient NewClient(Uri endpoint, RetryOptions retryOptions) =>
        // No socket factory: the client builds its default, real ClientWebSocketResponsesSocket.
        new(
            endpoint,
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            options: null,
            logger: null,
            socketFactory: null,
            retryOptions: retryOptions
        );

    private static ResponseCreateRequest Request() =>
        new()
        {
            Model = "gpt-5.5",
            Input = [new ResponseInputItem { Role = "user", Content = [new ResponseInputContent { Text = "hi" }] }],
        };

    private static async Task<List<ResponseEvent>> CollectAsync(IAsyncEnumerable<ResponseEvent> stream)
    {
        var list = new List<ResponseEvent>();
        await foreach (var ev in stream)
        {
            list.Add(ev);
        }

        return list;
    }

    /// <summary>
    ///     Raw-TCP loopback server speaking just enough HTTP/1.1 to answer a WebSocket upgrade. The first
    ///     <c>rejectFirst</c> upgrade requests get <c>404 not_found</c> (the Copilot transient body); later
    ///     ones complete the RFC 6455 handshake and serve one scripted Responses turn per
    ///     <c>response.create</c>. Raw TCP is used instead of Kestrel or <see cref="HttpListener"/> because
    ///     it binds an ephemeral loopback port with no URL ACL, no extra package, and no HTTP.sys
    ///     dependency, so it behaves identically on the Windows CI agent and on a dev box.
    /// </summary>
    private sealed class LoopbackUpgradeServer : IAsyncDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly int _rejectFirst;
        private readonly Task _acceptLoop;
        private int _attempts;

        public LoopbackUpgradeServer(int rejectFirst)
        {
            _rejectFirst = rejectFirst;
            _listener.Start();
            Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/responses");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri Endpoint { get; }

        /// <summary>Upgrade requests received, counted server-side.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        private async Task AcceptLoopAsync()
        {
            var connections = new List<Task>();
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    connections.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }

            await Task.WhenAll(connections);
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestHeadAsync(stream, _stop.Token);
                    var attempt = Interlocked.Increment(ref _attempts);

                    if (attempt <= _rejectFirst)
                    {
                        const string body = "{\"error\":{\"message\":\"\",\"code\":\"not_found\"}}";
                        var reject =
                            "HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\n"
                            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(reject), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        return;
                    }

                    var accept = Convert.ToBase64String(
                        SHA1.HashData(Encoding.ASCII.GetBytes(ReadHeader(request, "Sec-WebSocket-Key") + WebSocketGuid))
                    );
                    var upgrade =
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(upgrade), _stop.Token);
                    await stream.FlushAsync(_stop.Token);

                    using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                    await ServeTurnsAsync(ws, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
            }
        }

        private static async Task ServeTurnsAsync(WebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                foreach (
                    var frame in new[]
                    {
                        "{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp-1\"}}",
                        "{\"type\":\"response.completed\",\"sequence_number\":1,\"response\":{\"id\":\"resp-1\"}}",
                    }
                )
                {
                    await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct);
                }
            }
        }

        private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            // Read byte-by-byte up to the blank line, so nothing past the request head is consumed.
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, ct) == 0)
                {
                    throw new IOException("Connection closed before the upgrade request head was complete.");
                }

                _ = head.Append((char)one[0]);
            }

            return head.ToString();
        }

        private static string ReadHeader(string requestHead, string name) =>
            requestHead
                .Split("\r\n")
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1].Trim())
                .Single();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            _stop.Dispose();
        }
    }
}
