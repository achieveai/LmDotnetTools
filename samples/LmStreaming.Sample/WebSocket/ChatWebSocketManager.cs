using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly MultiTurnAgentPool _agentPool;
    private readonly WebSocketConnectionRegistry _connectionRegistry;
    private readonly Services.WorkflowRunRegistry _workflowRunRegistry;
    private readonly PendingAuthCoordinator _pendingAuth;
    private readonly IConversationStore _conversationStore;
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

    /// <summary>
    /// Test seam only (<see langword="null"/> in production, one null check per outbound frame):
    /// awaited by <see cref="PumpMessagesToClientAsync"/> before each frame is written, so a test can
    /// hold a consumer still and reproduce the slow-consumer eviction
    /// (<c>MultiTurnAgentBase.PublishToSubscriber</c>) as a counting argument rather than a race — a
    /// frozen pump plus a bounded output channel overflows after exactly <c>capacity + 1</c> publishes,
    /// with no timing sleeps anywhere. Receives the stream's thread id (<c>subagent-{agentId}</c> for
    /// the focus view) so a test can gate one stream and leave the others running. Because both the
    /// primary <c>/ws</c> stream and the sub-agent focus view pump through this one method, a gate here
    /// applies identically to both.
    /// </summary>
    internal Func<string, CancellationToken, Task>? OutboundPumpGate { get; set; }

    /// <summary>
    /// <c>$type</c> discriminator for an inbound frame resolving a previously-deferred client-hosted tool
    /// call (issue #246: <c>AskUserQuestion</c>/other client-tool answers). Recognized on BOTH the
    /// primary (<see cref="ProcessClientMessageAsync"/>) and sub-agent (<see cref="RelaySubAgentMessageAsync"/>)
    /// inbound paths, peeked before the existing <see cref="ChatRequest"/> deserialize so that shape is
    /// left completely unchanged for ordinary chat frames.
    /// </summary>
    private const string ClientToolResultFrameType = "client_tool_result";

    public ChatWebSocketManager(
        MultiTurnAgentPool agentPool,
        WebSocketConnectionRegistry connectionRegistry,
        Services.WorkflowRunRegistry workflowRunRegistry,
        PendingAuthCoordinator pendingAuth,
        IConversationStore conversationStore,
        ILogger<ChatWebSocketManager> logger
    )
    {
        _agentPool = agentPool ?? throw new ArgumentNullException(nameof(agentPool));
        _connectionRegistry = connectionRegistry ?? throw new ArgumentNullException(nameof(connectionRegistry));
        _workflowRunRegistry = workflowRunRegistry ?? throw new ArgumentNullException(nameof(workflowRunRegistry));
        _pendingAuth = pendingAuth ?? throw new ArgumentNullException(nameof(pendingAuth));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
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
    /// <param name="ownerUserId">
    /// The connecting user's <c>Principal.EffectiveUserId</c>, resolved by the identity middleware
    /// from the handshake credential (#342). Passed to every pool call this connection makes so a
    /// thread the UI opened a socket on is owned exactly as a REST-created one is (#399).
    /// </param>
    public async Task HandleConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string threadId,
        ChatMode? mode,
        string? providerId,
        string? requestResponseDumpFileName,
        StreamWriter? recordWriter,
        CancellationToken cancellationToken,
        string? workspaceId = null,
        string? ownerUserId = null
    )
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
            codexSessionId
        );

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
                        pending.Reason
                    ),
                    cancellationToken
                );
            }

            // Get or create agent for this thread with the specified mode and requested provider.
            // ProviderUnavailableException is surfaced to the client as a structured error event
            // before the connection is closed, so the UI can show "this provider is unavailable"
            // rather than a generic disconnect.
            IMultiTurnAgent agent;
            try
            {
                // ownerUserId on BOTH calls, matching ConversationsController. Whichever surface
                // touches a thread first decides whether the principal guard exists at all, because
                // AgentEntry.OwnerUserId is frozen at creation and EnsurePrincipalMatches returns
                // early when either side is null - and in the browser the first toucher is this
                // socket, opened on load before any REST turn (#399).
                _ = _agentPool.GetOrCreateAgent(
                    threadId,
                    resolvedMode,
                    providerId,
                    requestResponseDumpFileName,
                    workspaceId,
                    ownerUserId: ownerUserId
                );
                agent = (
                    await _agentPool
                        .EnsureCurrentAgentAsync(threadId, ct: cancellationToken, ownerUserId: ownerUserId)
                        .ConfigureAwait(false)
                ).Agent;
            }
            catch (ProviderUnavailableException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider {ProviderId} unavailable for thread {ThreadId}: {Reason}",
                    ex.ProviderId,
                    threadId,
                    ex.Reason
                );

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
                    ex.StatusCode
                );

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
                    ex.RequestedAppId
                );

                await SendCredentialConflictErrorAsync(connection, ex, recordWriter, cancellationToken);
                return;
            }
            catch (PrincipalConflictException ex)
            {
                // The people-shaped sibling of the conflict above, reachable only since this
                // connection started owning the entries it creates (#399): the thread's live agent
                // belongs to a different USER. Refuse it here rather than letting the exception abort
                // the socket - an aborted handshake tells the UI nothing, and the REST surface answers
                // the same exception with 409 principal_conflict.
                //
                // This transport does NOT release the other user's agent the way the REST routes do
                // for an authorized grantee (#376): it performs no per-conversation authorization at
                // all, so there is no verdict here that could justify taking someone's agent away.
                // Refusing is the conservative half of that gap, and it is recorded in
                // docs/deployment/AUTH_ENFORCE.md.
                _logger.LogWarning(
                    ex,
                    "Principal conflict for thread {ThreadId}: bound to user '{ExistingUserId}', requested '{RequestedUserId}'",
                    threadId,
                    ex.ExistingUserId,
                    ex.RequestedUserId
                );

                await SendPrincipalConflictErrorAsync(connection, ex, recordWriter, cancellationToken);
                return;
            }
            catch (AgentNotPooledException ex)
            {
                // Setup calls GetOrCreateAgent and then EnsureCurrentAgentAsync, and a grantee handoff
                // removing the entry between those two lines - or inside the refresh's own await on the
                // live-session resolver - leaves the second call with nothing to refresh. The
                // per-message path already answers this; without the same catch here a connect landing
                // in that window aborts frameless, which is the failure #399 set out to remove. Same
                // answer as the message path: tell the client its agent is gone and close cleanly, so
                // it reconnects instead of guessing.
                _logger.LogInformation(
                    ex,
                    "Connection setup for thread {ThreadId} met a released agent; closing so the client reconnects",
                    threadId
                );

                await SendAgentReleasedAsync(connection, recordWriter, cancellationToken);
                return;
            }

            // Create linked cancellation for connection lifetime
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Start subscription task to stream messages to client
            var subscriptionTask = StreamMessagesToClientAsync(
                connection,
                agent,
                threadId,
                recordWriter,
                connectionCts.Token
            );

            // Handle incoming messages from client
            var receiveTask = ReceiveMessagesFromClientAsync(
                webSocket,
                connection,
                agent,
                threadId,
                connectionCts.Token,
                ownerUserId
            );

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
    /// <param name="mayReplayPersistedTranscript">
    /// Whether the persisted-transcript fallback below is available to this caller (#419). False when
    /// the named child's durable parent link names a DIFFERENT conversation than
    /// <paramref name="parentThreadId"/>: the caller is entitled to the parent they named, but not to
    /// that child, and without this the authorized parent id would be a passphrase for any child in
    /// the deployment. The refusal is deliberately indistinguishable from "no such agent" - see
    /// <see cref="LmStreaming.Sample.Identity.SubAgentSocketAdmission"/>.
    /// <para>
    /// REQUIRED, and moved ahead of the token so it cannot be forgotten. It defaulted to <c>true</c>,
    /// which made "replay this child's transcript" the answer a caller got by saying nothing at all -
    /// so a new call site added without a thought about provenance was, silently, the permissive one.
    /// A security decision must be stated by whoever decides it; there is no safe default here,
    /// because <c>false</c> would instead have new call sites quietly withholding replay from
    /// legitimate callers.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task HandleSubAgentConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        string parentThreadId,
        string agentId,
        bool mayReplayPersistedTranscript,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        _logger.LogInformation(
            "Sub-agent WebSocket connection started for parent thread {ParentThreadId} sub-agent {AgentId}",
            parentThreadId,
            agentId
        );

        // Register before resolution so every outbound frame (including the subagent_unavailable
        // error below) flows through the connection's single gated write path, mirroring
        // HandleConnectionAsync which registers before agent resolution.
        var connection = _connectionRegistry.Register($"subagent-{agentId}", webSocket);
        try
        {
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Resolve the stream: an Agent-tool sub-agent (via its parent's SubAgentManager) OR a
            // StartWorkflowAgent run's isolated controller loop (via this conversation's WorkflowManager).
            System.Collections.Generic.IAsyncEnumerable<AchieveAi.LmDotnetTools.LmCore.Messages.IMessage>? stream =
                null;
            SubAgentManager? subAgentManager = null;

            if (
                _agentPool.TryGet(parentThreadId, out var parentAgent)
                && parentAgent is MultiTurnAgentLoop loop
                && loop.SubAgentManager is { } sam
                && sam.TryGetAgent(agentId, out var childAgent)
                && childAgent is not null
            )
            {
                subAgentManager = sam;

                // Stream via the manager's restart-spanning enumerable rather than a single captured
                // instance: relaying a follow-up to a FINISHED owned-provider child restarts it (disposing
                // the old loop and swapping in a fresh one), and this enumerable re-resolves + re-subscribes
                // internally so the focused client keeps receiving the restarted turn's frames.
                stream = sam.SubscribeToAgentAcrossRestartsAsync(agentId, connectionCts.Token);
            }
            else if (
                _workflowRunRegistry.TryGet(parentThreadId, out var workflowManager)
                && workflowManager is not null
                && workflowManager.TryGetRunLoop(agentId, out var controllerLoop)
                && controllerLoop is not null
            )
            {
                // A StartWorkflowAgent run: stream the isolated controller loop's own conversation, the
                // same way the main /ws pump subscribes to a loop. The tab is read-only.
                stream = controllerLoop.SubscribeAsync(connectionCts.Token);
            }
            else if (
                _workflowRunRegistry.TryGet(parentThreadId, out var nestingManager)
                && nestingManager is not null
                && nestingManager.TryGetRunLoopOwningSubAgent(agentId, out var ownerLoop)
                && ownerLoop?.SubAgentManager is { } nestedManager
                && nestedManager.TryGetAgent(agentId, out var nestedAgent)
                && nestedAgent is not null
            )
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
                // No LIVE stream: the parent loop was evicted (app restart, or the parent conversation
                // was disposed/aged out of the pool). A COMPLETED sub-agent's transcript still persists
                // under "subagent-{agentId}", and the client already renders that history from REST. So
                // instead of a scary "unavailable" error, settle the client with the done sentinel and
                // hold the socket open read-only (drain client frames to detect disconnect) so the
                // persisted transcript replays. Only a genuinely missing agent (no persisted history)
                // falls through to the structured error.
                // The load runs even when the replay is withheld, and is then discarded. Skipping it
                // would make the withheld answer measurably cheaper than the genuinely-absent one and
                // reintroduce as a timing difference exactly the existence oracle the withholding
                // closes (the same equalisation ConversationAuthorizer.EqualizeGrantLookupAsync makes
                // on the REST refusal paths).
                var persisted = await _conversationStore.LoadMessagesAsync($"subagent-{agentId}", connectionCts.Token);
                if (persisted.Count == 0 || !mayReplayPersistedTranscript)
                {
                    _logger.LogWarning(
                        "Sub-agent {AgentId} unavailable for parent thread {ParentThreadId} (no live stream, no persisted history)",
                        agentId,
                        parentThreadId
                    );

                    await SendSubAgentUnavailableErrorAsync(connection, agentId, cancellationToken);
                    return;
                }

                _logger.LogInformation(
                    "Sub-agent {AgentId} has no live stream; replaying {Count} persisted messages read-only for parent thread {ParentThreadId}",
                    agentId,
                    persisted.Count,
                    parentThreadId
                );

                // The transcript itself renders from REST; the socket emits ONLY the done sentinel so the
                // client settles its focused-streaming state (no WS content ⇒ no merge-key/duplicate risk).
                var doneJson = /*lang=json,strict*/
                    """{"$type":"done"}""";
                if (await connection.TrySendTextAsync(doneJson, connectionCts.Token))
                {
                    // Keep the read-only socket open until the client disconnects (or shutdown cancels),
                    // mirroring a completed shared-provider tab. Drain inbound frames without relaying.
                    await ReceiveTextMessagesAsync(
                        webSocket,
                        $"subagent-replay {agentId}",
                        (_, _) => Task.CompletedTask,
                        connectionCts.Token
                    );
                }

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
                    webSocket,
                    connection,
                    subAgentManager,
                    agentId,
                    connectionCts.Token
                )
                : ReceiveTextMessagesAsync(
                    webSocket,
                    $"workflow {agentId}",
                    (_, _) => Task.CompletedTask,
                    connectionCts.Token
                );

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
            agentId
        );
    }

    /// <summary>
    /// Subscribes to agent messages and streams them to the WebSocket client.
    /// </summary>
    private Task StreamMessagesToClientAsync(
        RegisteredWebSocketConnection connection,
        IMultiTurnAgent agent,
        string threadId,
        StreamWriter? recordWriter,
        CancellationToken ct
    )
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
        CancellationToken ct
    )
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
                ex.GetType().Name
            );

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
    private async Task SendSubAgentStreamFailedErrorAsync(RegisteredWebSocketConnection connection, string agentId)
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
            CancellationToken.None
        );
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
        CancellationToken ct
    )
    {
        try
        {
            await foreach (var message in source.WithCancellation(ct))
            {
                // Test-only stall point (no-op in production — see OutboundPumpGate). Placed INSIDE the
                // loop on purpose: the subscription is an async iterator, so the subscriber only exists
                // once the first message has been pulled. Gating before the loop would leave the agent
                // with no subscriber at all, and nothing would ever queue.
                if (OutboundPumpGate is { } gate)
                {
                    await gate(threadId, ct);
                }

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
                    message.RunId
                );

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
                    var uncachedInput =
                        u.TotalCachedTokens <= u.PromptTokens ? u.PromptTokens - u.TotalCachedTokens : u.PromptTokens;
                    _logger.LogInformation(
                        "Cache: read={CacheRead}, created={CacheCreation}, uncached_input={Uncached}, prompt={Prompt}, output={Output}, total={Total}",
                        u.TotalCachedTokens,
                        cacheCreation,
                        uncachedInput,
                        u.PromptTokens,
                        u.CompletionTokens,
                        u.TotalTokens
                    );
                }

                // Send done signal after RunCompletedMessage
                if (message is RunCompletedMessage)
                {
                    var doneJson = /*lang=json,strict*/
                        """{"$type":"done"}""";
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

                // A resync signal (see MultiTurnAgentBase.PublishToSubscriber / SubscribeAsync) is NOT a
                // run completion: the frame above already carried it (content-free - only
                // $type/reason/thread/run/generationId). Whether it also ENDS the stream is a property of
                // its reason:
                //  - SlowConsumer: this subscriber was dropped from fan-out and receives nothing further,
                //    so close deliberately with a dedicated reason instead of the `done` sentinel.
                //  - ReplayTruncated: only the run's already-published PREFIX is missing and the live tail
                //    still follows on this same subscription, so keep pumping. Closing here would make
                //    every consumer reconnect, land on the same still-truncated buffer, and be advised
                //    again for the rest of the run.
                // Applies identically to the primary /ws stream and the sub-agent focus view, since both
                // call through this shared method. The close uses CancellationToken.None (matching
                // SendSubAgentStreamFailedErrorAsync's terminal close): a cancellation landing between the
                // frame's send and this close must not be able to suppress the close (and the
                // "resync_required" reason it carries) via OperationCanceledException.
                if (message is StreamRecoveryMessage { Reason: not StreamRecoveryReason.ReplayTruncated })
                {
                    await connection.TryCloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "resync_required",
                        CancellationToken.None
                    );
                    break;
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
        RegisteredWebSocketConnection connection,
        IMultiTurnAgent agent,
        string threadId,
        CancellationToken ct,
        string? ownerUserId
    ) =>
        ReceiveTextMessagesAsync(
            webSocket,
            $"thread {threadId}",
            (message, token) => ProcessClientMessageAsync(connection, agent, threadId, message, token, ownerUserId),
            ct
        );

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
        CancellationToken ct
    )
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
                    await CloseAsync(
                        webSocket,
                        WebSocketCloseStatus.MessageTooBig,
                        "Message too big",
                        logLabel,
                        "oversized",
                        length
                    );
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
                                await CloseAsync(
                                    webSocket,
                                    WebSocketCloseStatus.MessageTooBig,
                                    "Message too big",
                                    logLabel,
                                    "oversized",
                                    length
                                );
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
                            "assembly_timeout"
                        );
                        await CloseAsync(
                            webSocket,
                            WebSocketCloseStatus.PolicyViolation,
                            "Assembly deadline exceeded",
                            logLabel,
                            "assembly_timeout",
                            length
                        );
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
                        "invalid_utf8"
                    );
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
        string logLabel
    )
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
                "binary_frame"
            );
            await webSocket.CloseAsync(
                WebSocketCloseStatus.InvalidMessageType,
                "Binary frames are not supported",
                CancellationToken.None
            );
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
        int byteCount
    )
    {
        _logger.LogWarning(
            "Closing {ConnectionLabel} ({ByteCount} bytes); category {RejectCategory}, status {CloseStatus}",
            logLabel,
            byteCount,
            category,
            status
        );

        try
        {
            await webSocket.CloseAsync(status, description, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Processes a message received from the client.
    /// </summary>
    private async Task ProcessClientMessageAsync(
        RegisteredWebSocketConnection connection,
        IMultiTurnAgent agent,
        string threadId,
        string json,
        CancellationToken ct,
        string? ownerUserId
    )
    {
        if (TryPeekFrameType(json, out var frameType) && frameType == ClientToolResultFrameType)
        {
            await HandleClientToolResultAsync(connection, agent, threadId, json, ct);
            return;
        }

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
                request.Message
            );

            var refresh = await _agentPool
                .EnsureCurrentAgentAsync(threadId, ct: ct, replace: false, ownerUserId: ownerUserId)
                .ConfigureAwait(false);
            var agentChanged = !ReferenceEquals(refresh.Agent, agent);
            if (
                agentChanged
                || refresh.Status
                    is MultiTurnAgentPool.AgentRefreshStatus.RefreshRequired
                        or MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred
            )
            {
                var refreshType =
                    refresh.Status == MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred
                        ? "sandbox_session_refresh_deferred"
                        : "sandbox_session_refresh";
                var refreshed = JsonSerializer.Serialize(new Dictionary<string, string> { ["$type"] = refreshType });
                _ = await connection.TrySendTextAsync(refreshed, ct).ConfigureAwait(false);
                if (agentChanged || refresh.Status == MultiTurnAgentPool.AgentRefreshStatus.RefreshRequired)
                {
                    await connection
                        .TryCloseAsync(WebSocketCloseStatus.NormalClosure, "Sandbox session refreshed", ct)
                        .ConfigureAwait(false);
                }
                return;
            }

            agent = refresh.Agent;

            // Create user message
            var userMessage = new TextMessage { Role = Role.User, Text = request.Message };

            // Send to agent (non-blocking - queues the message)
            var inputId = Guid.NewGuid().ToString();

            // No pool ledger call here (#442). The agent announces this accept itself from the place
            // the receipt id is minted, and the pool refuses to pool an agent that cannot - so the id
            // below reaches the ledger through SendAsync, and a failed send withdraws it there too.
            var receipt = await agent.SendAsync([userMessage], inputId: inputId, ct: ct);

            _logger.LogDebug("Message queued for thread {ThreadId}, receipt: {InputId}", threadId, receipt.InputId);
        }
        catch (SandboxSessionUnavailableException ex)
        {
            _logger.LogWarning(
                ex,
                "Sandbox refresh failed before dispatch for thread {ThreadId} (gateway status {StatusCode})",
                threadId,
                ex.StatusCode
            );
            await SendSandboxUnavailableErrorAsync(connection, ex, recordWriter: null, ct);
        }
        catch (PrincipalConflictException ex)
        {
            // The per-message refresh above began asserting this connection's user (#399), which made
            // the pool's principal guard reachable HERE and not only during connection setup. It fires
            // on an already-open socket when the thread's agent changed hands underneath it: an
            // authorized editor grantee releases the owner's pooled entry and the pool recreates it
            // owned by them (#376), and the owner's next typed message then addresses an entry that is
            // no longer hers. Unhandled, that escapes the receive pump into the host and the socket is
            // aborted with no frame - the same silent disconnect the handshake refusal was added to
            // remove, arriving one layer later.
            //
            // Answered with the SAME frame the handshake path sends, deliberately: one condition must
            // not grow a second name because it was reached from a different direction, and the frame
            // omits the other user's id for the same reason it does there.
            _logger.LogWarning(
                ex,
                "Principal conflict on an open socket for thread {ThreadId}: the agent is now bound to a different user",
                threadId
            );
            await SendPrincipalConflictErrorAsync(connection, ex, recordWriter: null, ct);
        }
        catch (AgentNotPooledException ex)
        {
            // The other half of the same handoff, and the reason catching only the conflict above would
            // leave a hole: releasing and recreating are two steps, and a message arriving BETWEEN them
            // finds no entry at all. Same client outcome as a refreshed sandbox session - the socket is
            // closed normally so the UI reconnects and gets whatever agent the thread now has - rather
            // than an abort that tells it nothing.
            _logger.LogInformation(
                ex,
                "Message for thread {ThreadId} arrived while its pooled agent was being handed off; closing so the client reconnects",
                threadId
            );
            await SendAgentReleasedAsync(connection, recordWriter: null, ct).ConfigureAwait(false);
        }
        catch (InputAcceptanceRefusedException ex)
        {
            // The third arrival of the same handoff, one step later than the two above. Those two fire
            // while the agent is being RESOLVED; this one fires when the resolution succeeded and the
            // replacement landed while the send was reporting its accept. Nothing was queued.
            //
            // Answered with the same frame for the same reason the principal conflict is: one
            // condition must not grow a third name because it was reached from a third direction. The
            // client reconnects and gets whatever agent the thread now has, and the message it typed
            // is the message it retries.
            _logger.LogInformation(
                ex,
                "Message for thread {ThreadId} raced an agent replacement; closing so the client reconnects",
                threadId
            );
            await SendAgentReleasedAsync(connection, recordWriter: null, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON from thread {ThreadId}: {Json}", threadId, json);
        }
    }

    /// <summary>
    /// Receives frames from the sub-agent WebSocket client and relays their text to the focused child
    /// sub-agent, via the shared bounded receive pump (<see cref="ReceiveTextMessagesAsync"/>). Delivery
    /// goes through <see cref="RelaySubAgentMessageAsync"/> (using <see cref="SubAgentManager.SendMessageAsync(string, string, bool, CancellationToken)"/>
    /// in background mode) rather than <c>IMultiTurnAgent.SendAsync</c> — the sub-agent sink.
    /// </summary>
    private Task ReceiveSubAgentMessagesFromClientAsync(
        System.Net.WebSockets.WebSocket webSocket,
        RegisteredWebSocketConnection connection,
        SubAgentManager subAgentManager,
        string agentId,
        CancellationToken ct
    ) =>
        ReceiveTextMessagesAsync(
            webSocket,
            $"sub-agent {agentId}",
            (message, token) => RelaySubAgentMessageAsync(connection, subAgentManager, agentId, message, token),
            ct
        );

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
        CancellationToken ct
    )
    {
        var byteCount = Encoding.UTF8.GetByteCount(json);

        if (TryPeekFrameType(json, out var frameType) && frameType == ClientToolResultFrameType)
        {
            await HandleSubAgentClientToolResultAsync(connection, subAgentManager, agentId, json, ct);
            return;
        }

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
                "invalid_json"
            );
            return;
        }

        if (request?.Message is null)
        {
            _logger.LogWarning(
                "Discarded chat request with no message ({ByteCount} bytes) for sub-agent {AgentId}; category {RejectCategory}",
                byteCount,
                agentId,
                "invalid_json"
            );
            return;
        }

        _logger.LogDebug("Relaying message ({ByteCount} bytes) to sub-agent {AgentId}", byteCount, agentId);

        try
        {
            // Background mode is REQUIRED: a synchronous send blocks until the child's whole run
            // completes, which would stall this receive loop. Background returns a JSON receipt
            // immediately (discarded) while the sibling StreamMessagesToClientAsync task carries the
            // child's live deltas back to the client.
            _ = await subAgentManager.SendMessageAsync(agentId, request.Message, runInBackground: true, ct);
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
                "subagent_unavailable"
            );
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
    /// Resolves a <see cref="ClientToolResultFrameType"/> frame against the PRIMARY thread's agent
    /// (issue #246). The target loop is the <paramref name="agent"/> already bound to this connection by
    /// its route — never anything named in the payload. An agent that is not a <see cref="MultiTurnAgentLoop"/>
    /// (e.g. a CLI-backed loop, which never exposes deferred client-tool calls) is reported as
    /// <c>not_found</c>: there is definitionally nothing there to resolve.
    /// </summary>
    /// <remarks>
    /// Internal (not private) so tests can drive the disposed-agent race (issue #246) directly,
    /// deterministically, without racing the connection's own subscription teardown — disposing the
    /// agent through the full <see cref="HandleConnectionAsync"/> path also completes that
    /// connection's <c>agent.SubscribeAsync</c> subscriber channel, which legitimately (and
    /// near-instantly) ends the whole connection before a queued frame can ever reach this handler.
    /// </remarks>
    internal async Task HandleClientToolResultAsync(
        RegisteredWebSocketConnection connection,
        IMultiTurnAgent agent,
        string threadId,
        string json,
        CancellationToken ct
    )
    {
        if (!TryParseClientToolResultFrame(json, out var toolCallId, out var result, out var isError))
        {
            _logger.LogWarning("Discarded malformed client_tool_result frame for thread {ThreadId}", threadId);
            await SendClientToolResultErrorAsync(connection, toolCallId, "invalid", ct);
            return;
        }

        if (agent is not MultiTurnAgentLoop loop)
        {
            _logger.LogWarning(
                "client_tool_result for thread {ThreadId} (tool_call {ToolCallId}) targets an agent that "
                    + "does not support deferred tool resolution",
                threadId,
                toolCallId
            );
            await SendClientToolResultErrorAsync(connection, toolCallId, "not_found", ct);
            return;
        }

        ResolveToolCallOutcome outcome;
        try
        {
            outcome = await loop.TryResolveToolCallAsync(toolCallId!, result!, isError, ct: ct);
        }
        catch (ObjectDisposedException)
        {
            // Reachable race (issue #246): the connection captured this agent before a concurrent
            // ConversationsController.Delete (or pool eviction) disposed it. Treat it the same as an
            // agent that can no longer be resolved rather than letting the receive loop fault.
            _logger.LogWarning(
                "client_tool_result for thread {ThreadId} (tool_call {ToolCallId}) targets an agent "
                    + "that was disposed before the frame was processed",
                threadId,
                toolCallId
            );
            await SendClientToolResultErrorAsync(connection, toolCallId, "not_found", ct);
            return;
        }

        await SendClientToolResultOutcomeAsync(connection, toolCallId!, outcome, ct);
    }

    /// <summary>
    /// Resolves a <see cref="ClientToolResultFrameType"/> frame against the focused SUB-AGENT (issue
    /// #246). The target loop is derived from the route (<paramref name="agentId"/>, the sub-agent
    /// socket's own path segment) via <see cref="SubAgentManager.TryGetAgent"/> — never from the payload.
    /// </summary>
    /// <remarks>
    /// Internal (not private) for the same testability reason as <see cref="HandleClientToolResultAsync"/>.
    /// </remarks>
    internal async Task HandleSubAgentClientToolResultAsync(
        RegisteredWebSocketConnection connection,
        SubAgentManager subAgentManager,
        string agentId,
        string json,
        CancellationToken ct
    )
    {
        if (!TryParseClientToolResultFrame(json, out var toolCallId, out var result, out var isError))
        {
            _logger.LogWarning("Discarded malformed client_tool_result frame for sub-agent {AgentId}", agentId);
            await SendClientToolResultErrorAsync(connection, toolCallId, "invalid", ct);
            return;
        }

        if (!subAgentManager.TryGetAgent(agentId, out var childAgent) || childAgent is not MultiTurnAgentLoop loop)
        {
            _logger.LogWarning(
                "client_tool_result for sub-agent {AgentId} (tool_call {ToolCallId}) targets an unavailable "
                    + "or non-deferrable agent",
                agentId,
                toolCallId
            );
            await SendClientToolResultErrorAsync(connection, toolCallId, "not_found", ct);
            return;
        }

        ResolveToolCallOutcome outcome;
        try
        {
            outcome = await loop.TryResolveToolCallAsync(toolCallId!, result!, isError, ct: ct);
        }
        catch (ObjectDisposedException)
        {
            // Same reachable race as the primary path, but here `childAgent` was re-resolved from
            // SubAgentManager on this very frame (see the doc comment above), and STILL lost the race
            // against a concurrent dispose (e.g. parent teardown tearing down the whole descendant tree).
            _logger.LogWarning(
                "client_tool_result for sub-agent {AgentId} (tool_call {ToolCallId}) targets an agent "
                    + "that was disposed before the frame was processed",
                agentId,
                toolCallId
            );
            await SendClientToolResultErrorAsync(connection, toolCallId, "not_found", ct);
            return;
        }

        await SendClientToolResultOutcomeAsync(connection, toolCallId!, outcome, ct);
    }

    /// <summary>
    /// Peeks the <c>$type</c> discriminator of an inbound frame without committing to any particular
    /// payload shape. Returns <see langword="false"/> for non-JSON-object or unparsable input, in which
    /// case the caller falls through to its existing (unchanged) <see cref="ChatRequest"/> handling — the
    /// same malformed input then surfaces via that path's own <see cref="JsonException"/> handling.
    /// </summary>
    private static bool TryPeekFrameType(string json, out string? frameType)
    {
        frameType = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("$type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
            )
            {
                frameType = typeEl.GetString();
                return true;
            }
        }
        catch (JsonException) { }

        return false;
    }

    /// <summary>
    /// Parses a <see cref="ClientToolResultFrameType"/> frame by exact-key <see cref="JsonDocument"/>
    /// reads — sidestepping any <see cref="_jsonOptions"/> naming-policy question, consistent with how
    /// <c>AskUserQuestionToolProvider</c>/<c>NotifyClientToolProvider</c> parse their own tool args.
    /// Requires a non-empty <c>toolCallId</c> and a present (possibly empty-string) <c>result</c>;
    /// <c>isError</c> defaults to <see langword="false"/> when absent.
    /// </summary>
    private static bool TryParseClientToolResultFrame(
        string json,
        out string? toolCallId,
        out string? result,
        out bool isError
    )
    {
        toolCallId = null;
        result = null;
        isError = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            toolCallId =
                root.TryGetProperty("toolCallId", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
            result =
                root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.String
                    ? resultEl.GetString()
                    : null;
            isError = root.TryGetProperty("isError", out var errorEl) && errorEl.ValueKind == JsonValueKind.True;

            return !string.IsNullOrEmpty(toolCallId) && result != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends the ack/error frame corresponding to a <see cref="ResolveToolCallOutcome"/> (issue #246).
    /// <see cref="ResolveToolCallOutcome.Resolved"/>/<see cref="ResolveToolCallOutcome.Duplicate"/> are
    /// both successes (the call is settled) and map to <c>client_tool_result_ack</c>; every other value is
    /// permanent-or-retryable-but-still-unresolved and maps to <c>client_tool_result_error</c>.
    /// </summary>
    private Task SendClientToolResultOutcomeAsync(
        RegisteredWebSocketConnection connection,
        string toolCallId,
        ResolveToolCallOutcome outcome,
        CancellationToken ct
    ) =>
        outcome switch
        {
            ResolveToolCallOutcome.Resolved => SendClientToolResultAckAsync(connection, toolCallId, "resolved", ct),
            ResolveToolCallOutcome.Duplicate => SendClientToolResultAckAsync(connection, toolCallId, "duplicate", ct),
            ResolveToolCallOutcome.NotFound => SendClientToolResultErrorAsync(connection, toolCallId, "not_found", ct),
            ResolveToolCallOutcome.Conflict => SendClientToolResultErrorAsync(connection, toolCallId, "conflict", ct),
            ResolveToolCallOutcome.StoreFailed => SendClientToolResultErrorAsync(
                connection,
                toolCallId,
                "store_failed",
                ct
            ),
            ResolveToolCallOutcome.Cancelled => SendClientToolResultErrorAsync(connection, toolCallId, "cancelled", ct),
            _ => SendClientToolResultErrorAsync(connection, toolCallId, "invalid", ct),
        };

    private async Task SendClientToolResultAckAsync(
        RegisteredWebSocketConnection connection,
        string toolCallId,
        string status,
        CancellationToken ct
    )
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "client_tool_result_ack",
            ["toolCallId"] = toolCallId,
            ["status"] = status,
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        _ = await connection.TrySendTextAsync(json, ct);
    }

    private async Task SendClientToolResultErrorAsync(
        RegisteredWebSocketConnection connection,
        string? toolCallId,
        string code,
        CancellationToken ct
    )
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "client_tool_result_error",
            ["toolCallId"] = toolCallId,
            ["code"] = code,
            ["message"] = SafeClientToolResultErrorMessage(code),
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        _ = await connection.TrySendTextAsync(json, ct);
    }

    /// <summary>
    /// Maps a <c>client_tool_result_error</c> <paramref name="code"/> to a stable, safe-to-display
    /// diagnostic message (PR #249 review). Before this, the frame carried only <c>code</c> — the
    /// browser client's <c>onClientToolResultError</c> handler falls back to the literal string
    /// "Unknown error" whenever <c>message</c> is absent, so every real rejection reason (a stale
    /// tool-call id, a conflicting resubmission, a persistence failure, a cancelled request) was
    /// indistinguishable to the user and to anyone reading client-side logs. These strings never echo
    /// server internals (no exception text, no store paths) — only the fixed, code-derived reason.
    /// </summary>
    private static string SafeClientToolResultErrorMessage(string code) =>
        code switch
        {
            "invalid" => "The client_tool_result frame was malformed and could not be parsed.",
            "not_found" => "No deferred tool call was found with this identifier.",
            "conflict" => "This tool call was already resolved with different content.",
            "store_failed" => "The result could not be saved; please retry.",
            "cancelled" => "The request was cancelled before it could be saved; please retry.",
            _ => "Unknown error",
        };

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
            ex.GetType().Name
        );

    /// <summary>
    /// Sends a structured, correlated <c>relay_failed</c> error frame to the client without closing the
    /// connection — a transient relay failure must not silently drop the client's input nor tear down the
    /// presentation-only view. The frame carries no message body (EUII).
    /// </summary>
    private async Task SendRelayFailedErrorAsync(
        RegisteredWebSocketConnection connection,
        string agentId,
        CancellationToken ct
    )
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
        CancellationToken ct
    )
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
            CancellationToken.None
        );
    }

    private async Task SendProviderUnavailableErrorAsync(
        RegisteredWebSocketConnection connection,
        ProviderUnavailableException ex,
        StreamWriter? recordWriter,
        CancellationToken ct
    )
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
            CancellationToken.None
        );
    }

    private async Task SendSandboxUnavailableErrorAsync(
        RegisteredWebSocketConnection connection,
        SandboxSessionUnavailableException ex,
        StreamWriter? recordWriter,
        CancellationToken ct
    )
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
            CancellationToken.None
        );
    }

    /// <summary>
    /// Tells a client its pooled agent is gone and closes the socket normally, so it reconnects onto
    /// whatever agent the thread now has.
    /// </summary>
    /// <remarks>
    /// Deliberately the SAME frame a replaced sandbox session sends: from the client's side the two are
    /// one situation - "the thing you were talking to is no longer there, reconnect" - and giving it a
    /// second name would buy a second branch that behaves identically. What it must never be is silence:
    /// an unhandled release aborts the socket, and an abort is indistinguishable from the network
    /// dropping.
    /// </remarks>
    private static async Task SendAgentReleasedAsync(
        RegisteredWebSocketConnection connection,
        StreamWriter? recordWriter,
        CancellationToken ct
    )
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["$type"] = "sandbox_session_refresh" });

        if (await connection.TrySendTextAsync(json, ct).ConfigureAwait(false) && recordWriter != null)
        {
            await recordWriter.WriteLineAsync(json);
            await recordWriter.FlushAsync();
        }

        await connection.TryCloseAsync(WebSocketCloseStatus.NormalClosure, "Agent released", ct).ConfigureAwait(false);
    }

    private async Task SendPrincipalConflictErrorAsync(
        RegisteredWebSocketConnection connection,
        PrincipalConflictException ex,
        StreamWriter? recordWriter,
        CancellationToken ct
    )
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",

            // Same code the REST 409 carries, so a client needs one branch rather than two names for
            // one condition.
            ["code"] = "principal_conflict",

            // Deliberately does NOT relay ex.Message: that names the OTHER user's id, and this
            // connection has not been authorized to learn who else uses this conversation.
            ["message"] = "This conversation's agent is in use by a different user and cannot be continued here.",
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
            "Principal conflict",
            CancellationToken.None
        );
    }

    private async Task SendCredentialConflictErrorAsync(
        RegisteredWebSocketConnection connection,
        SandboxCredentialConflictException ex,
        StreamWriter? recordWriter,
        CancellationToken ct
    )
    {
        var payload = new Dictionary<string, object?>
        {
            ["$type"] = "error",
            ["code"] = "caller_credential_conflict",

            // Deliberately does NOT relay ex.Message, for the same reason as its principal sibling
            // below: the message interpolates BOTH app ids, and this connection has not been
            // authorized to learn which service the conversation is frozen to. It used to be appended
            // on the grounds that app ids are not app keys - true, and beside the point once the REST
            // body stopped carrying them. Two transports answering one condition must agree on what
            // the refused caller may learn. The ids remain on the log line above.
            ["message"] = "This conversation belongs to a different caller identity and cannot be continued here.",
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
            CancellationToken.None
        );
    }
}

/// <summary>
/// Request format for chat messages from client.
/// </summary>
public record ChatRequest(string Message);
