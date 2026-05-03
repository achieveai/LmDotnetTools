using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Multi-turn agent implementation using raw LLM APIs with middleware pipeline.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
/// Supports deferred tool execution: a tool handler may return
/// <see cref="ToolHandlerResult.Deferred"/>, in which case the loop records a placeholder
/// in history, ends the run, and waits for an external caller to invoke
/// <see cref="ResolveToolCallAsync"/>. When all deferrals from the latest turn resolve,
/// a new run starts automatically.
/// </summary>
/// <remarks>
/// This implementation uses a middleware stack for message processing:
/// - MessageTransformationMiddleware (assigns messageOrderIdx, handles aggregates)
/// - JsonFragmentUpdateMiddleware (handles JSON fragment updates)
/// - MessagePublishingMiddleware (publishes ALL messages to subscribers - updates + full)
/// - MessageUpdateJoinerMiddleware (joins update messages into full messages for history)
/// - ToolCallInjectionMiddleware (injects function contracts for tool calling)
/// </remarks>
public sealed class MultiTurnAgentLoop : MultiTurnAgentBase
{
    private readonly IStreamingAgent _agent;
    private readonly IDictionary<string, ToolHandler> _toolHandlers;
    private readonly SubAgentManager? _subAgentManager;

    // Deferred tool tracking. Keyed by ToolCallId. Concurrent because resolutions arrive on
    // arbitrary threads (UI callbacks, webhook handlers).
    private readonly ConcurrentDictionary<string, DeferredEntry> _deferred = new();

    // Auto-resume gating. _resumeLock guards _resumeScheduled, _runActive, and the
    // _lastDeferring* pair so we never schedule two sentinels for the same wave nor
    // schedule one while the loop is still mid-run.
    private readonly object _resumeLock = new();
    private bool _resumeScheduled;
    private bool _runActive;
    private string? _lastDeferringRunId;
    private string? _lastDeferringGenerationId;

    /// <summary>
    /// Creates a new MultiTurnAgentLoop with FunctionRegistry for tool management.
    /// The loop owns the complete middleware stack creation.
    /// </summary>
    /// <param name="providerAgent">The base provider streaming agent (without middleware - the loop builds the stack)</param>
    /// <param name="functionRegistry">The function registry containing tool definitions and handlers</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template (ModelId, Temperature, MaxThinkingTokens, etc.)</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run before stopping (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="subAgentOptions">Optional sub-agent orchestration configuration</param>
    public MultiTurnAgentLoop(
        IStreamingAgent providerAgent,
        FunctionRegistry functionRegistry,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<MultiTurnAgentLoop>? logger = null,
        SubAgentOptions? subAgentOptions = null)
        : base(threadId, systemPrompt, defaultOptions, maxTurnsPerRun, inputChannelCapacity, outputChannelCapacity, store, logger)
    {
        ArgumentNullException.ThrowIfNull(providerAgent);
        ArgumentNullException.ThrowIfNull(functionRegistry);

        // When sub-agent orchestration is configured, snapshot the current tools
        // and register Agent/CheckAgent tools before building the middleware stack.
        if (subAgentOptions != null)
        {
            // IMPORTANT: Snapshot parent tools BEFORE registering sub-agent tools.
            // This ensures sub-agents inherit the parent's domain tools but NOT the
            // Agent/CheckAgent tools, preventing unbounded recursive delegation.
            var (contracts, handlers) = functionRegistry.Build();

            _subAgentManager = new SubAgentManager(
                parentAgent: this,
                parentContracts: contracts.ToList(),
                parentHandlers: handlers,
                options: subAgentOptions,
                logger: logger);

            var toolProvider = new SubAgentToolProvider(
                _subAgentManager,
                subAgentOptions.Templates.Keys.ToList());

            functionRegistry.AddProvider(toolProvider);
        }

        // Build tool call components from registry
        var (toolCallMiddleware, finalHandlers) = functionRegistry.BuildToolCallComponents(name: "MultiTurnAgentTools");
        _toolHandlers = finalHandlers;

        // Create publishing middleware that publishes to subscribers
        // Positioned BEFORE MessageUpdateJoinerMiddleware to capture streaming updates
        var publishingMiddleware = new MessagePublishingMiddleware(PublishToAllAsync);

        // Build the complete middleware stack (loop owns the pipeline)
        // Response path order: Provider -> MessageTransformation -> JsonFragment -> Publishing -> Joiner -> ToolCall
        _agent = providerAgent
            .WithMessageTransformation()
            .WithMiddleware(new JsonFragmentUpdateMiddleware())
            .WithMiddleware(publishingMiddleware)
            .WithMiddleware(new MessageUpdateJoinerMiddleware(name: "MessageJoiner"))
            .WithMiddleware(toolCallMiddleware);
    }

    /// <inheritdoc />
    protected override async Task OnDisposeAsync()
    {
        if (_subAgentManager != null)
        {
            await _subAgentManager.DisposeAsync();
        }
    }

    /// <inheritdoc />
    protected override async Task RunLoopAsync(CancellationToken ct)
    {
        Logger.LogDebug("MultiTurnAgentLoop run loop started");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for at least one input
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break; // Channel completed
                }

                // Drain all available inputs
                _ = TryDrainInputs(out var batch);
                if (batch.Count == 0)
                {
                    continue;
                }

                // Split into real user inputs vs internal resume sentinels. A batch can mix
                // both if a real input lands while a resume is already queued; both feed into
                // the same run.
                var realInputs = batch.Where(b => b.Resume == null).ToList();
                var resumeSentinels = batch.Where(b => b.Resume != null).ToList();

                if (realInputs.Count == 0 && resumeSentinels.Count == 0)
                {
                    continue;
                }

                if (resumeSentinels.Count > 0)
                {
                    // Clear the scheduled flag now that we're consuming the sentinel; future
                    // resolution waves can schedule again.
                    lock (_resumeLock)
                    {
                        _resumeScheduled = false;
                    }
                }

                // Start run with the available inputs (use real if any, otherwise sentinels
                // — StartRun records receipt IDs for telemetry but doesn't otherwise care).
                var inputsForAssignment = realInputs.Count > 0 ? realInputs : resumeSentinels;
                var assignment = StartRun(inputsForAssignment);
                await PublishToAllAsync(new RunAssignmentMessage
                {
                    Assignment = assignment,
                    ThreadId = ThreadId,
                }, ct);

                // Add real-input messages to history. Resume sentinels contribute no new
                // messages — the loop continues from history that already has the resolved
                // tool_results in place of the prior placeholders.
                foreach (var input in realInputs)
                {
                    foreach (var msg in input.Input.Messages)
                    {
                        AddToHistory(msg);
                    }
                }

                lock (_resumeLock)
                {
                    _runActive = true;
                }

                try
                {
                    // Execute turns - poll for new input between turns
                    await ExecuteRunTurnsAsync(assignment.RunId, assignment.GenerationId, ct);

                    // Complete run - simple loop has no pending messages
                    await CompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        wasForked: false,
                        forkedToRunId: null,
                        pendingMessageCount: 0,
                        ct: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per-run error: log, notify client, but keep the loop alive
                    Logger.LogError(ex, "Error during run {RunId}", assignment.RunId);
                    await CompleteRunAsync(
                        assignment.RunId,
                        assignment.GenerationId,
                        isError: true,
                        errorMessage: ex.Message,
                        ct: ct);
                }
                finally
                {
                    lock (_resumeLock)
                    {
                        _runActive = false;
                    }

                    // The run is over. If resolutions arrived during the run such that all
                    // deferreds for the deferring generation are already resolved, schedule
                    // an auto-resume now (the in-run resolutions deliberately deferred this
                    // until _runActive flipped false).
                    TryScheduleAutoResume(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("MultiTurnAgentLoop run loop cancelled");
        }
        catch (ChannelClosedException)
        {
            Logger.LogDebug("Input channel closed");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error in run loop");
            throw;
        }
        finally
        {
            await OnAfterRunAsync();
        }
    }

    /// <summary>
    /// Execute the agentic turns for a run, polling for new input between turns.
    /// Ends the run early if any tool call deferrals from the current generation remain
    /// unresolved at the end of a turn — see <see cref="ResolveToolCallAsync"/>.
    /// </summary>
    private async Task ExecuteRunTurnsAsync(string runId, string generationId, CancellationToken ct)
    {
        var turnCount = 0;

        while (turnCount < MaxTurnsPerRun)
        {
            ct.ThrowIfCancellationRequested();

            // POLL: Check for new inputs before each turn (skip resume sentinels — they're
            // not real injected messages, they only exist to wake the channel).
            if (TryDrainInputs(out var newInputs) && newInputs.Count > 0)
            {
                var realNewInputs = newInputs.Where(b => b.Resume == null).ToList();
                if (realNewInputs.Count > 0)
                {
                    var injectionAssignment = new RunAssignment(
                        RunId: runId,
                        GenerationId: generationId,
                        InputIds: [.. realNewInputs.Select(i => i.ReceiptId)],
                        ParentRunId: null,
                        WasInjected: true
                    );

                    await PublishToAllAsync(new RunAssignmentMessage
                    {
                        Assignment = injectionAssignment,
                        ThreadId = ThreadId,
                    }, ct);

                    foreach (var input in realNewInputs)
                    {
                        foreach (var msg in input.Input.Messages)
                        {
                            AddToHistory(msg);
                        }
                    }

                    Logger.LogInformation(
                        "Injected {Count} new inputs into run {RunId}, sent RunAssignment",
                        realNewInputs.Count,
                        runId);
                }
            }

            turnCount++;
            Logger.LogDebug("Executing turn {Turn} of run {RunId}", turnCount, runId);

            var hasToolCalls = await ExecuteTurnAsync(runId, generationId, turnCount, ct);

            // If any tool call from this generation deferred and is still unresolved, end
            // the run cleanly. Resolution of the last deferred entry will auto-trigger a
            // new run via the resume sentinel.
            if (HasUnresolvedDeferralsForGeneration(generationId))
            {
                lock (_resumeLock)
                {
                    _lastDeferringRunId = runId;
                    _lastDeferringGenerationId = generationId;
                    _resumeScheduled = false;
                }

                Logger.LogInformation(
                    "Run {RunId} pausing on {Count} deferred tool call(s); awaiting external resolution",
                    runId,
                    _deferred.Values.Count(d => d.GenerationId == generationId));
                break;
            }

            if (!hasToolCalls)
            {
                Logger.LogDebug("No tool calls in turn {Turn}, run complete", turnCount);
                break;
            }
        }

        if (turnCount >= MaxTurnsPerRun)
        {
            Logger.LogWarning("Max turns ({MaxTurns}) reached for run {RunId}", MaxTurnsPerRun, runId);
        }
    }

    private async Task<bool> ExecuteTurnAsync(
        string runId,
        string generationId,
        int turnNumber,
        CancellationToken ct)
    {
        // Use defaultOptions as template, override run-specific fields
        var options = DefaultOptions with
        {
            RunId = runId,
            ThreadId = ThreadId,
        };

        // Build messages list with system prompt prepended (if configured)
        var messagesToSend = GetMessagesWithSystemPrompt();

        var stream = await _agent.GenerateReplyStreamingAsync(messagesToSend, options, ct);

        var hasToolCalls = false;
        var pendingToolCalls = new Dictionary<string, Task<ToolCallResultMessage>>();

        await foreach (var msg in stream.WithCancellation(ct))
        {
            // Add to history (messages already published by MessagePublishingMiddleware)
            AddToHistory(msg);

            // Handle tool calls - MessageTransformationMiddleware converts ToolsCallMessage -> ToolCallMessage
            if (msg is ToolCallMessage toolCall)
            {
                if (toolCall.ExecutionTarget != ExecutionTarget.LocalFunction)
                {
                    // Provider/server tools are executed remotely and should not be routed
                    // through local tool handlers.
                    Logger.LogDebug(
                        "Skipping non-local tool call (executed remotely): FunctionName={FunctionName}, ToolCallId={ToolCallId}, ExecutionTarget={ExecutionTarget}",
                        toolCall.FunctionName,
                        toolCall.ToolCallId,
                        toolCall.ExecutionTarget
                    );
                    continue;
                }

                hasToolCalls = true;

                // Fail-fast: ToolCallId is required for proper correlation
                if (string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    throw new InvalidOperationException(
                        $"ToolCallMessage.ToolCallId is required but was null or empty. " +
                        $"FunctionName: {toolCall.FunctionName ?? "(null)"}");
                }

                Logger.LogDebug(
                    "Tool call received: {FunctionName} (id: {ToolCallId})",
                    toolCall.FunctionName,
                    toolCall.ToolCallId);

                // Start execution and publish result immediately when complete
                // This runs in parallel with LLM streaming and other tool executions.
                // Pass the run's generationId so deferred entries are tagged consistently —
                // toolCall.GenerationId is set by the provider/middleware and may be missing.
                var executionTask = ExecuteAndPublishToolCallAsync(toolCall, runId, generationId, ct);
                pendingToolCalls[toolCall.ToolCallId] = executionTask;
            }
        }

        // Wait for all tool executions to complete before next turn
        // Results are already published as each tool completes. Deferred handlers complete
        // synchronously with a placeholder, so this never blocks on external resolution.
        if (pendingToolCalls.Count > 0)
        {
            Logger.LogDebug("Awaiting {Count} tool call results", pendingToolCalls.Count);
            _ = await Task.WhenAll(pendingToolCalls.Values);
        }

        return hasToolCalls;
    }

    /// <summary>
    /// Executes a tool call and immediately publishes the result to all subscribers.
    /// This enables parallel execution with LLM streaming - results are sent to clients
    /// as each tool completes, rather than waiting for all tools to finish.
    /// For deferred handlers, the placeholder is registered in <see cref="_deferred"/>
    /// before publishing so a racing <see cref="ResolveToolCallAsync"/> always finds it.
    /// </summary>
    private async Task<ToolCallResultMessage> ExecuteAndPublishToolCallAsync(
        ToolCallMessage toolCall,
        string runId,
        string generationId,
        CancellationToken ct)
    {
        var result = await ExecuteToolCallAsync(toolCall, ct);

        if (result.IsDeferred)
        {
            // Register the deferred entry BEFORE making the placeholder visible to history
            // or subscribers. Any incoming ResolveToolCallAsync needs to find the entry to
            // succeed. Stamp with the run's runId/generationId — toolCall.RunId/GenerationId
            // are set by the provider/middleware and may be unset in some agents/tests.
            var deferredEntry = new DeferredEntry(
                ToolCallId: result.ToolCallId!,
                FunctionName: toolCall.FunctionName ?? string.Empty,
                FunctionArgs: toolCall.FunctionArgs ?? "{}",
                Placeholder: result.Result,
                DeferredAtUnixMs: result.DeferredAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RunId: toolCall.RunId ?? runId,
                GenerationId: toolCall.GenerationId ?? generationId,
                Metadata: result.DeferralMetadata);
            _deferred[result.ToolCallId!] = deferredEntry;

            // Persist synchronously so a webhook-triggered ReplaceMessageAsync cannot race
            // the placeholder's append.
            await AddDeferredToHistoryAsync(result, ct);
            await PublishToAllAsync(result, ct);

            Logger.LogInformation(
                "Tool call {ToolCallId} ({FunctionName}) deferred with placeholder length {Length}",
                toolCall.ToolCallId,
                toolCall.FunctionName,
                result.Result.Length);

            return result;
        }

        // Non-deferred result. Add text-only version to LLM history (captions are in the
        // text for id:// referencing); publish full version with ContentBlocks to
        // subscribers (for image data resolution).
        var historyResult = result.ContentBlocks != null
            ? result with { ContentBlocks = null }
            : result;
        AddToHistory(historyResult);
        await PublishToAllAsync(result, ct);

        Logger.LogDebug(
            "Tool result for {ToolCallId}: {ResultPreview}",
            toolCall.ToolCallId,
            result.Result.Length > 100 ? result.Result[..100] + "..." : result.Result);

        return result;
    }

    private async Task<ToolCallResultMessage> ExecuteToolCallAsync(
        ToolCallMessage toolCall,
        CancellationToken ct)
    {
        // Fail-fast validation: these fields are required for proper tool execution
        ArgumentNullException.ThrowIfNull(toolCall);

        if (string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            throw new ArgumentException(
                "ToolCallMessage.ToolCallId is required but was null or empty.",
                nameof(toolCall));
        }

        if (string.IsNullOrEmpty(toolCall.FunctionName))
        {
            throw new ArgumentException(
                $"ToolCallMessage.FunctionName is required but was null or empty. ToolCallId: {toolCall.ToolCallId}",
                nameof(toolCall));
        }

        // FunctionArgs can be null for parameterless functions - treat as empty object
        var functionArgs = toolCall.FunctionArgs ?? "{}";

        try
        {
            if (!_toolHandlers.TryGetValue(toolCall.FunctionName, out var handler))
            {
                // Unknown function - likely LLM hallucination, return error to allow self-correction
                Logger.LogWarning(
                    "No handler registered for function '{FunctionName}'. Returning error to LLM. " +
                    "ToolCallId: {ToolCallId}. Available functions: [{AvailableFunctions}]",
                    toolCall.FunctionName,
                    toolCall.ToolCallId,
                    string.Join(", ", _toolHandlers.Keys));

                return new ToolCallResultMessage
                {
                    ToolCallId = toolCall.ToolCallId,
                    ToolName = toolCall.FunctionName,
                    Result = JsonSerializer.Serialize(new
                    {
                        error = $"Unknown function: {toolCall.FunctionName}",
                        available_functions = _toolHandlers.Keys.ToArray(),
                    }),
                    IsError = true,
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                    Role = Role.User,
                    FromAgent = toolCall.FromAgent,
                    GenerationId = toolCall.GenerationId,
                };
            }

            var ctx = new ToolCallContext
            {
                ToolCallId = toolCall.ToolCallId,
            };
            var result = await handler(functionArgs, ctx, ct);
            return BuildResultMessage(toolCall, result);
        }
        catch (Exception ex)
        {
            // Tool execution errors are returned to the LLM for retry/correction
            Logger.LogError(ex, "Error executing tool call: {FunctionName}", toolCall.FunctionName);
            return new ToolCallResultMessage
            {
                ToolCallId = toolCall.ToolCallId,
                ToolName = toolCall.FunctionName,
                Result = JsonSerializer.Serialize(new { error = ex.Message }),
                IsError = true,
                ExecutionTarget = ExecutionTarget.LocalFunction,
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            };
        }
    }

    private static ToolCallResultMessage BuildResultMessage(
        ToolCallMessage toolCall,
        ToolHandlerResult result)
    {
        return result switch
        {
            ToolHandlerResult.Resolved r => new ToolCallResultMessage
            {
                ToolCallId = toolCall.ToolCallId,
                ToolName = toolCall.FunctionName,
                Result = r.Result.Result ?? string.Empty,
                ContentBlocks = r.Result.ContentBlocks,
                IsError = r.Result.IsError,
                ErrorCode = r.Result.ErrorCode,
                ExecutionTarget = ExecutionTarget.LocalFunction,
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            },
            ToolHandlerResult.Deferred d => new ToolCallResultMessage
            {
                ToolCallId = toolCall.ToolCallId,
                ToolName = toolCall.FunctionName,
                Result = d.Placeholder,
                IsDeferred = true,
                DeferralMetadata = d.Metadata,
                DeferredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExecutionTarget = ExecutionTarget.LocalFunction,
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            },
            _ => throw new InvalidOperationException(
                $"Unknown ToolHandlerResult variant '{result.GetType().Name}'"),
        };
    }

    /// <summary>
    /// Resolves a previously-deferred tool call. Mutates the placeholder in history and
    /// persisted store, publishes the resolved <see cref="ToolCallResultMessage"/> to
    /// subscribers, and — when this resolution completes the most recent turn's deferred
    /// set — auto-triggers a new run.
    /// </summary>
    /// <remarks>
    /// Idempotent for byte-equal duplicate deliveries (webhook retries are common).
    /// Throws <see cref="InvalidOperationException"/> if no matching deferred call exists,
    /// or if the call has already been resolved with different content.
    /// </remarks>
    public Task ResolveToolCallAsync(
        string toolCallId,
        string result,
        bool isError = false,
        IList<ToolResultContentBlock>? contentBlocks = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentNullException.ThrowIfNull(result);

        return ResolveToolCallInternalAsync(toolCallId, result, isError, contentBlocks, ct);
    }

    private async Task ResolveToolCallInternalAsync(
        string toolCallId,
        string result,
        bool isError,
        IList<ToolResultContentBlock>? contentBlocks,
        CancellationToken ct)
    {
        // Capture deferred entry first to fail-fast on unknown ids without locking history.
        if (!_deferred.TryGetValue(toolCallId, out var deferredEntry))
        {
            // It might have been resolved already — UpdateToolResultByCallId will surface a
            // proper conflict error. If history doesn't have the id either, that throws
            // InvalidOperationException with a clear message.
        }

        var noOp = false;

        var (oldMessage, newMessage) = UpdateToolResultByCallId(toolCallId, existing =>
        {
            // Case 1: still deferred — apply the resolution.
            if (existing.IsDeferred)
            {
                return existing with
                {
                    Result = result,
                    ContentBlocks = null, // history-side is text-only; subscribers get full
                    IsError = isError,
                    ErrorCode = isError ? "deferred_resolution_error" : null,
                    IsDeferred = false,
                    DeferralMetadata = null,
                    ResolvedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
            }

            // Case 2: already resolved with the same content — idempotent no-op.
            if (existing.Result == result && existing.IsError == isError)
            {
                noOp = true;
                return existing;
            }

            // Case 3: already resolved with different content — conflict.
            throw new InvalidOperationException(
                $"Tool call '{toolCallId}' has already been resolved with different content. " +
                "Cannot resolve again with a different value.");
        });

        if (noOp)
        {
            Logger.LogDebug(
                "ResolveToolCallAsync no-op: '{ToolCallId}' was already resolved with identical content",
                toolCallId);
            // Make sure the deferred entry is cleaned up in case it lingered.
            _ = _deferred.TryRemove(toolCallId, out _);
            return;
        }

        // Persist the replacement (best-effort; failures are logged inside).
        await ReplacePersistedAsync(oldMessage, newMessage, ct);

        // Publish the full message (including ContentBlocks) to subscribers so UIs can
        // render images. The history entry stays text-only.
        var publishMessage = contentBlocks != null && contentBlocks.Count > 0
            ? newMessage with { ContentBlocks = contentBlocks }
            : newMessage;
        await PublishToAllAsync(publishMessage, ct);

        _ = _deferred.TryRemove(toolCallId, out _);

        Logger.LogInformation(
            "Tool call {ToolCallId} resolved (was deferred for {ElapsedMs}ms)",
            toolCallId,
            deferredEntry != null
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - deferredEntry.DeferredAtUnixMs
                : 0);

        TryScheduleAutoResume(ct);
    }

    /// <summary>
    /// Returns the set of tool calls currently deferred (awaiting external resolution).
    /// Hosts use this to inspect state, render pending UI, or — on process restart —
    /// reconnect external workflows to the calls they're supposed to complete.
    /// </summary>
    public Task<IReadOnlyList<DeferredToolCallInfo>> GetDeferredToolCallsAsync(CancellationToken ct = default)
    {
        var snapshot = _deferred.Values
            .Select(e => new DeferredToolCallInfo
            {
                ToolCallId = e.ToolCallId,
                FunctionName = e.FunctionName,
                FunctionArgs = e.FunctionArgs,
                Placeholder = e.Placeholder,
                DeferredAtUnixMs = e.DeferredAtUnixMs,
                Metadata = e.Metadata,
                RunId = e.RunId,
                GenerationId = e.GenerationId,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DeferredToolCallInfo>>(snapshot);
    }

    /// <inheritdoc />
    protected override Task OnHistoryRestoredAsync(IReadOnlyList<IMessage> messages, CancellationToken ct)
    {
        // Rebuild the deferred registry from persisted history. Each ToolCallResultMessage
        // with IsDeferred=true gets re-registered so GetDeferredToolCallsAsync surfaces it
        // and ResolveToolCallAsync can complete it after restart.
        if (messages == null || messages.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Index ToolCallMessages by ToolCallId so we can recover function name/args for
        // each deferred result.
        var toolCallsById = new Dictionary<string, ToolCallMessage>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg is ToolCallMessage tc && !string.IsNullOrEmpty(tc.ToolCallId))
            {
                toolCallsById[tc.ToolCallId] = tc;
            }
        }

        string? mostRecentDeferringRun = null;
        string? mostRecentDeferringGen = null;

        foreach (var msg in messages)
        {
            if (msg is not ToolCallResultMessage tcr || !tcr.IsDeferred || string.IsNullOrEmpty(tcr.ToolCallId))
            {
                continue;
            }

            toolCallsById.TryGetValue(tcr.ToolCallId, out var sourceCall);

            var entry = new DeferredEntry(
                ToolCallId: tcr.ToolCallId,
                FunctionName: sourceCall?.FunctionName ?? tcr.ToolName ?? string.Empty,
                FunctionArgs: sourceCall?.FunctionArgs ?? "{}",
                Placeholder: tcr.Result,
                DeferredAtUnixMs: tcr.DeferredAt ?? 0,
                RunId: tcr.RunId,
                GenerationId: tcr.GenerationId,
                Metadata: tcr.DeferralMetadata);
            _deferred[tcr.ToolCallId] = entry;

            // Track the most recent (last in load order) deferring run/generation so a
            // resolution after restart will trigger an auto-resume into the right context.
            mostRecentDeferringRun = tcr.RunId ?? mostRecentDeferringRun;
            mostRecentDeferringGen = tcr.GenerationId ?? mostRecentDeferringGen;
        }

        if (_deferred.Count > 0)
        {
            lock (_resumeLock)
            {
                _lastDeferringRunId = mostRecentDeferringRun;
                _lastDeferringGenerationId = mostRecentDeferringGen;
                _resumeScheduled = false;
            }

            Logger.LogInformation(
                "Restored {Count} deferred tool call(s) from persisted history",
                _deferred.Count);
        }

        return Task.CompletedTask;
    }

    private bool HasUnresolvedDeferralsForGeneration(string generationId)
    {
        foreach (var entry in _deferred.Values)
        {
            if (entry.GenerationId == generationId)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Schedule an auto-resume sentinel onto the input channel when:
    /// (1) we have recorded a deferring generation,
    /// (2) all deferred entries for that generation have been resolved,
    /// (3) the loop is not currently inside a run, and
    /// (4) a sentinel hasn't already been scheduled for this wave.
    /// All four conditions are checked under <see cref="_resumeLock"/> to avoid scheduling
    /// duplicate sentinels when many resolutions land at once.
    /// </summary>
    private void TryScheduleAutoResume(CancellationToken ct)
    {
        string runId;
        string genId;

        lock (_resumeLock)
        {
            if (_lastDeferringRunId == null || _lastDeferringGenerationId == null)
            {
                return;
            }

            if (_runActive || _resumeScheduled)
            {
                return;
            }

            if (HasUnresolvedDeferralsForGeneration(_lastDeferringGenerationId))
            {
                return;
            }

            runId = _lastDeferringRunId;
            genId = _lastDeferringGenerationId;
            _resumeScheduled = true;

            // Clear the deferring marker now — once we've scheduled a resume for this wave,
            // future no-op resolutions or post-run TryScheduleAutoResume calls must not
            // re-schedule. The marker is set again only when a new turn defers.
            _lastDeferringRunId = null;
            _lastDeferringGenerationId = null;
        }

        EnqueueResumeSentinel(runId, genId, ct);
    }

    private void EnqueueResumeSentinel(string runId, string generationId, CancellationToken ct)
    {
        // Build a QueuedInput marked with a ResumeSentinel. The Input.Messages list is
        // empty by design — resumes contribute no new messages, only a wake-up.
        var sentinel = new ResumeSentinel(runId, generationId);
        var emptyInput = new UserInput([], InputId: null, ParentRunId: runId);
        var queuedInput = new QueuedInput(
            emptyInput,
            ReceiptId: $"resume:{Guid.NewGuid():N}",
            QueuedAt: DateTimeOffset.UtcNow,
            Resume: sentinel);

        _ = WriteResumeSentinelAsync(queuedInput, ct);
    }

    private async Task WriteResumeSentinelAsync(QueuedInput sentinelInput, CancellationToken ct)
    {
        try
        {
            await EnqueueRawAsync(sentinelInput, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to enqueue resume sentinel for run {RunId}",
                sentinelInput.Resume?.ResumeForRunId);
        }
    }

    /// <summary>
    /// Internal record describing a single deferred tool call awaiting external resolution.
    /// Public surface is <see cref="DeferredToolCallInfo"/>.
    /// </summary>
    private sealed record DeferredEntry(
        string ToolCallId,
        string FunctionName,
        string FunctionArgs,
        string Placeholder,
        long DeferredAtUnixMs,
        string? RunId,
        string? GenerationId,
        ImmutableDictionary<string, string>? Metadata);
}
