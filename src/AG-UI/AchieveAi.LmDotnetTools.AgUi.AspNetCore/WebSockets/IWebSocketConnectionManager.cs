using System.Net.WebSockets;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.WebSockets;

/// <summary>
///     Manages WebSocket connections and their association with AG-UI sessions
/// </summary>
public interface IWebSocketConnectionManager
{
    /// <summary>
    ///     Gets the count of active connections
    /// </summary>
    int ConnectionCount { get; }

    /// <summary>
    ///     Registers a new WebSocket connection for a session
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="webSocket">WebSocket connection</param>
    void AddConnection(string sessionId, WebSocket webSocket);

    /// <summary>
    ///     Removes a WebSocket connection for a session
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <returns>True if connection was removed, false if not found</returns>
    bool RemoveConnection(string sessionId);

    /// <summary>
    ///     Gets the WebSocket connection for a session
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <returns>WebSocket connection if found, null otherwise</returns>
    WebSocket? GetConnection(string sessionId);

    /// <summary>
    ///     Gets all active session IDs
    /// </summary>
    /// <returns>Collection of active session IDs</returns>
    IEnumerable<string> GetActiveSessions();
}
