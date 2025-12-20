using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace LmStreaming.Sample.Agents;

/// <summary>
/// Pool manager for MultiTurnAgentLoop instances, keyed by threadId.
/// Creates agents on-demand and reuses them for the same thread.
/// </summary>
public sealed class MultiTurnAgentPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
    private readonly Func<string, IMultiTurnAgent> _agentFactory;
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
    /// Creates a new MultiTurnAgentPool.
    /// </summary>
    /// <param name="agentFactory">Factory function that creates an IMultiTurnAgent for a given threadId</param>
    /// <param name="logger">Logger for pool operations</param>
    public MultiTurnAgentPool(
        Func<string, IMultiTurnAgent> agentFactory,
        ILogger<MultiTurnAgentPool> logger)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets or creates an agent for the specified threadId.
    /// If the agent doesn't exist, it's created and its RunAsync() is started.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <returns>The agent for this thread</returns>
    public IMultiTurnAgent GetOrCreateAgent(string threadId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        var entry = _agents.GetOrAdd(threadId, CreateAgentEntry);
        return entry.Agent;
    }

    /// <summary>
    /// Checks if an agent exists for the given threadId.
    /// </summary>
    public bool HasAgent(string threadId)
        => _agents.ContainsKey(threadId);

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

    private AgentEntry CreateAgentEntry(string threadId)
    {
        _logger.LogInformation("Creating new agent for thread {ThreadId}", threadId);

        var agent = _agentFactory(threadId);
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
        };
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
