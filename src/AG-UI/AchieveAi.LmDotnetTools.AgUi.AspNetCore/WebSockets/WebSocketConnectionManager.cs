using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.WebSockets;

/// <summary>
/// Thread-safe implementation of WebSocket connection management
/// </summary>
public sealed class WebSocketConnectionManager : IWebSocketConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ILogger<WebSocketConnectionManager> _logger;

    public WebSocketConnectionManager(ILogger<WebSocketConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void AddConnection(string sessionId, WebSocket webSocket)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        }

        if (webSocket == null)
        {
            throw new ArgumentNullException(nameof(webSocket));
        }

        if (_connections.TryAdd(sessionId, webSocket))
        {
            _logger.LogInformation("WebSocket connection added for session {SessionId}. Total connections: {Count}",
                sessionId, _connections.Count);
        }
        else
        {
            _logger.LogWarning("Failed to add WebSocket connection for session {SessionId} - session already exists",
                sessionId);
            throw new InvalidOperationException($"Session {sessionId} already has an active connection");
        }
    }

    /// <inheritdoc/>
    public bool RemoveConnection(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        if (_connections.TryRemove(sessionId, out var webSocket))
        {
            _logger.LogInformation("WebSocket connection removed for session {SessionId}. Remaining connections: {Count}",
                sessionId, _connections.Count);

            // Dispose the WebSocket if it's still open
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session closed", CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing WebSocket for session {SessionId}", sessionId);
                }
            }

            webSocket.Dispose();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public WebSocket? GetConnection(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        return _connections.TryGetValue(sessionId, out var webSocket) ? webSocket : null;
    }

    /// <inheritdoc/>
    public IEnumerable<string> GetActiveSessions()
    {
        return _connections.Keys.ToList();
    }

    /// <inheritdoc/>
    public int ConnectionCount => _connections.Count;
}
