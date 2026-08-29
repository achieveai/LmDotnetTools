using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A minimal in-memory <see cref="System.Net.WebSockets.WebSocket"/> test double: captures every
/// outbound text frame and feeds inbound frames from an in-memory queue.
/// </summary>
/// <remarks>
/// Shared by the <c>ChatWebSocketManager</c> suites that only need to drive frames in and read frames
/// out. <c>ChatWebSocketManagerSubAgentTests</c> keeps its own nested variant, which carries extra
/// seams (fragment/binary enqueue, a send callback, close-description capture) that exist for its
/// stream-recovery assertions and would be dead weight here.
/// </remarks>
internal sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
{
    private readonly Channel<InboundFrame> _inbound = Channel.CreateUnbounded<InboundFrame>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    private readonly List<string> _sent = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _activity = new(0);
    private WebSocketState _state = WebSocketState.Open;
    private InboundFrame? _current;
    private int _currentOffset;

    private readonly record struct InboundFrame(byte[] Payload, WebSocketMessageType Type, bool EndOfMessage);

    public bool CloseAsyncCalled { get; private set; }

    public WebSocketCloseStatus? LastCloseStatus { get; private set; }

    public IReadOnlyList<string> SentFrames
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public bool SentContains(string fragment)
    {
        lock (_lock)
        {
            return _sent.Any(f => f.Contains(fragment, StringComparison.Ordinal));
        }
    }

    public void EnqueueTextFrame(string text) =>
        _inbound.Writer.TryWrite(
            new InboundFrame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, EndOfMessage: true)
        );

    public async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await _activity.WaitAsync(ct);
        }
    }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken
    )
    {
        var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
        lock (_lock)
        {
            _sent.Add(text);
        }
        _ = _activity.Release();
        return Task.CompletedTask;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        if (_current is null)
        {
            _current = await _inbound.Reader.ReadAsync(cancellationToken);
            _currentOffset = 0;
        }

        var frame = _current.Value;

        if (frame.Type == WebSocketMessageType.Close)
        {
            _current = null;
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
        }

        var remaining = frame.Payload.Length - _currentOffset;
        var count = Math.Min(remaining, buffer.Count);
        Array.Copy(frame.Payload, _currentOffset, buffer.Array!, buffer.Offset, count);
        _currentOffset += count;

        bool endOfMessage;
        if (_currentOffset >= frame.Payload.Length)
        {
            endOfMessage = frame.EndOfMessage;
            _current = null;
        }
        else
        {
            endOfMessage = false;
        }

        return new WebSocketReceiveResult(count, frame.Type, endOfMessage);
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    )
    {
        CloseAsyncCalled = true;
        LastCloseStatus = closeStatus;
        _state = WebSocketState.Closed;
        _ = _activity.Release();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    )
    {
        _state = WebSocketState.Closed;
        _ = _activity.Release();
        return Task.CompletedTask;
    }

    public override void Abort() => _state = WebSocketState.Aborted;

    public override void Dispose() { }
}
