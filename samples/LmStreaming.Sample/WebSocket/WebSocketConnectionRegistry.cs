using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace LmStreaming.Sample.WebSocket;

/// <summary>
/// A live chat WebSocket connection registered with <see cref="WebSocketConnectionRegistry"/>.
/// All outbound text frames MUST go through <see cref="TrySendTextAsync"/> — it serializes writers
/// (the per-connection agent stream loop and out-of-band broadcasts such as auth events) behind a
/// single async gate, because <see cref="System.Net.WebSockets.WebSocket.SendAsync(ArraySegment{byte}, WebSocketMessageType, bool, CancellationToken)"/>
/// is not safe for concurrent calls.
/// </summary>
public sealed class RegisteredWebSocketConnection : IDisposable
{
    private readonly System.Net.WebSockets.WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    internal RegisteredWebSocketConnection(string connectionId, string threadId, System.Net.WebSockets.WebSocket socket)
    {
        ConnectionId = connectionId;
        ThreadId = threadId;
        _socket = socket;
    }

    /// <summary>Registry-unique id for this connection.</summary>
    public string ConnectionId { get; }

    /// <summary>The conversation thread this connection streams.</summary>
    public string ThreadId { get; }

    /// <summary>
    /// Sends a single text frame, serialized behind the connection's send gate. Returns false (and
    /// never throws) when the socket is closed or the send fails — a dying connection must not
    /// fault a broadcast or the agent stream loop. Cancellation of <paramref name="ct"/> is also
    /// reported as false rather than thrown so best-effort broadcasts stay quiet on teardown.
    /// </summary>
    public async Task<bool> TrySendTextAsync(string json, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return false;
        }

        try
        {
            await _sendGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _ = _sendGate.Release();
        }
    }

    /// <summary>
    /// Closes the underlying socket. Closing is an outbound operation, so it goes through the wrapper
    /// (not the raw socket) to honor the single-write-path contract. Best-effort: a raced/already-torn
    /// socket throws <see cref="WebSocketException"/>/<see cref="ObjectDisposedException"/>/
    /// <see cref="InvalidOperationException"/>, all swallowed.
    /// </summary>
    public async Task TryCloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await _socket.CloseAsync(status, description, ct);
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Disposes the send gate. Safe to call after the connection's read/write loops have ended; any
    /// in-flight <see cref="TrySendTextAsync"/> already swallows <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose() => _sendGate.Dispose();
}

/// <summary>
/// Tracks the live chat WebSocket connections so backend services (e.g. the deferred-auth
/// coordinator) can push out-of-band frames to connected clients. This is a single-user demo app,
/// so pushes are broadcast to every connection rather than targeted per sandbox session.
/// </summary>
public sealed class WebSocketConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredWebSocketConnection> _connections = new();

    /// <summary>Registers a newly-accepted connection; the caller must <see cref="Unregister"/> it on teardown.</summary>
    public RegisteredWebSocketConnection Register(string threadId, System.Net.WebSockets.WebSocket socket)
    {
        var connection = new RegisteredWebSocketConnection(Guid.NewGuid().ToString("N"), threadId, socket);
        _connections[connection.ConnectionId] = connection;
        return connection;
    }

    /// <summary>Removes a connection from the registry (idempotent) and disposes its send gate.</summary>
    public void Unregister(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            connection.Dispose();
        }
    }

    /// <summary>Point-in-time view of the live connections.</summary>
    public IReadOnlyList<RegisteredWebSocketConnection> Snapshot() => [.. _connections.Values];

    /// <summary>
    /// Best-effort broadcast of a text frame to every live connection, fanned out concurrently so a
    /// single half-open or wedged socket cannot stall delivery to the others. A bounded
    /// <see cref="BroadcastTimeout"/> caps how long any one send may block — out-of-band auth frames
    /// must never wedge the auth path. <see cref="RegisteredWebSocketConnection.TrySendTextAsync"/>
    /// turns a timeout/cancel into a quiet <c>false</c>, so the broadcast itself never throws.
    /// </summary>
    public async Task BroadcastAsync(string json, CancellationToken ct)
    {
        var connections = Snapshot();
        if (connections.Count == 0)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(BroadcastTimeout);
        await Task.WhenAll(connections.Select(c => c.TrySendTextAsync(json, linked.Token)));
    }

    /// <summary>Upper bound on how long any one connection's send may block a broadcast.</summary>
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(5);
}
