using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Serilog.Context;

namespace LmStreaming.Sample.WebSocket;

/// <summary>
/// Manages WebSocket connections and bridges them to MultiTurnAgentPool.
/// Each WebSocket connection is associated with a threadId for conversation routing.
/// </summary>
public sealed class ChatWebSocketManager
{
    /// <summary>
    /// Strict UTF-8 decoder: throws <see cref="DecoderFallbackException"/> on invalid byte sequences
    /// instead of silently substituting replacement characters, so malformed inbound frames are
    /// detected and skipped rather than corrupting a relayed prompt.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly MultiTurnAgentPool _agentPool;
    private readonly WebSocketConnectionRegistry _connectionRegistry;
    private readonly Services.WorkflowRunRegistry _workflowRunRegistry;
    private readonly PendingAuthCoordinator _pendingAuth;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<ChatWebSocketManager> _logger;

    /// <summary>
    /// Maximum size, in bytes, of a single assembled inbound text message. A message whose accumulated
    /// payload exceeds this bound is rejected and the socket is closed with
    /// <see cref="WebSocketCloseStatus.MessageTooBig"/>. Chosen comfortably above any legitimate chat
    /// prompt while capping the memory a single connection can pin while assembling fragments. Settable
    /// as a test seam.
    /// </summary>
    internal int MaxInboundMessageBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>
    /// Upper bound on how long a single MULTI-FRAGMENT message may take to fully assemble, measured from
    /// the first fragment until <c>EndOfMessage</c>. The deadline runs ONLY while a partial message is
    /// being assembled — an idle connection simply waiting for the user's next message is never closed.
    /// Settable as a test seam.
    /// </summary>
    internal TimeSpan InboundAssemblyDeadline { get; set; } = TimeSpan.FromSeconds(30);

    public ChatWebSocketManager(
        MultiTurnAgentPool agentPool,
        WebSocketConnectionRegistry connectionRegistry,
        Services.WorkflowRunRegistry workflowRunRegistry,
        PendingAuthCoordinator pendingAuth,
        ILogger<ChatWebSocketManager> logger)
    {
        _agentPool = agentPool ?? throw new ArgumentNullException(nameof(agentPool));
        _connectionRegistry = connectionRegistry ?? throw new ArgumentNullException(nameof(connectionRegistry));
        _workflowRunRegistry = workflowRunRegistry ?? throw new ArgumentNullException(nameof(workflowRunRegistry));
        _pendingAuth = pendingAuth ?? throw new ArgumentNullException(nameof(pendingAuth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jsonOptions = JsonSerializerOptionsFactory.CreateForProduction();
    }

    /// <summary>
    /// Handles a WebSocket connection for chat.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection</param>
    /// <param name="threadId">The thread ID for routing to the correct agent</param>
    /// <param name="mode">Optional chat mode for agent configuration</param>
    /// <param name="providerId">
    /// Optional provider id requested by the client for this connection. Honored only when
    /// the thread has no persisted provider yet; otherwise the persisted value wins.
    /// </param>
    /// <param name="requestResponseDumpFileName">
    /// Optional base file name for provider request/response recording.
    /// </param>
    /// <param name="recordWriter">Optional writer for recording messages to a JSONL file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="workspaceId">
    /// Optional workspace id requested by the client for this connection. Honored only when the
    /// thread has no persisted workspace yet; otherwise the persisted value wins.
    /// </param>
    public async Task HandleConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string threadId,
        ChatMode? mode,
        string? providerId,
        string? requestResponseDumpFileName,
        StreamWriter? recordWriter,
        CancellationToken cancellationToken,
        string? workspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        var codexSessionId = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? Path.GetFileName(requestResponseDumpFileName)
            : $"{threadId}-{Guid.NewGuid():N}";
        using var logScope = LogContext.PushProperty("codex_session_id", codexSessionId);

        // Resolve the provider the pool will actually use (a thread is locked to its first
        // provider) so the log reflects reality instead of just the client's request.
        var effectiveProviderId = _agentPool.GetEffectiveProviderId(threadId, providerId);

        _logger.LogInformation(
            "WebSocket connection started for thread {ThreadId} with mode {ModeId} requested provider {RequestedProviderId} effective provider {EffectiveProviderId} and session {CodexSessionId}",
            threadId,
            mode?.Id ?? "default",
            providerId ?? "(default)",
            effectiveProviderId ?? "(default)",
            codexSessionId);

        var resolvedMode = mode ?? SystemChatModes.All[0];

        // Register before agent creation so every outbound frame (including the
        // provider_unavailable error below) flows through the connection's single gated write path.
        var connection = _connectionRegistry.Register(threadId, webSocket);
        try
        {
            // Replay any in-flight deferred-auth prompts: a webhook call may already be held
            // waiting for sign-in (it broadcast auth_required before this client connected).
            foreach (var pending in _pendingAuth.Snapshot())
            {
                _ = await connection.TrySendTextAsync(
                    WebSocketAuthEventNotifier.BuildAuthRequiredJson(
                        pending.ProviderId,
                        pending.SigninUrl,
                        pending.Reason),
                    cancellationToken);
            }

            // Get or create agent for this thread with the specified mode and requested provider.
            // ProviderUnavailableException is surfaced to the client as a structured error event
            // before the connection is closed, so the UI can show "this provider is unavailable"
            // rather than a generic disconnect.
            IMultiTurnAgent agent;
            try
            {
                agent = _agentPool.GetOrCreateAgent(
                    threadId,
                    resolvedMode,
                    providerId,
                    requestResponseDumpFileName,
                    workspaceId);
            }
            catch (ProviderUnavailableException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider {ProviderId} unavailable for thread {ThreadId}: {Reason}",
                    ex.ProviderId,
                    threadId,
                    ex.Reason);

                await SendProviderUnavailableErrorAsync(connection, ex, recordWriter, cancellationToken);
                return;
            }
            catch (SandboxSessionUnavailableException ex)
            {
                // Workspace Agent mode creates the sandbox session during agent setup; a gateway
                // rejection (e.g. an invalid network policy) or an unreachable gateway must surface
                // as a structured client error, not crash the connection with an unhandled 500.
                _logger.LogWarning(
                    ex,
                    "Sandbox unavailable for thread {ThreadId} (workspace {WorkspaceId}, gateway status {StatusCode})",
                    threadId,
                    workspaceId,
                    ex.StatusCode);

                await SendSandboxUnavailableErrorAsync(connection, ex, recordWriter, cancellationToken);
                return;
            }
            catch (SandboxCredentialConflictException ex)
            {
                // This conversation was created/driven by an S2S caller under its own sandbox
                // identity and is frozen to it for its lifetime; the interactive UI (default identity)
                // cannot silently take it over (#153 cross-actor resume matrix). Surface it as a
                // structured client error — the REST path maps the same exception to 409 — rather than
                // aborting the socket with an unhandled 500. App ids only; never the key.
                _logger.LogWarning(
                    ex,
                    "Credential conflict for thread {ThreadId}: bound to '{ExistingAppId}', requested '{RequestedAppId}'",
                    threadId,
                    ex.ExistingAppId,
                    ex.RequestedAppId);

                await SendCredentialConflictErrorAsync(connection, ex, recordWriter, cancellationToken);
                return;
            }

            // Create linked cancellation for connection lifetime
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Start subscription task to stream messages to client
            var subscriptionTask = StreamMessagesToClientAsync(connection, agent, threadId, recordWriter, connectionCts.Token);

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
        }
        finally
        {
            _connectionRegistry.Unregister(connection.ConnectionId);
        }

        _logger.LogInformation("WebSocket connection ended for thread {ThreadId}", threadId);
    }

    /// <summary>
    /// Handles a WebSocket connection focused on a single FOCUSED child sub-agent: streams that
    /// child's live + replayed output to the client (via the manager's restart-spanning
    /// <see cref="SubAgentManager.SubscribeToAgentAcrossRestartsAsync"/> fed through
    /// <see cref="PumpMessagesToClientAsync"/>) and relays inbound text frames to it in background mode.
    /// Presentation-only — it never mutates the parent connection or drives agent execution.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection.</param>
    /// <param name="parentThreadId">Thread id of the parent agent that owns the sub-agent.</param>
    /// <param name="agentId">Id (or caller-supplied name) of the focused child sub-agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task HandleSubAgentConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string parentThreadId,
        string agentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        _logger.LogInformation(
            "Sub-agent WebSocket connection started for parent thread {ParentThreadId} sub-agent {AgentId}",
            parentThreadId,
            agentId);

        // Register before resolution so every outbound frame (including the subagent_unavailable
        // error below) flows through the connection's single gated write path, mirroring
        // HandleConnectionAsync which registers before agent resolution.
        var connection = _connectionRegistry.Register($"subagent-{agentId}", webSocket);
        try
        {
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Resolve the stream: an Agent-tool sub-agent (via its parent's SubAgentManager) OR a
            // StartWorkflowAgent run's isolated controller loop (via this conversation's WorkflowManager).
            System.Collections.Generic.IAsyncEnumerable<AchieveAi.LmDotnetTools.LmCore.Messages.IMessage>? stream = null;
            SubAgentManager? subAgentManager = null;

            if (_agentPool.TryGet(parentThreadId, out var parentAgent)
                && parentAgent is MultiTurnAgentLoop loop
                && loop.SubAgentManager is { } sam
                && sam.TryGetAgent(agentId, out var childAgent)
                && childAgent is not null)
            {
                subAgentManager = sam;

                // Stream via the manager's restart-spanning enumerable rather than a single captured
                // instance: relaying a follow-up to a FINISHED owned-provider child restarts it (disposing
                // the old loop and swapping in a fresh one), and this enumerable re-resolves + re-subscribes
                // internally so the focused client keeps receiving the restarted turn's frames.
                stream = sam.SubscribeToAgentAcrossRestartsAsync(agentId, connectionCts.Token);
            }
            else if (_workflowRunRegistry.TryGet(parentThreadId, out var workflowManager)
                && workflowManager is not null
                && workflowManager.TryGetRunLoop(agentId, out var controllerLoop)
                && controllerLoop is not null)
            {
                // A StartWorkflowAgent run: stream the isolated controller loop's own conversation, the
                // same way the main /ws pump subscribes to a loop. The tab is read-only.
                stream = controllerLoop.SubscribeAsync(connectionCts.Token);
            }
            else if (_workflowRunRegistry.TryGet(parentThreadId, out var nestingManager)
                && nestingManager is not null
                && nestingManager.TryGetRunLoopOwningSubAgent(agentId, out var ownerLoop)
                && ownerLoop?.SubAgentManager is { } nestedManager
                && nestedManager.TryGetAgent(agentId, out var nestedAgent)
                && nestedAgent is not null)
            {
                // A nested delegate spawned BY a running controller: stream it through the controller's
                // own SubAgentManager, same as a top-level sub-agent (restart-spanning, follow-ups relay).
                // Branch order is safe: the run branch above treats agentId as a workflowId, and an opaque
                // workflowId never collides with a delegate's 12-char id, so control falls through here.
                subAgentManager = nestedManager;
                stream = nestedManager.SubscribeToAgentAcrossRestartsAsync(agentId, connectionCts.Token);
            }

            if (stream is null)
            {
                _logger.LogWarning(
                    "Sub-agent {AgentId} unavailable for parent thread {ParentThreadId}",
                    agentId,
                    parentThreadId);

                await SendSubAgentUnavailableErrorAsync(connection, agentId, cancellationToken);
                return;
            }

            // The sub-agent wrapper reuses the shared frame pump (the {"$type":"done"} sentinel after
            // RunCompletedMessage) but adds failure-to-structured-error handling scoped to this path.
            // This is a presentation-only view, so no recording.
            var subscriptionTask = PumpSubAgentStreamAsync(connection, stream, agentId, connectionCts.Token);

            // Follow-up messages relay only to an Agent-tool sub-agent; a workflow tab is read-only, so its
            // receive loop just drains client frames to detect disconnect.
            var receiveTask = subAgentManager is not null
                ? ReceiveSubAgentMessagesFromClientAsync(
                    webSocket, connection, subAgentManager, agentId, connectionCts.Token)
                : ReceiveTextMessagesAsync(
                    webSocket, $"workflow {agentId}", (_, _) => Task.CompletedTask, connectionCts.Token);

            try
            {
                _ = await Task.WhenAny(subscriptionTask, receiveTask);
            }
            finally
            {
                await connectionCts.CancelAsync();

                try
                {
                    await Task.WhenAll(subscriptionTask, receiveTask);
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation.
                }
            }
        }
        finally
        {
            _connectionRegistry.Unregister(connection.ConnectionId);
        }

        _logger.LogInformation(
            "Sub-agent WebSocket connection ended for parent thread {ParentThreadId} sub-agent {AgentId}",
            parentThreadId,
            agentId);
    }

    /// <summary>
    /// Subscribes to agent messages and streams them to the WebSocket client.
    /// </summary>
    private Task StreamMessagesToClientAsync(
        RegisteredWebSocketConnection connection,
        IMultiTurnAgent agent,
        string threadId,
        StreamWriter? recordWriter,
        CancellationToken ct)
    {
        return PumpMessagesToClientAsync(connection, agent.SubscribeAsync(ct), threadId, recordWriter, ct);
    }

    /// <summary>
    /// Drives the shared frame pump for the FOCUSED sub-agent view, translating a NON-cancellation
    /// fault from the message source (the restart-spanning subscription enumeration) or from
    /// serialization into a structured, content-free <c>subagent_stream_failed</c> error frame plus an
    /// ABNORMAL WebSocket close, so the client can tell a hard failure apart from a clean backpressure
    /// close. Caller cancellation stays the normal teardown path: the shared pump swallows
    /// <see cref="OperationCanceledException"/> internally and returns, so this wrapper emits no error
    /// frame and performs no close on cancellation (the route's normal close applies). Scoped to the
    /// sub-agent call site so the parent <c>/ws</c> pump behavior is unchanged.
    /// </summary>
    internal async Task PumpSubAgentStreamAsync(
        RegisteredWebSocketConnection connection,
        IAsyncEnumerable<IMessage> source,
        string agentId,
        CancellationToken ct)
    {
        try
        {
            await PumpMessagesToClientAsync(connection, source, $"subagent-{agentId}", recordWriter: null, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal teardown: the connection (or the caller) was cancelled. No error frame; the clean
            // close is the caller/route's responsibility.
            throw;
        }
        catch (Exception ex)
        {
            // Content-free: log ONLY the agent id and a stable exception category/type, never the
            // exception message or stack (provider/restart/store faults can echo prompt/transcript/tool
            // content — EUII).
            _logger.LogError(
                "Sub-agent {AgentId} focus stream failed; category {FailureCategory}, exceptionType {ExceptionType}",
                agentId,
                "subagent_stream_failed",
                ex.GetType().Name);

            await SendSubAgentStreamFailedErrorAsync(connection, agentId);
        }
    }

    /// <summary>
    /// Best-effort sends a content-free, structured <c>subagent_stream_failed</c> error frame and then
    /// closes the connection with an ABNORMAL status
    /// (<see cref="WebSocketCloseStatus.InternalServerError"/>) so the client distinguishes a hard
    /// sub-agent stream failure from a clean backpressure/normal close. The frame carries NO exception
    /// detail or message body (EUII). Uses <see cref="CancellationToken.None"/> for the send+close: the
    /// stream already faulted, and a cancelled connection token would suppress the very frame that tells
    /// the client what happened.
    /// </summary>
    private async Task SendSubAgentStreamFailedErrorAsync(
        RegisteredWebSocketConnection connection,
        string agentId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "subagent_stream_failed",
            ["agentId"] = agentId,
            ["message"] = "The sub-agent stream failed.",
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);

        _ = await connection.TrySendTextAsync(json, CancellationToken.None);

        await connection.TryCloseAsync(
            WebSocketCloseStatus.InternalServerError,
            "Sub-agent stream failed",
            CancellationToken.None);
    }

    /// <summary>
    /// Serializes each message from <paramref name="source"/> to the client, mirrors it to the optional
    /// recording writer, logs usage/cache metrics, and emits the <c>{"$type":"done"}</c> sentinel after a
    /// <see cref="RunCompletedMessage"/> — the shared frame body reused by both the parent <c>/ws</c>
    /// stream (fed <c>agent.SubscribeAsync</c>) and the sub-agent focus view (fed a restart-spanning
    /// enumerable). Pulling this out of the subscription source keeps the wire behavior byte-identical
    /// while letting the focus path substitute a source that follows the child across instance swaps.
    /// </summary>
    private async Task PumpMessagesToClientAsync(
        RegisteredWebSocketConnection connection,
        IAsyncEnumerable<IMessage> source,
        string threadId,
        StreamWriter? recordWriter,
        CancellationToken ct)
    {
        try
        {
            await foreach (var message in source.WithCancellation(ct))
            {
                var messageJson = JsonSerializer.Serialize(message, _jsonOptions);
                if (!await connection.TrySendTextAsync(messageJson, ct))
                {
                    break;
                }

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
                    // cached_tokens is a SUBSET of PromptTokens for the OpenAI family (Responses + chat
                    // completions), so uncached = prompt - cached. Anthropic instead reports cache reads
                    // SEPARATELY from input_tokens, so when the cache read exceeds the reported prompt we
                    // fall back to PromptTokens (the additive case) and never go negative. The proper
                    // cross-provider normalization of Usage is tracked as a follow-up.
                    var uncachedInput = u.TotalCachedTokens <= u.PromptTokens
                        ? u.PromptTokens - u.TotalCachedTokens
                        : u.PromptTokens;
                    _logger.LogInformation(
                        "Cache: read={CacheRead}, created={CacheCreation}, uncached_input={Uncached}, prompt={Prompt}, output={Output}, total={Total}",
                        u.TotalCachedTokens,
                        cacheCreation,
                        uncachedInput,
                        u.PromptTokens,
                        u.CompletionTokens,
                        u.TotalTokens);
                }

                // Send done signal after RunCompletedMessage
                if (message is RunCompletedMessage)
                {
                    var doneJson = /*lang=json,strict*/ """{"$type":"done"}""";
                    if (!await connection.TrySendTextAsync(doneJson, ct))
                    {
                        break;
                    }

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
    /// Receives messages from the WebSocket client and sends them to the agent, via the shared bounded
    /// receive pump (<see cref="ReceiveTextMessagesAsync"/>).
    /// </summary>
    private Task ReceiveMessagesFromClientAsync(
        System.Net.WebSockets.WebSocket webSocket,
        IMultiTurnAgent agent,
        string threadId,
        CancellationToken ct)
        => ReceiveTextMessagesAsync(
            webSocket,
            $"thread {threadId}",
            (message, token) => ProcessClientMessageAsync(agent, threadId, message, token),
            ct);

    /// <summary>
    /// Shared bounded receive pump used by BOTH the parent <c>/ws</c> and the <c>/ws/subagent</c>
    /// endpoints. It reads inbound WebSocket frames, enforces a text-only, size-bounded,
    /// assembly-deadline-bounded policy, and delivers each fully-assembled UTF-8 message to
    /// <paramref name="onMessage"/>. Protecting properties:
    /// <list type="bullet">
    /// <item>Raw bytes are accumulated across fragments and decoded to UTF-8 exactly ONCE at
    /// <c>EndOfMessage</c>, so a multi-byte character split across a fragment boundary is never
    /// corrupted.</item>
    /// <item>An assembled payload exceeding <see cref="MaxInboundMessageBytes"/> closes the socket with
    /// <see cref="WebSocketCloseStatus.MessageTooBig"/>.</item>
    /// <item>The <see cref="InboundAssemblyDeadline"/> applies only while assembling a partial
    /// (multi-fragment) message; an idle connection awaiting the next message is never closed. A breach
    /// closes with <see cref="WebSocketCloseStatus.PolicyViolation"/>.</item>
    /// <item>Close frames close normally; binary frames are rejected
    /// (<see cref="WebSocketCloseStatus.InvalidMessageType"/>) — this endpoint family is text-only.</item>
    /// <item>Invalid UTF-8 is detected (throwing decoder) and the message is skipped (metadata logged),
    /// keeping the connection alive.</item>
    /// </list>
    /// The pump itself never logs message bodies; each <paramref name="onMessage"/> delivery callback
    /// owns its own logging policy.
    /// </summary>
    private async Task ReceiveTextMessagesAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string logLabel,
        Func<string, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        var assembled = new byte[4096];

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var length = 0;

                // First fragment: a plain idle wait with NO assembly deadline, so a connection waiting
                // for the user's next message is never torn down.
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (await TryHandleNonTextFrameAsync(webSocket, result, logLabel))
                {
                    return;
                }

                if (!AppendFragment(ref assembled, ref length, buffer, result.Count))
                {
                    await CloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "Message too big", logLabel, "oversized", length);
                    return;
                }

                if (!result.EndOfMessage)
                {
                    // A partial message is now assembling: bound the rest with the assembly deadline.
                    using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadlineCts.CancelAfter(InboundAssemblyDeadline);

                    try
                    {
                        do
                        {
                            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), deadlineCts.Token);

                            if (await TryHandleNonTextFrameAsync(webSocket, result, logLabel))
                            {
                                return;
                            }

                            if (!AppendFragment(ref assembled, ref length, buffer, result.Count))
                            {
                                await CloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "Message too big", logLabel, "oversized", length);
                                return;
                            }
                        } while (!result.EndOfMessage);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Inbound assembly deadline exceeded for {ConnectionLabel} after {ByteCount} bytes; category {RejectCategory}",
                            logLabel,
                            length,
                            "assembly_timeout");
                        await CloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "Assembly deadline exceeded", logLabel, "assembly_timeout", length);
                        return;
                    }
                }

                // Decode ONCE, over the fully-assembled byte payload.
                string message;
                try
                {
                    message = StrictUtf8.GetString(assembled, 0, length);
                }
                catch (DecoderFallbackException)
                {
                    _logger.LogWarning(
                        "Skipped invalid UTF-8 message ({ByteCount} bytes) for {ConnectionLabel}; category {RejectCategory}",
                        length,
                        logLabel,
                        "invalid_utf8");
                    continue;
                }

                if (message.Length == 0)
                {
                    continue;
                }

                await onMessage(message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Receive cancelled for {ConnectionLabel}", logLabel);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error during receive for {ConnectionLabel}", logLabel);
        }
    }

    /// <summary>
    /// Handles a non-text frame during receive: a close frame closes the socket normally; a binary frame
    /// is rejected (this endpoint family is text-only). Returns <c>true</c> when the pump must stop.
    /// </summary>
    private async Task<bool> TryHandleNonTextFrameAsync(
        System.Net.WebSockets.WebSocket webSocket,
        WebSocketReceiveResult result,
        string logLabel)
    {
        if (result.MessageType == WebSocketMessageType.Close)
        {
            _logger.LogInformation("Client requested close for {ConnectionLabel}", logLabel);
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
            return true;
        }

        if (result.MessageType == WebSocketMessageType.Binary)
        {
            _logger.LogWarning(
                "Rejected binary frame ({ByteCount} bytes) for {ConnectionLabel}; category {RejectCategory}",
                result.Count,
                logLabel,
                "binary_frame");
            await webSocket.CloseAsync(
                WebSocketCloseStatus.InvalidMessageType, "Binary frames are not supported", CancellationToken.None);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Appends a received fragment to the growing assembly buffer, growing it as needed. Returns
    /// <c>false</c> when appending would exceed <see cref="MaxInboundMessageBytes"/> (the message must
    /// then be rejected as oversized).
    /// </summary>
    private bool AppendFragment(ref byte[] assembled, ref int length, byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return true;
        }

        if ((long)length + count > MaxInboundMessageBytes)
        {
            length += count;
            return false;
        }

        if (length + count > assembled.Length)
        {
            var newSize = Math.Min(Math.Max(assembled.Length * 2, length + count), MaxInboundMessageBytes);
            Array.Resize(ref assembled, newSize);
        }

        Buffer.BlockCopy(buffer, 0, assembled, length, count);
        length += count;
        return true;
    }

    /// <summary>
    /// Closes the socket with the given status, logging content-free rejection metadata (never the body).
    /// </summary>
    private async Task CloseAsync(
        System.Net.WebSockets.WebSocket webSocket,
        WebSocketCloseStatus status,
        string description,
        string logLabel,
        string category,
        int byteCount)
    {
        _logger.LogWarning(
            "Closing {ConnectionLabel} ({ByteCount} bytes); category {RejectCategory}, status {CloseStatus}",
            logLabel,
            byteCount,
            category,
            status);

        try
        {
            await webSocket.CloseAsync(status, description, CancellationToken.None);
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

    /// <summary>
    /// Receives frames from the sub-agent WebSocket client and relays their text to the focused child
    /// sub-agent, via the shared bounded receive pump (<see cref="ReceiveTextMessagesAsync"/>). Delivery
    /// goes through <see cref="RelaySubAgentMessageAsync"/> (using <see cref="SubAgentManager.SendMessageAsync"/>
    /// in background mode) rather than <c>IMultiTurnAgent.SendAsync</c> — the sub-agent sink.
    /// </summary>
    private Task ReceiveSubAgentMessagesFromClientAsync(
        System.Net.WebSockets.WebSocket webSocket,
        RegisteredWebSocketConnection connection,
        SubAgentManager subAgentManager,
        string agentId,
        CancellationToken ct)
        => ReceiveTextMessagesAsync(
            webSocket,
            $"sub-agent {agentId}",
            (message, token) => RelaySubAgentMessageAsync(connection, subAgentManager, agentId, message, token),
            ct);

    /// <summary>
    /// Relays one already-assembled client frame to the focused child sub-agent. Never logs the message
    /// body or prompt (EUII) — only content-free metadata (agent id, byte count, a stable category). On a
    /// transient/unknown relay failure the receive loop is kept alive and a structured, correlated
    /// <c>relay_failed</c> error frame is sent so the client's input is not silently lost; a clearly
    /// terminal target (the child is gone) surfaces the <c>subagent_unavailable</c> error and closes.
    /// </summary>
    private async Task RelaySubAgentMessageAsync(
        RegisteredWebSocketConnection connection,
        SubAgentManager subAgentManager,
        string agentId,
        string json,
        CancellationToken ct)
    {
        var byteCount = Encoding.UTF8.GetByteCount(json);

        ChatRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ChatRequest>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            // EUII: never log the payload — only content-free metadata.
            _logger.LogWarning(
                "Discarded invalid JSON ({ByteCount} bytes) for sub-agent {AgentId}; category {RejectCategory}",
                byteCount,
                agentId,
                "invalid_json");
            return;
        }

        if (request?.Message is null)
        {
            _logger.LogWarning(
                "Discarded chat request with no message ({ByteCount} bytes) for sub-agent {AgentId}; category {RejectCategory}",
                byteCount,
                agentId,
                "invalid_json");
            return;
        }

        _logger.LogDebug(
            "Relaying message ({ByteCount} bytes) to sub-agent {AgentId}",
            byteCount,
            agentId);

        try
        {
            // Background mode is REQUIRED: a synchronous send blocks until the child's whole run
            // completes, which would stall this receive loop. Background returns a JSON receipt
            // immediately (discarded) while the sibling StreamMessagesToClientAsync task carries the
            // child's live deltas back to the client.
            _ = await subAgentManager.SendMessageAsync(
                agentId, request.Message, runInBackground: true, ct);
        }
        catch (ArgumentException)
        {
            // Terminal target: the focused child is gone (finished and pruned — "Unknown sub-agent").
            // There is nothing left to relay to, so surface the structured subagent_unavailable error
            // and close. No body logged.
            _logger.LogWarning(
                "Sub-agent {AgentId} is gone ({ByteCount} bytes discarded); category {RejectCategory}",
                agentId,
                byteCount,
                "subagent_unavailable");
            await SendSubAgentUnavailableErrorAsync(connection, agentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient/unknown failure (e.g. an owned-provider restart race). Keep the receive loop
            // alive so one relay fault does not tear down the whole connection — but surface a
            // structured, correlated error frame so the client's input is not silently lost. Only a
            // stable category and content-free identifiers are logged; the exception object is never
            // handed to the logger (its message/ToString can echo prompt/transcript/tool content — EUII).
            LogSubAgentRelayFailure(agentId, byteCount, ex);
            await SendRelayFailedErrorAsync(connection, agentId, ct);
        }
    }

    /// <summary>
    /// Logs a sub-agent relay failure with a STABLE category plus content-free identifiers only
    /// (agent id, byte count, and <c>exception.GetType().Name</c>). The exception object is never
    /// passed to the logger and neither <c>ex.Message</c> nor <c>ex.ToString()</c> is logged, because a
    /// downstream provider/restart/store fault can carry prompt/transcript/tool content (EUII).
    /// </summary>
    internal void LogSubAgentRelayFailure(string agentId, int byteCount, Exception ex) =>
        _logger.LogWarning(
            "Failed to relay message ({ByteCount} bytes) to sub-agent {AgentId}; category {RejectCategory}, exceptionType {ExceptionType}, keeping the stream open",
            byteCount,
            agentId,
            "relay_failed",
            ex.GetType().Name);

    /// <summary>
    /// Sends a structured, correlated <c>relay_failed</c> error frame to the client without closing the
    /// connection — a transient relay failure must not silently drop the client's input nor tear down the
    /// presentation-only view. The frame carries no message body (EUII).
    /// </summary>
    private async Task SendRelayFailedErrorAsync(
        RegisteredWebSocketConnection connection,
        string agentId,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "relay_failed",
            ["agentId"] = agentId,
            ["message"] = $"Failed to relay the message to sub-agent '{agentId}'. Please retry.",
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);

        // Best-effort: a dying connection turns this into a quiet false. No close (transient failure).
        _ = await connection.TrySendTextAsync(json, ct);
    }

    private async Task SendSubAgentUnavailableErrorAsync(
        RegisteredWebSocketConnection connection,
        string agentId,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "subagent_unavailable",
            ["agentId"] = agentId,
            ["message"] = $"Sub-agent '{agentId}' is not available.",
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);

        if (!await connection.TrySendTextAsync(json, ct))
        {
            return;
        }

        // Close through the wrapper so the single-write-path contract holds (closing is outbound).
        await connection.TryCloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Sub-agent unavailable",
            CancellationToken.None);
    }

    private async Task SendProviderUnavailableErrorAsync(
        RegisteredWebSocketConnection connection,
        ProviderUnavailableException ex,
        StreamWriter? recordWriter,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "provider_unavailable",
            ["providerId"] = ex.ProviderId,
            ["reason"] = ex.Reason,
            ["message"] = ex.Message,
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);

        if (!await connection.TrySendTextAsync(json, ct))
        {
            return;
        }

        if (recordWriter != null)
        {
            await recordWriter.WriteLineAsync(json);
            await recordWriter.FlushAsync();
        }

        // Close through the wrapper so the single-write-path contract holds (closing is outbound).
        await connection.TryCloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Provider unavailable",
            CancellationToken.None);
    }

    private async Task SendSandboxUnavailableErrorAsync(
        RegisteredWebSocketConnection connection,
        SandboxSessionUnavailableException ex,
        StreamWriter? recordWriter,
        CancellationToken ct)
    {
        var summary = ex.StatusCode is { } status
            ? $"Workspace Agent is unavailable: the sandbox gateway rejected the session (HTTP {status})."
            : "Workspace Agent is unavailable: the sandbox gateway could not be reached.";

        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "sandbox_unavailable",
            ["statusCode"] = ex.StatusCode,
            // Keep the gateway's own message in the client error — this is a developer sample and the
            // detail (e.g. which network-policy rule was rejected) is exactly what's needed to act.
            ["message"] = $"{summary} {ex.Message}",
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);

        if (!await connection.TrySendTextAsync(json, ct))
        {
            return;
        }

        if (recordWriter != null)
        {
            await recordWriter.WriteLineAsync(json);
            await recordWriter.FlushAsync();
        }

        // Close through the wrapper so the single-write-path contract holds (closing is outbound).
        await connection.TryCloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Sandbox unavailable",
            CancellationToken.None);
    }

    private async Task SendCredentialConflictErrorAsync(
        RegisteredWebSocketConnection connection,
        SandboxCredentialConflictException ex,
        StreamWriter? recordWriter,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "caller_credential_conflict",
            // App ids only — the exception message never contains the app key.
            ["message"] =
                "This conversation belongs to a different caller identity and cannot be continued here. "
                + ex.Message,
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);

        if (!await connection.TrySendTextAsync(json, ct))
        {
            return;
        }

        if (recordWriter != null)
        {
            await recordWriter.WriteLineAsync(json);
            await recordWriter.FlushAsync();
        }

        await connection.TryCloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Credential conflict",
            CancellationToken.None);
    }
}

/// <summary>
/// Request format for chat messages from client.
/// </summary>
public record ChatRequest(string Message);
