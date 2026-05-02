using System.Collections.Concurrent;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Agents;

/// <summary>
/// Pool manager for MultiTurnAgentLoop instances, keyed by threadId.
/// Creates agents on-demand and reuses them for the same thread.
/// Supports mode-aware agent creation with customizable system prompts and tool filtering.
/// </summary>
public sealed class MultiTurnAgentPool : IAsyncDisposable
{
    /// <summary>
    /// Property key in <see cref="ThreadMetadata.Properties"/> that stores the provider
    /// id chosen for a thread. Once set, the value is treated as immutable for the
    /// lifetime of that thread (a thread is "locked" to a single provider).
    /// </summary>
    public const string ProviderPropertyKey = "provider";

    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
    private readonly ConcurrentDictionary<string, object> _creationLocks = new();
    private readonly Func<string, ChatMode, string, string?, AgentCreationResult> _agentFactory;
    private readonly ProviderRegistry? _providerRegistry;
    private readonly IConversationStore? _conversationStore;
    private readonly ILogger<MultiTurnAgentPool> _logger;
    private readonly CancellationTokenSource _poolCts = new();
    private bool _disposed;

    public sealed record RunStateInfo(
        bool IsInProgress,
        string? CurrentRunId,
        bool AgentIsRunning,
        bool RunTaskCompleted,
        bool IsStale);

    /// <summary>
    /// Result from the agent factory, including the agent and any owned resources
    /// (e.g., MCP clients) that should be disposed with the agent.
    /// </summary>
    public sealed record AgentCreationResult(
        IMultiTurnAgent Agent,
        IReadOnlyList<IAsyncDisposable>? OwnedResources = null);

    /// <summary>
    /// Wrapper to track agent and its background task.
    /// </summary>
    private sealed class AgentEntry : IAsyncDisposable
    {
        public required IMultiTurnAgent Agent { get; init; }
        public required Task RunTask { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required ChatMode Mode { get; init; }
        public required string ProviderId { get; init; }
        public string? RequestResponseDumpFileName { get; init; }
        public IReadOnlyList<IAsyncDisposable>? OwnedResources { get; init; }

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

            if (OwnedResources != null)
            {
                foreach (var resource in OwnedResources)
                {
                    try
                    {
                        await resource.DisposeAsync();
                    }
                    catch
                    {
                        // Ignore cleanup errors for owned resources
                    }
                }
            }

            Cts.Dispose();
        }
    }

    /// <summary>
    /// Creates a new MultiTurnAgentPool with a provider-aware, mode-aware agent factory.
    /// </summary>
    /// <param name="agentFactory">
    /// Factory invoked once per (threadId, providerId) combination. Receives the resolved
    /// provider id (after metadata lookup / default fallback) so the factory does not need
    /// to know about the request hop or persistence rules.
    /// </param>
    /// <param name="providerRegistry">
    /// Provider registry used to resolve the default provider and validate availability.
    /// May be <c>null</c> in legacy/test scenarios — when null, the pool skips the
    /// availability check and persists whatever provider id the caller supplied.
    /// </param>
    /// <param name="conversationStore">
    /// Conversation store used to read/write the persisted provider id under
    /// <see cref="ProviderPropertyKey"/>. Optional — when null, providers are not
    /// persisted and the pool falls back to caller-supplied / default values only.
    /// </param>
    /// <param name="logger">Logger for pool operations.</param>
    public MultiTurnAgentPool(
        Func<string, ChatMode, string, string?, AgentCreationResult> agentFactory,
        ProviderRegistry? providerRegistry,
        IConversationStore? conversationStore,
        ILogger<MultiTurnAgentPool> logger)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _providerRegistry = providerRegistry;
        _conversationStore = conversationStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Back-compat overload that omits the provider-id parameter from the factory. The
    /// pool injects a fixed provider id (<c>"legacy"</c>) when invoking the factory. Use
    /// the provider-aware constructor for new code.
    /// </summary>
    public MultiTurnAgentPool(
        Func<string, ChatMode, string?, AgentCreationResult> agentFactory,
        ILogger<MultiTurnAgentPool> logger)
        : this(
            agentFactory: WrapLegacyFactory(agentFactory),
            providerRegistry: null,
            conversationStore: null,
            logger: logger)
    {
    }

    private static Func<string, ChatMode, string, string?, AgentCreationResult> WrapLegacyFactory(
        Func<string, ChatMode, string?, AgentCreationResult> agentFactory)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        return (threadId, mode, _, dump) => agentFactory(threadId, mode, dump);
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
    /// The provider id is resolved from persisted metadata (if any) or the registry default.
    /// </summary>
    public IMultiTurnAgent GetOrCreateAgent(
        string threadId,
        ChatMode mode,
        string? requestResponseDumpFileName = null)
    {
        return GetOrCreateAgent(threadId, mode, requestedProviderId: null, requestResponseDumpFileName);
    }

    /// <summary>
    /// Gets or creates an agent for the specified threadId using the specified mode and a
    /// requested provider id (used only on first creation; persisted threads keep their
    /// original provider).
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="mode">The chat mode.</param>
    /// <param name="requestedProviderId">
    /// Provider id requested by the caller (typically from the WS query string). Honored
    /// only when the thread has no persisted provider yet; otherwise the persisted value
    /// wins. May be <c>null</c> to fall back to the registry default.
    /// </param>
    /// <param name="requestResponseDumpFileName">
    /// Optional base file name for provider request/response recording.
    /// Only applied when creating a new agent instance.
    /// </param>
    /// <exception cref="ProviderUnavailableException">
    /// Thrown when the resolved provider id (whether from persisted metadata or the
    /// caller's request) is no longer available in the current process.
    /// </exception>
    public IMultiTurnAgent GetOrCreateAgent(
        string threadId,
        ChatMode mode,
        string? requestedProviderId,
        string? requestResponseDumpFileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(mode);

        // Resolve provider OUTSIDE the lock to avoid blocking other threadIds on file I/O.
        // The lock below is acquired only after we know which provider to use, so concurrent
        // first-creation calls for the same threadId still serialise on creation.
        var resolvedProviderId = ResolveProviderId(threadId, requestedProviderId);

        // Use per-key lock to prevent concurrent factory invocations for the same threadId.
        // ConcurrentDictionary.GetOrAdd does not guarantee the factory runs at most once,
        // which would leak disposable resources (MCP clients) from the losing invocation.
        var lockObj = _creationLocks.GetOrAdd(threadId, _ => new object());
        AgentEntry entry;
        bool created = false;
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out var existing))
            {
                entry = CreateAgentEntry(threadId, mode, resolvedProviderId, requestResponseDumpFileName);
                _agents[threadId] = entry;
                created = true;
            }
            else
            {
                entry = existing;
            }
        }

        if (created)
        {
            // Persist the provider on first creation. Fire-and-forget — the WS connect
            // path runs in a request scope, and a transient failure here only means the
            // next reconnect will re-resolve from the registry default. We log warnings
            // so silent persistence drift is visible.
            _ = PersistProviderIfNeededAsync(threadId, resolvedProviderId);
        }

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
    /// Returns the provider id that <see cref="GetOrCreateAgent(string, ChatMode, string?, string?)"/>
    /// would use for <paramref name="threadId"/>, without creating an agent. Useful when a
    /// caller needs to surface "this thread is locked to provider X" to the UI.
    /// </summary>
    public string? GetEffectiveProviderId(string threadId, string? requestedProviderId)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            return null;
        }

        try
        {
            return ResolveProviderId(threadId, requestedProviderId);
        }
        catch (ProviderUnavailableException)
        {
            // Surface the persisted id even when unavailable so the UI can show the badge.
            var persisted = LoadPersistedProviderId(threadId);
            return persisted ?? _providerRegistry?.DefaultProviderId;
        }
    }

    private string ResolveProviderId(string threadId, string? requestedProviderId)
    {
        var persistedProviderId = LoadPersistedProviderId(threadId);
        if (!string.IsNullOrWhiteSpace(persistedProviderId))
        {
            EnsureAvailableOrThrow(persistedProviderId, source: "persisted");
            return persistedProviderId;
        }

        if (!string.IsNullOrWhiteSpace(requestedProviderId))
        {
            EnsureAvailableOrThrow(requestedProviderId, source: "requested");
            return requestedProviderId;
        }

        var fallback = _providerRegistry?.DefaultProviderId;
        if (string.IsNullOrWhiteSpace(fallback))
        {
            // No registry was wired up — surface a stable sentinel for the legacy back-compat path.
            return "default";
        }

        EnsureAvailableOrThrow(fallback, source: "default");
        return fallback;
    }

    private void EnsureAvailableOrThrow(string providerId, string source)
    {
        if (_providerRegistry == null)
        {
            return;
        }

        if (!_providerRegistry.IsAvailable(providerId))
        {
            var reason = _providerRegistry.IsKnown(providerId)
                ? $"required configuration is missing (source: {source})"
                : $"unknown provider id (source: {source})";
            throw new ProviderUnavailableException(providerId, reason);
        }
    }

    private string? LoadPersistedProviderId(string threadId)
    {
        if (_conversationStore == null)
        {
            return null;
        }

        try
        {
            // Sync-over-async: provider lookup happens once per thread on first WS connect.
            // The pool is already invoked from a request scope; making this whole code path
            // async would cascade through GetOrCreateAgent and break existing call sites.
            var metadata = _conversationStore.LoadMetadataAsync(threadId).GetAwaiter().GetResult();
            if (metadata?.Properties != null
                && metadata.Properties.TryGetValue(ProviderPropertyKey, out var raw)
                && raw is string s
                && !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim().ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load persisted provider for thread {ThreadId}; falling back to default",
                threadId);
        }

        return null;
    }

    private async Task PersistProviderIfNeededAsync(string threadId, string providerId)
    {
        if (_conversationStore == null)
        {
            return;
        }

        try
        {
            var existing = await _conversationStore.LoadMetadataAsync(threadId).ConfigureAwait(false);
            var hasProvider = existing?.Properties?.ContainsKey(ProviderPropertyKey) == true;
            if (hasProvider)
            {
                return;
            }

            var properties = existing?.Properties ?? ImmutableDictionary<string, object>.Empty;
            properties = properties.SetItem(ProviderPropertyKey, providerId);

            var updated = (existing ?? new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }) with
            {
                Properties = properties,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            await _conversationStore.SaveMetadataAsync(threadId, updated).ConfigureAwait(false);

            _logger.LogInformation(
                "Persisted provider {ProviderId} for thread {ThreadId}",
                providerId,
                threadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist provider {ProviderId} for thread {ThreadId}",
                providerId,
                threadId);
        }
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
    /// Returns true when an existing agent has an active run in progress.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    public bool IsRunInProgress(string threadId)
    {
        return GetRunStateInfo(threadId).IsInProgress;
    }

    public RunStateInfo GetRunStateInfo(string threadId)
    {
        if (!_agents.TryGetValue(threadId, out var entry))
        {
            return new RunStateInfo(
                IsInProgress: false,
                CurrentRunId: null,
                AgentIsRunning: false,
                RunTaskCompleted: true,
                IsStale: false);
        }

        var currentRunId = entry.Agent.CurrentRunId;
        var hasRunId = !string.IsNullOrWhiteSpace(currentRunId);
        var runTaskCompleted = entry.RunTask.IsCompleted;
        var agentIsRunning = entry.Agent.IsRunning;
        var isInProgress = hasRunId && agentIsRunning && !runTaskCompleted;
        var isStale = hasRunId && !isInProgress;
        return new RunStateInfo(
            IsInProgress: isInProgress,
            CurrentRunId: currentRunId,
            AgentIsRunning: agentIsRunning,
            RunTaskCompleted: runTaskCompleted,
            IsStale: isStale);
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

        // Resolve provider before re-entering the lock — the same persisted provider must
        // continue to be used after a mode-switch (mode-switch is not a provider switch).
        var resolvedProviderId = ResolveProviderId(threadId, requestedProviderId: null);

        // Acquire the per-key lock to prevent races with concurrent GetOrCreateAgent calls.
        var lockObj = _creationLocks.GetOrAdd(threadId, _ => new object());
        AgentEntry? oldEntry = null;
        AgentEntry entry;
        lock (lockObj)
        {
            _agents.TryRemove(threadId, out oldEntry);
            entry = CreateAgentEntry(threadId, mode, resolvedProviderId, requestResponseDumpFileName: null);
            _agents[threadId] = entry;
        }

        // Dispose old entry outside the lock to avoid blocking concurrent operations
        if (oldEntry != null)
        {
            _logger.LogInformation("Removing agent for thread {ThreadId}", threadId);
            await oldEntry.DisposeAsync();
        }

        return entry.Agent;
    }

    private AgentEntry CreateAgentEntry(
        string threadId,
        ChatMode mode,
        string providerId,
        string? requestResponseDumpFileName)
    {
        _logger.LogInformation(
            "Creating new agent for thread {ThreadId} with mode {ModeId} ({ModeName}) and provider {ProviderId}, dump recording enabled: {DumpEnabled}",
            threadId,
            mode.Id,
            mode.Name,
            providerId,
            !string.IsNullOrWhiteSpace(requestResponseDumpFileName));

        var result = _agentFactory(threadId, mode, providerId, requestResponseDumpFileName);
        var agent = result.Agent;
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
            ProviderId = providerId,
            RequestResponseDumpFileName = requestResponseDumpFileName,
            OwnedResources = result.OwnedResources,
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
