using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmConfigUsageExample;

#region Core Types

/// <summary>
/// Input to send to the agentic loop.
/// </summary>
/// <param name="Messages">The messages to submit (user messages, possibly with images)</param>
/// <param name="InputId">Client-provided correlation ID (optional) - echoed back in assignment</param>
/// <param name="ParentRunId">Parent run ID to fork from. If null, continues from latest run</param>
public record UserInput(
    List<IMessage> Messages,
    string? InputId = null,
    string? ParentRunId = null);

/// <summary>
/// Assignment info returned when input is accepted.
/// </summary>
/// <param name="RunId">The run ID assigned to this submission</param>
/// <param name="GenerationId">Server-assigned generation ID for all messages in this generation</param>
/// <param name="InputId">Echoed back if client provided</param>
/// <param name="ParentRunId">The parent run ID if this was a fork</param>
/// <param name="WasInjected">Whether this was injected into an ongoing run</param>
public record RunAssignment(
    string RunId,
    string GenerationId,
    string? InputId = null,
    string? ParentRunId = null,
    bool WasInjected = false);

/// <summary>
/// Message published when a run assignment is created.
/// Allows subscribers to track when user input is assigned to a run.
/// </summary>
public record RunAssignmentMessage : IMessage
{
    public required RunAssignment Assignment { get; init; }

    public string? FromAgent { get; init; }
    public Role Role => Role.System;
    public ImmutableDictionary<string, object>? Metadata { get; init; }
    public string? RunId => Assignment.RunId;
    public string? ParentRunId => Assignment.ParentRunId;
    public string? ThreadId { get; init; }
    public string? GenerationId => Assignment.GenerationId;
    public int? MessageOrderIdx { get; init; }
}

/// <summary>
/// Message published when a run completes.
/// </summary>
public record RunCompletedMessage : IMessage
{
    public required string CompletedRunId { get; init; }
    public bool WasForked { get; init; }
    public string? ForkedToRunId { get; init; }

    public string? FromAgent { get; init; }
    public Role Role => Role.System;
    public ImmutableDictionary<string, object>? Metadata { get; init; }
    public string? RunId => CompletedRunId;
    public string? ParentRunId { get; init; }
    public string? ThreadId { get; init; }
    public string? GenerationId { get; init; }
    public int? MessageOrderIdx { get; init; }
}

#endregion

#region Publishing Middleware

/// <summary>
/// Middleware that intercepts messages and publishes them to subscribers as a side effect.
/// Follows the interceptor pattern - yields all messages through unchanged while publishing.
/// Positioned BEFORE MessageUpdateJoinerMiddleware to capture streaming updates.
/// </summary>
internal sealed class MessagePublishingMiddleware : IStreamingMiddleware
{
    private readonly Func<IMessage, CancellationToken, ValueTask> _publishAction;

    public string? Name => "MessagePublishing";

    public MessagePublishingMiddleware(Func<IMessage, CancellationToken, ValueTask> publishAction)
    {
        ArgumentNullException.ThrowIfNull(publishAction);
        _publishAction = publishAction;
    }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken ct = default)
    {
        var messages = await agent.GenerateReplyAsync(context.Messages, context.Options, ct);
        var result = new List<IMessage>();

        foreach (var msg in messages)
        {
            await _publishAction(msg, ct);
            result.Add(msg);
        }

        return result;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken ct = default)
    {
        var stream = await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, ct);
        return ProcessAndPublishAsync(stream, ct);
    }

    private async IAsyncEnumerable<IMessage> ProcessAndPublishAsync(
        IAsyncEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var msg in messages.WithCancellation(ct))
        {
            // Publish as side effect (fire-and-forget for non-blocking)
            await _publishAction(msg, ct);

            // Always yield through unchanged
            yield return msg;
        }
    }
}

#endregion

/// <summary>
/// Background agentic loop that accepts messages and produces LLM responses.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
/// </summary>
public sealed class BackgroundAgenticLoop : IAsyncDisposable
{
    #region Dependencies

    private readonly IStreamingAgent _agent;
    private readonly FunctionRegistry _functionRegistry;
    private readonly IDictionary<string, Func<string, Task<string>>> _toolHandlers;
    private readonly string _threadId;
    private readonly int _maxTurnsPerRun;
    private readonly GenerateReplyOptions _defaultOptions;
    private readonly ILogger<BackgroundAgenticLoop> _logger;

    #endregion

    #region Channels

    // Input channel for user messages
    private readonly Channel<(UserInput Input, TaskCompletionSource<RunAssignment> Tcs)> _inputChannel;

    // Per-subscriber output channels (EventPublisher pattern)
    private readonly ConcurrentDictionary<string, Channel<IMessage>> _outputSubscribers = new();
    private readonly int _outputChannelCapacity;

    #endregion

    #region State

    private string? _currentRunId;
    private string? _latestRunId;
    private readonly List<IMessage> _conversationHistory = [];
    private readonly ConcurrentQueue<(UserInput Input, RunAssignment Assignment)> _pendingInjections = new();
    private readonly Lock _stateLock = new();

    #endregion

    #region Lifecycle

    private Task? _runTask;
    private CancellationTokenSource? _internalCts;
    private bool _isDisposed;

    #endregion

    #region Public Properties

    /// <summary>
    /// The current run ID being processed, or null if idle.
    /// </summary>
    public string? CurrentRunId
    {
        get
        {
            lock (_stateLock)
            {
                return _currentRunId;
            }
        }
    }

    /// <summary>
    /// The thread ID for this loop instance.
    /// </summary>
    public string ThreadId => _threadId;

    /// <summary>
    /// Whether the loop is currently running.
    /// </summary>
    public bool IsRunning => _runTask != null && !_runTask.IsCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new BackgroundAgenticLoop with FunctionRegistry for tool management.
    /// The loop owns the complete middleware stack creation:
    /// - MessageTransformationMiddleware (assigns messageOrderIdx, handles aggregates)
    /// - JsonFragmentUpdateMiddleware (handles JSON fragment updates)
    /// - MessagePublishingMiddleware (publishes ALL messages to subscribers - updates + full)
    /// - MessageUpdateJoinerMiddleware (joins update messages into full messages for history)
    /// - ToolCallInjectionMiddleware (injects function contracts for tool calling)
    /// </summary>
    /// <param name="providerAgent">The base provider streaming agent (without middleware - the loop builds the stack)</param>
    /// <param name="functionRegistry">The function registry containing tool definitions and handlers</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template (ModelId, Temperature, MaxThinkingTokens, etc.)</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run before stopping (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="logger">Optional logger</param>
    public BackgroundAgenticLoop(
        IStreamingAgent providerAgent,
        FunctionRegistry functionRegistry,
        string threadId,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        ILogger<BackgroundAgenticLoop>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providerAgent);
        ArgumentNullException.ThrowIfNull(functionRegistry);
        ArgumentNullException.ThrowIfNull(threadId);

        _functionRegistry = functionRegistry;
        _logger = logger ?? NullLogger<BackgroundAgenticLoop>.Instance;
        _threadId = threadId;
        _maxTurnsPerRun = maxTurnsPerRun;
        _outputChannelCapacity = outputChannelCapacity;
        _defaultOptions = defaultOptions ?? new GenerateReplyOptions();

        // Build tool call components from registry
        var (toolCallMiddleware, handlers) = functionRegistry.BuildToolCallComponents(name: "BackgroundLoopTools");
        _toolHandlers = handlers;

        // Create publishing middleware that publishes to subscribers
        // Positioned BEFORE MessageUpdateJoinerMiddleware to capture streaming updates
        var publishingMiddleware = new MessagePublishingMiddleware(PublishToAllAsync);

        // Build the complete middleware stack (loop owns the pipeline)
        // Response path order: Provider → MessageTransformation → JsonFragment → Publishing → Joiner → ToolCall
        // - Publishing captures ALL messages (updates + full) BEFORE the joiner aggregates them
        // - Subscribers get real-time streaming updates (TextUpdateMessage, etc.)
        // - Joiner aggregates for conversation history (internal use)
        _agent = providerAgent
            .WithMessageTransformation()                                              // Assigns messageOrderIdx, handles aggregates
            .WithMiddleware(new JsonFragmentUpdateMiddleware())                       // Handles JSON fragment updates
            .WithMiddleware(publishingMiddleware)                                     // Publishes to subscribers (updates + full)
            .WithMiddleware(new MessageUpdateJoinerMiddleware(name: "MessageJoiner")) // Joins updates into full messages
            .WithMiddleware(toolCallMiddleware);                                      // Injects function contracts

        // Create input channel with bounded capacity
        _inputChannel = Channel.CreateBounded<(UserInput, TaskCompletionSource<RunAssignment>)>(
            new BoundedChannelOptions(inputChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    #endregion

    #region Input API

    /// <summary>
    /// Enqueue messages for processing. Returns immediately with run assignment.
    /// If a run is in progress, messages are injected into the next turn as a NEW run.
    /// </summary>
    public async ValueTask<RunAssignment> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var input = new UserInput(messages, inputId, parentRunId);
        var tcs = new TaskCompletionSource<RunAssignment>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Check if we should inject into current run
        string? currentRun;
        lock (_stateLock)
        {
            currentRun = _currentRunId;
        }

        if (currentRun != null)
        {
            // A run is in progress - inject into pending queue
            var runId = Guid.NewGuid().ToString("N");
            var generationId = Guid.NewGuid().ToString("N");
            var effectiveParentRunId = parentRunId ?? currentRun;

            var assignment = new RunAssignment(
                runId,
                generationId,
                inputId,
                effectiveParentRunId,
                WasInjected: true);

            _pendingInjections.Enqueue((input, assignment));

            _logger.LogInformation(
                "Messages injected into pending queue. Will fork from run {ParentRunId} to new run {RunId}",
                effectiveParentRunId,
                runId);

            // Publish assignment event immediately
            await PublishToAllAsync(new RunAssignmentMessage
            {
                Assignment = assignment,
                ThreadId = _threadId,
            }, ct);

            return assignment;
        }

        // No run in progress - queue normally
        await _inputChannel.Writer.WriteAsync((input, tcs), ct);

        _logger.LogDebug("Message queued for processing. InputId: {InputId}", inputId);

        return await tcs.Task;
    }

    /// <summary>
    /// Execute a single run synchronously (foreground-style).
    /// Sends the user input, subscribes to messages, and yields all messages for this run until completion.
    /// This provides a simpler API for cases where you don't need the full background loop capabilities.
    /// </summary>
    /// <param name="userInput">The user input containing messages to process</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>AsyncEnumerable of all messages produced during this run</returns>
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Subscribe first to ensure we don't miss any messages
        var subscriberId = Guid.NewGuid().ToString("N");
        var outputChannel = Channel.CreateBounded<IMessage>(new BoundedChannelOptions(_outputChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        if (!_outputSubscribers.TryAdd(subscriberId, outputChannel))
        {
            throw new InvalidOperationException("Failed to create subscriber for ExecuteRun");
        }

        try
        {
            // Send the input and get the run assignment
            var assignment = await SendAsync(userInput.Messages, userInput.InputId, userInput.ParentRunId, ct);
            var targetRunId = assignment.RunId;

            _logger.LogDebug("ExecuteRun started for RunId: {RunId}", targetRunId);

            // Yield messages until run completes
            await foreach (var msg in outputChannel.Reader.ReadAllAsync(ct))
            {
                // Filter to only messages for this run (or null RunId for backwards compatibility)
                if (msg.RunId == targetRunId || msg.RunId == null)
                {
                    yield return msg;

                    // Check for run completion
                    if (msg is RunCompletedMessage completed && completed.CompletedRunId == targetRunId)
                    {
                        _logger.LogDebug("ExecuteRun completed for RunId: {RunId}", targetRunId);
                        yield break;
                    }
                }
            }
        }
        finally
        {
            // Clean up subscriber
            if (_outputSubscribers.TryRemove(subscriberId, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    #endregion

    #region Output API (EventPublisher Pattern)

    /// <summary>
    /// Subscribe to output messages from the loop.
    /// Each subscriber gets an independent stream.
    /// </summary>
    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subscriberId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateBounded<IMessage>(new BoundedChannelOptions(_outputChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _outputSubscribers[subscriberId] = channel;
        _logger.LogDebug("Subscriber {SubscriberId} connected", subscriberId);

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct))
            {
                yield return message;
            }
        }
        finally
        {
            // Cleanup on unsubscribe
            if (_outputSubscribers.TryRemove(subscriberId, out var removed))
            {
                _ = removed.Writer.TryComplete();
            }

            _logger.LogDebug("Subscriber {SubscriberId} disconnected", subscriberId);
        }
    }

    /// <summary>
    /// Publish a message to all subscribers.
    /// </summary>
    private async ValueTask PublishToAllAsync(IMessage message, CancellationToken ct)
    {
        var tasks = new List<Task>();

        foreach (var (subscriberId, channel) in _outputSubscribers)
        {
            tasks.Add(PublishToSubscriberAsync(subscriberId, channel, message, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task PublishToSubscriberAsync(
        string subscriberId,
        Channel<IMessage> channel,
        IMessage message,
        CancellationToken ct)
    {
        try
        {
            await channel.Writer.WriteAsync(message, ct);
        }
        catch (ChannelClosedException)
        {
            _logger.LogDebug("Channel for subscriber {SubscriberId} is closed", subscriberId);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error publishing to subscriber {SubscriberId}", subscriberId);
        }
    }

    #endregion

    #region Lifecycle API

    /// <summary>
    /// Start the background loop. Runs until cancellation or disposal.
    /// </summary>
    public Task RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_runTask != null && !_runTask.IsCompleted)
        {
            throw new InvalidOperationException("Loop is already running");
        }

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = RunLoopAsync(_internalCts.Token);

        _logger.LogInformation("Background agentic loop started. ThreadId: {ThreadId}", _threadId);

        return _runTask;
    }

    /// <summary>
    /// Stop the background loop gracefully.
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (_internalCts == null || _runTask == null)
        {
            return;
        }

        _logger.LogInformation("Stopping background agentic loop...");

        // Signal cancellation
        await _internalCts.CancelAsync();

        // Complete input channel
        _inputChannel.Writer.TryComplete();

        // Wait for loop to finish
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            await _runTask.WaitAsync(effectiveTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Loop stop timed out after {Timeout}", effectiveTimeout);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _logger.LogInformation("Background agentic loop stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        await StopAsync();

        _internalCts?.Dispose();

        // Close all subscriber channels
        foreach (var (_, channel) in _outputSubscribers)
        {
            _ = channel.Writer.TryComplete();
        }

        _outputSubscribers.Clear();
    }

    #endregion

    #region Core Loop Implementation

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("Loop started processing");

        try
        {
            await foreach (var (input, tcs) in _inputChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessInputAsync(input, tcs, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing input");
                    tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Loop cancelled");
        }
        catch (ChannelClosedException)
        {
            _logger.LogDebug("Input channel closed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in loop");
            throw;
        }
    }

    private async Task ProcessInputAsync(
        UserInput input,
        TaskCompletionSource<RunAssignment> tcs,
        CancellationToken ct)
    {
        // Create run, determine parent
        var runId = Guid.NewGuid().ToString("N");
        var generationId = Guid.NewGuid().ToString("N");
        var parentRunId = input.ParentRunId ?? _latestRunId;

        // Complete assignment immediately
        var assignment = new RunAssignment(runId, generationId, input.InputId, parentRunId);
        tcs.SetResult(assignment);

        // Set current run
        lock (_stateLock)
        {
            _currentRunId = runId;
        }

        _logger.LogInformation(
            "Starting run {RunId} (parent: {ParentRunId}, generation: {GenerationId})",
            runId,
            parentRunId ?? "none",
            generationId);

        try
        {
            // Publish assignment event
            await PublishToAllAsync(new RunAssignmentMessage
            {
                Assignment = assignment,
                ThreadId = _threadId,
            }, ct);

            // Add user messages to history with proper IDs
            foreach (var msg in input.Messages)
            {
                _conversationHistory.Add(msg);
            }

            // Execute agentic turns
            var wasForked = await ExecuteAgenticLoopAsync(runId, generationId, ct);

            // Publish completion
            string? forkedToRunId = null;
            if (wasForked && _pendingInjections.TryPeek(out var pending))
            {
                forkedToRunId = pending.Assignment.RunId;
            }

            await PublishToAllAsync(new RunCompletedMessage
            {
                CompletedRunId = runId,
                WasForked = wasForked,
                ForkedToRunId = forkedToRunId,
                ThreadId = _threadId,
                GenerationId = generationId,
            }, ct);

            _logger.LogInformation(
                "Run {RunId} completed. WasForked: {WasForked}",
                runId,
                wasForked);

            // Process any pending injections
            if (wasForked && _pendingInjections.TryDequeue(out var injection))
            {
                // Requeue the injection as a new input
                var injectionTcs = new TaskCompletionSource<RunAssignment>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                injectionTcs.SetResult(injection.Assignment); // Already assigned

                await ProcessInputAsync(injection.Input, injectionTcs, ct);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _latestRunId = runId;
                _currentRunId = null;
            }
        }
    }

    private async Task<bool> ExecuteAgenticLoopAsync(
        string runId,
        string generationId,
        CancellationToken ct)
    {
        var turnCount = 0;

        while (turnCount < _maxTurnsPerRun)
        {
            ct.ThrowIfCancellationRequested();

            // Check for pending injections (fork case)
            if (!_pendingInjections.IsEmpty)
            {
                _logger.LogInformation(
                    "Injection detected during run {RunId}, will fork after this turn",
                    runId);
                return true; // Signal fork
            }

            turnCount++;
            _logger.LogDebug("Executing turn {Turn} of run {RunId}", turnCount, runId);

            var hasToolCalls = await ExecuteTurnAsync(runId, generationId, turnCount, ct);

            if (!hasToolCalls)
            {
                _logger.LogDebug("No tool calls in turn {Turn}, run complete", turnCount);
                break;
            }
        }

        if (turnCount >= _maxTurnsPerRun)
        {
            _logger.LogWarning("Max turns ({MaxTurns}) reached for run {RunId}", _maxTurnsPerRun, runId);
        }

        return false; // No fork
    }

    private async Task<bool> ExecuteTurnAsync(
        string runId,
        string generationId,
        int turnNumber,
        CancellationToken ct)
    {
        // Use defaultOptions as template, override run-specific fields
        var options = _defaultOptions with
        {
            RunId = runId,
            ThreadId = _threadId,
        };

        var stream = await _agent.GenerateReplyStreamingAsync(_conversationHistory, options, ct);

        var hasToolCalls = false;
        var pendingToolCalls = new Dictionary<string, Task<ToolCallResultMessage>>();

        await foreach (var msg in stream.WithCancellation(ct))
        {
            // Add to history (messages already published by MessagePublishingMiddleware)
            _conversationHistory.Add(msg);

            // Handle tool calls
            if (msg is ToolCallMessage toolCall)
            {
                hasToolCalls = true;

                // Fail-fast: ToolCallId is required for proper correlation
                if (string.IsNullOrEmpty(toolCall.ToolCallId))
                {
                    throw new InvalidOperationException(
                        $"ToolCallMessage.ToolCallId is required but was null or empty. " +
                        $"FunctionName: {toolCall.FunctionName ?? "(null)"}");
                }

                _logger.LogDebug(
                    "Tool call received: {FunctionName} (id: {ToolCallId})",
                    toolCall.FunctionName,
                    toolCall.ToolCallId);

                // Start execution immediately
                var executionTask = ExecuteToolCallAsync(toolCall, ct);
                pendingToolCalls[toolCall.ToolCallId] = executionTask;
            }
        }

        // Await all tool executions and add results
        if (pendingToolCalls.Count > 0)
        {
            _logger.LogDebug("Awaiting {Count} tool call results", pendingToolCalls.Count);

            await Task.WhenAll(pendingToolCalls.Values);

            foreach (var (toolCallId, task) in pendingToolCalls)
            {
                var result = await task;
                _conversationHistory.Add(result);
                await PublishToAllAsync(result, ct);

                _logger.LogDebug(
                    "Tool result for {ToolCallId}: {ResultPreview}",
                    toolCallId,
                    result.Result.Length > 100 ? result.Result[..100] + "..." : result.Result);
            }
        }

        return hasToolCalls;
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
                _logger.LogWarning(
                    "No handler registered for function '{FunctionName}'. Returning error to LLM. " +
                    "ToolCallId: {ToolCallId}. Available functions: [{AvailableFunctions}]",
                    toolCall.FunctionName,
                    toolCall.ToolCallId,
                    string.Join(", ", _toolHandlers.Keys));

                return new ToolCallResultMessage
                {
                    ToolCallId = toolCall.ToolCallId,
                    Result = JsonSerializer.Serialize(new
                    {
                        error = $"Unknown function: {toolCall.FunctionName}",
                        available_functions = _toolHandlers.Keys.ToArray(),
                    }),
                    Role = Role.User,
                    FromAgent = toolCall.FromAgent,
                    GenerationId = toolCall.GenerationId,
                };
            }

            var result = await handler(functionArgs);
            return new ToolCallResultMessage
            {
                ToolCallId = toolCall.ToolCallId,
                Result = result,
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            };
        }
        catch (Exception ex)
        {
            // Tool execution errors are returned to the LLM for retry/correction
            _logger.LogError(ex, "Error executing tool call: {FunctionName}", toolCall.FunctionName);
            return new ToolCallResultMessage
            {
                ToolCallId = toolCall.ToolCallId,
                Result = JsonSerializer.Serialize(new { error = ex.Message }),
                Role = Role.User,
                FromAgent = toolCall.FromAgent,
                GenerationId = toolCall.GenerationId,
            };
        }
    }

    #endregion
}
