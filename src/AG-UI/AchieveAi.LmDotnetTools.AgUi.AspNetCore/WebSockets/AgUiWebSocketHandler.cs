using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.WebSockets;

/// <summary>
/// Handles WebSocket connections for AG-UI protocol communication
/// </summary>
public sealed class AgUiWebSocketHandler
{
    private readonly IWebSocketConnectionManager _connectionManager;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<AgUiWebSocketHandler> _logger;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    public AgUiWebSocketHandler(
        IWebSocketConnectionManager connectionManager,
        IEventPublisher eventPublisher,
        ILogger<AgUiWebSocketHandler> logger)
    {
        _connectionManager = connectionManager;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>
    /// Handles a WebSocket connection lifecycle
    /// </summary>
    /// <param name="context">HTTP context containing the WebSocket request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task HandleWebSocketAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required", cancellationToken);
            return;
        }

        WebSocket? webSocket = null;
        string? sessionId = null;

        try
        {
            // Accept the WebSocket connection
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogInformation("WebSocket connection accepted");

            // Generate session ID (or retrieve from query parameters if provided)
            var sessionIdFromQuery = context.Request.Query["sessionId"].FirstOrDefault();
            sessionId = sessionIdFromQuery ?? Guid.NewGuid().ToString();
            _logger.LogInformation("[DEBUG] WebSocket session ID from query: {QuerySessionId}, Final session ID: {SessionId}, Generated new: {Generated}",
                sessionIdFromQuery ?? "NULL", sessionId, sessionIdFromQuery == null);

            // Register the connection
            _connectionManager.AddConnection(sessionId, webSocket);
            _logger.LogDebug("[DEBUG] Registered WebSocket connection for session {SessionId}", sessionId);

            // Send session-started event
            await SendSessionStartedEventAsync(webSocket, sessionId, cancellationToken);

            // Start event streaming task
            var eventStreamingTask = StreamEventsToClientAsync(webSocket, sessionId, cancellationToken);

            // Start message receiving task
            var messageReceivingTask = ReceiveMessagesFromClientAsync(webSocket, sessionId, cancellationToken);

            // Wait for either task to complete
            await Task.WhenAny(eventStreamingTask, messageReceivingTask);

            // Clean shutdown
            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Session ended", cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for session {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket operation cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling WebSocket for session {SessionId}", sessionId);

            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.InternalServerError, "Server error", cancellationToken);
            }
        }
        finally
        {
            // Clean up connection
            if (sessionId != null)
            {
                _connectionManager.RemoveConnection(sessionId);
                _eventPublisher.Unsubscribe(sessionId);
            }

            webSocket?.Dispose();
        }
    }

    /// <summary>
    /// Sends the session-started event to the client
    /// </summary>
    private async Task SendSessionStartedEventAsync(WebSocket webSocket, string sessionId, CancellationToken cancellationToken)
    {
        var sessionStartedEvent = new SessionStartedEvent
        {
            SessionId = sessionId,
            StartedAt = DateTime.UtcNow
        };

        await SendEventAsync(webSocket, sessionStartedEvent, cancellationToken);
    }

    /// <summary>
    /// Streams AG-UI events to the WebSocket client
    /// </summary>
    private async Task StreamEventsToClientAsync(WebSocket webSocket, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _eventPublisher.SubscribeAsync(sessionId, cancellationToken))
            {
                if (webSocket.State != WebSocketState.Open)
                {
                    _logger.LogWarning("WebSocket not open for session {SessionId}, stopping event stream", sessionId);
                    break;
                }

                await SendEventAsync(webSocket, evt, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Event streaming cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming events for session {SessionId}", sessionId);
            throw;
        }
    }

    /// <summary>
    /// Receives messages from the WebSocket client
    /// </summary>
    private async Task ReceiveMessagesFromClientAsync(WebSocket webSocket, string sessionId, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var messageBuilder = new StringBuilder();

                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Client initiated WebSocket close for session {SessionId}", sessionId);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var messageChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(messageChunk);
                    }
                }
                while (!result.EndOfMessage);

                if (messageBuilder.Length > 0)
                {
                    var message = messageBuilder.ToString();
                    await ProcessClientMessageAsync(webSocket, sessionId, message, cancellationToken);
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("WebSocket connection closed prematurely for session {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Message receiving cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving messages for session {SessionId}", sessionId);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Processes a message received from the client
    /// </summary>
    private async Task ProcessClientMessageAsync(WebSocket webSocket, string sessionId, string message, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Received message from client for session {SessionId}: {Message}", sessionId, message);

            // For Phase 4, we'll implement basic message handling
            // Full protocol handler integration will be in later phases
            // For now, just log the message (no specific acknowledgment event needed)
            _logger.LogDebug("Message processed successfully for session {SessionId}", sessionId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON received from client for session {SessionId}", sessionId);

            var errorEvent = new ErrorEvent
            {
                SessionId = sessionId,
                ErrorCode = "INVALID_JSON",
                Message = "Invalid JSON format",
                Recoverable = true
            };

            await SendEventAsync(webSocket, errorEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing client message for session {SessionId}", sessionId);

            var errorEvent = new ErrorEvent
            {
                SessionId = sessionId,
                ErrorCode = "PROCESSING_ERROR",
                Message = "Failed to process message",
                Recoverable = false
            };

            await SendEventAsync(webSocket, errorEvent, cancellationToken);
        }
    }

    /// <summary>
    /// Sends an AG-UI event to the WebSocket client
    /// </summary>
    private async Task SendEventAsync(WebSocket webSocket, AgUiEventBase evt, CancellationToken cancellationToken)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("Cannot send event - WebSocket not open");
                return;
            }

            var json = JsonSerializer.Serialize(evt, AgUiJsonOptions.Default);
            var bytes = Encoding.UTF8.GetBytes(json);

            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);

            _logger.LogDebug("Sent event {EventType} for session {SessionId}", evt.Type, evt.SessionId);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    /// <summary>
    /// Closes the WebSocket connection gracefully
    /// </summary>
    private async Task CloseWebSocketAsync(WebSocket webSocket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseAsync(status, description, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket: {Error}", ex.Message);
            }
        }
    }
}
