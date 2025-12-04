using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Abstract base class for multi-turn agents providing common infrastructure for
/// channel management, subscription handling, and lifecycle management.
/// </summary>
public abstract class MultiTurnAgentBase : IMultiTurnAgent
{
    #region Fields

    private readonly int _outputChannelCapacity;

    // Channels
    private readonly Channel<(UserInput Input, TaskCompletionSource<RunAssignment> Tcs)> _inputChannel;
    private readonly ConcurrentDictionary<string, Channel<IMessage>> _outputSubscribers = new();

    // State
    private string? _currentRunId;
    private string? _latestRunId;
    private readonly object _stateLock = new();
    private readonly object _historyLock = new();

    // Lifecycle
    private Task? _runTask;
    private CancellationTokenSource? _internalCts;
    private volatile bool _isDisposed;

    #endregion

    #region Protected Properties

    /// <summary>
    /// Logger for this agent instance.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// The system prompt for the agent.
    /// </summary>
    protected string? SystemPrompt { get; }

    /// <summary>
    /// The maximum turns per run.
    /// </summary>
    protected int MaxTurnsPerRun { get; }

    /// <summary>
    /// The default options for generating replies.
    /// </summary>
    protected GenerateReplyOptions DefaultOptions { get; }

    /// <summary>
    /// The conversation history. Access via AddToHistory and GetHistorySnapshot for thread safety.
    /// </summary>
    private List<IMessage> ConversationHistory { get; } = [];

    /// <summary>
    /// Pending injections queue.
    /// </summary>
    protected ConcurrentQueue<(UserInput Input, RunAssignment Assignment)> PendingInjections { get; } = new();

    #endregion

    #region Public Properties

    /// <inheritdoc />
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

    /// <inheritdoc />
    public string ThreadId { get; }

    /// <inheritdoc />
    public bool IsRunning => _runTask != null && !_runTask.IsCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new MultiTurnAgentBase.
    /// </summary>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="logger">Optional logger</param>
    protected MultiTurnAgentBase(
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        ThreadId = threadId;
        SystemPrompt = systemPrompt;
        MaxTurnsPerRun = maxTurnsPerRun;
        _outputChannelCapacity = outputChannelCapacity;
        DefaultOptions = defaultOptions ?? new GenerateReplyOptions();
        Logger = logger ?? NullLogger.Instance;

        _inputChannel = Channel.CreateBounded<(UserInput, TaskCompletionSource<RunAssignment>)>(
            new BoundedChannelOptions(inputChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    #endregion

    #region Conversation History Thread-Safe Access

    /// <summary>
    /// Adds a message to the conversation history in a thread-safe manner.
    /// </summary>
    /// <param name="message">The message to add</param>
    protected void AddToHistory(IMessage message)
    {
        lock (_historyLock)
        {
            ConversationHistory.Add(message);
        }
    }

    /// <summary>
    /// Gets a snapshot of the conversation history in a thread-safe manner.
    /// </summary>
    /// <returns>A read-only list containing the current conversation history</returns>
    protected IReadOnlyList<IMessage> GetHistorySnapshot()
    {
        lock (_historyLock)
        {
            return [.. ConversationHistory];
        }
    }

    /// <summary>
    /// Gets messages with the system prompt prepended if configured.
    /// This is a helper to avoid code duplication across implementations.
    /// </summary>
    /// <returns>Messages ready to send to the LLM</returns>
    protected IEnumerable<IMessage> GetMessagesWithSystemPrompt()
    {
        var history = GetHistorySnapshot();

        if (!string.IsNullOrEmpty(SystemPrompt))
        {
            var systemMessage = new TextMessage { Text = SystemPrompt, Role = Role.System };
            return new IMessage[] { systemMessage }.Concat(history);
        }

        return history;
    }

    #endregion

    #region Input API

    /// <inheritdoc />
    public virtual async ValueTask<RunAssignment> SendAsync(
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

        if (currentRun != null && SupportsInjection)
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

            PendingInjections.Enqueue((input, assignment));

            Logger.LogInformation(
                "Messages injected into pending queue. Will fork from run {ParentRunId} to new run {RunId}",
                effectiveParentRunId,
                runId);

            // Publish assignment event immediately
            await PublishToAllAsync(new RunAssignmentMessage
            {
                Assignment = assignment,
                ThreadId = ThreadId,
            }, ct);

            return assignment;
        }

        // No run in progress - queue normally
        await _inputChannel.Writer.WriteAsync((input, tcs), ct);

        Logger.LogDebug("Message queued for processing. InputId: {InputId}", inputId);

        return await tcs.Task;
    }

    /// <inheritdoc />
    public virtual async IAsyncEnumerable<IMessage> ExecuteRunAsync(
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

            Logger.LogDebug("ExecuteRun started for RunId: {RunId}", targetRunId);

            // Yield messages until run completes
            await foreach (var msg in outputChannel.Reader.ReadAllAsync(ct))
            {
                yield return msg;

                // Check for run completion
                if (msg is RunCompletedMessage completed)
                {
                    Logger.LogDebug("ExecuteRun completed for RunId: {RunId}", targetRunId);
                    yield break;
                }
            }
        }
        finally
        {
            // Clean up subscriber
            if (_outputSubscribers.TryRemove(subscriberId, out var channel))
            {
                _ = channel.Writer.TryComplete();
            }
        }
    }

    #endregion

    #region Output API

    /// <inheritdoc />
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
        Logger.LogDebug("Subscriber {SubscriberId} connected", subscriberId);

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

            Logger.LogDebug("Subscriber {SubscriberId} disconnected", subscriberId);
        }
    }

    /// <summary>
    /// Publish a message to all subscribers.
    /// </summary>
    /// <param name="message">The message to publish</param>
    /// <param name="ct">Cancellation token</param>
    protected async ValueTask PublishToAllAsync(IMessage message, CancellationToken ct)
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
            Logger.LogDebug("Channel for subscriber {SubscriberId} is closed", subscriberId);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error publishing to subscriber {SubscriberId}", subscriberId);
        }
    }

    #endregion

    #region Lifecycle API

    /// <inheritdoc />
    public Task RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_runTask != null && !_runTask.IsCompleted)
        {
            throw new InvalidOperationException("Loop is already running");
        }

        OnBeforeRun();

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = RunLoopAsync(_internalCts.Token);

        Logger.LogInformation("{AgentType} started. ThreadId: {ThreadId}", GetType().Name, ThreadId);

        return _runTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (_internalCts == null || _runTask == null)
        {
            return;
        }

        Logger.LogInformation("Stopping {AgentType}...", GetType().Name);

        // Signal cancellation
        await _internalCts.CancelAsync();

        // Complete input channel
        _ = _inputChannel.Writer.TryComplete();

        // Wait for loop to finish
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            await _runTask.WaitAsync(effectiveTimeout);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Loop stop timed out after {Timeout}", effectiveTimeout);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Logger.LogInformation("{AgentType} stopped", GetType().Name);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        await StopAsync();

        _internalCts?.Dispose();

        OnDispose();

        // Close all subscriber channels
        foreach (var (_, channel) in _outputSubscribers)
        {
            _ = channel.Writer.TryComplete();
        }

        _outputSubscribers.Clear();

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Core Loop Implementation

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Logger.LogDebug("Loop started processing");

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
                    _ = tcs.TrySetCanceled(ct);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing input");
                    _ = tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("Loop cancelled");
        }
        catch (ChannelClosedException)
        {
            Logger.LogDebug("Input channel closed");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error in loop");
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
        string? parentRunId;
        lock (_stateLock)
        {
            parentRunId = input.ParentRunId ?? _latestRunId;
        }

        // Complete assignment immediately
        var assignment = new RunAssignment(runId, generationId, input.InputId, parentRunId);
        tcs.SetResult(assignment);

        // Set current run
        lock (_stateLock)
        {
            _currentRunId = runId;
        }

        Logger.LogInformation(
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
                ThreadId = ThreadId,
            }, ct);

            // Add user messages to history with proper IDs
            foreach (var msg in input.Messages)
            {
                AddToHistory(msg);
            }

            // Execute agentic turns (implementation-specific)
            var wasForked = await ExecuteAgenticLoopAsync(runId, generationId, ct);

            // Publish completion
            string? forkedToRunId = null;
            if (wasForked && PendingInjections.TryPeek(out var pending))
            {
                forkedToRunId = pending.Assignment.RunId;
            }

            await PublishToAllAsync(new RunCompletedMessage
            {
                CompletedRunId = runId,
                WasForked = wasForked,
                ForkedToRunId = forkedToRunId,
                ThreadId = ThreadId,
                GenerationId = generationId,
            }, ct);

            Logger.LogInformation(
                "Run {RunId} completed. WasForked: {WasForked}",
                runId,
                wasForked);

            // Process any pending injections
            if (wasForked && PendingInjections.TryDequeue(out var injection))
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

    #endregion

    #region Abstract/Virtual Members

    /// <summary>
    /// Whether this agent supports injection of messages into an ongoing run.
    /// Default is true. Override to disable injection behavior.
    /// </summary>
    protected virtual bool SupportsInjection => true;

    /// <summary>
    /// Called before the run loop starts. Override to perform initialization.
    /// </summary>
    protected virtual void OnBeforeRun()
    {
    }

    /// <summary>
    /// Called during disposal. Override to clean up implementation-specific resources.
    /// </summary>
    protected virtual void OnDispose()
    {
    }

    /// <summary>
    /// Execute the agentic loop for a single run.
    /// </summary>
    /// <param name="runId">The run ID for this execution</param>
    /// <param name="generationId">The generation ID for this execution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the run was forked (due to injection), false otherwise</returns>
    protected abstract Task<bool> ExecuteAgenticLoopAsync(
        string runId,
        string generationId,
        CancellationToken ct);

    #endregion
}
