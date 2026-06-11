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
public sealed class RegisteredWebSocketConnection
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

    /// <summary>Removes a connection from the registry (idempotent).</summary>
    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Point-in-time view of the live connections.</summary>
    public IReadOnlyList<RegisteredWebSocketConnection> Snapshot() => [.. _connections.Values];

    /// <summary>Best-effort broadcast of a text frame to every live connection.</summary>
    public async Task BroadcastAsync(string json, CancellationToken ct)
    {
        foreach (var connection in Snapshot())
        {
            _ = await connection.TrySendTextAsync(json, ct);
        }
    }
}
