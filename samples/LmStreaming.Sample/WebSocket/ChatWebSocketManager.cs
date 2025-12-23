using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using LmStreaming.Sample.Agents;

namespace LmStreaming.Sample.WebSocket;

/// <summary>
/// Manages WebSocket connections and bridges them to MultiTurnAgentPool.
/// Each WebSocket connection is associated with a threadId for conversation routing.
/// </summary>
public sealed class ChatWebSocketManager
{
    private readonly MultiTurnAgentPool _agentPool;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<ChatWebSocketManager> _logger;

    public ChatWebSocketManager(
        MultiTurnAgentPool agentPool,
        ILogger<ChatWebSocketManager> logger)
    {
        _agentPool = agentPool ?? throw new ArgumentNullException(nameof(agentPool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jsonOptions = JsonSerializerOptionsFactory.CreateForProduction();
    }

    /// <summary>
    /// Handles a WebSocket connection for chat.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection</param>
    /// <param name="threadId">The thread ID for routing to the correct agent</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task HandleConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string threadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        _logger.LogInformation("WebSocket connection started for thread {ThreadId}", threadId);

        // Get or create agent for this thread
        var agent = _agentPool.GetOrCreateAgent(threadId);

        // Create linked cancellation for connection lifetime
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start subscription task to stream messages to client
        var subscriptionTask = StreamMessagesToClientAsync(webSocket, agent, threadId, connectionCts.Token);

        // Handle incoming messages from client
        var receiveTask = ReceiveMessagesFromClientAsync(webSocket, agent, threadId, connectionCts.Token);

        try
        {
            // Wait for either task to complete (connection close or error)
            await Task.WhenAny(subscriptionTask, receiveTask);
        }
        finally
        {
            // Cancel the other task
            await connectionCts.CancelAsync();

            // Ensure both tasks complete
            try
            {
                await Task.WhenAll(subscriptionTask, receiveTask);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        _logger.LogInformation("WebSocket connection ended for thread {ThreadId}", threadId);
    }

    /// <summary>
    /// Subscribes to agent messages and streams them to the WebSocket client.
    /// </summary>
    private async Task StreamMessagesToClientAsync(
        System.Net.WebSockets.WebSocket webSocket,
        IMultiTurnAgent agent,
        string threadId,
        CancellationToken ct)
    {
        try
        {
            await foreach (var message in agent.SubscribeAsync(ct))
            {
                if (webSocket.State != WebSocketState.Open)
                {
                    break;
                }

                var messageJson = JsonSerializer.Serialize<IMessage>(message, _jsonOptions);
                var bytes = Encoding.UTF8.GetBytes(messageJson);

                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct);

                _logger.LogDebug(
                    "Sent message type {MessageType} to thread {ThreadId}",
                    message.GetType().Name,
                    threadId);

                // Send done signal after RunCompletedMessage
                if (message is RunCompletedMessage)
                {
                    var doneJson = """{"$type":"done"}""";
                    var doneBytes = Encoding.UTF8.GetBytes(doneJson);
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(doneBytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Subscription cancelled for thread {ThreadId}", threadId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error during subscription for thread {ThreadId}", threadId);
        }
    }

    /// <summary>
    /// Receives messages from the WebSocket client and sends them to the agent.
    /// </summary>
    private async Task ReceiveMessagesFromClientAsync(
        System.Net.WebSockets.WebSocket webSocket,
        IMultiTurnAgent agent,
        string threadId,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // #region agent log
                System.IO.File.AppendAllText(@"d:\Source\repos\LmDotnetTools\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { location = "ChatWebSocketManager.cs:150", message = "Loop iteration start", data = new { threadId, wsState = webSocket.State.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessionId = "debug-session", runId = "run1", hypothesisId = "D" }) + "\n");
                // #endregion
                
                WebSocketReceiveResult result;
                messageBuilder.Clear();

                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    
                    // #region agent log
                    System.IO.File.AppendAllText(@"d:\Source\repos\LmDotnetTools\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { location = "ChatWebSocketManager.cs:157", message = "ReceiveAsync completed", data = new { threadId, messageType = result.MessageType.ToString(), count = result.Count, endOfMessage = result.EndOfMessage }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessionId = "debug-session", runId = "run1", hypothesisId = "A" }) + "\n");
                    // #endregion

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Client requested close for thread {ThreadId}", threadId);
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closed by client",
                            CancellationToken.None);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(chunk);
                    }
                } while (!result.EndOfMessage);

                if (messageBuilder.Length > 0)
                {
                    // #region agent log
                    System.IO.File.AppendAllText(@"d:\Source\repos\LmDotnetTools\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { location = "ChatWebSocketManager.cs:176", message = "Processing client message", data = new { threadId, messageLength = messageBuilder.Length, messagePreview = messageBuilder.ToString().Substring(0, Math.Min(100, messageBuilder.Length)) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessionId = "debug-session", runId = "run1", hypothesisId = "C" }) + "\n");
                    // #endregion
                    
                    await ProcessClientMessageAsync(agent, threadId, messageBuilder.ToString(), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Receive cancelled for thread {ThreadId}", threadId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error during receive for thread {ThreadId}", threadId);
        }
    }

    /// <summary>
    /// Processes a message received from the client.
    /// </summary>
    private async Task ProcessClientMessageAsync(
        IMultiTurnAgent agent,
        string threadId,
        string json,
        CancellationToken ct)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ChatRequest>(json, _jsonOptions);
            if (request?.Message == null)
            {
                _logger.LogWarning("Invalid chat request from thread {ThreadId}: {Json}", threadId, json);
                return;
            }

            _logger.LogInformation(
                "Processing chat request for thread {ThreadId}: {Message}",
                threadId,
                request.Message);

            // Create user message
            var userMessage = new TextMessage
            {
                Role = Role.User,
                Text = request.Message,
            };

            // Send to agent (non-blocking - queues the message)
            // #region agent log
            var inputId = Guid.NewGuid().ToString();
            System.IO.File.AppendAllText(@"d:\Source\repos\LmDotnetTools\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { location = "ChatWebSocketManager.cs:223", message = "Before agent.SendAsync", data = new { threadId, inputId, messageText = request.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessionId = "debug-session", runId = "run1", hypothesisId = "E" }) + "\n");
            // #endregion
            
            var receipt = await agent.SendAsync(
                [userMessage],
                inputId: inputId,
                ct: ct);

            // #region agent log
            System.IO.File.AppendAllText(@"d:\Source\repos\LmDotnetTools\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { location = "ChatWebSocketManager.cs:230", message = "After agent.SendAsync", data = new { threadId, receiptInputId = receipt.InputId, receiptId = receipt.ReceiptId }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessionId = "debug-session", runId = "run1", hypothesisId = "E" }) + "\n");
            // #endregion

            _logger.LogDebug(
                "Message queued for thread {ThreadId}, receipt: {InputId}",
                threadId,
                receipt.InputId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON from thread {ThreadId}: {Json}", threadId, json);
        }
    }
}

/// <summary>
/// Request format for chat messages from client.
/// </summary>
public record ChatRequest(string Message);
