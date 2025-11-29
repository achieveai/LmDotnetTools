using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmConfigUsageExample;

/// <summary>
/// Background agentic loop using ClaudeAgentSdk CLI with MCP tools.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
/// </summary>
/// <remarks>
/// This class provides the same interface as BackgroundAgenticLoop but uses ClaudeAgentSdkAgent
/// (which wraps the claude-agent-sdk CLI) instead of a provider agent with middleware stack.
/// Tools are exposed via MCP servers configured externally.
/// </remarks>
public sealed class ClaudeAgentSdkBackgroundLoop : IAsyncDisposable
{
    #region Dependencies

    private readonly ClaudeAgentSdkOptions _claudeOptions;
    private readonly Dictionary<string, McpServerConfig> _mcpServers;
    private readonly string _threadId;
    private readonly string? _systemPrompt;
    private readonly int _maxTurnsPerRun;
    private readonly GenerateReplyOptions _defaultOptions;
    private readonly ILogger<ClaudeAgentSdkBackgroundLoop> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    #endregion

    #region Runtime Components

    private ClaudeAgentSdkAgent? _agent;
    private IClaudeAgentSdkClient? _client;

    #endregion

    #region Channels

    private readonly Channel<(UserInput Input, TaskCompletionSource<RunAssignment> Tcs)> _inputChannel;
    private readonly ConcurrentDictionary<string, Channel<IMessage>> _outputSubscribers = new();
    private readonly int _outputChannelCapacity;

    #endregion

    #region State

    private string? _currentRunId;
    private string? _latestRunId;
    private readonly List<IMessage> _conversationHistory = [];
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
    /// Creates a new ClaudeAgentSdkBackgroundLoop.
    /// </summary>
    /// <param name="claudeOptions">Options for the ClaudeAgentSdk client</param>
    /// <param name="mcpServers">MCP server configurations for tools</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers for internal components</param>
    public ClaudeAgentSdkBackgroundLoop(
        ClaudeAgentSdkOptions claudeOptions,
        Dictionary<string, McpServerConfig>? mcpServers,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        ILogger<ClaudeAgentSdkBackgroundLoop>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(claudeOptions);
        ArgumentNullException.ThrowIfNull(threadId);

        _claudeOptions = claudeOptions;
        _mcpServers = mcpServers ?? [];
        _threadId = threadId;
        _systemPrompt = systemPrompt;
        _maxTurnsPerRun = maxTurnsPerRun;
        _outputChannelCapacity = outputChannelCapacity;
        _defaultOptions = defaultOptions ?? new GenerateReplyOptions();
        _logger = logger ?? NullLogger<ClaudeAgentSdkBackgroundLoop>.Instance;
        _loggerFactory = loggerFactory;

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

        await _inputChannel.Writer.WriteAsync((input, tcs), ct);

        _logger.LogDebug("Message queued for processing. InputId: {InputId}", inputId);

        return await tcs.Task;
    }

    /// <summary>
    /// Execute a single run synchronously (foreground-style).
    /// Sends the user input, subscribes to messages, and yields all messages for this run until completion.
    /// </summary>
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

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
            var assignment = await SendAsync(userInput.Messages, userInput.InputId, userInput.ParentRunId, ct);
            var targetRunId = assignment.RunId;

            _logger.LogDebug("ExecuteRun started for RunId: {RunId}", targetRunId);

            await foreach (var msg in outputChannel.Reader.ReadAllAsync(ct))
            {
                if (msg.RunId == targetRunId || msg.RunId == null)
                {
                    yield return msg;

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
            if (_outputSubscribers.TryRemove(subscriberId, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    #endregion

    #region Output API

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
            if (_outputSubscribers.TryRemove(subscriberId, out var removed))
            {
                _ = removed.Writer.TryComplete();
            }

            _logger.LogDebug("Subscriber {SubscriberId} disconnected", subscriberId);
        }
    }

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

        // Initialize client and agent
        InitializeAgent();

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = RunLoopAsync(_internalCts.Token);

        _logger.LogInformation("ClaudeAgentSdk background loop started. ThreadId: {ThreadId}", _threadId);

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

        _logger.LogInformation("Stopping ClaudeAgentSdk background loop...");

        await _internalCts.CancelAsync();
        _inputChannel.Writer.TryComplete();

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

        _logger.LogInformation("ClaudeAgentSdk background loop stopped");
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
        _agent?.Dispose();

        foreach (var (_, channel) in _outputSubscribers)
        {
            _ = channel.Writer.TryComplete();
        }

        _outputSubscribers.Clear();
    }

    #endregion

    #region Initialization

    private void InitializeAgent()
    {
        _logger.LogInformation(
            "Initializing ClaudeAgentSdk with {Count} MCP servers",
            _mcpServers.Count);

        foreach (var (name, config) in _mcpServers)
        {
            _logger.LogDebug(
                "MCP Server '{Name}': Type={Type}, Command={Command}, Url={Url}",
                name,
                config.Type,
                config.Command,
                config.Url);
        }

        // Create loggers for internal components
        var clientLogger = _loggerFactory?.CreateLogger<ClaudeAgentSdkClient>();
        var agentLogger = _loggerFactory?.CreateLogger<ClaudeAgentSdkAgent>();

        // Create client and agent with loggers
        _client = new ClaudeAgentSdkClient(_claudeOptions, clientLogger);
        _agent = new ClaudeAgentSdkAgent(
            name: "ClaudeAgentSdkBackgroundLoop",
            client: _client,
            options: _claudeOptions,
            logger: agentLogger);
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
                    _ = tcs.TrySetCanceled(ct);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing input");
                    _ = tcs.TrySetException(ex);
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
        var runId = Guid.NewGuid().ToString("N");
        var generationId = Guid.NewGuid().ToString("N");
        var parentRunId = input.ParentRunId ?? _latestRunId;

        var assignment = new RunAssignment(runId, generationId, input.InputId, parentRunId);
        tcs.SetResult(assignment);

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

            // Add user messages to history
            foreach (var msg in input.Messages)
            {
                _conversationHistory.Add(msg);
            }

            // Execute the agentic loop via ClaudeAgentSdk
            await ExecuteAgenticLoopAsync(runId, generationId, ct);

            // Publish completion
            await PublishToAllAsync(new RunCompletedMessage
            {
                CompletedRunId = runId,
                ThreadId = _threadId,
                GenerationId = generationId,
            }, ct);

            _logger.LogInformation("Run {RunId} completed", runId);
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

    private async Task ExecuteAgenticLoopAsync(
        string runId,
        string generationId,
        CancellationToken ct)
    {
        if (_agent == null)
        {
            throw new InvalidOperationException("Agent not initialized");
        }

        // Build options with MCP servers config
        var extraPropertiesBuilder = _defaultOptions.ExtraProperties?.ToBuilder()
            ?? System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, object?>();

        // Add MCP servers to extra properties
        if (_mcpServers.Count > 0)
        {
            extraPropertiesBuilder["mcpServers"] = _mcpServers;
        }

        var options = _defaultOptions with
        {
            RunId = runId,
            ThreadId = _threadId,
            MaxToken = 16192,
            ExtraProperties = extraPropertiesBuilder.ToImmutable(),
        };

        // Build messages list with system prompt prepended (if configured)
        IEnumerable<IMessage> messagesToSend = _conversationHistory;
        if (!string.IsNullOrEmpty(_systemPrompt))
        {
            var systemMessage = new TextMessage { Text = _systemPrompt, Role = Role.System };
            messagesToSend = new IMessage[] { systemMessage }.Concat(_conversationHistory);
        }

        // Stream responses from ClaudeAgentSdk
        // Note: The CLI handles tool execution via MCP - we just publish all messages
        var stream = await _agent.GenerateReplyStreamingAsync(messagesToSend, options, ct);

        await foreach (var msg in stream.WithCancellation(ct))
        {
            // Publish message to subscribers
            await PublishToAllAsync(msg, ct);

            // Add to conversation history
            _conversationHistory.Add(msg);
        }
    }

    #endregion
}
