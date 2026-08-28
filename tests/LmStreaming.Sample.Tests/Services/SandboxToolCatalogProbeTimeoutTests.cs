using System.Net;
using System.Net.Sockets;
using System.Text;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The labelled-baseline fallback must be reachable by TIME, not only by an exception.
/// </summary>
/// <remarks>
/// <para>
/// A gateway that refuses connections fails fast, and the session-liveness probe self-bounds at a
/// couple of seconds and then assumes the session is alive. Both of those already reached the
/// fallback. The hole was the step after them: <c>McpClient.CreateAsync</c> ran with no cancellation
/// token and no connection timeout, and it does so while holding the probe's single-entry lock. A
/// gateway healthy enough to hand out a session but wedged on <c>/mcp</c> therefore turned
/// <c>/api/tools</c> — and with it the whole Modes editor — from degraded-but-usable into a hang.
/// </para>
/// <para>
/// That is why the fake below ANSWERS the lifecycle calls and stalls only on <c>/mcp</c>. A fake
/// that stalls everything never gets past session creation, so it exercises the registry's
/// pre-existing bound instead of this one and passes against a probe with no timeout at all. It is a
/// raw socket rather than <c>HttpListener</c> because the probe builds its own <c>HttpClient</c> for
/// the MCP transport, so a stub handler injected into the registry cannot see that call.
/// </para>
/// </remarks>
public sealed class SandboxToolCatalogProbeTimeoutTests
{
    /// <summary>A gateway that serves the session lifecycle normally and never answers on <c>/mcp</c>.</summary>
    private sealed class WedgedOnMcpGateway : IDisposable
    {
        private const string SessionJson =
            "{\"session_id\":\"probe-session\",\"container_id\":\"probe-container\",\"status\":\"running\"}";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _held = [];
        private readonly TaskCompletionSource _mcpReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WedgedOnMcpGateway()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        /// <summary>Completes once the probe has actually reached the wedged <c>/mcp</c> endpoint.</summary>
        public Task McpReached => _mcpReached.Task;

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _ = ServeAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            lock (_held)
            {
                _held.Add(client);
            }

            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                var path = PathOf(request);

                if (path.StartsWith("/mcp", StringComparison.Ordinal))
                {
                    // Read but never answered. The connection is held open until Dispose, which is
                    // precisely the shape of gateway that used to hang the catalog forever.
                    _mcpReached.TrySetResult();
                    return;
                }

                var body = path.StartsWith("/api/v1/sandboxes", StringComparison.Ordinal) ? SessionJson : "{}";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json\r\n"
                        + $"Content-Length: {bytes.Length}\r\n"
                        + "Connection: close\r\n\r\n"
                );

                await stream.WriteAsync(head, _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // A caller that gave up mid-request is the point of this fixture, not a failure of it.
            }
        }

        /// <summary>Reads request head plus body. Returns the head, or empty if the peer hung up.</summary>
        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();

            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var head = text.ToString();
                var end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                // Drain the declared body so the client's write completes and it moves on to waiting
                // for a response, rather than blocking on a full send buffer.
                var length = ContentLength(head[..end]);
                var have = Encoding.UTF8.GetByteCount(head[(end + 4)..]);
                while (have < length)
                {
                    var more = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (more == 0)
                    {
                        break;
                    }

                    have += more;
                }

                return head[..end];
            }

            return text.ToString();
        }

        private static int ContentLength(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(line["Content-Length:".Length..].Trim(), out var value) ? value : 0;
                }
            }

            return 0;
        }

        private static string PathOf(string head)
        {
            var line = head.Split("\r\n")[0].Split(' ');
            var target = line.Length > 1 ? line[1] : "/";
            var query = target.IndexOf('?', StringComparison.Ordinal);
            return query < 0 ? target : target[..query];
        }

        public void Dispose()
        {
            _stop.Cancel();
            lock (_held)
            {
                foreach (var client in _held)
                {
                    client.Dispose();
                }
            }

            _listener.Dispose();
            _stop.Dispose();
        }
    }

    private static SandboxToolCatalogProbe CreateProbe(WedgedOnMcpGateway wedged)
    {
        var options = new SandboxGatewayOptions { BaseUrl = wedged.BaseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient()
        );

        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        return new SandboxToolCatalogProbe(registry, gateway, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task WedgedOnMcp_YieldsTheLabelledBaselineOnceTheProbeBudgetExpires()
    {
        using var wedged = new WedgedOnMcpGateway();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(wedged);

        var pending = probe.GetAsync(time);

        // Advance only AFTER the probe is stuck on /mcp: its budget starts when the probe does, so
        // advancing earlier would expire a timer that does not exist yet and the test would pass
        // without the timeout ever being exercised.
        await wedged.McpReached.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(SandboxToolCatalogProbe.ProbeTimeout + TimeSpan.FromSeconds(1));

        var catalog = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        catalog.IsLive.Should().BeFalse();
        catalog.Warning.Should().NotBeNullOrWhiteSpace();
        catalog.Tools.Select(t => t.Name).Should().BeEquivalentTo(SandboxToolCatalogProbe.StaticBaseline);
    }

    [Fact]
    public async Task WedgedOnMcp_StaysBlockedUntilTheBudgetExpires()
    {
        // The other half of the same claim: the fallback is reached BY the bound, not merely at some
        // point. Without this, a probe that gave up for an unrelated reason satisfies the test above
        // while proving nothing about the timeout.
        using var wedged = new WedgedOnMcpGateway();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(wedged);

        var pending = probe.GetAsync(time);
        await wedged.McpReached.WaitAsync(TimeSpan.FromSeconds(30));

        time.Advance(SandboxToolCatalogProbe.ProbeTimeout - TimeSpan.FromSeconds(1));
        var raced = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(2)));
        raced.Should().NotBeSameAs(pending, "the probe must still be waiting on the wedged gateway");

        time.Advance(TimeSpan.FromSeconds(2));
        (await pending.WaitAsync(TimeSpan.FromSeconds(30))).IsLive.Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_IsNotDisguisedAsAGatewayFailure()
    {
        // The fallback catches the probe's OWN timeout, so it must not also swallow a caller who
        // walked away - that would report a degraded catalog for a request nobody is waiting on.
        using var wedged = new WedgedOnMcpGateway();
        var time = new FakeTimeProvider();
        var probe = CreateProbe(wedged);
        using var caller = new CancellationTokenSource();

        var pending = probe.GetAsync(time, caller.Token);
        await wedged.McpReached.WaitAsync(TimeSpan.FromSeconds(30));

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
