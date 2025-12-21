using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.WebSockets;

/// <summary>
/// Handles WebSocket connections for streaming IMessage directly.
/// </summary>
public sealed class IMessageWebSocketHandler
{
    private readonly IWebSocketConnectionManager _connectionManager;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<IMessageWebSocketHandler> _logger;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    public IMessageWebSocketHandler(
        IWebSocketConnectionManager connectionManager,
        ILogger<IMessageWebSocketHandler> logger,
        IOptions<LmStreamingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionManager = connectionManager;
        _logger = logger;
        _jsonOptions = options.Value.WriteIndentedJson
            ? JsonSerializerOptionsFactory.CreateForTesting()
            : JsonSerializerOptionsFactory.CreateForProduction();
    }

    /// <summary>
    /// Handles a WebSocket connection lifecycle.
    /// </summary>
    public async Task HandleWebSocketAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required", cancellationToken);
            return;
        }

        WebSocket? webSocket = null;
        string? connectionId = null;

        try
        {
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
            connectionId = context.Request.Query["connectionId"].FirstOrDefault() ?? Guid.NewGuid().ToString();

            _connectionManager.AddConnection(connectionId, webSocket);
            _logger.LogInformation("WebSocket connection established: {ConnectionId}", connectionId);

            await ReceiveMessagesAsync(webSocket, connectionId, cancellationToken);

            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Connection closed", cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for connection {ConnectionId}", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket operation cancelled for connection {ConnectionId}", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling WebSocket for connection {ConnectionId}", connectionId);

            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.InternalServerError, "Server error", cancellationToken);
            }
        }
        finally
        {
            if (connectionId != null)
            {
                _ = _connectionManager.RemoveConnection(connectionId);
            }
            webSocket?.Dispose();
        }
    }

    /// <summary>
    /// Sends an IMessage to a specific connection.
    /// </summary>
    public async Task SendMessageAsync(string connectionId, IMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var webSocket = _connectionManager.GetConnection(connectionId);
        if (webSocket == null || webSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot send message - connection {ConnectionId} not available", connectionId);
            return;
        }

        await SendMessageAsync(webSocket, message, cancellationToken);
    }

    /// <summary>
    /// Streams messages from an IAsyncEnumerable to a specific connection.
    /// </summary>
    public async Task StreamMessagesAsync(
        string connectionId,
        IAsyncEnumerable<IMessage> messages,
        CancellationToken cancellationToken)
    {
        var webSocket = _connectionManager.GetConnection(connectionId);
        if (webSocket == null || webSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot stream messages - connection {ConnectionId} not available", connectionId);
            return;
        }

        await foreach (var message in messages.WithCancellation(cancellationToken))
        {
            if (webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("WebSocket closed during streaming for connection {ConnectionId}", connectionId);
                break;
            }

            await SendMessageAsync(webSocket, message, cancellationToken);
        }
    }

    private async Task SendMessageAsync(WebSocket webSocket, IMessage message, CancellationToken cancellationToken)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var json = JsonSerializer.Serialize(message, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

            _logger.LogDebug("Sent message type: {MessageType}", message.GetType().Name);
        }
        finally
        {
            _ = _sendSemaphore.Release();
        }
    }

    private async Task ReceiveMessagesAsync(WebSocket webSocket, string connectionId, CancellationToken cancellationToken)
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
                        _logger.LogInformation("Client initiated close for connection {ConnectionId}", connectionId);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _ = messageBuilder.Append(chunk);
                    }
                } while (!result.EndOfMessage);

                if (messageBuilder.Length > 0)
                {
                    var json = messageBuilder.ToString();
                    ProcessReceivedMessage(connectionId, json);
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("WebSocket connection closed prematurely for {ConnectionId}", connectionId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ProcessReceivedMessage(string connectionId, string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<IMessage>(json, _jsonOptions);
            if (message != null)
            {
                _logger.LogDebug("Received message type: {MessageType} from {ConnectionId}", message.GetType().Name, connectionId);
                OnMessageReceived?.Invoke(this, new MessageReceivedEventArgs(connectionId, message));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON received from connection {ConnectionId}", connectionId);
        }
    }

    private async Task CloseWebSocketAsync(
        WebSocket webSocket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
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

    /// <summary>
    /// Event raised when a message is received from a client.
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;
}

/// <summary>
/// Event arguments for received messages.
/// </summary>
public sealed class MessageReceivedEventArgs : EventArgs
{
    public MessageReceivedEventArgs(string connectionId, IMessage message)
    {
        ConnectionId = connectionId;
        Message = message;
    }

    public string ConnectionId { get; }
    public IMessage Message { get; }
}
