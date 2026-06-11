using System.Net.WebSockets;
using LmStreaming.Sample.WebSocket;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// Pins the single load-bearing behavior of <see cref="RegisteredWebSocketConnection"/>: it exists
/// to serialize concurrent writes to one socket, because the agent stream loop and out-of-band auth
/// broadcasts now both write the same socket and <c>WebSocket.SendAsync</c>
/// is not concurrency-safe. Dropping the send-gate would pass every other test and produce
/// intermittent interleaved-frame corruption — this test fails loudly instead.
/// </summary>
public class WebSocketConnectionRegistryTests
{
    /// <summary>A fake socket whose <c>SendAsync</c> records the peak number of overlapping calls.</summary>
    private sealed class ConcurrencyTrackingWebSocket : System.Net.WebSockets.WebSocket
    {
        private int _inFlight;
        public int MaxConcurrent;
        public int SendCount;

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlight);
            UpdateMax(current);
            _ = Interlocked.Increment(ref SendCount);
            try
            {
                // Hold the "write" open briefly so an ungated caller would overlap here.
                await Task.Delay(5, cancellationToken);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMax(int candidate)
        {
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref MaxConcurrent);
                if (candidate <= snapshot)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref MaxConcurrent, candidate, snapshot) != snapshot);
        }

        public override void Abort() { }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task TrySendTextAsync_serializes_concurrent_sends_to_one_socket()
    {
        var fake = new ConcurrencyTrackingWebSocket();
        var registry = new WebSocketConnectionRegistry();
        var connection = registry.Register("thread-1", fake);

        const int senders = 50;
        var sends = Enumerable.Range(0, senders)
            .Select(i => Task.Run(() => connection.TrySendTextAsync($"{{\"i\":{i}}}", CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(sends);

        results.Should().OnlyContain(ok => ok, "every send to an open socket must succeed");
        fake.SendCount.Should().Be(senders, "no send should be dropped");
        fake.MaxConcurrent.Should().Be(1, "the send-gate must serialize concurrent writes to one socket");
    }
}
