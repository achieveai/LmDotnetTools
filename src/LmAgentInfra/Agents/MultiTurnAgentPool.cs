using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

/// <summary>
/// Pool manager for MultiTurnAgentLoop instances, keyed by threadId.
/// Creates agents on-demand and reuses them for the same thread.
/// Supports mode-aware agent creation with customizable system prompts and tool filtering.
/// </summary>
public sealed class MultiTurnAgentPool : IAsyncDisposable, IAgentRunActivityProbe, IInputAcceptanceObserver
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
    private bool _disposed;

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
    /// Everything a grantee handoff needs to decide, read from ONE entry under ONE lock (#418).
    /// </summary>
    /// <param name="OwnerUserId">The user the pooled entry is frozen to, or null.</param>
    /// <param name="CallerAppId">The app the pooled entry is frozen to, or null.</param>
    /// <param name="IsBusy">
    /// Whether the entry has work in hand: a run in progress, OR an input that has been accepted and
    /// not yet started. The second disjunct is the whole reason this is not
    /// <see cref="IsRunInProgress"/>.
    /// </param>
    /// <param name="EntryToken">
    /// Opaque identity of the entry these facts came from. Hand it back to
    /// <see cref="TryReleaseIdleAgentAsync"/> unchanged; nothing else may interpret it.
    /// </param>
    /// <remarks>
    /// A single value rather than three accessors because the three answers only mean anything
    /// together. The handoff used to take them as separate unlocked lookups, and an entry replaced
    /// between two of them produced a view of the thread that never existed - most sharply a null app
    /// id, which is indistinguishable from "never frozen" and is how the cross-app freeze got
    /// dropped.
    /// </remarks>
    public sealed record AgentHandoffState(
        string? OwnerUserId,
        string? CallerAppId,
        bool IsBusy,
        object EntryToken
    );

    /// <summary>What <see cref="TryReleaseIdleAgentAsync"/> actually did.</summary>
    public enum AgentReleaseOutcome
    {
        /// <summary>The observed entry was idle and has been removed and disposed.</summary>
        Released,

        /// <summary>No entry is pooled for the thread. Nothing was disposed.</summary>
        NotPooled,

        /// <summary>
        /// A DIFFERENT entry is pooled for the thread now, so the decision the caller made no longer
        /// applies to it. It is left alone - releasing it would destroy a live agent nobody decided
        /// anything about.
        /// </summary>
        Replaced,

        /// <summary>The entry has work in hand: a run in progress, or an accepted-but-unstarted input.</summary>
        Busy,
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
        /// The <c>{tid}:{oid}</c> of the end user that created this thread's agent - <c>null</c>
        /// for an app-only caller, and for every request made before P1. Set ONCE at creation and
        /// never reassigned, exactly like <see cref="CallerCredential"/>: this is the second,
        /// PARALLEL dimension of the freeze (P1 spec 7.6), and it is what makes the guard mean
        /// something in the UI, where today both sides of the app-id comparison are null and
        /// therefore always match.
        /// </summary>
        public string? OwnerUserId { get; init; }

        /// <summary>
        /// The conversation's sandbox-established binding for this (workspace-mode) entry, or <c>null</c>
        /// for a non-workspace entry. Carried from the factory's <see cref="AgentCreationResult.StagedBinding"/>
        /// and published to the <see cref="ISandboxBindingSink"/> right after this entry commits under the
        /// per-thread lock. A mode switch either restages a fresh binding (workspace target) or stages none
        /// (non-workspace target, leaving the prior binding untouched).
        /// </summary>
        public SandboxEstablishedBinding? EstablishedBinding { get; init; }

        /// <summary>
        /// The accepted-input ledger (#418): the ids of inputs this entry's agent has accepted and
        /// that no run has picked up yet. MUTABLE, and every read and write of it and of
        /// <see cref="IdleSinceUtc"/> happens under the entry's per-thread lock - unlike everything
        /// above them, which is frozen at creation.
        /// </summary>
        /// <remarks>
        /// It exists because "is this entry idle?" cannot be answered from
        /// <see cref="IMultiTurnAgent"/> alone: an input that has been accepted and not yet started
        /// leaves <see cref="IMultiTurnAgent.CurrentRunId"/> null and <c>IsRunning</c> false, which is
        /// indistinguishable from having nothing to do. It is a SET of ids rather than a flag because
        /// a flag cannot represent two accepts: the first run to start would clear the flag while the
        /// second input was still queued, and the entry would read idle with a turn still owed.
        /// </remarks>
        public readonly HashSet<string> OutstandingInputIds = new(StringComparer.Ordinal);

        /// <summary>
        /// When this entry was first OBSERVED not-in-progress while holding an accepted input, or null
        /// while it is in progress. Restarted on every in-progress observation, so the grace measures
        /// continuous idleness rather than time since the accept.
        /// </summary>
        public DateTimeOffset? IdleSinceUtc;

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
        )
    { }

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
        )
    { }

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
        )
    { }

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
    /// <param name="ownerUserId">
    /// <c>Principal.EffectiveUserId</c> of the caller making THIS request - <c>null</c> for an
    /// app-only caller. Frozen onto the entry on first creation and compared on every later call;
    /// a mismatch throws <see cref="PrincipalConflictException"/> (P1 spec 7.6).
    /// </param>
    /// <exception cref="PrincipalConflictException">
    /// Thrown when <paramref name="ownerUserId"/> differs from the user the thread's existing agent
    /// was created under.
    /// </exception>
    public IMultiTurnAgent GetOrCreateAgent(
        string threadId,
        AgentProfile mode,
        string? requestedProviderId,
        string? requestResponseDumpFileName,
        string? requestedWorkspaceId = null,
        SandboxCredential? callerCredential = null,
        string? ownerUserId = null
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
        var created = false;
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
                    callerCredential,
                    ownerUserId
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

                EnsurePrincipalMatches(threadId, existing, ownerUserId);

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
    /// <see cref="GetOrCreateAgent(string, AgentProfile, string?, string?, string?, SandboxCredential?, string?)"/>
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
        if (deferred.Any(d =>
            string.Equals(d.FunctionName, AskUserQuestionToolProvider.ToolName, StringComparison.Ordinal)))
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
    /// The user this thread's live agent is frozen to, or null when no agent is pooled for it or the
    /// entry was created without a principal.
    /// </summary>
    /// <remarks>
    /// A READ of the freeze, never an enforcement of it - <see cref="EnsurePrincipalMatches"/> stays
    /// the only thing that refuses. It exists so a caller that has ALREADY authorized this request
    /// can ask whose agent is in the way before it is thrown at, which is what lets a legitimate
    /// grantee be handed their own agent instead of a <c>409</c> (#376). Answering that question by
    /// catching the conflict instead would mean the pool decides an authorization outcome it has no
    /// way to evaluate.
    /// </remarks>
    /// <param name="threadId">The thread identifier.</param>
    public string? GetAgentOwnerUserId(string threadId)
    {
        return _agents.TryGetValue(threadId, out var entry) ? entry.OwnerUserId : null;
    }

    /// <summary>
    /// The app id this thread's live agent is frozen to, or null when no agent is pooled for it or the
    /// entry was created by a caller with no sandbox credential (every interactive UI caller).
    /// </summary>
    /// <remarks>
    /// The app-id sibling of <see cref="GetAgentOwnerUserId"/>, and a READ of the freeze for the same
    /// reason: <see cref="EnsureCallerMatches"/> stays the only thing that refuses. It exists because
    /// releasing a pooled entry for an authorized grantee (#376) removes the entry - and with it the
    /// <see cref="AgentEntry.CallerCredential"/> the app-id compare reads - so a caller that intends to
    /// release must be able to ask what the thread is frozen to BEFORE the removal makes the answer
    /// unavailable. Reading it afterwards would always answer null, which is indistinguishable from
    /// "never frozen" and is precisely how the freeze got dropped (#153).
    /// </remarks>
    /// <param name="threadId">The thread identifier.</param>
    public string? GetAgentCallerAppId(string threadId)
    {
        return _agents.TryGetValue(threadId, out var entry) ? entry.CallerCredential?.AppId : null;
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
        bool replace = true,
        string? ownerUserId = null
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
                throw new AgentNotPooledException(threadId);
            }

            EnsureCallerMatches(threadId, observed, callerCredential);
            EnsurePrincipalMatches(threadId, observed, ownerUserId);
        }

        if (
            _liveSessionResolver is null
            || observed.EstablishedBinding is not { SessionId.Length: > 0 } binding
        )
        {
            return new AgentRefreshResult(observed.Agent, AgentRefreshStatus.Current);
        }

        var liveSession = await _liveSessionResolver(binding, ct).ConfigureAwait(false);
        if (string.Equals(binding.SessionId, liveSession.SessionId, StringComparison.Ordinal))
        {
            return new AgentRefreshResult(observed.Agent, AgentRefreshStatus.Current);
        }

        AgentEntry? replacedEntry = null;
        AgentEntry current;
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out current!))
            {
                throw new AgentNotPooledException(threadId);
            }

            EnsureCallerMatches(threadId, current, callerCredential);
            EnsurePrincipalMatches(threadId, current, ownerUserId);

            if (!ReferenceEquals(current, observed))
            {
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.Current);
            }

            // The deferral asks "does this entry have work in hand?", not "is a run executing?" — and
            // those differ for exactly the window #418 is about. An input accepted and not yet picked
            // up leaves CurrentRunId null and IsRunning false, so IsEntryInProgress reads it as idle;
            // the refresh below then replaces _agents[threadId] and disposes the old entry, taking the
            // queued turn with it. Same read the handoff path uses, so a turn is never idle to one
            // caller and busy to the other.
            if (IsBusyUnderLock(current))
            {
                _logger.LogInformation(
                    "Deferring sandbox session refresh for thread {ThreadId} while it has work in hand "
                        + "(run {RunId}, {OutstandingInputs} accepted input(s) not yet started)",
                    threadId,
                    current.Agent.CurrentRunId,
                    current.OutstandingInputIds.Count
                );
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.RefreshDeferred);
            }

            if (!replace)
            {
                return new AgentRefreshResult(current.Agent, AgentRefreshStatus.RefreshRequired);
            }

            // Both the credential and the principal are frozen-at-creation facts, so both are read
            // off the entry being replaced. Deliberately NOT `?? ownerUserId`: this is a refresh,
            // not a swap, and adopting the caller's principal onto a previously unowned entry would
            // let whoever happens to trigger the refresh claim the thread (#398).
            var replacement = CreateAgentEntry(
                threadId,
                current.Mode,
                current.ProviderId,
                current.RequestResponseDumpFileName,
                current.WorkspaceId,
                current.CallerCredential,
                current.OwnerUserId
            );
            _agents[threadId] = replacement;
            PublishBindingIfStaged(threadId, replacement);
            replacedEntry = current;
            current = replacement;
        }

        _logger.LogInformation(
            "Refreshed sandbox-backed agent for thread {ThreadId} from session {PreviousSessionId} to {SessionId}",
            threadId,
            binding.SessionId,
            liveSession.SessionId
        );

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

        return new AgentRefreshResult(current.Agent, AgentRefreshStatus.Replaced);
    }

    private static void EnsureCallerMatches(
        string threadId,
        AgentEntry entry,
        SandboxCredential? callerCredential
    )
    {
        var existingAppId = entry.CallerCredential?.AppId;
        var requestedAppId = callerCredential?.AppId;
        if (!string.Equals(existingAppId, requestedAppId, StringComparison.Ordinal))
        {
            throw new SandboxCredentialConflictException(threadId, existingAppId, requestedAppId);
        }
    }

    /// <summary>
    /// The principal half of the freeze (P1 spec 7.6). Deliberately a SECOND check beside
    /// <see cref="EnsureCallerMatches"/> rather than a widening of it: the app-id freeze is the
    /// tenancy boundary between services, this is the boundary between people, and collapsing the
    /// two would let a matching app id excuse a mismatched user.
    /// </summary>
    /// <remarks>
    /// DEVIATION from 7.6's unqualified "on mismatch", argued in the PR body: a null on EITHER side
    /// is the absence of an assertion, not a second person, and does not conflict. The WebSocket
    /// transport asserts no principal in P1 (that is #345/#346, out of this slice), so a strict
    /// comparison would make the very first UI reconnect after any REST call throw - in the DEFAULT
    /// configuration - to enforce a rule whose own stated purpose is to stop two different humans
    /// sharing one live agent.
    /// </remarks>
    /// <remarks>
    /// This is not the vacuity 7.6 objects to. 7.6's complaint is that "today both sides are null
    /// and therefore always match"; under enforcement every <c>/api</c> request carries a principal,
    /// so both sides are populated and the case the spec names - user A's live agent, a request
    /// asserting user B - throws. Nor is anything authorized here: IResourceAccessPolicy runs on
    /// every route before the pool is reached, and a caller with no principal never gets past
    /// IdentityMiddleware while enforcement is on.
    /// </remarks>
    private static void EnsurePrincipalMatches(
        string threadId,
        AgentEntry entry,
        string? ownerUserId
    )
    {
        if (ownerUserId is null || entry.OwnerUserId is null)
        {
            return;
        }

        if (!string.Equals(entry.OwnerUserId, ownerUserId, StringComparison.Ordinal))
        {
            throw new PrincipalConflictException(threadId, entry.OwnerUserId, ownerUserId);
        }
    }

    private static bool IsEntryInProgress(AgentEntry entry)
    {
        var hasRunId = !string.IsNullOrWhiteSpace(entry.Agent.CurrentRunId);
        return hasRunId && entry.Agent.IsRunning && !entry.RunTask.IsCompleted;
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
    /// Clock behind <see cref="AcceptedInputGrace"/>. Injectable so a test can advance it rather than
    /// wait it out; production never sets it.
    /// </summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long an accepted-but-unstarted input keeps an otherwise idle entry from being released.
    /// </summary>
    /// <remarks>
    /// A backstop, not the mechanism. An id normally retires on the agent's OWN evidence: the
    /// <see cref="RunAssignmentMessage"/> that echoes it in <see cref="RunAssignment.InputIds"/> when
    /// a run picks it up (see <see cref="WatchDrainsAsync"/>). This covers the two cases that evidence
    /// never arrives for: an agent that accepted an input and then wedged, and a drain watcher that
    /// ended or was dropped for a slow read. Without it, either of those makes that conversation's
    /// every future handoff answer <c>409</c> forever, which trades a lost turn for a permanently
    /// unusable thread. The clock runs only while the entry is observed NOT in progress (see
    /// <see cref="IsBusyUnderLock"/>), so a turn queued behind a long-running one is never timed out
    /// by it however long that run takes.
    /// </remarks>
    internal static readonly TimeSpan AcceptedInputGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Records that an input has been accepted by <paramref name="threadId"/>'s agent and has not been
    /// started yet, so a concurrent handoff does not read the entry as idle and discard the turn
    /// (#418).
    /// </summary>
    /// <param name="threadId">The conversation whose agent took the input.</param>
    /// <param name="inputId">
    /// The id of the accepted input - the same id handed to <c>SendAsync</c>/<c>TrySendAsync</c>.
    /// </param>
    /// <param name="acceptedBy">The agent instance that actually accepted it.</param>
    /// <remarks>
    /// <para>
    /// PRIVATE since #442, and reached only through
    /// <see cref="IInputAcceptanceObserver.OnInputAccepted"/> - the accepting agent's own report. It
    /// used to be public so a host transport could record ahead of its own send, but the four sites
    /// that did are gone: they duplicated a fact the agent already reports, and a ledger maintained by
    /// a list of call sites is a ledger with a hole exactly the size of whatever is missing from that
    /// list. Three accept paths could never be on it (the two <c>SubAgentManager</c> parent relays and
    /// the collaboration write endpoint all live in <c>LmMultiTurn</c>, which this assembly depends
    /// on), which is what #434 was. Left public with no caller it would be a way to hold an entry busy
    /// for an accept no agent made - an id no run can ever name, and so one only the
    /// <see cref="AcceptedInputGrace"/> can clear.
    /// </para>
    /// <para>
    /// The report arrives BEFORE the input is enqueued, and is withdrawn (
    /// <see cref="RemoveOutstandingInput"/>) if the enqueue does not take. Recording afterwards would
    /// leave a window in which the input is already sitting in the agent's channel and not yet in this
    /// ledger - the same hole this exists to close, only narrower.
    /// </para>
    /// <para>
    /// The pool has to keep this itself because the fact it needs is not on
    /// <see cref="IMultiTurnAgent"/>: an agent exposes <see cref="IMultiTurnAgent.CurrentRunId"/>,
    /// which is null precisely while an input sits queued, and its pending-input count is not part of
    /// the interface. So the pool records the accepted id and retires it when the agent itself says a
    /// run picked that id up (<see cref="WatchDrainsAsync"/>).
    /// </para>
    /// <para>
    /// Recording the ID, not a flag, is what makes two accepts representable. With a flag, the first
    /// run to start clears it while the second input is still queued and the entry reads idle with a
    /// turn still owed - a handoff then disposes the agent and that turn is lost.
    /// </para>
    /// <para>
    /// <paramref name="acceptedBy"/> is compared by reference against the pooled agent, and a
    /// mismatch is REFUSED (<see langword="false"/>), not merely ignored. Recording it would mark
    /// whatever entry happens to be pooled NOW, which after a concurrent refresh or mode swap is a
    /// DIFFERENT agent that never saw the input: the replacement would be held busy for work it does
    /// not have, and the entry that actually holds the turn would not be held at all.
    /// </para>
    /// <para>
    /// Refusing rather than ignoring is what closes the last hole (#442). This runs under the SAME
    /// per-thread lock a swap holds, so a report can be parked here while the conversation's agent is
    /// replaced underneath it. Ignoring the mismatch left the reporting agent free to complete its
    /// enqueue: the turn landed in an agent already being torn down, was in no ledger, and its sender
    /// held a receipt for it. The refusal travels back up the send path and fails it instead, so the
    /// caller learns immediately and a retry reaches the replacement.
    /// </para>
    /// <para>
    /// <see langword="true"/> for a thread with no pooled entry, and NOT a refusal: the pool has no
    /// grounds to contradict an agent it does not track, and refusing there would break every sender
    /// of an unpooled agent that happens to report here. An entry created later starts with a clean
    /// ledger, which is correct - an input accepted by an agent that has since been replaced is not
    /// work the replacement holds.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> only when this thread's pooled entry names a different agent, which
    /// means the accept must not proceed. <see langword="true"/> in every other case, including the
    /// argument guards and a thread with no entry.
    /// </returns>
    private bool AddOutstandingInput(string threadId, string inputId, IMultiTurnAgent acceptedBy)
    {
        if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(inputId) || acceptedBy is null)
        {
            // A malformed report is not evidence about which agent the thread holds, so it cannot be
            // a refusal. It records nothing and lets the send through, exactly as before.
            return true;
        }

        var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out var entry))
            {
                return true;
            }

            if (!ReferenceEquals(entry.Agent, acceptedBy))
            {
                _logger.LogWarning(
                    "Refusing input {InputId} for thread {ThreadId}: the accepting agent is no longer "
                        + "the pooled one, so the turn would be queued in an agent being torn down",
                    inputId,
                    threadId
                );
                return false;
            }

            entry.OutstandingInputIds.Add(inputId);
            entry.IdleSinceUtc = null;
            return true;
        }
    }

    /// <summary>
    /// Withdraws an id added by <see cref="AddOutstandingInput"/> when the hand-over it was recorded
    /// for did not happen - a full input channel, or a throwing send.
    /// </summary>
    /// <remarks>
    /// The partner of recording BEFORE the send, and the reason recording early costs nothing.
    /// Without it a refused send leaves an id nothing can ever retire - no run will name an input the
    /// agent never received - so the conversation reads busy until the grace expires: thirty seconds
    /// of refused handoffs bought for a turn that was never queued. Reference-checked exactly like
    /// the add, so a rollback cannot reach past a replacement and clear work the new entry holds.
    /// </remarks>
    private void RemoveOutstandingInput(string threadId, string inputId, IMultiTurnAgent acceptedBy)
    {
        if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(inputId) || acceptedBy is null)
        {
            return;
        }

        var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
        lock (lockObj)
        {
            if (
                _agents.TryGetValue(threadId, out var entry)
                && ReferenceEquals(entry.Agent, acceptedBy)
                && entry.OutstandingInputIds.Remove(inputId)
                && entry.OutstandingInputIds.Count == 0
            )
            {
                entry.IdleSinceUtc = null;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The agent's own report of an accept, and since #442 the ledger's ONLY source. It fires for
    /// every accept taken through the two places <c>MultiTurnAgentBase</c> mints a receipt id -
    /// <c>SendAsync</c> and <c>TrySendAsync</c> - which is every accept on the public send path,
    /// including the three that live in <c>LmMultiTurn</c> and cannot reach this pool at all (see
    /// <see cref="AddOutstandingInput"/>'s remarks). It is NOT every enqueue: a derived loop's
    /// internal raw enqueues bypass both mint sites and therefore this observer, which
    /// <c>MultiTurnAgentBase.InputAcceptanceObserver</c>'s remarks itemise. That is what the pool
    /// refusing a non-<see cref="IAcceptanceReportingAgent"/> agent buys - completeness over the
    /// accepts an agent can report, not over every way an input can reach a channel.
    /// </remarks>
    bool IInputAcceptanceObserver.OnInputAccepted(string threadId, string inputId, IMultiTurnAgent acceptedBy) =>
        AddOutstandingInput(threadId, inputId, acceptedBy);

    /// <inheritdoc />
    /// <remarks>
    /// The rollback partner of <see cref="IInputAcceptanceObserver.OnInputAccepted"/>, mapping onto
    /// the same withdrawal a transport performs for a refused send.
    /// </remarks>
    void IInputAcceptanceObserver.OnInputAcceptanceRescinded(
        string threadId,
        string inputId,
        IMultiTurnAgent acceptedBy
    ) => RemoveOutstandingInput(threadId, inputId, acceptedBy);

    /// <summary>
    /// Retires accepted-input ids for <paramref name="threadId"/> once the agent reports a run has
    /// picked them up. Runs for the life of one entry; ends when that entry's token is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The evidence is the agent's own <see cref="RunAssignmentMessage"/>, which echoes the ids the
    /// assignment consumed. That is exact and needs no arithmetic: an id that appears there has left
    /// the input channel. The guarantee is therefore about the TURN, not about continuous busyness -
    /// an id retires only once a run has actually TAKEN it, so an accepted turn is never released out
    /// from under its sender. It is NOT true that either the ledger or the run always marks the entry
    /// busy: this reads the assignment off an async channel, so a short run can complete before the
    /// watcher observes it (<c>MultiTurnAgentBase</c> nulls the run id at completion, and the agent
    /// loops publish assignments on a run's final steps). What that costs is a briefly
    /// over-conservative read - the id lingers - never a lost turn.
    /// </para>
    /// <para>
    /// Subscribing cannot backpressure the agent: <c>PublishToAllAsync</c> writes to each subscriber
    /// non-blocking and DROPS one whose channel is full. A dropped or ended watcher therefore costs
    /// retirement-by-evidence, not liveness, and <see cref="AcceptedInputGrace"/> is the backstop for
    /// exactly that.
    /// </para>
    /// </remarks>
    private async Task WatchDrainsAsync(string threadId, IMultiTurnAgent agent, CancellationToken ct)
    {
        try
        {
            await foreach (var message in agent.SubscribeAsync(ct).ConfigureAwait(false))
            {
                if (message is not RunAssignmentMessage assignment)
                {
                    continue;
                }

                var assignedIds = assignment.Assignment.InputIds;
                if (assignedIds is not { Count: > 0 })
                {
                    continue;
                }

                var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
                lock (lockObj)
                {
                    // Same reference check AddOutstandingInput makes, and for the same reason: this
                    // watcher outlives nothing, but the entry it was started for can be replaced
                    // while a message is in flight, and retiring ids off the replacement would clear
                    // a turn that agent never accepted.
                    if (
                        !_agents.TryGetValue(threadId, out var entry)
                        || !ReferenceEquals(entry.Agent, agent)
                        || entry.OutstandingInputIds.Count == 0
                    )
                    {
                        continue;
                    }

                    foreach (var assignedId in assignedIds)
                    {
                        entry.OutstandingInputIds.Remove(assignedId);
                    }

                    if (entry.OutstandingInputIds.Count == 0)
                    {
                        entry.IdleSinceUtc = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The entry was disposed or the pool shut down.
        }
        catch (Exception ex)
        {
            // Only a FAULTED watcher reaches here. The other two ways it stops are silent by
            // construction: a stream that simply ends, and a subscriber the agent drops for reading
            // too slowly. Neither raises, so neither is logged - the absence of this warning is not
            // evidence the watcher is alive. Either way the cost is retirement-by-evidence for this
            // one entry rather than correctness, because the grace backstop still releases it.
            _logger.LogWarning(
                ex,
                "Accepted-input drain watcher for thread {ThreadId} ended; falling back to the {Grace} grace",
                threadId,
                AcceptedInputGrace
            );
        }
    }

    /// <summary>
    /// Reads everything a grantee handoff decides on - owner, frozen app id, and whether the entry has
    /// work in hand - from ONE entry under ONE lock (#418).
    /// </summary>
    /// <param name="threadId">The conversation.</param>
    /// <param name="state">The facts, valid only as the input to <see cref="TryReleaseIdleAgentAsync"/>.</param>
    /// <returns><see langword="true"/> when an entry is pooled for the thread.</returns>
    /// <remarks>
    /// Returning <see langword="false"/> for an absent entry is load-bearing and is not the same as
    /// returning a state with a null app id. The accessors this replaces
    /// (<see cref="GetAgentOwnerUserId"/>, <see cref="GetAgentCallerAppId"/>) answer null for both
    /// "no entry" and "no credential", so a caller could not tell a thread that vanished from one
    /// created by an interactive caller - and the cross-app compare then either admitted a handoff it
    /// should have refused or refused one it should have admitted.
    /// </remarks>
    public bool TryGetHandoffState(string threadId, out AgentHandoffState state)
    {
        state = null!;
        if (string.IsNullOrEmpty(threadId))
        {
            return false;
        }

        var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out var entry))
            {
                return false;
            }

            state = new AgentHandoffState(
                entry.OwnerUserId,
                entry.CallerCredential?.AppId,
                IsBusyUnderLock(entry),
                entry
            );
            return true;
        }
    }

    /// <summary>
    /// Removes and disposes the entry <paramref name="observed"/> describes, but ONLY if that same
    /// entry is still pooled and still idle - and says which of those it found (#418).
    /// </summary>
    /// <param name="threadId">The conversation.</param>
    /// <param name="observed">The state <see cref="TryGetHandoffState"/> returned for this thread.</param>
    /// <returns>What was actually done, which the caller answers on instead of on its own stale read.</returns>
    /// <remarks>
    /// <para>
    /// This exists because the decision and the removal used to be two steps with nothing holding the
    /// entry between them: a caller asked <see cref="IsRunInProgress"/>, and
    /// <see cref="RemoveAgentAsync"/> then disposed whatever the dictionary held, re-checking nothing.
    /// A run that started in the gap was aborted anyway - the outcome the check exists to prevent -
    /// and an entry that had been REPLACED in the gap was destroyed although nobody had decided
    /// anything about it.
    /// </para>
    /// <para>
    /// The re-validation compares entry IDENTITY and nothing else, deliberately. An entry's owner and
    /// caller credential are frozen at creation and never reassigned, so re-comparing them here would
    /// be a second conjunct that can never independently fail - and two conjuncts that cannot fail
    /// apart make each other's mutations pass.
    /// </para>
    /// <para>
    /// Disposal happens outside the lock, because it awaits. What the lock covers is the decision and
    /// the removal, which is what makes them one step: after the removal commits, a concurrent
    /// creation observes an empty slot and builds a fresh entry rather than handing out one that is
    /// being torn down.
    /// </para>
    /// </remarks>
    public async ValueTask<AgentReleaseOutcome> TryReleaseIdleAgentAsync(
        string threadId,
        AgentHandoffState observed
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(observed);

        AgentEntry removed;
        var lockObj = _creationLocks.GetOrAdd(threadId, static _ => new object());
        lock (lockObj)
        {
            if (!_agents.TryGetValue(threadId, out var entry))
            {
                return AgentReleaseOutcome.NotPooled;
            }

            if (!ReferenceEquals(entry, observed.EntryToken))
            {
                return AgentReleaseOutcome.Replaced;
            }

            if (IsBusyUnderLock(entry))
            {
                return AgentReleaseOutcome.Busy;
            }

            // The keyed overload, so a removal that lost a race to a concurrent RemoveAgentAsync
            // takes nothing with it.
            if (!_agents.TryRemove(new KeyValuePair<string, AgentEntry>(threadId, entry)))
            {
                return AgentReleaseOutcome.Replaced;
            }

            removed = entry;
        }

        _logger.LogInformation(
            "Released the idle pooled agent for thread {ThreadId} to an authorized caller",
            threadId
        );

        try
        {
            await removed.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Same compare-and-clear RemoveAgentAsync does, and for the same reason: a concurrent
            // creation that republished a binding while this was disposing must keep it.
            lock (lockObj)
            {
                if (!_agents.ContainsKey(threadId))
                {
                    _bindingSink?.ClearEstablishedBinding(threadId);
                }
            }
        }

        RaiseThreadRemoved(threadId);
        return AgentReleaseOutcome.Released;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> has work in hand. MUST be called under the entry's per-thread
    /// lock: it both reads and retires the accepted-input ledger.
    /// </summary>
    private bool IsBusyUnderLock(AgentEntry entry)
    {
        if (IsEntryInProgress(entry))
        {
            // The grace clock measures continuous idleness, so an observation that finds a run
            // running restarts it. Without this a turn queued behind a ten-minute run would have its
            // marker expire during that run and the hole would reopen the moment the run ended.
            entry.IdleSinceUtc = null;
            return true;
        }

        if (entry.OutstandingInputIds.Count == 0)
        {
            return false;
        }

        var now = TimeProvider.GetUtcNow();
        entry.IdleSinceUtc ??= now;
        if (now - entry.IdleSinceUtc.Value < AcceptedInputGrace)
        {
            return true;
        }

        _logger.LogWarning(
            "Thread {ThreadId} has {OutstandingInputs} accepted input(s) that never started a run within "
                + "{Grace}; releasing the entry rather than refusing every future handoff for it",
            entry.Agent.ThreadId,
            entry.OutstandingInputIds.Count,
            AcceptedInputGrace
        );
        ClearAcceptedInput(entry);
        return false;
    }

    private static void ClearAcceptedInput(AgentEntry entry)
    {
        entry.OutstandingInputIds.Clear();
        entry.IdleSinceUtc = null;
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
    /// <param name="ownerUserId">
    /// <c>Principal.EffectiveUserId</c> of the caller requesting the switch, or <c>null</c> for an
    /// app-only caller. Validated against the user the conversation was frozen to at creation; the
    /// frozen owner itself is preserved across the swap - this parameter only authorizes the switch.
    /// </param>
    /// <returns>The new agent for this thread</returns>
    /// <exception cref="SandboxCredentialConflictException">
    /// Thrown when <paramref name="callerCredential"/>'s <c>AppId</c> differs from the app id the
    /// conversation is bound to.
    /// </exception>
    public async Task<IMultiTurnAgent> RecreateAgentWithModeAsync(
        string threadId,
        AgentProfile mode,
        SandboxCredential? callerCredential = null,
        string? ownerUserId = null
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
            callerCredential,
            ownerUserId
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
    /// <param name="ownerUserId">
    /// <c>Principal.EffectiveUserId</c> of the caller requesting the switch, or <c>null</c> for an
    /// app-only caller. Validated against the user the conversation was frozen to at creation; the
    /// frozen owner itself is preserved across the swap - this parameter only authorizes the switch.
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
        SandboxCredential? callerCredential = null,
        string? ownerUserId = null
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
            callerCredential,
            ownerUserId
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
    /// <remarks>
    /// <para>
    /// <b>A switch discards in-flight work, deliberately, and that includes an accepted-but-unstarted
    /// input.</b> This is the third place the "does the entry have work in hand?" question could be
    /// asked (the other two - the grantee handoff and the sandbox session refresh - both ask it and
    /// both refuse). This one does not ask: it builds the replacement, swaps it in and disposes the
    /// old entry, and the old agent's input channel goes with it.
    /// </para>
    /// <para>
    /// That is a decision rather than an oversight, and the reason is the kind of caller. A handoff
    /// and a session refresh are INCIDENTAL to the person whose turn is queued - another actor, or
    /// infrastructure - so silently dropping their turn is a loss they neither asked for nor can see.
    /// A mode or provider switch is the same conversation's own explicit request, and it already
    /// discards a STREAMING run without asking (there is no in-progress check here either). Refusing
    /// only for a queued input would make the pool stricter about a turn that has not started than
    /// about one actively producing tokens, which is not a line worth drawing.
    /// </para>
    /// <para>
    /// The replacement deliberately does NOT inherit <see cref="AgentEntry.OutstandingInputIds"/>.
    /// Carrying them would be a lie: the replacement's input channel does not hold those inputs, so
    /// the ids could never be retired by evidence and the new entry would read busy for the whole
    /// grace and then clear - with the turn just as lost, and thirty seconds of refused handoffs
    /// added on top. Pinned by
    /// <c>SwitchingMode_DiscardsAQueuedTurn_AndDoesNotCarryItToTheReplacement</c> so the behaviour
    /// has to be changed on purpose rather than drifted into.
    /// </para>
    /// </remarks>
    private async Task<AgentEntry> SwapAgentUnderLockAsync(
        string threadId,
        AgentProfile mode,
        string providerId,
        string? workspaceId,
        string switchKind,
        SandboxCredential? callerCredential = null,
        string? ownerUserId = null
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
            _ = _agents.TryGetValue(threadId, out var existingEntry);
            var frozenCredential = existingEntry?.CallerCredential;

            // Same reason the credential is peeked rather than taken from the request: a switch is
            // neither a create nor a cross-actor request, so it must not change (or drop) the user
            // the thread is bound to. When there is no existing entry the recreate binds to the
            // caller as the new owner, exactly as a fresh create would.
            var frozenOwnerUserId = existingEntry?.OwnerUserId ?? ownerUserId;

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

                EnsurePrincipalMatches(threadId, existingEntry, ownerUserId);
            }

            // Construct BEFORE evicting — a throw here leaves the current agent registered (the thread
            // is untouched) rather than stranding the conversation with no pooled agent.
            entry = CreateAgentEntry(
                threadId,
                mode,
                providerId,
                requestResponseDumpFileName: null,
                workspaceId,
                frozenCredential,
                frozenOwnerUserId
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

    /// <summary>
    /// Tears down an agent the pool refused to register, and anything the factory created alongside
    /// it. Never throws: the caller is already failing the creation with a diagnosis, and a secondary
    /// disposal fault must not replace it.
    /// </summary>
    private async Task DiscardRefusedAgentAsync(
        string threadId,
        IMultiTurnAgent agent,
        IReadOnlyList<IAsyncDisposable>? ownedResources
    )
    {
        try
        {
            await agent.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to dispose the refused agent for thread {ThreadId}",
                threadId
            );
        }

        foreach (var resource in ownedResources ?? [])
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to dispose a resource owned by the refused agent for thread {ThreadId}",
                    threadId
                );
            }
        }
    }

    private AgentEntry CreateAgentEntry(
        string threadId,
        AgentProfile mode,
        string providerId,
        string? requestResponseDumpFileName,
        string? workspaceId,
        SandboxCredential? callerCredential = null,
        string? ownerUserId = null
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

        // FAIL CLOSED on an agent that cannot report its own accepts (#442). This is the ONLY moment
        // the pool can detect the condition. Nothing calls the pool at accept time any more — the four
        // synchronous host AddOutstandingInput sites are gone, because a ledger maintained by a list of
        // call sites is a ledger with a hole exactly the size of whatever is missing from the list, and
        // three accept paths (the sub-agent relays and the collaboration write) live in LmMultiTurn and
        // could never be on it. So an agent that announces nothing is not "less covered", it is
        // uncovered: its accepted-but-unstarted turns are invisible, the entry reads idle, and the
        // first symptom is a grantee handoff disposing the agent with the turn still queued (#418).
        //
        // Refusing HERE turns that silent, racy loss into a deterministic failure in whatever wired the
        // factory, on the first conversation, naming the type that has to change.
        if (agent is not IAcceptanceReportingAgent)
        {
            // The factory already built an agent (and possibly a sandbox session) before we could
            // check. Tear it down rather than leak it — best-effort and off the caller's thread,
            // because disposal awaits and this method is synchronous by contract (it runs under the
            // per-thread creation lock).
            _ = DiscardRefusedAgentAsync(threadId, agent, result.OwnedResources);

            throw new InvalidOperationException(
                $"The agent factory produced {agent.GetType().Name} for thread '{threadId}', which does "
                    + $"not implement {nameof(IAcceptanceReportingAgent)}. A pooled agent must report its "
                    + "own input acceptances: the pool's accepted-input ledger has no other source, and "
                    + "an unreported accept is released out from under its sender by the next handoff. "
                    + $"Derive from {nameof(MultiTurnAgentBase)}, or implement "
                    + $"{nameof(IAcceptanceReportingAgent)} and report from every accept path."
            );
        }

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

        var entry = new AgentEntry
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
            OwnerUserId = ownerUserId,
            EstablishedBinding = result.StagedBinding,
        };

        // Started here rather than lazily on the first accept so the subscription is in place BEFORE
        // any input can be accepted: a watcher attached after the fact could miss the assignment that
        // retires the very id that started it, and the ledger would then sit on the grace backstop.
        //
        // Discarded rather than held on the entry. Cancelling Cts during disposal is what ends it, and
        // nothing may await it: the watcher takes the per-thread lock, so awaiting it from a caller
        // that holds that lock would deadlock. A field nobody reads would only imply otherwise.
        _ = WatchDrainsAsync(threadId, agent, cts.Token);

        // Attached here, beside the drain watcher and for the same reason: this runs before the entry
        // is published to _agents, so the agent cannot yet be reached by any sender and therefore
        // cannot have accepted anything this pool would miss. An agent attached after the fact could
        // have taken a turn nobody recorded, which is the hole (#434) rather than the fix.
        //
        // LOCK ORDER. This runs under the per-thread creation lock (every CreateAgentEntry caller
        // holds it), and the reports it enables take that SAME lock from the sending caller's thread.
        // That is safe in one direction only, and the direction holds: no path in this pool calls a
        // send on an agent, and the two paths that call INTO agent code at all - AgentEntry.DisposeAsync
        // (StopAsync/DisposeAsync) and the release/swap teardown - both do so OUTSIDE the lock,
        // precisely because they await. So the only edge is send -> per-thread lock, and there is no
        // second edge to invert against. The factory below is the one call into foreign code made
        // while holding the lock; it constructs an agent, and a factory that somehow sent into that
        // half-built agent would re-enter this lock on its own thread (Monitor is reentrant) and find
        // no entry published yet, so the report would be a no-op rather than a deadlock.
        //
        // A direct cast, not a type test: the fail-closed check at the top of this method already
        // refused anything that is not IAcceptanceReportingAgent, so there is no "attaches to some
        // agents" case left to express. Reporting used to be a capability the pool tolerated the
        // absence of; since #442 it is the ledger's only source, so it is a condition of being pooled.
        ((IAcceptanceReportingAgent)agent).InputAcceptanceObserver = this;

        return entry;
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
