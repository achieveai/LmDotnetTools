using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// The failure text of the #936 assertions comes from <see cref="FrameCollection.Describe"/> and from the
/// reason <see cref="WebSocketTestClient.CollectUntilDoneAsync"/> records when it stops. Both only show up
/// when a test fails, so they are pinned here instead, without a host or a socket.
/// </summary>
public sealed class WebSocketTestClientTests
{
    [Fact]
    public void Describe_of_an_empty_collection_names_the_reason_and_no_frames()
    {
        using var frames = new FrameCollection([], "the server closed the socket (NormalClosure: bye)");

        frames.Describe().Should().Be("the server closed the socket (NormalClosure: bye); 0 frame(s): []");
    }

    [Fact]
    public void Describe_of_mixed_frames_lists_each_type_and_marks_the_untyped_ones()
    {
        using var frames = new FrameCollection(
            [
                JsonDocument.Parse("""{"$type":"error","message":"boom"}"""),
                JsonDocument.Parse("""{"text":"no type here"}"""),
                JsonDocument.Parse("""["not","an","object"]"""),
                JsonDocument.Parse("""{"$type":7}"""),
            ],
            "the done frame arrived"
        );

        frames.Describe().Should().Be("the done frame arrived; 4 frame(s): [error, ?, ?, ?]");
    }

    [Fact]
    public async Task A_timeout_between_frames_is_reported_as_the_timeout_not_as_an_open_socket()
    {
        // The frame is handed over only once the timeout has fired, so the loop stops between frames with
        // the socket still open: exactly the case the reason used to mislabel.
        await using var client = new WebSocketTestClient(new FrameAfterCancellationSocket());

        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromMilliseconds(50));

        frames.Count.Should().Be(1);
        frames.EndReason.Should().Be($"the {TimeSpan.FromMilliseconds(50)} timeout expired between frames");
    }

    [Fact]
    public async Task A_caller_cancellation_between_frames_is_reported_as_the_caller()
    {
        await using var client = new WebSocketTestClient(new FrameAfterCancellationSocket());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromMinutes(1), cts.Token);

        frames.Count.Should().Be(1);
        frames.EndReason.Should().Be("the caller cancelled");
    }

    /// <summary>
    /// An open socket that delivers one complete text frame, but only after the receive's token is
    /// cancelled, and without honouring that token. Any later receive is a test failure.
    /// </summary>
    private sealed class FrameAfterCancellationSocket : System.Net.WebSockets.WebSocket
    {
        private static readonly byte[] Frame = Encoding.UTF8.GetBytes("""{"$type":"text","text":"partial"}""");
        private WebSocketState _state = WebSocketState.Open;
        private int _receives;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            if (Interlocked.Increment(ref _receives) > 1)
            {
                throw new InvalidOperationException("The client read past a cancellation it had already seen.");
            }

            // A real wait on the token's state, not a guess at how long the timeout takes. The 10 s bound
            // only keeps a broken token from hanging the test.
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(5, bound.Token);
            }

            Frame.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(Frame.Length, WebSocketMessageType.Text, endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken
        )
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken
        ) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
