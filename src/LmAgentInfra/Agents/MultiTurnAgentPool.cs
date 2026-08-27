using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
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
    private readonly Func<SandboxEstablishedBinding, CancellationToken, Task<SandboxSession>>? _liveSessionResolver;
    private readonly MultiTurnLifecycleServices? _lifecycleServices;
    private readonly ILogger<MultiTurnAgentPool> _logger;
    private readonly CancellationTokenSource _poolCts = new();

    /// <summary>
    /// Guards the pool's terminal boundary: the <see cref="_disposed"/> flag, every commit into
    /// <see cref="_agents"/>, and every read of <see cref="_poolCts"/>'s token.
    /// <para>
    /// Without it, <c>_disposed</c> was a check-then-act spanning the agent factory: a caller checked
    /// the flag, the factory ran (provider handshake, sandbox session, MCP clients — not fast), and by
    /// the time the entry was committed <see cref="DisposeAsync"/> could already have snapshotted
    /// <see cref="_agents"/>, disposed what it found and cleared the map. The late entry then landed in
    /// a dictionary nobody would read again: a live agent with a running loop and owned resources, and
    /// no owner left to stop it.
    /// </para>
    /// <para>
    /// LOCK ORDER: per-thread creation lock -> this lock. <see cref="DisposeAsync"/> takes only this
    /// one, and nothing takes this one and then a creation lock, so the two cannot deadlock. Never await
    /// while holding it.
    /// </para>
    /// </summary>
    private readonly object _lifecycleLock = new();
    private volatile bool _disposed;

    public sealed record RunStateInfo(
        bool IsInProgress,
        string? CurrentRunId,
        bool AgentIsRunning,
        bool RunTaskCompleted,
        bool IsStale
    );

    public enum AgentRefreshStatus
    {
        Current,
        RefreshDeferred,
        RefreshRequired,
        Replaced,
    }

    public sealed record AgentRefreshResult(IMultiTurnAgent Agent, AgentRefreshStatus Status);

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
    /// <para>
    /// <c>LifecycleServices</c> is the lifecycle observation / tool approval bundle the pool was
    /// constructed with, handed to the factory so the loop it builds can be watched and gated. Null
    /// when the host wired none, which every loop reads as fully disabled. It travels beside
    /// <c>CallerCredential</c> deliberately: a factory that must scope observation to the calling
    /// owner has both halves of that decision in one place.
    /// </para>
    /// </summary>
    public sealed record AgentCreationContext(
        string ThreadId,
        AgentProfile Mode,
        string ProviderId,
        string? DumpFile,
        string? WorkspaceId,
        SandboxCredential? CallerCredential = null,
        MultiTurnLifecycleServices? LifecycleServices = null
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

        /// <summary>
        /// Tears the entry down. NOT SAFE TO RUN TWICE, and not safe to run concurrently with itself: its
        /// first act cancels a <see cref="CancellationTokenSource"/> it disposes at the end (cancelling a
        /// disposed source throws <see cref="ObjectDisposedException"/>), and <c>IMultiTurnAgent.StopAsync</c>
        /// is not concurrency-safe either — it nulls out the run task and the internal token source that a
        /// second caller is still reading.
        /// <para>
        /// The caller must therefore have CLAIMED this entry: obtained it from <c>ClaimEntry</c>, from
        /// <c>TryCommitEntry</c>'s <c>replaced</c> out-parameter, from the pool's own
        /// <c>DisposeAsync</c> snapshot, or as an entry that was never committed at all. All of those are
        /// mutually exclusive under <c>_lifecycleLock</c>, so a claimed entry has exactly one owner.
        /// </para>
        /// </summary>
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
    /// <param name="liveSessionResolver">
    /// Optional resolver used immediately before message dispatch to verify a sandbox-backed entry still
    /// targets the registry's live session. Null for non-sandbox hosts and legacy tests.
    /// </param>
    /// <param name="lifecycleServices">
    /// Optional lifecycle observation / tool approval, handed to the factory on every
    /// <see cref="AgentCreationContext"/> this pool builds. Null leaves every agent unobserved and
    /// ungated, exactly as before this parameter existed.
    /// </param>
    public MultiTurnAgentPool(
        Func<AgentCreationContext, AgentCreationResult> agentFactory,
        IProviderResolver? providerRegistry,
        IConversationStore? conversationStore,
        ILogger<MultiTurnAgentPool> logger,
        ISandboxBindingSink? bindingSink = null,
        Func<SandboxEstablishedBinding, CancellationToken, Task<SandboxSession>>? liveSessionResolver = null,
        MultiTurnLifecycleServices? lifecycleServices = null
    )
        : this(
            agentFactory,
            providerRegistry,
            conversationStore,
            logger,
            bindingSink,
            liveSessionResolver,
            lifecycleServices,
            factoryReadsContext: true
        ) { }

    /// <summary>
    /// Back-compat overload taking a four-arg (threadId, mode, providerId, dump) factory that
    /// predates the <see cref="AgentCreationContext"/> bundling. Existing callers/tests keep
    /// compiling; the workspace id defaults to <c>"default"</c>.
    /// </summary>
    public MultiTurnAgentPool(
        Func<string, AgentProfile, string, string?, AgentCreationResult> agentFactory,
        IProviderResolver? providerRegistry,
        IConversationStore? conversationStore,
        ILogger<MultiTurnAgentPool> logger,
        MultiTurnLifecycleServices? lifecycleServices = null
    )
        : this(
            WrapProviderFactory(agentFactory),
            providerRegistry,
            conversationStore,
            logger,
            bindingSink: null,
            liveSessionResolver: null,
            lifecycleServices,
            factoryReadsContext: false
        ) { }

    /// <summary>
    /// Back-compat overload that omits the provider-id parameter from the factory. The
    /// pool injects a fixed provider id (<c>"legacy"</c>) when invoking the factory. Use
    /// the context-aware constructor for new code.
    /// </summary>
    public MultiTurnAgentPool(
        Func<string, AgentProfile, string?, AgentCreationResult> agentFactory,
        ILogger<MultiTurnAgentPool> logger,
        MultiTurnLifecycleServices? lifecycleServices = null
    )
        : this(
            WrapLegacyFactory(agentFactory),
            providerRegistry: null,
            conversationStore: null,
            logger,
            bindingSink: null,
            liveSessionResolver: null,
            lifecycleServices,
            factoryReadsContext: false
        ) { }

    // factoryReadsContext is false for the back-compat overloads, whose factories take loose
    // positional arguments and so never see the AgentCreationContext — including the bundle on it.
    private MultiTurnAgentPool(
        Func<AgentCreationContext, AgentCreationResult> agentFactory,
        IProviderResolver? providerRegistry,
        IConversationStore? conversationStore,
        ILogger<MultiTurnAgentPool> logger,
        ISandboxBindingSink? bindingSink,
        Func<SandboxEstablishedBinding, CancellationToken, Task<SandboxSession>>? liveSessionResolver,
        MultiTurnLifecycleServices? lifecycleServices,
        bool factoryReadsContext
    )
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _providerRegistry = providerRegistry;
        _conversationStore = conversationStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bindingSink = bindingSink;
        _liveSessionResolver = liveSessionResolver;
        _lifecycleServices = lifecycleServices;

        // A legacy factory is handed loose arguments, so the bundle on the context cannot reach the
        // loops it builds — those agents run ungated no matter what was configured here. Approval
        // failing open silently is the dangerous half of that, so it is said out loud, once, at
        // construction: a host that believes its tools are gated learns otherwise before a run, not
        // after one. Observation-only bundles are not worth a warning; nothing unsafe happens.
        if (!factoryReadsContext && lifecycleServices?.Approval.IsEnabled == true)
        {
            _logger.LogWarning(LegacyFactoryApprovalWarning);
        }
    }

    /// <summary>
    /// The wording emitted when tool approval is configured on a pool whose factory cannot receive
    /// it. Held as a constant so it stays identical across releases and can be asserted on.
    /// </summary>
    internal const string LegacyFactoryApprovalWarning =
        "Tool approval is configured but this MultiTurnAgentPool was constructed with a legacy agent "
        + "factory that never sees AgentCreationContext, so the approval gate cannot reach the agents "
        + "it creates and they will run UNGATED. Switch to the AgentCreationContext-based constructor.";

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
    public IMultiTurnAgent GetOrCreateAgent(
        string threadId,
        AgentProfile mode,
        string? requestResponseDumpFileName = null
    )
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
        AgentEntry? entry = null;
        AgentEntry? uncommitted = null;
        var created = false;
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out var existing))
            {
                var candidate = CreateAgentEntry(
                    threadId,
                    mode,
                    resolvedProviderId,
                    requestResponseDumpFileName,
                    resolvedWorkspaceId,
                    callerCredential
                );

                // The commit re-checks disposal under the lifecycle lock. The disposed check at the top
                // of this method is only a fast path: the factory above can run for as long as a provider
                // handshake takes, and the pool can be disposed inside that window.
                if (TryCommitEntry(threadId, candidate, out _))
                {
                    entry = candidate;
                    PublishBindingIfStaged(threadId, candidate);
                    created = true;
                }
                else
                {
                    uncommitted = candidate;
                }
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

        if (uncommitted != null)
        {
            // Outside the per-thread lock: the abandonment must not hold it, and the caller gets the
            // same refusal it would have got had it arrived a moment later.
            AbandonUncommittedEntry(threadId, uncommitted);
            throw DisposedException();
        }

        // Non-null on every path that reaches here: the lock body either resolved an existing entry,
        // committed a new one, or left `uncommitted` set and threw above.
        var resolvedEntry = entry!;

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
            && !string.Equals(
                resolvedEntry.RequestResponseDumpFileName,
                requestResponseDumpFileName,
                StringComparison.Ordinal
            )
        )
        {
            _logger.LogWarning(
                "Request/response recording was requested for thread {ThreadId}, but an existing agent is being reused. "
                    + "Recording dump file is fixed at agent creation time. Existing dump base: {ExistingDumpBase}",
                threadId,
                resolvedEntry.RequestResponseDumpFileName ?? "(none)"
            );
        }

        return resolvedEntry.Agent;
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
            await _conversationStore
                .UpdateMetadataAsync(
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
                )
                .ConfigureAwait(false);

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
    private async Task PersistThreadPropertyAsync(string threadId, string propertyKey, string? value, string label)
    {
        if (_conversationStore == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            await _conversationStore
                .UpdateMetadataAsync(
                    threadId,
                    existing =>
                    {
                        var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                            propertyKey,
                            value
                        );

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
                )
                .ConfigureAwait(false);

            _logger.LogInformation("Persisted {Label} {Value} for thread {ThreadId}", label, value, threadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist {Label} {Value} for thread {ThreadId}", label, value, threadId);
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
            string.Equals(d.FunctionName, WaitToolProvider.WaitToolName, StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Returns true when the pooled agent for <paramref name="threadId"/> currently has an unanswered
    /// <c>AskUserQuestion</c> parked — i.e. a deferred tool call named
    /// <see cref="AskUserQuestionToolProvider.ToolName"/> — OR any LIVE descendant in its sub-agent
    /// tree (direct child, or a further-nested descendant reached through a child's own
    /// <c>SubAgentManager</c>) does. Unlike <see cref="HasArmedWaitAsync"/> (which is warn-only),
    /// callers use this to HARD-block a mode/provider switch (issue #246): recreating the primary
    /// agent disposes its ENTIRE live descendant tree, so a pending question belonging to a child —
    /// not just the primary itself — would otherwise be silently discarded and orphaned, with no way
    /// for the client to recover it. Returns false when no agent is pooled or the pooled agent type
    /// does not expose deferred-call inspection (e.g. a CLI-backed loop).
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> HasPendingAskUserQuestionAsync(string threadId, CancellationToken ct = default)
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
        if (
            deferred.Any(d =>
                string.Equals(d.FunctionName, AskUserQuestionToolProvider.ToolName, StringComparison.Ordinal)
            )
        )
        {
            return true;
        }

        return loop.SubAgentManager is { } subAgentManager
            && await subAgentManager.HasPendingAskUserQuestionInDescendantsAsync(ct);
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
    /// Ensures a sandbox-backed pooled agent still targets the registry's live session before a new
    /// message is dispatched. A replaced session rebuilds an idle entry transactionally; an active run
    /// is never interrupted and will be checked again before the next message.
    /// </summary>
    public async Task<AgentRefreshResult> EnsureCurrentAgentAsync(
        string threadId,
        SandboxCredential? callerCredential = null,
        CancellationToken ct = default,
        bool replace = true
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
        AgentEntry observed;
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out observed!))
            {
                throw new InvalidOperationException($"No pooled agent exists for thread '{threadId}'.");
            }

            EnsureCallerMatches(threadId, observed, callerCredential);
        }

        if (_liveSessionResolver is null || observed.EstablishedBinding is not { SessionId.Length: > 0 } binding)
        {
            return new AgentRefreshResult(observed.Agent, AgentRefreshStatus.Current);
        }

        var liveSession = await _liveSessionResolver(binding, ct).ConfigureAwait(false);
        if (string.Equals(binding.SessionId, liveSession.SessionId, StringComparison.Ordinal))
        {
            return new AgentRefreshResult(observed.Agent, AgentRefreshStatus.Current);
        }

        AgentEntry? replacedEntry = null;
        AgentEntry? abandonedReplacement = null;
        AgentEntry? uncommitted = null;
        AgentEntry current;
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out current!))
            {
                throw new InvalidOperationException($"No pooled agent exists for thread '{threadId}'.");
            }

            EnsureCallerMatches(threadId, current, callerCredential);

            if (!ReferenceEquals(current, observed))
            {
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.Current);
            }

            if (IsEntryInProgress(current))
            {
                _logger.LogInformation(
                    "Deferring sandbox session refresh for thread {ThreadId} while run {RunId} is active",
                    threadId,
                    current.Agent.CurrentRunId
                );
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.RefreshDeferred);
            }

            // The second half of "busy", and the one CurrentRunId cannot report. An input is
            // acknowledged to its caller — a receipt returned, an accepted-input row written — the
            // moment the agent takes it, but no run names it until the run loop wakes, drains it and
            // mints a run id. Every signal above reads idle for that whole span, so replacing here
            // disposed the agent, completed its input channel, and dropped work this host had
            // already promised to do, with nothing failing anywhere: the caller keeps its 202 and
            // its inputId, and the turn simply never happens.
            //
            // Deferring instead is safe in both directions. It cannot wedge, because the signal is
            // gated on the run loop still being alive to consume the input — a dead loop refreshes
            // as before — and because it clears as soon as the run loop assigns the input a run.
            // Placed AHEAD of the replace:false early return deliberately: that probe's caller
            // closes the client's socket on RefreshRequired, which is the wrong move on top of an
            // input the loop is about to run.
            if (HasUnconsumedAcknowledgedInput(current))
            {
                _logger.LogInformation(
                    "Deferring sandbox session refresh for thread {ThreadId}: input has been acknowledged but not yet assigned to a run",
                    threadId
                );
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.RefreshDeferred);
            }

            if (!replace)
            {
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.RefreshRequired);
            }

            var replacement = CreateAgentEntry(
                threadId,
                current.Mode,
                current.ProviderId,
                current.RequestResponseDumpFileName,
                current.WorkspaceId,
                current.CallerCredential
            );

            // Repeat the check on the FAR side of construction. Building the replacement is the slow
            // part of this method — a workspace agent starts a sandbox session over the network — and
            // callers hold their agent reference outside this lock, so a send can be acknowledged by
            // the entry we are one line away from discarding. Checking only before construction would
            // leave that whole span open, which is the same defect at a shorter timescale. Throw the
            // fresh entry away instead of the input: nothing has been published for it yet
            // (PublishBindingIfStaged runs only on the commit below), so discarding it is clean, and
            // the refresh simply happens on the next attempt.
            //
            // ORDER IS LOAD-BEARING: the acknowledged-input check runs BEFORE the commit, never after.
            // TryCommitEntry publishes on success, so testing it first would publish the replacement
            // and drop the acknowledged input in exactly the case this branch exists to prevent. Taken
            // in this order both invariants hold: input pending means nothing is committed AND the
            // replacement is still disposed, so neither the input nor the entry is lost.
            if (HasUnconsumedAcknowledgedInput(current))
            {
                abandonedReplacement = replacement;
            }
            // Same disposal-atomic commit as the create and swap paths: a sandbox refresh that outlives
            // the pool must not publish a replacement into a map disposal has already drained.
            else if (!TryCommitEntry(threadId, replacement, out _))
            {
                uncommitted = replacement;
            }
            else
            {
                PublishBindingIfStaged(threadId, replacement);
                replacedEntry = current;
                current = replacement;
            }
        }

        if (abandonedReplacement != null)
        {
            _logger.LogInformation(
                "Discarding the freshly built replacement agent for thread {ThreadId}: input was acknowledged while it was being constructed, so the original agent keeps the conversation",
                threadId
            );

            try
            {
                await abandonedReplacement.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to dispose the discarded replacement agent for thread {ThreadId}",
                    threadId
                );
            }

            return new AgentRefreshResult(current.Agent, AgentRefreshStatus.RefreshDeferred);
        }

        // A DIFFERENT abandonment from the one above, kept separate because the reason is the whole
        // diagnostic: that one keeps a live conversation and answers RefreshDeferred, this one means the
        // pool is going away and owes the caller an ObjectDisposedException rather than a retryable
        // "not now".
        if (uncommitted != null)
        {
            await AbandonUncommittedEntryAsync(threadId, uncommitted);
            throw DisposedException();
        }

        _logger.LogInformation(
            "Refreshed sandbox-backed agent for thread {ThreadId} from session {PreviousSessionId} to {SessionId}",
            threadId,
            binding.SessionId,
            liveSession.SessionId
        );

        // Non-null on every path that reaches here: the commit branch above is the only one that
        // falls through, and the discard branch returns. Checked rather than suppressed so a later
        // edit that adds a third way out of the lock cannot turn a missed assignment into a NRE.
        if (replacedEntry != null)
        {
            try
            {
                await replacedEntry.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to dispose the previous agent for thread {ThreadId} after sandbox session refresh",
                    threadId
                );
            }
        }

        return new AgentRefreshResult(current.Agent, AgentRefreshStatus.Replaced);
    }

    private static void EnsureCallerMatches(string threadId, AgentEntry entry, SandboxCredential? callerCredential)
    {
        var existingAppId = entry.CallerCredential?.AppId;
        var requestedAppId = callerCredential?.AppId;
        if (!string.Equals(existingAppId, requestedAppId, StringComparison.Ordinal))
        {
            throw new SandboxCredentialConflictException(threadId, existingAppId, requestedAppId);
        }
    }

    private static bool IsEntryInProgress(AgentEntry entry)
    {
        var hasRunId = !string.IsNullOrWhiteSpace(entry.Agent.CurrentRunId);
        return hasRunId && entry.Agent.IsRunning && !entry.RunTask.IsCompleted;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> holds input it has already acknowledged to a caller that no
    /// run owns yet — the window in which <see cref="IsEntryInProgress"/> reads idle but tearing the
    /// entry down would silently destroy accepted work.
    /// </summary>
    /// <remarks>
    /// Gated on the same liveness half as <see cref="IsEntryInProgress"/>, and for the same reason
    /// read backwards: a loop that has already exited can never consume the input, so holding the
    /// refresh open for it would trade a lost input for a permanently stale sandbox and never
    /// recover either. Deliberately NOT folded into <see cref="IsEntryInProgress"/>: that predicate
    /// also answers <see cref="GetRunStateInfo"/> / <see cref="IsRunInProgress"/> for clients, where
    /// "a run is in progress" must keep meaning a run actually exists.
    /// </remarks>
    private static bool HasUnconsumedAcknowledgedInput(AgentEntry entry) =>
        entry.Agent.HasUnassignedInput && entry.Agent.IsRunning && !entry.RunTask.IsCompleted;

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
        var isInProgress = IsEntryInProgress(entry);
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
    /// <para>
    /// The entry is CLAIMED (see <see cref="ClaimEntry"/>) rather than merely removed, so this method and
    /// <see cref="DisposeAsync"/> can never both end up owning the same entry. A no-op when the thread has
    /// no agent — including when the pool has already been disposed, which drained every entry.
    /// </para>
    /// </summary>
    public async ValueTask RemoveAgentAsync(string threadId)
    {
        var entry = ClaimEntry(threadId);
        if (entry is null)
        {
            return;
        }

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
        AgentEntry? oldEntry = null;
        AgentEntry? entry = null;
        AgentEntry? uncommitted = null;
        lock (lockObj)
        {
            // Preserve the credential the conversation was frozen to at creation — a mode/provider
            // switch is neither a create nor a cross-actor request, so it must not change (or drop)
            // the caller identity the thread is bound to. Peeked (not removed) under the same lock
            // that guards the swap below, so no concurrent GetOrCreateAgent can interleave.
            _ = _agents.TryGetValue(threadId, out var existingEntry);
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
            var candidate = CreateAgentEntry(
                threadId,
                mode,
                providerId,
                requestResponseDumpFileName: null,
                workspaceId,
                frozenCredential
            );

            // The swap is a commit, so it takes the same disposal-atomic path as a fresh create: the
            // factory above can outlive the pool, and a replacement published into a cleared map would
            // leak exactly as a late creation does. On refusal the CURRENT entry stays registered, so
            // pool disposal still tears it down.
            if (TryCommitEntry(threadId, candidate, out oldEntry))
            {
                entry = candidate;
                PublishBindingIfStaged(threadId, candidate);
            }
            else
            {
                uncommitted = candidate;
            }
        }

        if (uncommitted != null)
        {
            await AbandonUncommittedEntryAsync(threadId, uncommitted);
            throw DisposedException();
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

        return entry!;
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
                callerCredential,
                _lifecycleServices
            )
        );
        var agent = result.Agent;
        var cts = CreateEntryCts();

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

    /// <summary>
    /// Takes the thread's entry OUT of <see cref="_agents"/> under the lifecycle lock and hands the caller
    /// sole ownership of tearing it down, or returns <c>null</c> if there was nothing to claim.
    /// <para>
    /// This is the removal half of the claim protocol, and it is what makes "exactly one path disposes an
    /// entry" true rather than merely likely. Every act that takes an entry out of <see cref="_agents"/> —
    /// this, the overwrite inside <see cref="TryCommitEntry"/>, and the snapshot-and-clear inside
    /// <see cref="DisposeAsync"/> — now runs under <see cref="_lifecycleLock"/>, so they are totally
    /// ordered. A bare <c>ConcurrentDictionary.TryRemove</c> here was NOT ordered against
    /// <c>DisposeAsync</c>'s <c>[.. _agents.Values]</c> / <c>Clear()</c> pair: a remove landing between
    /// those two statements handed the same entry to both paths.
    /// </para>
    /// <para>
    /// Deliberately does NOT take the per-thread creation lock. Doing so would order removal against a
    /// concurrent <c>GetOrCreateAgent</c>'s lookup, but that lock is held across the agent factory
    /// — a provider handshake or sandbox session — so a remover would block a thread for the whole of
    /// somebody else's creation.
    /// </para>
    /// <para>
    /// KNOWN REMAINDER, unchanged by this protocol and pre-existing: a caller that has already been
    /// HANDED an <c>IMultiTurnAgent</c> by <c>GetOrCreateAgent</c> holds no claim on it, so a
    /// <c>RemoveAgentAsync</c> arriving immediately afterwards can dispose the agent under that caller's
    /// feet. What the protocol guarantees is that the entry is torn down exactly ONCE, not that nobody
    /// else is still using it. Closing that would need reference counting or holding the creation lock
    /// across the caller's use — both far wider than this change.
    /// </para>
    /// </summary>
    private AgentEntry? ClaimEntry(string threadId)
    {
        lock (_lifecycleLock)
        {
            return _agents.TryRemove(threadId, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// The cancellation source for one entry, obtained atomically against pool disposal.
    /// <para>
    /// Deliberately does NOT refuse when the pool is disposing. The factory has already run by this
    /// point, so refusing here would strand the agent and owned resources it built with nobody to
    /// dispose them; the refusal belongs at the commit, which hands the caller back an entry it can tear
    /// down. When the pool is disposing we return a standalone, already-cancelled source — exactly the
    /// state a source linked to the (already cancelled) pool token would be in.
    /// </para>
    /// </summary>
    private CancellationTokenSource CreateEntryCts()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                return cancelled;
            }

            // Reading _poolCts.Token under the lock is what keeps it from racing the _poolCts.Dispose()
            // at the end of DisposeAsync, which would otherwise surface as an ObjectDisposedException
            // naming CancellationTokenSource from deep inside a creation.
            return CancellationTokenSource.CreateLinkedTokenSource(_poolCts.Token);
        }
    }

    /// <summary>
    /// Publishes <paramref name="entry"/> as the thread's agent, atomically against pool disposal, and
    /// hands back the entry it replaced (<c>null</c> on a first creation). Returns <c>false</c> when the
    /// pool has begun disposing, in which case NOTHING was published and the caller owns disposing
    /// <paramref name="entry"/> — see <see cref="AbandonUncommittedEntry"/>.
    /// <para>
    /// MUST be called under the per-thread creation lock (see <see cref="_lifecycleLock"/> for the
    /// ordering rule). The sandbox binding is deliberately NOT published here: that call reaches an
    /// injected sink, and a foreign call has no business running under the pool's global lifecycle lock.
    /// Callers publish it immediately after a <c>true</c> return, still under the per-thread lock.
    /// </para>
    /// </summary>
    private bool TryCommitEntry(string threadId, AgentEntry entry, out AgentEntry? replaced)
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                replaced = null;
                return false;
            }

            // Peek-then-overwrite, never remove-then-add: every commit runs under the per-thread lock so
            // no other writer can interleave, and an overwrite leaves no instant where a lock-free reader
            // (GetRunStateInfo, HasAgent) sees the thread as having no agent at all.
            _ = _agents.TryGetValue(threadId, out replaced);
            _agents[threadId] = entry;
            return true;
        }
    }

    /// <summary>
    /// Tears down an entry whose commit lost the race to pool disposal. It was never published, so the
    /// pool's own disposal cannot see it and this is the only owner it will ever have. Fire-and-forget
    /// because the synchronous creation path cannot await, and blocking it would hold the caller (and,
    /// on the swap path, a per-thread lock) on another agent's shutdown.
    /// </summary>
    private void AbandonUncommittedEntry(string threadId, AgentEntry entry)
    {
        _logger.LogWarning(
            "Pool disposal raced agent creation for thread {ThreadId}; disposing the uncommitted agent rather than leaking it",
            threadId
        );

        _ = Task.Run(async () =>
        {
            try
            {
                await entry.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose the uncommitted agent for thread {ThreadId}", threadId);
            }
        });
    }

    /// <summary>
    /// The awaited form of <see cref="AbandonUncommittedEntry"/>, for the async commit paths (mode /
    /// provider switch, sandbox refresh) that can wait for the orphan to be torn down before they
    /// surface the refusal. A teardown failure is logged, never rethrown: the caller is already going to
    /// throw <see cref="ObjectDisposedException"/>, and that is the more useful diagnosis.
    /// </summary>
    private async Task AbandonUncommittedEntryAsync(string threadId, AgentEntry entry)
    {
        _logger.LogWarning(
            "Pool disposal raced agent creation for thread {ThreadId}; disposing the uncommitted agent rather than leaking it",
            threadId
        );

        try
        {
            await entry.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose the uncommitted agent for thread {ThreadId}", threadId);
        }
    }

    private ObjectDisposedException DisposedException() => new(GetType().FullName);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        AgentEntry[] entries;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Snapshot AND clear under the same lock every commit takes. From here on a commit is
            // refused, so this array is provably the complete set of entries the pool ever published —
            // no later arrival can slip in behind the snapshot and be cleared away undisposed.
            entries = [.. _agents.Values];
            _agents.Clear();
        }

        _logger.LogInformation("Disposing agent pool with {Count} agents", entries.Length);

        // Signal all agents to stop
        await _poolCts.CancelAsync();

        // Dispose all agent entries
        var disposeTasks = entries.Select(async entry =>
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

        // Under the lock so it cannot race a creation reading _poolCts.Token (see CreateEntryCts).
        lock (_lifecycleLock)
        {
            _poolCts.Dispose();
        }

        _logger.LogInformation("Agent pool disposed");
    }
}
