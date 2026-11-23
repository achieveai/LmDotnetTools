using System.Net.WebSockets;
using System.Text;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// Records sent frames and the close. With <see cref="AnswersClose"/> off, a close waits for a close frame that never
/// comes, as a real socket does when the peer goes silent, until its token is cancelled.
/// </summary>
internal sealed class RefusalSocket : System.Net.WebSockets.WebSocket
{
    private readonly List<string> _sent = [];
    private readonly Lock _lock = new();
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;

    public bool AnswersClose { get; init; } = true;

    public bool Aborted { get; private set; }

    public string? CloseDescription { get; private set; }

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

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => CloseDescription;
    public override string? SubProtocol => null;

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken
    )
    {
        lock (_lock)
        {
            _sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        }

        return Task.CompletedTask;
    }

    /// <summary>The refusal is sent before any receive starts, so nothing arrives.</summary>
    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new OperationCanceledException(cancellationToken);
    }

    public override async Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    )
    {
        _closeStatus = closeStatus;
        CloseDescription = statusDescription;
        _state = WebSocketState.CloseSent;
        if (!AnswersClose)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        _state = WebSocketState.Closed;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    ) => CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        Aborted = true;
        _state = WebSocketState.Aborted;
    }

    public override void Dispose() { }
}
