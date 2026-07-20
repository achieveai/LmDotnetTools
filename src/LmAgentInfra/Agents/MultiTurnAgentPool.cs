using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

/// <summary>
/// Pool manager for MultiTurnAgentLoop instances, keyed by threadId.
/// Creates agents on-demand and reuses them for the same thread.
/// Supports mode-aware agent creation with customizable system prompts and tool filtering.
/// </summary>
public sealed class MultiTurnAgentPool : IAsyncDisposable
{
    /// <summary>
    /// Property key in <see cref="ThreadMetadata.Properties"/> that stores the provider
    /// id chosen for a thread. Seeded on first creation and treated as immutable for plain
    /// reconnects (a persisted value wins over a later requested one in
    /// <see cref="ResolveProviderId"/>), but MUTABLE via a deliberate provider switch on an idle
    /// conversation (<see cref="RecreateAgentWithProviderAsync"/>), which overwrites it so a later
    /// refresh restores the switched-to provider.
    /// </summary>
    public const string ProviderPropertyKey = "provider";

    /// <summary>
    /// Property key in <see cref="ThreadMetadata.Properties"/> that stores the workspace id
    /// chosen for a thread. Persisted on first creation, then treated as immutable for the
    /// lifetime of that thread (persisted value wins over a later requested value) and — unlike
    /// the provider — never changed by a switch: a thread stays bound to its workspace for life.
    /// </summary>
    public const string WorkspacePropertyKey = "workspace";

    /// <summary>
    /// Property key in <see cref="ThreadMetadata.Properties"/> that stores the chat mode id chosen
    /// for a thread. Seeded on first creation and updated on a deliberate mode switch
    /// (<see cref="RecreateAgentWithModeAsync"/>) — unlike provider/workspace, the mode is MUTABLE.
    /// Persisting it lets the client restore the conversation's bound mode after a refresh instead of
    /// falling back to the default.
    /// </summary>
    public const string ModePropertyKey = "mode";

    private const string DefaultWorkspaceId = "default";

    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new();
    private readonly ConcurrentDictionary<string, object> _creationLocks = new();
    private readonly Func<AgentCreationContext, AgentCreationResult> _agentFactory;
    private readonly IProviderResolver? _providerRegistry;
    private readonly IConversationStore? _conversationStore;
    private readonly ISandboxBindingSink? _bindingSink;
    private readonly ILogger<MultiTurnAgentPool> _logger;
    private readonly CancellationTokenSource _poolCts = new();
    private bool _disposed;

    public sealed record RunStateInfo(
        bool IsInProgress,
        string? CurrentRunId,
        bool AgentIsRunning,
        bool RunTaskCompleted,
        bool IsStale
    );

    /// <summary>
    /// Inputs the agent factory receives for one (threadId) creation. Bundles the resolved
    /// provider id, the chat mode, the optional request/response dump base file name, and the
    /// resolved workspace id so the factory can mount the chosen workspace's sandbox directory.
    /// <para>
    /// <c>CallerCredential</c> is the sandbox credential of the caller that (first) created this
    /// thread — <c>null</c> for the interactive UI default. Frozen for the conversation's lifetime;
    /// the factory threads it into the sandbox session create call and the <c>/mcp</c> headers. See
    /// <see cref="AgentEntry.CallerCredential"/> for the pooled-side invariant.
    /// </para>
    /// </summary>
    public sealed record AgentCreationContext(
        string ThreadId,
        AgentProfile Mode,
        string ProviderId,
        string? DumpFile,
        string? WorkspaceId,
        SandboxCredential? CallerCredential = null
    );

    /// <summary>
    /// Result from the agent factory, including the agent and any owned resources
    /// (e.g., MCP clients) that should be disposed with the agent.
    /// <para>
    /// <c>StagedBinding</c> is the conversation's <see cref="SandboxEstablishedBinding"/> when this is a
    /// workspace-mode creation whose sandbox session was established — <c>null</c> otherwise. The pool
    /// publishes it (via the injected <see cref="ISandboxBindingSink"/>) ONLY as part of a successful
    /// agent-entry commit under the per-thread lock, so a failed construction publishes nothing.
    /// </para>
    /// </summary>
    public sealed record AgentCreationResult(
        IMultiTurnAgent Agent,
        IReadOnlyList<IAsyncDisposable>? OwnedResources = null
    )
    {
        /// <summary>The sandbox binding to publish on a successful commit, or null for a non-workspace agent.</summary>
        public SandboxEstablishedBinding? StagedBinding { get; init; }
    }

    /// <summary>
    /// Wrapper to track agent and its background task.
    /// </summary>
    private sealed class AgentEntry : IAsyncDisposable
    {
        public required IMultiTurnAgent Agent { get; init; }
        public required Task RunTask { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required AgentProfile Mode { get; init; }
        public required string ProviderId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? RequestResponseDumpFileName { get; init; }
        public IReadOnlyList<IAsyncDisposable>? OwnedResources { get; init; }

        /// <summary>
        /// The sandbox credential of the caller that created this thread's agent — <c>null</c> for
        /// the interactive UI default. Set ONCE at creation (via <see cref="CreateAgentEntry"/>) and
        /// never reassigned: a conversation is frozen to its creating caller's <c>AppId</c> for its
        /// lifetime (Cross-Actor Resume Matrix, issue #153). Mode/provider recreation
        /// (<see cref="SwapAgentUnderLockAsync"/>) reads the OLD entry's value and threads it into the
        /// replacement entry so a mode/provider switch never changes the frozen caller identity.
        /// </summary>
        public SandboxCredential? CallerCredential { get; init; }

        /// <summary>
        /// The conversation's sandbox-established binding for this (workspace-mode) entry, or <c>null</c>
        /// for a non-workspace entry. Carried from the factory's <see cref="AgentCreationResult.StagedBinding"/>
        /// and published to the <see cref="ISandboxBindingSink"/> right after this entry commits under the
        /// per-thread lock. A mode switch either restages a fresh binding (workspace target) or stages none
        /// (non-workspace target, leaving the prior binding untouched).
        /// </summary>
        public SandboxEstablishedBinding? EstablishedBinding { get; init; }

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
    /// <param name="bindingSink">
    /// Optional sink the pool uses to publish/clear a conversation's sandbox-established binding as part of
    /// the agent-entry commit/removal. Null in legacy/test scenarios that do not wire the sandbox registry.
    /// </param>
    public MultiTurnAgentPool(
        Func<AgentCreationContext, AgentCreationResult> agentFactory,
        IProviderResolver? providerRegistry,
        IConversationStore? conversationStore,
        ILogger<MultiTurnAgentPool> logger,
        ISandboxBindingSink? bindingSink = null
    )
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _providerRegistry = providerRegistry;
        _conversationStore = conversationStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bindingSink = bindingSink;
    }

    /// <summary>
    /// Back-compat overload taking a four-arg (threadId, mode, providerId, dump) factory that
    /// predates the <see cref="AgentCreationContext"/> bundling. Existing callers/tests keep
    /// compiling; the workspace id defaults to <c>"default"</c>.
    /// </summary>
    public MultiTurnAgentPool(
        Func<string, AgentProfile, string, string?, AgentCreationResult> agentFactory,
        IProviderResolver? providerRegistry,
        IConversationStore? conversationStore,
        ILogger<MultiTurnAgentPool> logger
    )
        : this(
            agentFactory: WrapProviderFactory(agentFactory),
            providerRegistry: providerRegistry,
            conversationStore: conversationStore,
            logger: logger
        )
    { }

    /// <summary>
    /// Back-compat overload that omits the provider-id parameter from the factory. The
    /// pool injects a fixed provider id (<c>"legacy"</c>) when invoking the factory. Use
    /// the context-aware constructor for new code.
    /// </summary>
    public MultiTurnAgentPool(
        Func<string, AgentProfile, string?, AgentCreationResult> agentFactory,
        ILogger<MultiTurnAgentPool> logger
    )
        : this(
            agentFactory: WrapLegacyFactory(agentFactory),
            providerRegistry: null,
            conversationStore: null,
            logger: logger
        )
    { }

    private static Func<AgentCreationContext, AgentCreationResult> WrapProviderFactory(
        Func<string, AgentProfile, string, string?, AgentCreationResult> agentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        return ctx => agentFactory(ctx.ThreadId, ctx.Mode, ctx.ProviderId, ctx.DumpFile);
    }

    private static Func<AgentCreationContext, AgentCreationResult> WrapLegacyFactory(
        Func<string, AgentProfile, string?, AgentCreationResult> agentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        return ctx => agentFactory(ctx.ThreadId, ctx.Mode, ctx.DumpFile);
    }

    /// <summary>
    /// Gets or creates an agent for the specified threadId using the specified mode.
    /// If the agent doesn't exist, it's created and its RunAsync() is started.
    /// The provider id is resolved from persisted metadata (if any) or the registry default.
    /// </summary>
    public IMultiTurnAgent GetOrCreateAgent(string threadId, AgentProfile mode, string? requestResponseDumpFileName = null)
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
    /// <param name="requestedWorkspaceId">
    /// Workspace id requested by the caller (typically from the WS query string). Honored only
    /// when the thread has no persisted workspace yet; otherwise the persisted value wins. May be
    /// <c>null</c> to fall back to the <c>"default"</c> workspace.
    /// </param>
    /// <param name="callerCredential">
    /// Sandbox credential of the caller making THIS request — <c>null</c> for the interactive UI
    /// default. On first creation it is frozen onto the new <see cref="AgentEntry"/> for the
    /// thread's lifetime. On every later call it is compared (by <c>AppId</c> only, null-safe) to
    /// the entry's frozen credential; a mismatch throws
    /// <see cref="SandboxCredentialConflictException"/> — a conversation cannot change its owning
    /// app identity (Cross-Actor Resume Matrix, issue #153).
    /// </param>
    /// <exception cref="ProviderUnavailableException">
    /// Thrown when the resolved provider id (whether from persisted metadata or the
    /// caller's request) is no longer available in the current process.
    /// </exception>
    /// <exception cref="SandboxCredentialConflictException">
    /// Thrown when <paramref name="callerCredential"/>'s <c>AppId</c> differs from the app id the
    /// thread's existing agent was created under.
    /// </exception>
    public IMultiTurnAgent GetOrCreateAgent(
        string threadId,
        AgentProfile mode,
        string? requestedProviderId,
        string? requestResponseDumpFileName,
        string? requestedWorkspaceId = null,
        SandboxCredential? callerCredential = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(mode);

        // Resolve provider and workspace OUTSIDE the lock to avoid blocking other threadIds on
        // file I/O. The lock below is acquired only after we know which provider/workspace to use,
        // so concurrent first-creation calls for the same threadId still serialise on creation.
        var resolvedProviderId = ResolveProviderId(threadId, requestedProviderId);
        var resolvedWorkspaceId = ResolveWorkspaceId(threadId, requestedWorkspaceId);

        // Surface silent provider overrides: a thread is locked to its first provider, so a
        // later connection requesting a different provider is ignored. Without this warning the
        // logs show the requested provider while a stale agent of a different provider serves
        // the turn — which makes log-based debugging misleading.
        if (
            !string.IsNullOrWhiteSpace(requestedProviderId)
            && !string.Equals(requestedProviderId, resolvedProviderId, StringComparison.OrdinalIgnoreCase)
        )
        {
            _logger.LogWarning(
                "Thread {ThreadId} is locked to provider {EffectiveProviderId}; requested provider {RequestedProviderId} is ignored for this connection.",
                threadId,
                resolvedProviderId,
                requestedProviderId
            );
        }

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
                entry = CreateAgentEntry(
                    threadId,
                    mode,
                    resolvedProviderId,
                    requestResponseDumpFileName,
                    resolvedWorkspaceId,
                    callerCredential
                );
                _agents[threadId] = entry;
                PublishBindingIfStaged(threadId, entry);
                created = true;
            }
            else
            {
                // Cross-actor guard: a conversation is bound to the AppId that created it for its
                // whole lifetime. Compare by AppId only (never the key), null-safe — both null
                // (two plain UI callers) matches; one null / one set (UI<->S2S, either direction)
                // and two differing set values (S2S-A<->S2S-B) both conflict. This MUST run inside
                // the same per-thread lock that guards entry lookup/creation so a concurrent
                // GetOrCreateAgent for a different caller can't race between the lookup and this
                // check (no separate check-then-act window).
                var existingAppId = existing.CallerCredential?.AppId;
                var requestedAppId = callerCredential?.AppId;
                if (!string.Equals(existingAppId, requestedAppId, StringComparison.Ordinal))
                {
                    throw new SandboxCredentialConflictException(threadId, existingAppId, requestedAppId);
                }

                entry = existing;
            }
        }

        if (created)
        {
            // Persist the provider, workspace, and mode on first creation in ONE atomic metadata
            // update. Fire-and-forget — the WS connect path runs in a request scope, and a transient
            // failure here only means the next reconnect will re-resolve from the registry default. We
            // log warnings so silent persistence drift is visible. A single atomic write (not two
            // concurrent read-modify-writes) is what keeps the provider from being clobbered by the
            // workspace write — the lost-update race that dropped the persisted provider.
            _ = PersistThreadBindingsIfNeededAsync(threadId, resolvedProviderId, resolvedWorkspaceId, mode.Id);
        }

        if (
            !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            && !string.Equals(entry.RequestResponseDumpFileName, requestResponseDumpFileName, StringComparison.Ordinal)
        )
        {
            _logger.LogWarning(
                "Request/response recording was requested for thread {ThreadId}, but an existing agent is being reused. "
                    + "Recording dump file is fixed at agent creation time. Existing dump base: {ExistingDumpBase}",
                threadId,
                entry.RequestResponseDumpFileName ?? "(none)"
            );
        }

        return entry.Agent;
    }

    /// <summary>
    /// Returns the provider id that
    /// <see cref="GetOrCreateAgent(string, AgentProfile, string?, string?, string?, SandboxCredential?)"/>
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

    /// <summary>
    /// Resolves the workspace id for <paramref name="threadId"/>: the persisted value wins (a
    /// thread is locked to the workspace it was created with), then the requested value, then the
    /// <c>"default"</c> sentinel. Mirrors <see cref="ResolveProviderId"/> but without availability
    /// checks — any workspace id is acceptable.
    /// </summary>
    private string ResolveWorkspaceId(string threadId, string? requestedWorkspaceId)
    {
        var persisted = LoadPersistedWorkspaceId(threadId);
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            return persisted;
        }

        return !string.IsNullOrWhiteSpace(requestedWorkspaceId) ? requestedWorkspaceId : DefaultWorkspaceId;
    }

    private string? LoadPersistedWorkspaceId(string threadId)
    {
        if (_conversationStore == null)
        {
            return null;
        }

        try
        {
            // Sync-over-async: ResolveWorkspaceId runs on the synchronous agent-creation path and the
            // metadata read is a fast local-store lookup, so we block here rather than threading async
            // through every caller. The catch below keeps a failed read non-fatal (falls back to default).
            var metadata = _conversationStore.LoadMetadataAsync(threadId).GetAwaiter().GetResult();
            if (
                metadata?.Properties != null
                && metadata.Properties.TryGetValue(WorkspacePropertyKey, out var raw)
                && TryNormalizeStringValue(raw, out var workspaceId)
            )
            {
                return workspaceId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load persisted workspace for thread {ThreadId}; falling back to default",
                threadId
            );
        }

        return null;
    }

    /// <summary>
    /// Persists a thread's provider, workspace, and mode bindings in ONE atomic metadata update on
    /// first creation. Provider and workspace are immutable (seeded only when absent); mode is seeded
    /// here too but stays mutable (a later <see cref="RecreateAgentWithModeAsync"/> overwrites it via
    /// <see cref="PersistModeAsync"/>). A single read-modify-write — rather than the two concurrent
    /// ones this replaced — is what stops the provider from being clobbered by the workspace write.
    /// </summary>
    private async Task PersistThreadBindingsIfNeededAsync(
        string threadId,
        string providerId,
        string workspaceId,
        string? modeId
    )
    {
        if (_conversationStore == null)
        {
            return;
        }

        try
        {
            await _conversationStore.UpdateMetadataAsync(
                threadId,
                existing =>
                {
                    var properties = existing?.Properties ?? ImmutableDictionary<string, object>.Empty;

                    if (!properties.ContainsKey(ProviderPropertyKey))
                    {
                        properties = properties.SetItem(ProviderPropertyKey, providerId);
                    }

                    if (!properties.ContainsKey(WorkspacePropertyKey))
                    {
                        properties = properties.SetItem(WorkspacePropertyKey, workspaceId);
                    }

                    // Seed the mode only when absent — a plain reconnect that recreates the agent must
                    // not overwrite a mode the user deliberately switched to.
                    if (!string.IsNullOrWhiteSpace(modeId) && !properties.ContainsKey(ModePropertyKey))
                    {
                        properties = properties.SetItem(ModePropertyKey, modeId);
                    }

                    return (
                        existing
                        ?? new ThreadMetadata
                        {
                            ThreadId = threadId,
                            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        }
                    ) with
                    {
                        Properties = properties,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                }
            ).ConfigureAwait(false);

            _logger.LogInformation(
                "Persisted bindings for thread {ThreadId} (provider={ProviderId}, workspace={WorkspaceId}, mode={ModeId})",
                threadId,
                providerId,
                workspaceId,
                modeId
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist bindings for thread {ThreadId} (provider={ProviderId}, workspace={WorkspaceId}, mode={ModeId})",
                threadId,
                providerId,
                workspaceId,
                modeId
            );
        }
    }

    /// <summary>
    /// Overwrites the persisted mode for a thread (a deliberate, mutable mode switch). Provider and
    /// workspace are left untouched.
    /// </summary>
    private Task PersistModeAsync(string threadId, string? modeId)
    {
        return PersistThreadPropertyAsync(threadId, ModePropertyKey, modeId, label: "mode");
    }

    /// <summary>
    /// Overwrites the persisted provider for a thread (a deliberate provider switch on an idle
    /// conversation). Mode and workspace are left untouched. Unlike
    /// <see cref="PersistThreadBindingsIfNeededAsync"/> (seed-only), this unconditionally sets the
    /// value so a later refresh restores the switched-to provider.
    /// </summary>
    private Task PersistProviderAsync(string threadId, string? providerId)
    {
        return PersistThreadPropertyAsync(threadId, ProviderPropertyKey, providerId, label: "provider");
    }

    /// <summary>
    /// Unconditionally overwrites a single <see cref="ThreadMetadata.Properties"/> entry via a
    /// read-modify-write (<see cref="IConversationStore.UpdateMetadataAsync"/>). Shared by the
    /// deliberate, mutable mode- and provider-switch persist paths. A no-op when there is no store or
    /// the value is blank; persistence failures are logged and swallowed — the in-memory swap already
    /// succeeded, so a failed persist only forfeits the restore-after-refresh, not the live switch.
    /// </summary>
    private async Task PersistThreadPropertyAsync(
        string threadId,
        string propertyKey,
        string? value,
        string label
    )
    {
        if (_conversationStore == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            await _conversationStore.UpdateMetadataAsync(
                threadId,
                existing =>
                {
                    var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
                        .SetItem(propertyKey, value);

                    return (
                        existing
                        ?? new ThreadMetadata
                        {
                            ThreadId = threadId,
                            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        }
                    ) with
                    {
                        Properties = properties,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                }
            ).ConfigureAwait(false);

            _logger.LogInformation(
                "Persisted {Label} {Value} for thread {ThreadId}",
                label,
                value,
                threadId
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist {Label} {Value} for thread {ThreadId}",
                label,
                value,
                threadId
            );
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
            if (
                metadata?.Properties != null
                && metadata.Properties.TryGetValue(ProviderPropertyKey, out var raw)
                && TryNormalizeProviderId(raw, out var providerId)
            )
            {
                return providerId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load persisted provider for thread {ThreadId}; falling back to default",
                threadId
            );
        }

        return null;
    }

    private static bool TryNormalizeProviderId(object raw, out string providerId)
    {
        var value = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            providerId = string.Empty;
            return false;
        }

        providerId = value.Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Extracts a non-empty trimmed string from a persisted metadata value (raw string or a
    /// <see cref="JsonElement"/> string). Unlike <see cref="TryNormalizeProviderId"/> the value is
    /// NOT lowercased — workspace ids are opaque (GUIDs / the <c>"default"</c> sentinel).
    /// </summary>
    private static bool TryNormalizeStringValue(object raw, out string value)
    {
        var extracted = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(extracted))
        {
            value = string.Empty;
            return false;
        }

        value = extracted.Trim();
        return true;
    }

    /// <summary>
    /// Checks if an agent exists for the given threadId.
    /// </summary>
    public bool HasAgent(string threadId)
    {
        return _agents.ContainsKey(threadId);
    }

    /// <summary>
    /// Tries to return the live agent for <paramref name="threadId"/> without creating one. Used
    /// by external dispatchers (e.g. the context-discovery webhook) that need to push a message
    /// into an existing conversation but must remain best-effort when the thread has been torn
    /// down between the trigger and the dispatch.
    /// </summary>
    public bool TryGet(string threadId, out IMultiTurnAgent? agent)
    {
        if (string.IsNullOrEmpty(threadId) || !_agents.TryGetValue(threadId, out var entry))
        {
            agent = null;
            return false;
        }

        agent = entry.Agent;
        return true;
    }

    /// <summary>
    /// Raised after <see cref="RemoveAgentAsync"/> tears down a thread's agent so external
    /// tables (session→thread routing in the sandbox registry, presence lists, etc.) can drop
    /// the entry. Intentionally NOT raised by <see cref="RecreateAgentWithModeAsync"/>: a mode
    /// switch preserves the same threadId, so any external routing keyed on threadId must remain
    /// intact across the swap.
    /// </summary>
    public event Action<string>? ThreadRemoved;

    /// <summary>
    /// Returns true when the pooled agent for <paramref name="threadId"/> currently has an armed,
    /// unresolved <c>Wait</c> — i.e. a deferred tool call named <see cref="WaitToolProvider.WaitToolName"/>.
    /// A mode/provider switch recreates the agent (discarding its trigger runtime), so callers use this
    /// to warn that a pending wait will be lost. Returns false when no agent is pooled or the pooled
    /// agent type does not expose deferred-call inspection (e.g. a CLI-backed loop).
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> HasArmedWaitAsync(string threadId, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(threadId, out var entry))
        {
            return false;
        }

        if (entry.Agent is not MultiTurnAgentLoop loop)
        {
            return false;
        }

        var deferred = await loop.GetDeferredToolCallsAsync(ct);
        return deferred.Any(d =>
            string.Equals(d.FunctionName, WaitToolProvider.WaitToolName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Gets the current mode for an agent.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <returns>The current mode, or null if no agent exists</returns>
    public AgentProfile? GetAgentMode(string threadId)
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
                IsStale: false
            );
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
            IsStale: isStale
        );
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
            try
            {
                await entry.DisposeAsync();
            }
            finally
            {
                // Clear the conversation's sandbox binding even if agent disposal threw: the pooled agent
                // is already removed, so the browse binding must not outlive it. Compare-and-clear under the
                // SAME per-thread lock the publish uses (CreateAgentEntry commit): if a concurrent
                // GetOrCreate/swap re-created the agent for this thread while we were disposing, _agents holds
                // the new entry and its freshly-published binding — leave it intact rather than clobbering it.
                // Clearing never destroys the shared (workspaceId, appId) gateway session another conversation
                // may still use. ClearEstablishedBinding is a lock-free dictionary remove, safe under the lock.
                var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
                lock (lockObj)
                {
                    if (!_agents.ContainsKey(threadId))
                    {
                        _bindingSink?.ClearEstablishedBinding(threadId);
                    }
                }
            }

            RaiseThreadRemoved(threadId);
        }
    }

    /// <summary>
    /// Publishes <paramref name="entry"/>'s sandbox binding, if it carries one, to the injected
    /// <see cref="ISandboxBindingSink"/>. MUST be called under the per-thread lock, immediately after the
    /// entry is committed to <see cref="_agents"/>, so the binding is published atomically with the commit
    /// (a construction that threw never reaches here, so it publishes nothing). A non-workspace entry
    /// carries no binding and leaves any prior binding untouched.
    /// </summary>
    private void PublishBindingIfStaged(string threadId, AgentEntry entry)
    {
        if (entry.EstablishedBinding is { } binding)
        {
            _bindingSink?.PublishEstablishedBinding(threadId, binding);
        }
    }

    private void RaiseThreadRemoved(string threadId)
    {
        try
        {
            ThreadRemoved?.Invoke(threadId);
        }
        catch (Exception ex)
        {
            // External subscribers (session registry, etc.) must not poison the pool's lifecycle
            // if they throw — log and swallow so a buggy listener can't strand other threads.
            _logger.LogWarning(ex, "ThreadRemoved subscriber threw for thread {ThreadId}", threadId);
        }
    }

    /// <summary>
    /// Recreates an agent with a new mode. This will dispose the existing agent
    /// and create a new one with the specified mode.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="mode">The new chat mode to use</param>
    /// <param name="callerCredential">
    /// The credential of the caller requesting the switch, or <c>null</c> for the interactive
    /// (no-credential) UI path. Validated against the app id the conversation was frozen to at
    /// creation: a mismatch throws <see cref="SandboxCredentialConflictException"/> so a foreign S2S
    /// caller cannot mutate another app's conversation mode (issue #153 M2). The frozen credential
    /// itself is preserved across the swap — this parameter only authorizes the switch.
    /// </param>
    /// <returns>The new agent for this thread</returns>
    /// <exception cref="SandboxCredentialConflictException">
    /// Thrown when <paramref name="callerCredential"/>'s <c>AppId</c> differs from the app id the
    /// conversation is bound to.
    /// </exception>
    public async Task<IMultiTurnAgent> RecreateAgentWithModeAsync(
        string threadId,
        AgentProfile mode,
        SandboxCredential? callerCredential = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(mode);

        _logger.LogInformation(
            "Recreating agent for thread {ThreadId} with mode {ModeId} ({ModeName})",
            threadId,
            mode.Id,
            mode.Name
        );

        // Resolve provider/workspace before re-entering the lock — the same persisted provider and
        // workspace must continue to be used after a mode-switch (mode-switch is neither a provider
        // nor a workspace switch).
        var resolvedProviderId = ResolveProviderId(threadId, requestedProviderId: null);
        var resolvedWorkspaceId = ResolveWorkspaceId(threadId, requestedWorkspaceId: null);

        var entry = await SwapAgentUnderLockAsync(
            threadId,
            mode,
            resolvedProviderId,
            resolvedWorkspaceId,
            switchKind: "mode",
            callerCredential
        );

        // A mode switch is deliberate and mutable: overwrite the persisted mode so a later refresh
        // restores the switched-to mode (provider/workspace are untouched by a mode switch).
        await PersistModeAsync(threadId, mode.Id);

        return entry.Agent;
    }

    /// <summary>
    /// Tears down a thread's agent and recreates it against a DIFFERENT provider, preserving the
    /// thread's current mode and persisted workspace. Used when the user switches a conversation's
    /// provider after its run has completed (provider is mutable when idle; workspace stays bound for
    /// life). The new provider is validated up-front (an unavailable/unknown id throws
    /// <see cref="ProviderUnavailableException"/>), used directly for the new agent, then persisted
    /// (overwrite) so a later refresh restores it — deliberately bypassing the "persisted wins"
    /// immutability that <see cref="ResolveProviderId"/> enforces for plain reconnects.
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="newProviderId">The provider to switch to</param>
    /// <param name="currentMode">The thread's current mode, preserved across the switch</param>
    /// <param name="callerCredential">
    /// The credential of the caller requesting the switch, or <c>null</c> for the interactive
    /// (no-credential) UI path. Validated against the app id the conversation was frozen to at
    /// creation: a mismatch throws <see cref="SandboxCredentialConflictException"/> so a foreign S2S
    /// caller cannot mutate another app's conversation provider (issue #153 M2). The frozen credential
    /// itself is preserved across the swap — this parameter only authorizes the switch.
    /// </param>
    /// <returns>The new agent for this thread</returns>
    /// <exception cref="SandboxCredentialConflictException">
    /// Thrown when <paramref name="callerCredential"/>'s <c>AppId</c> differs from the app id the
    /// conversation is bound to.
    /// </exception>
    public async Task<IMultiTurnAgent> RecreateAgentWithProviderAsync(
        string threadId,
        string newProviderId,
        AgentProfile currentMode,
        SandboxCredential? callerCredential = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(newProviderId);
        ArgumentNullException.ThrowIfNull(currentMode);

        // Validate the target BEFORE tearing down the existing agent — a bad id must leave the thread
        // untouched (and surface as a clean 503 at the controller), not evict a working agent.
        EnsureAvailableOrThrow(newProviderId, source: "requested");

        _logger.LogInformation(
            "Recreating agent for thread {ThreadId} with provider {ProviderId} (mode {ModeId} preserved)",
            threadId,
            newProviderId,
            currentMode.Id
        );

        // Provider is the switch; workspace stays bound (resolve the persisted one). Resolved before
        // the lock to avoid blocking other threadIds on file I/O.
        var resolvedWorkspaceId = ResolveWorkspaceId(threadId, requestedWorkspaceId: null);

        var entry = await SwapAgentUnderLockAsync(
            threadId,
            currentMode,
            newProviderId,
            resolvedWorkspaceId,
            switchKind: "provider",
            callerCredential
        );

        // A provider switch is deliberate and mutable: overwrite the persisted provider so a later
        // refresh restores it (mode/workspace untouched).
        await PersistProviderAsync(threadId, newProviderId);

        return entry.Agent;
    }

    /// <summary>
    /// Swaps a thread's pooled agent for a freshly-built one under the per-thread creation lock,
    /// transactionally. The replacement is constructed FIRST: if construction throws (e.g. a provider
    /// or Workspace-Agent sandbox session fails to start), the existing agent stays registered and the
    /// thread is left untouched — the failure surfaces as a clean 503 upstream instead of a broken
    /// conversation with no pooled agent. Only once the new entry is built is the old one evicted,
    /// swapped in, and disposed — outside the lock and non-fatally, because the new agent is already
    /// active, so a failure tearing down the OLD one must not fail the switch. Shared by the
    /// mode-switch (<see cref="RecreateAgentWithModeAsync"/>) and provider-switch
    /// (<see cref="RecreateAgentWithProviderAsync"/>) recreate paths.
    /// </summary>
    private async Task<AgentEntry> SwapAgentUnderLockAsync(
        string threadId,
        AgentProfile mode,
        string providerId,
        string? workspaceId,
        string switchKind,
        SandboxCredential? callerCredential = null
    )
    {
        // Acquire the per-key lock to prevent races with concurrent GetOrCreateAgent calls.
        var lockObj = _creationLocks.GetOrAdd(threadId, _ => new object());
        AgentEntry? oldEntry;
        AgentEntry entry;
        lock (lockObj)
        {
            // Preserve the credential the conversation was frozen to at creation — a mode/provider
            // switch is neither a create nor a cross-actor request, so it must not change (or drop)
            // the caller identity the thread is bound to. Peeked (not removed) under the same lock
            // that guards the swap below, so no concurrent GetOrCreateAgent can interleave.
            _agents.TryGetValue(threadId, out var existingEntry);
            var frozenCredential = existingEntry?.CallerCredential;

            // Cross-actor guard (issue #153): a switch must be rejected for a caller that is NOT the
            // app the conversation is bound to — otherwise a different S2S caller could mutate another
            // app's mode/provider, bypassing the same guard SendMessage enforces. Compare by AppId
            // only (never the key), null-safe, mirroring GetOrCreateAgent: both null (interactive UI)
            // matches; one null / one set (UI<->S2S) and two differing set values (S2S-A<->S2S-B)
            // conflict. Skipped when there is no existing entry (the agent was evicted) — the recreate
            // then binds to the caller as the new owner, exactly as a fresh create would. Runs inside
            // the same lock as the swap so no concurrent caller can race between check and act.
            if (existingEntry != null)
            {
                var existingAppId = frozenCredential?.AppId;
                var requestedAppId = callerCredential?.AppId;
                if (!string.Equals(existingAppId, requestedAppId, StringComparison.Ordinal))
                {
                    throw new SandboxCredentialConflictException(threadId, existingAppId, requestedAppId);
                }
            }

            // Construct BEFORE evicting — a throw here leaves the current agent registered (the thread
            // is untouched) rather than stranding the conversation with no pooled agent.
            entry = CreateAgentEntry(
                threadId,
                mode,
                providerId,
                requestResponseDumpFileName: null,
                workspaceId,
                frozenCredential
            );
            _ = _agents.TryRemove(threadId, out oldEntry);
            _agents[threadId] = entry;
            PublishBindingIfStaged(threadId, entry);
        }

        // Dispose old entry outside the lock to avoid blocking concurrent operations. The new agent is
        // already swapped in, so a failure tearing down the OLD one (e.g. its provider's CLI is missing,
        // or its StopAsync throws) must NOT fail the switch — log and move on, otherwise the endpoint
        // leaks a 500 for a swap that actually succeeded.
        if (oldEntry != null)
        {
            _logger.LogInformation("Removing agent for thread {ThreadId}", threadId);
            try
            {
                await oldEntry.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to dispose the previous agent for thread {ThreadId} after a {SwitchKind} switch; the new agent is already active",
                    threadId,
                    switchKind
                );
            }
        }

        return entry;
    }

    private AgentEntry CreateAgentEntry(
        string threadId,
        AgentProfile mode,
        string providerId,
        string? requestResponseDumpFileName,
        string? workspaceId,
        SandboxCredential? callerCredential = null
    )
    {
        _logger.LogInformation(
            "Creating new agent for thread {ThreadId} with mode {ModeId} ({ModeName}), provider {ProviderId}, workspace {WorkspaceId}, dump recording enabled: {DumpEnabled}",
            threadId,
            mode.Id,
            mode.Name,
            providerId,
            workspaceId ?? DefaultWorkspaceId,
            !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
        );

        var result = _agentFactory(
            new AgentCreationContext(
                threadId,
                mode,
                providerId,
                requestResponseDumpFileName,
                workspaceId,
                callerCredential
            )
        );
        var agent = result.Agent;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_poolCts.Token);

        // Start the agent's background run loop
        var runTask = Task.Run(
            async () =>
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
            },
            cts.Token
        );

        return new AgentEntry
        {
            Agent = agent,
            RunTask = runTask,
            Cts = cts,
            Mode = mode,
            ProviderId = providerId,
            WorkspaceId = workspaceId,
            RequestResponseDumpFileName = requestResponseDumpFileName,
            OwnedResources = result.OwnedResources,
            CallerCredential = callerCredential,
            EstablishedBinding = result.StagedBinding,
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
