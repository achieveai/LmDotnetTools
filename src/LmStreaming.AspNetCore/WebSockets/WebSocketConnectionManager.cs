using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.WebSockets;

/// <summary>
/// Thread-safe WebSocket connection manager.
/// </summary>
public sealed class WebSocketConnectionManager : IWebSocketConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public int ConnectionCount => _connections.Count;

    public void AddConnection(string connectionId, WebSocket webSocket)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(webSocket);
        _connections[connectionId] = webSocket;
    }

    public bool RemoveConnection(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        return _connections.TryRemove(connectionId, out _);
    }

    public WebSocket? GetConnection(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        return _connections.TryGetValue(connectionId, out var webSocket) ? webSocket : null;
    }

    public IEnumerable<string> GetActiveConnections()
    {
        return _connections.Keys;
    }
}
