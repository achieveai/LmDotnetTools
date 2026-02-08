using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Agents;

/// <summary>
/// Pool manager for MultiTurnAgentLoop instances, keyed by threadId.
/// Creates agents on-demand and reuses them for the same thread.
/// Supports mode-aware agent creation with customizable system prompts and tool filtering.
/// </summary>
public sealed class MultiTurnAgentPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
    private readonly Func<string, ChatMode, string?, IMultiTurnAgent> _agentFactory;
    private readonly ILogger<MultiTurnAgentPool> _logger;
    private readonly CancellationTokenSource _poolCts = new();
    private bool _disposed;

    /// <summary>
    /// Wrapper to track agent and its background task.
    /// </summary>
    private sealed class AgentEntry : IAsyncDisposable
    {
        public required IMultiTurnAgent Agent { get; init; }
        public required Task RunTask { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required ChatMode Mode { get; init; }
        public string? RequestResponseDumpFileName { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            try
            {
                await Agent.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore stop errors during disposal
            }

            await Agent.DisposeAsync();
            Cts.Dispose();
        }
    }

    /// <summary>
    /// Creates a new MultiTurnAgentPool with mode-aware agent factory.
    /// </summary>
    /// <param name="agentFactory">Factory function that creates an IMultiTurnAgent for a given threadId and ChatMode</param>
    /// <param name="logger">Logger for pool operations</param>
    public MultiTurnAgentPool(
        Func<string, ChatMode, string?, IMultiTurnAgent> agentFactory,
        ILogger<MultiTurnAgentPool> logger)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets or creates an agent for the specified threadId using the default mode.
    /// If the agent doesn't exist, it's created and its RunAsync() is started.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <returns>The agent for this thread</returns>
    public IMultiTurnAgent GetOrCreateAgent(string threadId)
    {
        return GetOrCreateAgent(threadId, GetDefaultMode(), null);
    }

    /// <summary>
    /// Gets or creates an agent for the specified threadId using the specified mode.
    /// If the agent doesn't exist, it's created and its RunAsync() is started.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="mode">The chat mode to use for the agent</param>
    /// <param name="requestResponseDumpFileName">
    /// Optional base file name for provider request/response recording.
    /// Only applied when creating a new agent instance.
    /// </param>
    /// <returns>The agent for this thread</returns>
    public IMultiTurnAgent GetOrCreateAgent(
        string threadId,
        ChatMode mode,
        string? requestResponseDumpFileName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(mode);

        var entry = _agents.GetOrAdd(
            threadId,
            id => CreateAgentEntry(id, mode, requestResponseDumpFileName));

        if (!string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            && !string.Equals(
                entry.RequestResponseDumpFileName,
                requestResponseDumpFileName,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Request/response recording was requested for thread {ThreadId}, but an existing agent is being reused. " +
                "Recording dump file is fixed at agent creation time. Existing dump base: {ExistingDumpBase}",
                threadId,
                entry.RequestResponseDumpFileName ?? "(none)");
        }

        return entry.Agent;
    }

    /// <summary>
    /// Checks if an agent exists for the given threadId.
    /// </summary>
    public bool HasAgent(string threadId)
    {
        return _agents.ContainsKey(threadId);
    }

    /// <summary>
    /// Gets the current mode for an agent.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <returns>The current mode, or null if no agent exists</returns>
    public ChatMode? GetAgentMode(string threadId)
    {
        return _agents.TryGetValue(threadId, out var entry) ? entry.Mode : null;
    }

    /// <summary>
    /// Gets the count of active agents.
    /// </summary>
    public int ActiveAgentCount => _agents.Count;

    /// <summary>
    /// Removes and disposes an agent for the specified threadId.
    /// </summary>
    public async ValueTask RemoveAgentAsync(string threadId)
    {
        if (_agents.TryRemove(threadId, out var entry))
        {
            _logger.LogInformation("Removing agent for thread {ThreadId}", threadId);
            await entry.DisposeAsync();
        }
    }

    /// <summary>
    /// Recreates an agent with a new mode. This will dispose the existing agent
    /// and create a new one with the specified mode.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="mode">The new chat mode to use</param>
    /// <returns>The new agent for this thread</returns>
    public async Task<IMultiTurnAgent> RecreateAgentWithModeAsync(string threadId, ChatMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(mode);

        _logger.LogInformation(
            "Recreating agent for thread {ThreadId} with mode {ModeId} ({ModeName})",
            threadId,
            mode.Id,
            mode.Name);

        // Remove existing agent (waits for graceful shutdown)
        await RemoveAgentAsync(threadId);

        // Create new agent with the specified mode
        var entry = CreateAgentEntry(threadId, mode, requestResponseDumpFileName: null);
        _agents[threadId] = entry;

        return entry.Agent;
    }

    private AgentEntry CreateAgentEntry(string threadId, ChatMode mode, string? requestResponseDumpFileName)
    {
        _logger.LogInformation(
            "Creating new agent for thread {ThreadId} with mode {ModeId} ({ModeName}), dump recording enabled: {DumpEnabled}",
            threadId,
            mode.Id,
            mode.Name,
            !string.IsNullOrWhiteSpace(requestResponseDumpFileName));

        var agent = _agentFactory(threadId, mode, requestResponseDumpFileName);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_poolCts.Token);

        // Start the agent's background run loop
        var runTask = Task.Run(async () =>
        {
            try
            {
                await agent.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Agent run loop cancelled for thread {ThreadId}", threadId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent run loop failed for thread {ThreadId}", threadId);
            }
        }, cts.Token);

        return new AgentEntry
        {
            Agent = agent,
            RunTask = runTask,
            Cts = cts,
            Mode = mode,
            RequestResponseDumpFileName = requestResponseDumpFileName,
        };
    }

    private static ChatMode GetDefaultMode()
    {
        return Persistence.SystemChatModes.All[0];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogInformation("Disposing agent pool with {Count} agents", _agents.Count);

        // Signal all agents to stop
        await _poolCts.CancelAsync();

        // Dispose all agent entries
        var disposeTasks = _agents.Values.Select(async entry =>
        {
            try
            {
                await entry.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing agent entry");
            }
        });

        await Task.WhenAll(disposeTasks);
        _agents.Clear();
        _poolCts.Dispose();

        _logger.LogInformation("Agent pool disposed");
    }
}
