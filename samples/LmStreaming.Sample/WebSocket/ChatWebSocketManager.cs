using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using LmStreaming.Sample.Agents;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;

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
    /// <param name="mode">Optional chat mode for agent configuration</param>
    /// <param name="requestResponseDumpFileName">
    /// Optional base file name for provider request/response recording.
    /// </param>
    /// <param name="recordWriter">Optional writer for recording messages to a JSONL file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task HandleConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string threadId,
        ChatMode? mode,
        string? requestResponseDumpFileName,
        StreamWriter? recordWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        _logger.LogInformation(
            "WebSocket connection started for thread {ThreadId} with mode {ModeId}",
            threadId,
            mode?.Id ?? "default");

        // Get or create agent for this thread with the specified mode
        var agent = mode != null
            ? _agentPool.GetOrCreateAgent(threadId, mode, requestResponseDumpFileName)
            : _agentPool.GetOrCreateAgent(
                threadId,
                SystemChatModes.All[0],
                requestResponseDumpFileName);

        // Create linked cancellation for connection lifetime
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start subscription task to stream messages to client
        var subscriptionTask = StreamMessagesToClientAsync(webSocket, agent, threadId, recordWriter, connectionCts.Token);

        // Handle incoming messages from client
        var receiveTask = ReceiveMessagesFromClientAsync(webSocket, agent, threadId, connectionCts.Token);

        try
        {
            // Wait for either task to complete (connection close or error)
            _ = await Task.WhenAny(subscriptionTask, receiveTask);
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
        StreamWriter? recordWriter,
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

                var messageJson = JsonSerializer.Serialize(message, _jsonOptions);
                var bytes = Encoding.UTF8.GetBytes(messageJson);

                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct);

                if (recordWriter != null)
                {
                    await recordWriter.WriteLineAsync(messageJson);
                    await recordWriter.FlushAsync();
                }

                _logger.LogDebug(
                    "Sent message type {MessageType} to thread {ThreadId}, orderIdx={MessageOrderIdx}, genId={GenerationId}, runId={RunId}",
                    message.GetType().Name,
                    threadId,
                    message.MessageOrderIdx,
                    message.GenerationId,
                    message.RunId);

                // Log cache metrics when usage message is received
                if (message is UsageMessage usageMsg)
                {
                    var u = usageMsg.Usage;
                    var cacheCreation = u.GetExtraProperty<int>("cache_creation_input_tokens");
                    _logger.LogInformation(
                        "Cache: read={CacheRead}, created={CacheCreation}, uncached_input={Input}, output={Output}, total={Total}",
                        u.TotalCachedTokens,
                        cacheCreation,
                        u.PromptTokens,
                        u.CompletionTokens,
                        u.TotalTokens);
                }

                // Send done signal after RunCompletedMessage
                if (message is RunCompletedMessage)
                {
                    var doneJson = /*lang=json,strict*/ """{"$type":"done"}""";
                    var doneBytes = Encoding.UTF8.GetBytes(doneJson);
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(doneBytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);

                    if (recordWriter != null)
                    {
                        await recordWriter.WriteLineAsync(doneJson);
                        await recordWriter.FlushAsync();
                    }
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
                WebSocketReceiveResult result;
                _ = messageBuilder.Clear();

                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

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
                        _ = messageBuilder.Append(chunk);
                    }
                } while (!result.EndOfMessage);

                if (messageBuilder.Length > 0)
                {
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
            var receipt = await agent.SendAsync(
                [userMessage],
                inputId: Guid.NewGuid().ToString(),
                ct: ct);

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
