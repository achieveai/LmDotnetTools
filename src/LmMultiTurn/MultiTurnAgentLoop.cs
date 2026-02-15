using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Multi-turn agent implementation using raw LLM APIs with middleware pipeline.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
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
    private readonly IDictionary<string, Func<string, Task<string>>> _toolHandlers;

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
        ILogger<MultiTurnAgentLoop>? logger = null)
        : base(threadId, systemPrompt, defaultOptions, maxTurnsPerRun, inputChannelCapacity, outputChannelCapacity, store, logger)
    {
        ArgumentNullException.ThrowIfNull(providerAgent);
        ArgumentNullException.ThrowIfNull(functionRegistry);

        // Build tool call components from registry
        var (toolCallMiddleware, handlers) = functionRegistry.BuildToolCallComponents(name: "MultiTurnAgentTools");
        _toolHandlers = handlers;

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

                // Start run with initial batch
                var assignment = StartRun(batch);
                await PublishToAllAsync(new RunAssignmentMessage
                {
                    Assignment = assignment,
                    ThreadId = ThreadId,
                }, ct);

                // Add initial messages to history
                foreach (var input in batch)
                {
                    foreach (var msg in input.Input.Messages)
                    {
                        AddToHistory(msg);
                    }
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
    /// </summary>
    private async Task ExecuteRunTurnsAsync(string runId, string generationId, CancellationToken ct)
    {
        var turnCount = 0;

        while (turnCount < MaxTurnsPerRun)
        {
            ct.ThrowIfCancellationRequested();

            // POLL: Check for new inputs before each turn
            if (TryDrainInputs(out var newInputs) && newInputs.Count > 0)
            {
                // Send RunAssignment for the newly injected inputs
                // Note: These are added to the CURRENT run, not a new run
                var injectionAssignment = new RunAssignment(
                    RunId: runId,
                    GenerationId: generationId,
                    InputIds: [.. newInputs.Select(i => i.ReceiptId)],
                    ParentRunId: null, // Injected into current run
                    WasInjected: true  // Mark as injected to differentiate from initial assignment
                );

                await PublishToAllAsync(new RunAssignmentMessage
                {
                    Assignment = injectionAssignment,
                    ThreadId = ThreadId,
                }, ct);

                // Add new messages to current run (injection)
                foreach (var input in newInputs)
                {
                    foreach (var msg in input.Input.Messages)
                    {
                        AddToHistory(msg);
                    }
                }

                Logger.LogInformation(
                    "Injected {Count} new inputs into run {RunId}, sent RunAssignment",
                    newInputs.Count,
                    runId);
            }

            turnCount++;
            Logger.LogDebug("Executing turn {Turn} of run {RunId}", turnCount, runId);

            var hasToolCalls = await ExecuteTurnAsync(runId, generationId, turnCount, ct);

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
                // This runs in parallel with LLM streaming and other tool executions
                var executionTask = ExecuteAndPublishToolCallAsync(toolCall, ct);
                pendingToolCalls[toolCall.ToolCallId] = executionTask;
            }
        }

        // Wait for all tool executions to complete before next turn
        // Results are already published as each tool completes
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
    /// </summary>
    private async Task<ToolCallResultMessage> ExecuteAndPublishToolCallAsync(
        ToolCallMessage toolCall,
        CancellationToken ct)
    {
        var result = await ExecuteToolCallAsync(toolCall, ct);

        // Publish immediately when this tool completes (parallel with other tools/streaming)
        AddToHistory(result);
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

            var result = await handler(functionArgs);
            return new ToolCallResultMessage
            {
                ToolCallId = toolCall.ToolCallId,
                ToolName = toolCall.FunctionName,
                Result = result,
                ExecutionTarget = ExecutionTarget.LocalFunction,
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            };
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
}
