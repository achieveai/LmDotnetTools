using System.Net.WebSockets;

namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.WebSockets;

/// <summary>
/// Manages WebSocket connections for IMessage streaming.
/// </summary>
public interface IWebSocketConnectionManager
{
    /// <summary>
    /// Adds a WebSocket connection.
    /// </summary>
    void AddConnection(string connectionId, WebSocket webSocket);

    /// <summary>
    /// Removes a WebSocket connection.
    /// </summary>
    bool RemoveConnection(string connectionId);

    /// <summary>
    /// Gets a WebSocket connection by ID.
    /// </summary>
    WebSocket? GetConnection(string connectionId);

    /// <summary>
    /// Gets all active connection IDs.
    /// </summary>
    IEnumerable<string> GetActiveConnections();

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    int ConnectionCount { get; }
}
