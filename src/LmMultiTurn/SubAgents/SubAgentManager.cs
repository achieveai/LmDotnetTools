using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Read-only, point-in-time snapshot of a single registered sub-agent, for presentation seams
/// (e.g. a UI listing children before resolving one for subscription). Carries no live handles and
/// is safe to hand to callers outside the SubAgents module.
/// </summary>
/// <param name="AgentId">Stable id of the sub-agent.</param>
/// <param name="Name">Optional caller-supplied name, or null if none was provided at spawn.</param>
/// <param name="TemplateName">Name of the template the sub-agent was spawned from.</param>
/// <param name="Task">The task the sub-agent was spawned with.</param>
/// <param name="Status">Lifecycle status at the moment the snapshot was taken.</param>
/// <param name="ThreadId">The sub-agent's conversation thread id.</param>
/// <param name="LastActivityUtc">Timestamp of the newest buffered turn, or null when no turn has
/// been recorded yet.</param>
/// <param name="TerminalAtUtc">UTC instant the sub-agent reached a terminal status, captured once at transition.</param>
/// <param name="EffectiveModelId">Concrete model selected after applying spawn, template, and parent precedence.</param>
/// <param name="EffectiveModelIntelligence">Tier that selected the model, or null for non-tier selection.</param>
/// <param name="ModelSelectionSource">Stable label identifying the winning selection input.</param>
public sealed record SubAgentSnapshot(
    string AgentId,
    string? Name,
    string TemplateName,
    string Task,
    SubAgentStatus Status,
    string ThreadId,
    DateTimeOffset? LastActivityUtc,
    DateTimeOffset? TerminalAtUtc = null,
    string? EffectiveModelId = null,
    int? EffectiveModelIntelligence = null,
    string ModelSelectionSource = "unknown");

/// <summary>Final model-routing decision captured when a sub-agent provider is built.</summary>
public sealed record SubAgentModelRouting(
    string? EffectiveModelId,
    int? EffectiveModelIntelligence,
    string SelectionSource);

/// <summary>
/// Manages sub-agent lifecycle: spawning, monitoring, resuming, and disposal.
/// Coordinates concurrency and relays completion results back to the parent agent.
/// </summary>
public sealed class SubAgentManager : IAsyncDisposable
{
    private readonly IMultiTurnAgent _parentAgent;
    private readonly string? _parentModelId;
    private readonly int? _parentMaxToken;
    private readonly IReadOnlyList<FunctionContract> _parentContracts;
    private readonly IDictionary<string, ToolHandler> _parentHandlers;
    private readonly SubAgentOptions _options;

    /// <summary>
    /// The options handed to each spawned child's own loop: this manager's options minus the spawn
    /// authority that belongs to THIS level only. Computed once because it is the same for every child.
    /// </summary>
    private readonly SubAgentOptions _childOptions;
    private readonly MutableSubAgentTemplateSource _source;
    private readonly ILogger _logger;
    private readonly MultiTurnLifecycleServices _lifecycleServices;

    // Optional root-conversation usage sink. When set, every UsageMessage a sub-agent (or workflow
    // task — same relay path) emits is folded into the root conversation's usage total (issue #196).
    // Null keeps the historical behaviour (descendant usage not aggregated).
    private readonly IUsageSink? _usageSink;

    // Optional durable-persist callback invoked after a descendant observation, so late/background
    // descendant usage is persisted immediately instead of waiting for a future primary usage event.
    private readonly Func<Task>? _persistUsageAsync;

    // Root-conversation delivery target for a descendant's parked AskUserQuestion (#246). Always
    // non-null — MultiTurnAgentLoop resolves a default (its own DeliverClientNotificationAsync) before
    // constructing this manager, so every level has a live delegate to call, whether it points at
    // itself (this manager's owner IS the root) or was threaded through from further up.
    private readonly Func<NotifyMessage, CancellationToken, ValueTask> _descendantQuestionSink;

    private readonly ConcurrentDictionary<string, SubAgentState> _agents = new();
    private readonly ConcurrentDictionary<string, string> _namesToIds = new();
    private readonly SemaphoreSlim _concurrencyGate;
    private int _disposeStarted;

    // Defer-queue for spawns that arrive when the concurrency pool is full: rather than REJECTING a
    // spawn with "Max concurrent reached", SpawnAsync enqueues it here and a single background pump
    // (_pumpTask) starts each queued spawn FIFO as a permit frees. _queueSignal counts pending items so
    // the pump parks (no busy-wait) until there is work; _pumpCts ends the pump at manager disposal.
    // Enqueue never holds a permit, so a permit-holding parent can never deadlock waiting on a child's
    // permit — the deadlock the old 5s-timeout valve guarded against cannot arise here (each loop owns
    // its own manager + gate, so this gate never has a permit-holder blocked awaiting itself).
    private readonly ConcurrentQueue<QueuedSpawn> _spawnQueue = new();
    private readonly ConcurrentDictionary<string, QueuedSpawn> _queuedSpawns = new();
    private readonly ConcurrentDictionary<string, string> _queuedNamesToIds = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;

    // Owned providers whose terminal disposal failed AND whose in-restart retry also failed: the state's
    // OwnedProviderAgent slot is about to be overwritten by the replacement, so their handle is retained
    // here for a best-effort final dispose at manager teardown rather than being silently abandoned.
    private readonly ConcurrentBag<IStreamingAgent> _abandonedProviders = [];

    /// <summary>
    /// Test-only seam: when set, <see cref="CreateSubAgentAsync"/> returns this factory's
    /// <see cref="IMultiTurnAgent"/> instead of building a real <see cref="MultiTurnAgentLoop"/>,
    /// so a unit test can substitute a fake agent (e.g. one whose <c>SubscribeAsync</c> throws a
    /// non-cancellation exception) while still going through the real <see cref="SpawnAsync"/>/
    /// <see cref="MonitorSubAgentAsync"/> plumbing (real gate acquisition, real monitor task).
    /// That is needed to exercise <see cref="MonitorSubAgentAsync"/>'s defensive terminal
    /// <c>catch (Exception)</c> path: every exception a real turn execution raises is already
    /// caught by <see cref="MultiTurnAgentLoop"/>'s own per-run try/catch and surfaces as a normal
    /// <c>RunCompletedMessage(IsError: true)</c>, which the (already-correct) error branch of
    /// <see cref="HandleRunCompletionAsync"/> resolves - so that path can't organically reach the
    /// monitor's own terminal catch. Null (the default) preserves normal production behavior;
    /// production code never sets this.
    /// </summary>
    internal Func<string, SubAgentTemplate, IMultiTurnAgent>? TestAgentFactoryOverride { get; set; }

    /// <summary>
    /// Test-only companion to <see cref="TestAgentFactoryOverride"/>: when set, supplies the OWNED
    /// provider agent returned alongside the fake loop, so a unit test can exercise the real
    /// owned-provider disposal lifecycle (completion disposal, pending-message deferral, restart
    /// recreation) that the plain <see cref="TestAgentFactoryOverride"/> path — which returns a null
    /// owned provider — cannot. Null (the default) keeps the fake loop's owned provider null.
    /// </summary>
    internal Func<string, SubAgentTemplate, IStreamingAgent?>? TestOwnedProviderOverride { get; set; }

    /// <summary>
    /// Test-only companion to <see cref="TestAgentFactoryOverride"/> supplying the child's conversation store.
    /// </summary>
    internal Func<string, SubAgentTemplate, IConversationStore?>? TestConversationStoreOverride { get; set; }

    /// <summary>Test-only barrier immediately before the shutdown-serialized registration commit.</summary>
    internal Func<Task>? TestBeforeAgentRegistrationAsync { get; set; }

    /// <summary>
    /// The parent agent's handle on the collaboration, or null when collaboration is off. Null is the
    /// feature gate: every collaboration branch in this manager is skipped and the legacy behaviour —
    /// per-manager limits only, one ordinary nesting level, no directory — is unchanged.
    /// </summary>
    public AgentCollaborationSetup? Collaboration { get; }

    /// <summary>
    /// Per-agent collaboration bookkeeping, keyed by agent id.
    /// </summary>
    /// <remarks>
    /// Kept beside the spawn rather than threaded through <see cref="StartWithHeldPermitAsync"/>,
    /// <see cref="QueuedSpawn"/>, and <see cref="SubAgentState"/> because the inline path, the defer
    /// queue, and the restart path all need the same two values at different times, and every one of
    /// them already knows the agent id. Empty whenever collaboration is off.
    /// </remarks>
    private readonly ConcurrentDictionary<string, SubAgentAdmission> _admissions = new(
        StringComparer.Ordinal);

    /// <summary>What the collaboration granted a spawn: the child's own handle, and its capacity slot.</summary>
    private sealed record SubAgentAdmission(AgentCollaborationSetup Child, AgentCapacityLease Lease);

    /// <summary>
    /// The child's handle on the collaboration, or null when collaboration is off or the id is unknown.
    /// </summary>
    internal AgentCollaborationSetup? GetChildCollaboration(string agentId) =>
        _admissions.TryGetValue(agentId, out var admission) ? admission.Child : null;

    /// <summary>
    /// Asks the collaboration to admit a spawn, reserving its capacity slot and publishing it in the
    /// directory so it is addressable from the moment the spawn is accepted.
    /// </summary>
    /// <remarks>
    /// No-op when collaboration is off. Registration happens HERE rather than in the child's own loop
    /// because the directory takes an agent's endpoint at registration time, and only this manager can
    /// deliver into a sub-agent whose lifecycle it owns.
    /// </remarks>
    /// <exception cref="SubAgentCollaborationException">The collaboration refused the spawn.</exception>
    private void AdmitToCollaboration(
        string agentId,
        string effectiveName,
        string templateName,
        SubAgentTemplate template,
        string? role,
        string? description)
    {
        if (Collaboration is not { } parent)
        {
            return;
        }

        if (!parent.CanDelegate)
        {
            throw new SubAgentCollaborationException(
                SubAgentCollaborationFailureCodes.DepthLimit,
                $"Maximum delegation depth ({parent.Options.MaxDelegationDepth}) reached. "
                    + "This agent cannot spawn sub-agents; do the work itself or report back.");
        }

        // Precedence, strongest first. (1) A role-fixed template owns its own label so a spawning LLM
        // cannot relabel it into something the directory would then advertise inaccurately to every other
        // agent. (2) A host that authored the delegation — a workflow controller spawning a defined task —
        // supplies trusted metadata, so the directory describes what was actually delegated rather than
        // what the tool-calling model chose to type. (3) Otherwise the caller-supplied values stand.
        // A conflicting caller role for a fixed template is rejected instead of silently discarded.
        var trusted = _options.SpawnMetadataResolver?.Invoke(effectiveName);
        var roleIsFixed = template.RoleMode == SubAgentRoleMode.Fixed;
        if (
            roleIsFixed
            && trusted is null
            && !string.IsNullOrWhiteSpace(role)
            && !string.Equals(role, template.Role, StringComparison.Ordinal)
        )
        {
            throw new SubAgentCollaborationException(
                SubAgentCollaborationFailureCodes.InvalidRole,
                $"Template '{templateName}' pins its own role and cannot be relabelled. "
                    + "Omit the 'role' parameter, or spawn from a template that allows one.");
        }

        var effectiveRole = roleIsFixed ? template.Role : trusted?.Role ?? role;
        if (string.IsNullOrWhiteSpace(effectiveRole))
        {
            throw new SubAgentCollaborationException(
                SubAgentCollaborationFailureCodes.InvalidRole,
                roleIsFixed
                    ? $"Template '{templateName}' pins its own role but declares none."
                    : "The 'role' parameter is required while collaboration is enabled.");
        }

        var effectiveDescription = trusted?.Description ?? description;
        if (string.IsNullOrWhiteSpace(effectiveDescription))
        {
            throw new SubAgentCollaborationException(
                SubAgentCollaborationFailureCodes.InvalidDescription,
                "The 'description' parameter is required while collaboration is enabled: other agents "
                    + "use it to decide whether to contact this one.");
        }

        AgentCollaborationContext childContext;
        try
        {
            // A spawn made BY a workflow controller is that workflow's delegate, not an ordinary sub-agent:
            // naming it accurately is what lets a roster tell workflow work apart from free delegation. The
            // depth arithmetic is unaffected — only the controller hop itself is delegation-free.
            var childKind = parent.Context.Kind == AgentKind.WorkflowController
                ? AgentKind.WorkflowDelegate
                : AgentKind.SubAgent;

            childContext = parent.Context.CreateChild(
                agentId,
                childKind,
                effectiveRole,
                effectiveDescription);
        }
        catch (ArgumentException ex)
        {
            // Length/shape rejection. The exception text describes the BOUND, never the value, so it is
            // safe to hand back to the caller verbatim.
            throw new SubAgentCollaborationException(
                SubAgentCollaborationFailureCodes.InvalidMetadata,
                ex.Message);
        }

        var lease = parent.Directory.TryAcquireCapacity(agentId)
            ?? throw new SubAgentCollaborationException(
                SubAgentCollaborationFailureCodes.CapacityExhausted,
                $"This collaboration already holds its maximum of {parent.Options.MaxTotalAgents} "
                    + "agents. Wait for one to finish before spawning another.");

        var registration = parent.Directory.TryRegister(
            childContext,
            effectiveName,
            AgentCollaborationStatuses.Queued,
            new SubAgentWriteEndpoint(this, agentId),
            readEndpoint: null,
            agentType: templateName);

        if (!registration.Succeeded)
        {
            _ = lease.Release();
            throw new SubAgentCollaborationException(
                registration.FailureCode ?? SubAgentCollaborationFailureCodes.RegistrationFailed,
                $"The collaboration refused this sub-agent ({registration.FailureCode}).");
        }

        _admissions[agentId] = new SubAgentAdmission(parent.ForChild(childContext, effectiveName), lease);
    }

    /// <summary>
    /// Publishes a lifecycle transition to the directory so a collaboration-wide listing agrees with
    /// this manager's own observation surface. No-op when collaboration is off.
    /// </summary>
    private void SyncCollaborationStatus(string agentId, string status)
    {
        if (Collaboration is { } parent && _admissions.ContainsKey(agentId))
        {
            _ = parent.Directory.TryUpdateStatus(agentId, status);
        }
    }

    /// <summary>
    /// Withdraws a sub-agent from the collaboration and returns its capacity slot.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT called when a run merely finishes: a completed background sub-agent stays
    /// addressable (a later message restarts it), so it is still an admitted member and still occupies
    /// a slot. Retirement belongs to the points where the agent stops existing — failed spawn rollback
    /// and manager disposal.
    /// </remarks>
    private void RetireFromCollaboration(string agentId, string status)
    {
        if (Collaboration is not { } parent || !_admissions.TryRemove(agentId, out var admission))
        {
            return;
        }

        _ = parent.Bundle.RetireAgent(agentId, status);
        _ = admission.Lease.Release();
    }

    public SubAgentManager(
        IMultiTurnAgent parentAgent,
        IReadOnlyList<FunctionContract> parentContracts,
        IDictionary<string, ToolHandler> parentHandlers,
        SubAgentOptions options,
        MutableSubAgentTemplateSource source,
        ILogger? logger = null,
        string? parentModelId = null,
        int? parentMaxToken = null,
        IUsageSink? usageSink = null,
        Func<Task>? persistUsageAsync = null,
        MultiTurnLifecycleServices? lifecycleServices = null,
        AgentCollaborationSetup? collaboration = null,
        Func<NotifyMessage, CancellationToken, ValueTask>? descendantQuestionSink = null)
    {
        ArgumentNullException.ThrowIfNull(parentAgent);
        ArgumentNullException.ThrowIfNull(parentContracts);
        ArgumentNullException.ThrowIfNull(parentHandlers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        if (options.MaxConcurrentSubAgents <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxConcurrentSubAgents must be greater than zero."
            );
        }

        if (options.MaxQueuedSubAgents < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxQueuedSubAgents cannot be negative."
            );
        }

        // Checked here rather than where a spawned child builds its bounded output channel: that read
        // happens inside a live stream, so a misconfigured host would surface as a broken sub-agent run
        // instead of as bad configuration.
        if (options.OutputChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "OutputChannelCapacity must be greater than zero."
            );
        }

        _parentAgent = parentAgent;
        _parentContracts = parentContracts;
        _parentHandlers = parentHandlers;
        _options = options;
        _childOptions = options.ForChildLoop();
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        _usageSink = usageSink;
        _persistUsageAsync = persistUsageAsync;
        // The parent's model, inherited by sub-agents whose template/override sets none (see
        // ResolveSubAgentOptions). Null when the parent has no model (e.g. CLI-backed parents).
        _parentModelId = parentModelId;
        // The parent's effective per-turn output budget, inherited by sub-agents whose template sets
        // none — so a delegate gets the same headroom as the conversation that spawned it instead of the
        // provider's 4096 default that truncates tool-call JSON. Null when the parent carried no budget.
        _parentMaxToken = parentMaxToken;
        // The parent's lifecycle wiring, from which each child's bundle is derived at spawn time.
        _lifecycleServices = lifecycleServices ?? MultiTurnLifecycleServices.Disabled;
        // The parent agent's handle on the collaboration, when the host enabled one. Null keeps every
        // collaboration branch in this class inert, which is exactly today's behaviour.
        Collaboration = collaboration;
        // Fall back to a direct one-hop relay when no upstream root target was supplied.
        _descendantQuestionSink = descendantQuestionSink ?? RelayDescendantQuestionToParentAsync;
        _concurrencyGate = new SemaphoreSlim(
            options.MaxConcurrentSubAgents,
            options.MaxConcurrentSubAgents);

        // Start the defer-queue pump. Its first action parks on _queueSignal (initialized above via its
        // field initializer), so this call returns to the ctor immediately without consuming a thread.
        _pumpTask = RunSpawnPumpAsync(_pumpCts.Token);
    }

    /// <summary>
    /// Default <see cref="_descendantQuestionSink"/> when no upstream root target is supplied: injects
    /// the notification into this manager's own owning agent via <see cref="IMultiTurnAgent.SendAsync"/>.
    /// </summary>
    private async ValueTask RelayDescendantQuestionToParentAsync(NotifyMessage notify, CancellationToken ct)
    {
        _ = await _parentAgent.SendAsync([notify], ct: ct);
    }


    /// <summary>
    /// The concrete model ids a spawn's <c>model</c> override may name, surfaced to the <c>Agent</c> tool
    /// descriptor (<see cref="SubAgentToolProvider"/>) so the parent/controller LLM picks a real id
    /// instead of inventing one. Sourced from <see cref="SubAgentOptions.AvailableModelIds"/>; null/empty
    /// when the host supplied none.
    /// </summary>
    internal IReadOnlyCollection<string>? AvailableModelIds => _options.AvailableModelIds;

    /// <summary>
    /// Host-supplied gate consulted at the <c>Agent</c> tool boundary (<see cref="SubAgentToolProvider"/>)
    /// before a spawn, mapping the spawn's <c>name</c> argument to null (allow) or a corrective message
    /// (reject). Sourced from <see cref="SubAgentOptions.SpawnNameGate"/>; null when the host supplied none.
    /// </summary>
    internal Func<string?, string?>? SpawnNameGate => _options.SpawnNameGate;

    /// <summary>Host authority for normalizing optional model selection on a named spawn.</summary>
    internal Func<string?, SubAgentSpawnModelSelection?>? SpawnModelSelectionResolver =>
        _options.SpawnModelSelectionResolver;

    /// <summary>
    /// Spawn a new sub-agent from a named template.
    /// When <paramref name="runInBackground"/> is false (default), blocks until the
    /// sub-agent's run completes and returns its final answer as the result. When true,
    /// returns immediately with a JSON spawn receipt (agent id) and relays the eventual
    /// result to the parent as an injected message.
    /// </summary>
    public async Task<string> SpawnAsync(
        string templateName,
        string task,
        string? name = null,
        string? model = null,
        bool runInBackground = false,
        string[]? addTools = null,
        string[]? removeTools = null,
        CancellationToken ct = default,
        int? modelIntelligence = null,
        string? spawningToolCallId = null,
        string? role = null,
        string? description = null)
    {
        // Snapshot the live source view so a concurrent TryRegister cannot make the
        // diagnostic Available list inconsistent with the lookup that produced template.
        var templates = _source.Templates;
        if (!TryResolveTemplateName(templateName, templates, out var resolvedName, out var suggestions))
        {
            // Ambiguous (several agents share the requested skill segment) vs genuinely unknown get
            // different, actionable messages so the caller (a controller/parent LLM) can self-correct
            // by re-calling with an EXACT name rather than collapsing to general-purpose. Both surface
            // as a recoverable {error} tool result (see SubAgentToolProvider.HandleAgentToolAsync).
            var message = suggestions.Count > 0
                ? $"Ambiguous subagent_type '{templateName}'. It matches multiple agents: "
                    + $"{string.Join(", ", suggestions)}. Re-call Agent with one of these EXACT names."
                : $"Unknown template '{templateName}'. "
                    + $"Available: {string.Join(", ", templates.Keys)}";
            // No paramName: this message is surfaced verbatim to the calling LLM as a recoverable
            // tool error, so the ArgumentException "(Parameter 'templateName')" suffix is just noise.
            throw new ArgumentException(message);
        }

        // Resolution may have mapped a bare/mis-prefixed request onto the real registered key
        // (e.g. 'logging-review' -> 'debugging:logging-review'). Use the resolved key for the
        // lookup AND for every downstream record (state, receipts, relay) so telemetry and the
        // parent see the actual template that ran.
        if (!string.Equals(resolvedName, templateName, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Resolved subagent_type '{Requested}' to registered template '{Resolved}' by name match",
                templateName, resolvedName);
        }

        templateName = resolvedName;
        var template = templates[resolvedName];

        // A spawned agent's id has TWO parts, mirroring the workflow controller's identity: a per-spawn
        // guid (uniqueness) and a conversation tag derived from the parent (launching) conversation's
        // thread id. The tag is deterministic (same conversation -> same tag) and content-free, so ids
        // born in different conversations are visibly distinct and never confused, while the guid keeps
        // each spawn unique. Guid stays FIRST so the readable-name suffix (agentId[..6]) and any short
        // display slice remain per-spawn distinct rather than collapsing onto a shared conversation prefix.
        var agentId = Guid.NewGuid().ToString("N")[..12] + "-" + ConversationTag(_parentAgent.ThreadId);
        var lineage = new AgentLineage
        {
            ParentThreadId = _parentAgent.ThreadId,
            ParentRunId = _parentAgent.CurrentRunId,
            SpawningToolCallId = spawningToolCallId,
            SubAgentId = agentId,
        };

        // Every spawned agent gets a human-readable handle. When the caller omits `name`
        // (a controller/loop that forgot, or a direct spawn), derive a readable one from the
        // resolved template so the agent never surfaces in telemetry - or as a SendMessage
        // target - as a bare guid. An explicitly supplied name is always kept verbatim.
        var effectiveName = string.IsNullOrWhiteSpace(name)
            ? DeriveReadableName(templateName, agentId)
            : name;

        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        // Admission to the collaboration happens BEFORE the concurrency permit and before the defer
        // queue: capacity and delegation depth are root-wide invariants, so a spawn that the
        // collaboration will not accept must never occupy a local slot or sit in the queue. No-op when
        // collaboration is off.
        AdmitToCollaboration(agentId, effectiveName, templateName, template, role, description);

        // Cap behaviour is DEFER-QUEUE, not reject: try to take a concurrency permit without blocking.
        // Wait(0) returns immediately whether or not a permit is free, so the historical hot path (a
        // permit is available -> run inline) is unchanged. Only the full-pool path differs: instead of
        // throwing "Max concurrent reached", the spawn is enqueued for the background pump below.
        if (_concurrencyGate.Wait(0))
        {
            // One independent release-guard instance for this gate-acquisition epoch (see
            // GateReleaseGuard) - shared between the spawn's own failure cleanup and the monitor task,
            // so whichever notices the run's end first is the one that actually releases the slot.
            var gateGuard = new GateReleaseGuard();
            var state = await StartWithHeldPermitAsync(
                agentId,
                effectiveName,
                templateName,
                template,
                task,
                model,
                addTools,
                removeTools,
                modelIntelligence,
                lineage,
                runInBackground,
                gateGuard,
                ct);

            // Past this point the monitor owns the concurrency gate; do not release it here.
            if (runInBackground)
            {
                // Nobody awaits the completion on the background path; observe any fault
                // so it never surfaces as an UnobservedTaskException.
                ObserveCompletionFaults(state);
                return SerializeSpawnReceipt(agentId, effectiveName, templateName, "spawned");
            }

            // Synchronous: block until the run completes and return its final answer.
            // Parent relay is suppressed (NotifyParentOnCompletion=false) so the result
            // flows back only as this tool result, in the same parent turn.
            return await AwaitCompletionAsync(state, ct);
        }

        // Pool full: defer-queue. The spawn is ACCEPTED immediately (a stable handle) and the pump
        // starts it when a permit frees. A background spawn returns a "queued" receipt now and relays
        // its eventual result to the parent (NotifyParentOnCompletion); a foreground (blocking) spawn
        // waits for the pump to create+start the agent (StateReady) and then awaits its completion.
        // Registration and enqueue are serialized with disposal so a successful receipt can never name
        // work accepted after shutdown began.
        var queued = new QueuedSpawn
        {
            AgentId = agentId,
            EffectiveName = effectiveName,
            TemplateName = templateName,
            Template = template,
            Task = task,
            Model = model,
            AddTools = addTools,
            RemoveTools = removeTools,
            ModelIntelligence = modelIntelligence,
            Lineage = lineage,
            RunInBackground = runInBackground,
            CallerCancellation = runInBackground ? CancellationToken.None : ct,
        };

        try
        {
            lock (_spawnQueue)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
                if (_queuedSpawns.Count >= _options.MaxQueuedSubAgents)
                {
                    throw new SubAgentQueueFullException(_options.MaxQueuedSubAgents);
                }

                _spawnQueue.Enqueue(queued);
                _queuedSpawns[agentId] = queued;
                _queuedNamesToIds[effectiveName] = agentId;
                _ = _queueSignal.Release();
            }
        }
        catch
        {
            // The spawn never reached the queue, so nothing downstream will ever retire it. Give the
            // collaboration its slot back here or the cap leaks one agent per rejected spawn.
            RetireFromCollaboration(agentId, "error");
            throw;
        }

        _logger.LogInformation(
            "Sub-agent pool full ({Max} in flight); queued spawn {AgentId} from template {Template} "
                + "(background={Background}). It will start when a slot frees.",
            _options.MaxConcurrentSubAgents, agentId, templateName, runInBackground);

        if (runInBackground)
        {
            // The background caller returns now with a "queued" receipt and never awaits StateReady, so
            // observe a potential start-failure fault on it to avoid an UnobservedTaskException.
            ObserveTaskFault(queued.StateReady.Task);
            return SerializeSpawnReceipt(agentId, effectiveName, templateName, "queued");
        }

        // Foreground (blocking) queued spawn: wait for the pump to create+start the agent, then await
        // its completion exactly as an inline foreground spawn would. Honours the caller's ct.
        var startedState = await queued.StateReady.Task.WaitAsync(ct);
        return await AwaitCompletionAsync(startedState, ct);
    }

    /// <summary>
    /// Creates, registers, and starts a sub-agent whose concurrency permit is ALREADY HELD by the
    /// caller (either <see cref="SpawnAsync"/>'s inline fast path or the background queue pump). Sets up
    /// the run task + monitor exactly as an inline spawn does, sends the initial task, and returns the
    /// live <see cref="SubAgentState"/>. On any failure it releases the held permit and rolls back the
    /// partial registration (via <see cref="CleanupFailedSpawnAsync"/> once a state exists), then
    /// rethrows — so neither the inline path nor the pump has to reason about the permit.
    /// </summary>
    private async Task<SubAgentState> StartWithHeldPermitAsync(
        string agentId,
        string effectiveName,
        string templateName,
        SubAgentTemplate template,
        string task,
        string? model,
        string[]? addTools,
        string[]? removeTools,
        int? modelIntelligence,
        AgentLineage lineage,
        bool runInBackground,
        GateReleaseGuard gateGuard,
        CancellationToken ct)
    {
        SubAgentState? state = null;

        try
        {
            ct.ThrowIfCancellationRequested();
            var (agent, store, ownedProviderAgent, routing) = await CreateSubAgentAsync(
                agentId,
                template,
                model,
                addTools,
                removeTools,
                modelIntelligence,
                lineage
            );

            state = new SubAgentState
            {
                AgentId = agentId,
                Lineage = lineage,
                TemplateName = templateName,
                Task = task,
                Agent = agent,
                Template = template,
                ModelOverride = model,
                ModelIntelligence = modelIntelligence,
                EffectiveModelId = routing.EffectiveModelId,
                EffectiveModelIntelligence = routing.EffectiveModelIntelligence,
                ModelSelectionSource = routing.SelectionSource,
                AddTools = addTools,
                RemoveTools = removeTools,
                Store = store,
                Name = effectiveName,
                NotifyParentOnCompletion = runInBackground,
            };
            state.SetOwnedProviderAgent(ownedProviderAgent);

            _logger.LogDebug(
                "Resolved sub-agent model routing for {AgentId} named {SpawnName} from template {TemplateName}: "
                    + "requested model {RequestedModel}, requested tier {RequestedModelIntelligence}, "
                    + "template model {TemplateModel}, template tier {TemplateModelIntelligence}, "
                    + "effective model {EffectiveModelId}, effective tier {EffectiveModelIntelligence}, "
                    + "source {RoutingSelectionSource}",
                agentId,
                effectiveName,
                templateName,
                model,
                modelIntelligence,
                template.DefaultOptions?.ModelId,
                template.ModelIntelligence,
                routing.EffectiveModelId,
                routing.EffectiveModelIntelligence,
                routing.SelectionSource
            );

            if (TestBeforeAgentRegistrationAsync is { } beforeRegistration)
            {
                await beforeRegistration();
            }

            ct.ThrowIfCancellationRequested();

            // Registration is the commit point for a constructed agent. Serialize it with the same
            // shutdown gate used by queue admission: if disposal started while provider construction was
            // awaiting, fail here so cleanup disposes the uncommitted agent instead of registering it after
            // DisposeAsync already enumerated the registry.
            lock (_spawnQueue)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
                _agents[agentId] = state;
                if (!string.IsNullOrWhiteSpace(effectiveName))
                {
                    if (_namesToIds.TryGetValue(effectiveName, out var existingId)
                        && existingId != agentId
                        && _agents.ContainsKey(existingId))
                    {
                        _logger.LogWarning(
                            "Sub-agent name '{Name}' already maps to agent {ExistingId}; reassigning it "
                                + "to the newly spawned agent {AgentId}. SendMessage by this name will now "
                                + "address the new agent.",
                            effectiveName, existingId, agentId);
                    }

                    _namesToIds[effectiveName] = agentId;
                }
            }

            // Start the agent loop in the background
            SyncCollaborationStatus(agentId, AgentCollaborationStatuses.Running);
            var cts = state.Cts;
            state.RunTask = agent.RunAsync(cts.Token);

            // Start monitoring BEFORE sending the task to avoid subscribe-after-send race:
            // if SendAsync triggers a fast completion before the monitor subscribes,
            // the RunCompletedMessage would fire with no subscriber listening.
            state.MonitorTask = MonitorSubAgentAsync(state, gateGuard, state.CurrentRunGeneration, cts.Token);

            // Send the task as user input (triggers first turn)
            _ = await agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = task }], ct: ct);

            _logger.LogInformation(
                "Spawned sub-agent {AgentId} from template {Template} (background={Background}) with task length {TaskLength}",
                agentId, templateName, runInBackground, task?.Length ?? 0);

            return state;
        }
        catch
        {
            if (state == null)
            {
                // Failed before a SubAgentState existed (e.g. CreateSubAgent threw): the
                // monitor never started, so this guard is the only path that will ever
                // release the slot. Collaboration admission happened earlier still, so the
                // root-wide lease has to be handed back here too - nothing downstream knows
                // about an agent that was never constructed, and a lease left behind would
                // shrink the whole hierarchy's capacity permanently.
                gateGuard.ReleaseOnce(_concurrencyGate);
                RetireFromCollaboration(agentId, AgentCollaborationStatuses.Error);
            }
            else
            {
                // State may have been constructed but rejected at the shutdown-serialized registration
                // boundary, or it may have registered and failed later. The shared cleanup handles both:
                // dictionary removals are idempotent and it disposes the constructed loop/provider.
                await CleanupFailedSpawnAsync(agentId, effectiveName, state, gateGuard);
            }

            throw;
        }
    }

    /// <summary>
    /// Background pump for defer-queued spawns: when <see cref="SpawnAsync"/> finds the pool full it
    /// enqueues the spawn instead of throwing, and this loop starts each queued spawn FIFO as a permit
    /// frees. It runs for the manager's lifetime (cancelled at disposal) and acquires permits with its
    /// OWN lifetime token — NOT any caller's — so a queued BACKGROUND spawn outlives the parent turn
    /// that requested it. A queued FOREGROUND (blocking) caller is bridged its live state via
    /// <see cref="QueuedSpawn.StateReady"/>. The pump holds no permit while parked, so it can never
    /// deadlock a permit-holder.
    /// </summary>
    private async Task RunSpawnPumpAsync(CancellationToken pumpCt)
    {
        while (!pumpCt.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(pumpCt);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_spawnQueue.TryDequeue(out var queued))
            {
                // Spurious wake or drained by disposal; loop and re-check cancellation.
                continue;
            }

            if (queued.CallerCancellation.IsCancellationRequested)
            {
                CancelQueuedSpawn(queued, queued.CallerCancellation);
                continue;
            }

            using var waitCts = queued.RunInBackground
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(pumpCt, queued.CallerCancellation);
            try
            {
                await _concurrencyGate.WaitAsync(waitCts?.Token ?? pumpCt);
            }
            catch (OperationCanceledException) when (
                queued.CallerCancellation.IsCancellationRequested && !pumpCt.IsCancellationRequested
            )
            {
                CancelQueuedSpawn(queued, queued.CallerCancellation);
                continue;
            }
            catch (OperationCanceledException)
            {
                // Manager disposing before a permit was available: fault the waiter so a foreground
                // caller unblocks (with cancellation) instead of hanging, then stop pumping.
                CancelQueuedSpawn(queued, pumpCt);
                break;
            }

            if (queued.CallerCancellation.IsCancellationRequested)
            {
                _ = _concurrencyGate.Release();
                CancelQueuedSpawn(queued, queued.CallerCancellation);
                continue;
            }

            RemoveQueuedSpawn(queued);
            var gateGuard = new GateReleaseGuard();
            try
            {
                var state = await StartWithHeldPermitAsync(
                    queued.AgentId,
                    queued.EffectiveName,
                    queued.TemplateName,
                    queued.Template,
                    queued.Task,
                    queued.Model,
                    queued.AddTools,
                    queued.RemoveTools,
                    queued.ModelIntelligence,
                    queued.Lineage,
                    queued.RunInBackground,
                    gateGuard,
                    queued.RunInBackground ? pumpCt : queued.CallerCancellation);

                if (queued.RunInBackground)
                {
                    // Nobody awaits a background queued spawn's completion; observe faults so a
                    // faulted run never surfaces as an UnobservedTaskException.
                    ObserveCompletionFaults(state);
                }

                // Unblocks a foreground caller (no-op for background, whose StateReady fault was already
                // observed at enqueue).
                _ = queued.StateReady.TrySetResult(state);
            }
            catch (Exception ex)
            {
                // StartWithHeldPermitAsync already released the permit + rolled back registration.
                _logger.LogError(
                    ex,
                    "Queued sub-agent {AgentId} (template {Template}) failed to start after dequeue.",
                    queued.AgentId, queued.TemplateName);
                _ = queued.StateReady.TrySetException(ex);
            }
        }

        // Fault any spawns still queued at shutdown so a foreground caller blocked on StateReady does
        // not hang past disposal. Routed through CancelQueuedSpawn (not a bare RemoveQueuedSpawn +
        // TrySetCanceled) so this exit also hands back the root-wide capacity lease and retires the
        // directory row admission took at queue time - the same accounting every other pre-start exit
        // in this loop already gets. RetireFromCollaboration is idempotent, so this is safe even though
        // DisposeAsync's own admissions sweep (see the loop over _admissions.Keys near the end of
        // DisposeAsync) would otherwise catch anything left behind here.
        while (_spawnQueue.TryDequeue(out var pending))
        {
            CancelQueuedSpawn(pending, CancellationToken.None);
        }
    }

    private void RemoveQueuedSpawn(QueuedSpawn queued)
    {
        _ = _queuedSpawns.TryRemove(queued.AgentId, out _);
        if (
            _queuedNamesToIds.TryGetValue(queued.EffectiveName, out var mapped)
            && mapped == queued.AgentId
        )
        {
            _ = _queuedNamesToIds.TryRemove(queued.EffectiveName, out _);
        }
    }

    /// <summary>
    /// Abandons a queued spawn that never got its held permit: drops the local queue bookkeeping,
    /// hands back the collaboration admission the queue-time <see cref="AdmitToCollaboration"/> call
    /// already granted, and unblocks a foreground caller waiting on <see cref="QueuedSpawn.StateReady"/>.
    /// </summary>
    /// <remarks>
    /// Every pre-start cancellation exit in <see cref="RunSpawnPumpAsync"/> must go through here rather
    /// than calling <see cref="RemoveQueuedSpawn"/> directly: admission reserves a root-wide capacity
    /// lease and a "queued" directory entry BEFORE the spawn ever reaches this queue, so a cancellation
    /// that only clears the queue leaves both behind. Left behind, they never come back — the lease
    /// stays charged against <c>MaxTotalAgents</c> and the directory entry stays "queued" forever — so
    /// repeated cancelled queued spawns permanently shrink the collaboration's capacity. No-op when
    /// collaboration is off, or when the admission was already retired (idempotent, like
    /// <see cref="RetireFromCollaboration"/> itself).
    /// </remarks>
    private void CancelQueuedSpawn(QueuedSpawn queued, CancellationToken cancellationToken)
    {
        RemoveQueuedSpawn(queued);
        RetireFromCollaboration(queued.AgentId, AgentCollaborationStatuses.Stopped);
        _ = queued.StateReady.TrySetCanceled(cancellationToken);
    }

    /// <summary>
    /// Serializes the JSON spawn receipt returned to the calling tool. <paramref name="status"/> is
    /// <c>"spawned"</c> for a spawn that started immediately or <c>"queued"</c> for one deferred to the
    /// pump because the pool was full.
    /// </summary>
    private static string SerializeSpawnReceipt(
        string agentId, string name, string templateName, string status)
    {
        return JsonSerializer.Serialize(new
        {
            agent_id = agentId,
            name,
            template = templateName,
            status,
        });
    }

    /// <summary>
    /// Attaches a fault-only observer to a task nobody will await, so a fault it may carry never
    /// surfaces as an <c>UnobservedTaskException</c> during GC. (Cancelled/completed tasks are safe to
    /// leave unobserved; only faults matter.)
    /// </summary>
    private static void ObserveTaskFault(Task task)
    {
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Resolves a caller-requested <paramref name="requested"/> subagent_type onto a registered
    /// template key, tolerating the common mismatch where an authored workflow or a parent LLM omits
    /// or misstates the <c>plugin:</c> prefix (e.g. asks for <c>logging-review</c> or
    /// <c>code-reviewer:logging-review</c> when the registered key is <c>debugging:logging-review</c>).
    /// Without this, an exact-match miss threw "Unknown template", and controller LLMs then silently
    /// collapsed to <c>general-purpose</c> — the specialised agent the workflow asked for never ran.
    /// </summary>
    /// <remarks>
    /// Resolution order, most-specific first (exact always wins, so fully-qualified names and the
    /// built-ins are never re-routed):
    /// <list type="number">
    ///   <item>Exact (ordinal) key match.</item>
    ///   <item>Case-insensitive exact key match.</item>
    ///   <item>Skill-segment match: the segment after the LAST <c>':'</c> of the request compared
    ///   case-insensitively against each key's own last-<c>':'</c> segment. A UNIQUE match
    ///   auto-resolves; SEVERAL matches are returned as <paramref name="suggestions"/> so the caller
    ///   can re-issue with an exact name (an LLM decides which; a deterministic caller sees the list).</item>
    /// </list>
    /// </remarks>
    /// <param name="requested">The requested subagent_type (may be bare or mis-prefixed).</param>
    /// <param name="templates">The live template snapshot to resolve against.</param>
    /// <param name="resolved">The registered key to spawn, when resolution succeeds.</param>
    /// <param name="suggestions">
    /// Candidate keys when the request is ambiguous (several agents share its skill segment); empty
    /// when the request is simply unknown. Only meaningful when the method returns false.
    /// </param>
    /// <returns>True when <paramref name="requested"/> resolves to exactly one template.</returns>
    internal static bool TryResolveTemplateName(
        string requested,
        IReadOnlyDictionary<string, SubAgentTemplate> templates,
        out string resolved,
        out IReadOnlyList<string> suggestions)
    {
        ArgumentNullException.ThrowIfNull(templates);
        resolved = string.Empty;
        suggestions = [];

        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        // 1. Exact ordinal match — the fast, unchanged path for correct names.
        if (templates.ContainsKey(requested))
        {
            resolved = requested;
            return true;
        }

        // 2. Case-insensitive exact match.
        foreach (var key in templates.Keys)
        {
            if (string.Equals(key, requested, StringComparison.OrdinalIgnoreCase))
            {
                resolved = key;
                return true;
            }
        }

        // 3. Skill-segment match (the plugin-prefix-insensitive case). Compare the part after the
        //    last ':' on both sides so 'logging-review' and 'code-reviewer:logging-review' both
        //    match the registered 'debugging:logging-review'.
        var requestedSegment = LastSegment(requested);
        var segmentMatches = new List<string>();
        foreach (var key in templates.Keys)
        {
            if (string.Equals(LastSegment(key), requestedSegment, StringComparison.OrdinalIgnoreCase))
            {
                segmentMatches.Add(key);
            }
        }

        if (segmentMatches.Count == 1)
        {
            resolved = segmentMatches[0];
            return true;
        }

        if (segmentMatches.Count > 1)
        {
            // Ambiguous: hand the candidates back so the caller can re-issue with an exact name.
            // Sort ordinally so the suggestion order (and the message built from it) is deterministic:
            // the live snapshot is an ImmutableDictionary whose key iteration order follows per-process
            // randomized string hashing, so an unsorted list would vary run-to-run.
            segmentMatches.Sort(StringComparer.Ordinal);
            suggestions = segmentMatches;
        }

        return false;
    }

    /// <summary>The segment after the last <c>':'</c> in <paramref name="key"/> (the whole string when none).</summary>
    private static string LastSegment(string key)
    {
        var idx = key.LastIndexOf(':');
        return idx >= 0 && idx < key.Length - 1 ? key[(idx + 1)..] : key;
    }

    /// <summary>
    /// Builds a short, human-readable handle for a sub-agent whose caller did not supply a
    /// <c>name</c>. Uses the resolved template's last <c>':'</c> segment (dropping any plugin prefix,
    /// e.g. <c>code-reviewer:performance-review</c> -&gt; <c>performance-review</c>) plus a short slice
    /// of the agent id for uniqueness, so the agent surfaces in telemetry and is addressable by
    /// SendMessage as e.g. <c>performance-review-1a2b3c</c> rather than a bare guid.
    /// </summary>
    private static string DeriveReadableName(string templateName, string agentId)
    {
        var role = LastSegment(templateName);
        if (string.IsNullOrWhiteSpace(role))
        {
            role = "agent";
        }

        var suffix = agentId.Length >= 6 ? agentId[..6] : agentId;
        return $"{role}-{suffix}";
    }

    /// <summary>
    /// Rolls back a spawn that failed after its <see cref="SubAgentState"/> was registered
    /// (possibly after the monitor already started, e.g. because <c>agent.SendAsync</c> threw):
    /// removes the partial registration from <see cref="_agents"/>/<see cref="_namesToIds"/>,
    /// cancels the sub-agent's <see cref="SubAgentState.Cts"/>, and awaits its
    /// <see cref="SubAgentState.RunTask"/>/<see cref="SubAgentState.MonitorTask"/> (if started)
    /// so neither leaks as an orphaned, unobserved background task. The concurrency slot itself
    /// is released via <paramref name="gateGuard"/> - the same, per-epoch
    /// <see cref="GateReleaseGuard"/> instance the (possibly already-started) monitor holds - so
    /// this is a no-op if the monitor's own completion/finally path already released it first.
    /// </summary>
    private async Task CleanupFailedSpawnAsync(
        string agentId,
        string? name,
        SubAgentState state,
        GateReleaseGuard gateGuard)
    {
        _ = _agents.TryRemove(agentId, out _);
        RetireFromCollaboration(agentId, "error");
        if (!string.IsNullOrWhiteSpace(name)
            && _namesToIds.TryGetValue(name, out var mappedId)
            && mappedId == agentId)
        {
            _ = _namesToIds.TryRemove(name, out _);
        }

        try
        {
            await state.Cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a racing path; nothing to cancel.
        }

        if (state.RunTask != null)
        {
            try { await state.RunTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RunTask faulted during spawn cleanup for sub-agent {AgentId}", agentId);
            }
        }

        if (state.MonitorTask != null)
        {
            try { await state.MonitorTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MonitorTask faulted during spawn cleanup for sub-agent {AgentId}", agentId);
            }
        }

        try { await state.Agent.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent dispose failed during spawn cleanup for sub-agent {AgentId}", agentId);
        }

        try { await state.DisposeOwnedProviderAgentAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Provider dispose failed during spawn cleanup for sub-agent {AgentId}",
                agentId
            );
        }

        state.Cts.Dispose();
        if (state.Store is IAsyncDisposable disposableStore)
        {
            try { await disposableStore.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Store dispose failed during spawn cleanup for sub-agent {AgentId}", agentId);
            }
        }

        gateGuard.ReleaseOnce(_concurrencyGate);
    }

    /// <summary>
    /// Continue an existing sub-agent identified by its id or caller-supplied name.
    /// A running agent receives the message in its current run; a finished agent is
    /// restarted. When <paramref name="runInBackground"/> is false (default), blocks
    /// until the (re)started run completes and returns its final answer; when true,
    /// returns a JSON receipt immediately and relays the result to the parent.
    /// </summary>
    public async Task<string> SendMessageAsync(
        string target,
        string prompt,
        bool runInBackground = false,
        CancellationToken ct = default)
    {
        return await SendMessageAsync(
            target,
            new TextMessage { Role = Role.User, Text = prompt },
            runInBackground,
            ct
        );
    }

    /// <summary>
    /// Continues a sub-agent with an already-formed message rather than plain text.
    /// </summary>
    /// <remarks>
    /// The collaboration delivery path uses this so an <see cref="AgentMessage"/> reaches the target as
    /// itself. Flattening it to text would strip the structured sender, type, and correlation that the
    /// UI and the persisted history read, leaving only the rendered envelope — and a rehydrated
    /// conversation would then have no way to tell an agent-to-agent message from anything else a user
    /// might have typed.
    /// </remarks>
    internal async Task<string> SendMessageAsync(
        string target,
        IMessage message,
        bool runInBackground,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var messageLength = (message as ICanGetText)?.GetText()?.Length ?? 0;
        var agentId = ResolveAgentId(target);
        if (_queuedSpawns.TryGetValue(agentId, out _))
        {
            throw new InvalidOperationException(
                $"Sub-agent '{target}' is queued and cannot receive messages until it starts. "
                    + "Poll it with CheckAgent/CheckAgents and retry when status is running."
            );
        }

        var state = _agents[agentId];

        // Decide how to continue this sub-agent atomically against a concurrent terminal completion and
        // any other concurrent continuation. An admitted "inject into the running loop" holds a send
        // lease that a terminal owned-provider disposal awaits, so a send can never race the provider
        // being disposed; a finished run is restarted with a fresh provider by exactly ONE caller (any
        // others await that restart and then inject into the re-armed loop). See
        // SubAgentState.BeginContinuation.
        bool wasRunning;
        while (true)
        {
            var decision = state.BeginContinuation(runInBackground);

            if (decision.Mode == ContinuationMode.Inject)
            {
                // A finished completion is replaced so this run can be awaited fresh; a pending one
                // (running agent) is kept so existing waiters observe the next resolution.
                state.ResetCompletionIfFinished();

                // The caller owns BeginContinuation/EndInjectLease; the helper performs the send under
                // that already-held lease and reports whether the run's lifecycle cancelled it.
                bool injectCancelledByLifecycle;
                List<IMessage> injected = [message];
                try
                {
                    injectCancelledByLifecycle = await InjectIntoRunningLoopAsync(state, injected, ct);
                }
                finally
                {
                    state.EndInjectLease();
                }

                if (injectCancelledByLifecycle)
                {
                    // The lease is released; re-enter the decision loop. The run is now terminal, so
                    // BeginContinuation routes this to a fresh-provider restart that delivers the prompt.
                    continue;
                }

                _logger.LogInformation(
                    "Sent message to running sub-agent {AgentId} ({MessageLength} chars)",
                    agentId, messageLength);

                wasRunning = true;
                break;
            }

            if (decision.Mode == ContinuationMode.Restart)
            {
                state.ResetCompletionIfFinished();

                try
                {
                    await RestartRunAsync(state, message, ct);
                }
                finally
                {
                    state.EndRestart();
                }

                wasRunning = false;
                break;
            }

            // AwaitRestart: another caller owns the in-flight restart. Wait for it to finish, then
            // re-evaluate — the restart flips the loop back to Running, so the retry injects into it.
            await decision.RestartCompleted!.WaitAsync(ct);
        }

        if (runInBackground)
        {
            ObserveCompletionFaults(state);

            return JsonSerializer.Serialize(new
            {
                agent_id = agentId,
                name = state.Name,
                status = wasRunning ? "message_sent" : "resumed",
            });
        }

        return await AwaitCompletionAsync(state, ct);
    }

    /// <summary>
    /// Attempts to deliver out-of-band context (a sandbox-discovered directory <c>CLAUDE.md</c>/
    /// <c>AGENTS.md</c>) into a currently-running sub-agent identified by id or caller-supplied name.
    /// Implements the running-branch half of <see cref="ISubAgentContextSink"/>: it delivers ONLY into a
    /// genuinely-running loop under a side-effect-free inject lease, and NEVER restarts a finished run,
    /// mutates the parent-relay preference, or relays a spurious completion. A target that is not safely
    /// running is refused so the caller drops the delivery (it must not fall back to the primary).
    /// </summary>
    /// <returns>
    /// <see cref="SubAgentContextDeliveryResult.NotOwned"/> when no live sub-agent matches
    /// <paramref name="agentId"/>; <see cref="SubAgentContextDeliveryResult.Delivered"/> when injected into
    /// a running loop; <see cref="SubAgentContextDeliveryResult.TargetNotDeliverable"/> when the matched
    /// sub-agent is finished/terminating (dropped, never restarted).
    /// </returns>
    public async Task<SubAgentContextDeliveryResult> TryDeliverToRunningAsync(
        string agentId,
        IReadOnlyList<IMessage> messages,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!TryResolveAgentId(agentId, out var resolvedId)
            || !_agents.TryGetValue(resolvedId, out var state))
        {
            // Not one of this sink's sub-agents (unknown id/name, or a pre-registration race). The caller
            // keeps looking / drops without marking-seen so a gateway redelivery can still route it.
            return SubAgentContextDeliveryResult.NotOwned;
        }

        // Admit ONLY under a side-effect-free inject lease — NOT BeginContinuation, which would clobber the
        // relay preference and claim the restart. A finished/terminating target refuses the lease: the
        // context is dropped, never restarted, never relayed, never run through a disposing provider.
        if (!state.TryBeginInjectLease())
        {
            return SubAgentContextDeliveryResult.TargetNotDeliverable;
        }

        try
        {
            var injectCancelledByLifecycle = await InjectIntoRunningLoopAsync(state, messages, ct);

            // A lifecycle cancel means a terminal disposal began mid-send, so the send did not land: drop
            // the context. Unlike SendMessageAsync, a context delivery does NOT re-evaluate into a restart.
            return injectCancelledByLifecycle
                ? SubAgentContextDeliveryResult.TargetNotDeliverable
                : SubAgentContextDeliveryResult.Delivered;
        }
        finally
        {
            state.EndInjectLease();
        }
    }

    /// <summary>
    /// Performs an inject send into the sub-agent's currently-running loop under a send lease the CALLER
    /// already holds (via <see cref="SubAgentState.BeginContinuation"/> or
    /// <see cref="SubAgentState.TryBeginInjectLease"/>) and releases (via
    /// <see cref="SubAgentState.EndInjectLease"/>). Links the caller token with the run's lifecycle token
    /// so a terminal owned-provider disposal can unblock a wedged send. Carries NEITHER a
    /// <see cref="SubAgentState.NotifyParentOnCompletion"/> mutation NOR
    /// <see cref="SubAgentState.ResetCompletionIfFinished"/> — those are the caller's concern.
    /// </summary>
    /// <returns><c>true</c> when the send was cancelled specifically by the run's LIFECYCLE token (terminal
    /// disposal began) so the loop is finishing — the caller re-evaluates (SendMessage restart) or drops
    /// (context delivery); <c>false</c> when the send landed.</returns>
    private static async Task<bool> InjectIntoRunningLoopAsync(
        SubAgentState state,
        IReadOnlyList<IMessage> messages,
        CancellationToken ct)
    {
        using var linkedCts = state.LinkLifecycleToken(ct);
        var payload = messages as List<IMessage> ?? [.. messages];
        try
        {
            _ = await state.Agent.SendAsync(payload, ct: linkedCts.Token);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && linkedCts.IsCancellationRequested)
        {
            // Cancelled specifically by the run's LIFECYCLE token (terminal disposal began) — NOT by the
            // caller, and NOT an internal SendAsync cancellation unrelated to either supplied token (which
            // must propagate rather than be swallowed, to avoid duplicate delivery / an unbounded retry).
            return true;
        }
    }

    /// <summary>
    /// Resolves a caller-supplied target (agent id or name) to a concrete agent id.
    /// </summary>
    private string ResolveAgentId(string target)
    {
        if (TryResolveAgentId(target, out var agentId))
        {
            return agentId;
        }

        throw new ArgumentException(
            $"Unknown sub-agent '{target}'. Provide a valid agent id or name.",
            nameof(target));
    }

    /// <summary>
    /// Non-throwing counterpart to <see cref="ResolveAgentId"/>: resolves a target (agent id or name) to a
    /// concrete, still-registered agent id. Returns false (rather than throwing) when the target matches no
    /// live sub-agent, so a caller can distinguish "not ours" from a deliverable target without exceptions.
    /// </summary>
    private bool TryResolveAgentId(string target, out string agentId)
    {
        if (_agents.ContainsKey(target) || _queuedSpawns.ContainsKey(target))
        {
            agentId = target;
            return true;
        }

        if (_namesToIds.TryGetValue(target, out var id) && _agents.ContainsKey(id))
        {
            agentId = id;
            return true;
        }

        if (_queuedNamesToIds.TryGetValue(target, out id) && _queuedSpawns.ContainsKey(id))
        {
            agentId = id;
            return true;
        }

        agentId = string.Empty;
        return false;
    }

    /// <summary>
    /// Restarts a finished (completed/error/stopped) sub-agent's run with a new message.
    /// On success the new monitor owns the concurrency gate; on failure the gate is released.
    /// </summary>
    private async Task RestartRunAsync(
        SubAgentState state,
        IMessage message,
        CancellationToken ct)
    {
        if (!await _concurrencyGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            throw new InvalidOperationException(
                $"Max concurrent sub-agents ({_options.MaxConcurrentSubAgents}) " +
                $"reached. Cannot resume agent '{state.AgentId}'.");
        }

        // One independent release-guard instance for this gate-acquisition epoch (see
        // GateReleaseGuard): the previous epoch (the original spawn or an earlier restart)
        // already released its own slot via its OWN guard instance when that run finished (or
        // will do so once its still-in-flight monitor task, awaited below, actually exits) - a
        // fresh instance here, rather than resetting a shared flag in place, means this new
        // epoch's release can never be conflated with (or spuriously consumed by) that old,
        // independent one.
        var gateGuard = new GateReleaseGuard();

        try
        {
            // Cancel and dispose the old CTS to prevent double-monitor bugs:
            // the old monitor's closure captured the old CTS, and both monitors
            // would receive RunCompletedMessage causing double Release/Decrement.
            try
            {
                await state.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // An EARLIER restart attempt that failed past the dispose below left this source
                // disposed while deliberately keeping the sub-agent registered. It is already cancelled
                // and about to be replaced, so there is nothing to cancel — but letting this throw would
                // abort every future restart here, permanently stranding an agent the directory still
                // advertises as addressable. The failure cleanup below tolerates it for the same reason.
            }

            // Observe the old RunTask to avoid unobserved exceptions
            // (must cancel first so the task receives the cancellation signal)
            if (state.RunTask != null)
            {
                try { await state.RunTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Old RunTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            if (state.MonitorTask != null)
            {
                try { await state.MonitorTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Old MonitorTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            state.Cts.Dispose();

            // Rebuild the provider pipeline when the previous run's owned provider was disposed at
            // completion OR its terminal disposal FAILED (poisoned): in both cases the provider must not
            // be reused — a failed disposal may have left it partially torn down. Also rebuild when a
            // previous restart attempt failed and disposed the live loop itself, which leaves this state
            // registered around an agent that can no longer accept a run.
            if (state.HasDisposedOwnedProviderAgent
                || state.OwnedProviderTerminalDisposeFailed
                || state.HasDisposedAgentLoop)
            {
                var previousAgent = state.Agent;
                var previousStore = state.Store;
                var (replacementAgent, replacementStore, replacementOwnedProviderAgent, replacementRouting) = await CreateSubAgentAsync(
                    state.AgentId,
                    state.Template,
                    state.ModelOverride,
                    state.AddTools,
                    state.RemoveTools,
                    state.ModelIntelligence,
                    // The rebuilt agent is the same sub-agent, so it keeps the lineage captured
                    // when it was first spawned rather than acquiring a new one from whatever run
                    // happens to be in flight now.
                    state.Lineage
                );

                // Presentation-only: the replacement is now built and we are about to dispose the previous
                // instance (which ends any focused observer's stream). Mark the restart in flight BEFORE
                // that dispose so an observer whose stream ends on it deterministically waits for the
                // imminent swap's replacement signal (no timeout) instead of treating the end as a
                // backpressure drop. Cleared under the same lock by SwapLiveAgentAndSignalReplaced below.
                state.SignalRestartStarting();

                try { await previousAgent.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Completed agent dispose failed before restart for sub-agent {AgentId}",
                        state.AgentId
                    );
                }

                if (
                    previousStore is IAsyncDisposable disposablePreviousStore
                    && !ReferenceEquals(previousStore, replacementStore)
                )
                {
                    try { await disposablePreviousStore.DisposeAsync(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Completed store dispose failed before restart for sub-agent {AgentId}",
                            state.AgentId
                        );
                    }
                }

                // If the previous terminal disposal FAILED (poisoned), retry disposing that provider
                // before swapping in the replacement so the partially-disposed instance isn't leaked.
                // The disposal guard reset to Idle on the earlier failure, so this genuinely retries;
                // when it had been cleanly disposed the flag is false and this block is skipped.
                if (state.OwnedProviderTerminalDisposeFailed)
                {
                    try { await state.DisposeOwnedProviderAgentAsync(); }
                    catch (Exception ex)
                    {
                        // Retry failed a second time: we are about to overwrite the OwnedProviderAgent
                        // slot, which would drop this handle forever. Retain it for a best-effort dispose
                        // at manager teardown so a repeatedly-undisposable provider is accounted for
                        // rather than silently abandoned.
                        var undisposed = state.OwnedProviderAgent;
                        if (undisposed is not null)
                        {
                            _abandonedProviders.Add(undisposed);
                        }

                        _logger.LogWarning(
                            ex,
                            "Retry dispose of poisoned owned provider failed before restart for sub-agent {AgentId}; retained for cleanup at manager disposal",
                            state.AgentId
                        );
                    }
                }

                state.Store = replacementStore;
                state.SetOwnedProviderAgent(replacementOwnedProviderAgent);
                // Refresh the billed model: the characteristics factory was re-invoked for the replacement and is
                // not required to make the same UseParentModel/routing decision, so the effective model can differ
                // from the original run. Without this, descendant usage after a restart would be attributed to the
                // stale init-time model.
                state.EffectiveModelId = replacementRouting.EffectiveModelId;
                state.EffectiveModelIntelligence = replacementRouting.EffectiveModelIntelligence;
                state.ModelSelectionSource = replacementRouting.SelectionSource;

                // Presentation-only: atomically install the replacement as the live Agent AND wake any
                // external observer whose subscription was bound to the now-disposed previous instance so
                // it can re-subscribe to this replacement and keep following the child across the swap.
                // Setting Agent and signalling together (SwapLiveAgentAndSignalReplaced) means an observer's
                // SnapshotForObservation can never see a torn (agent, replaced-signal) pair. Never awaited
                // by the run/monitor/restart logic.
                state.SwapLiveAgentAndSignalReplaced(replacementAgent);
            }

            // Recover conversation history after replacing a completed owned-provider loop, so a
            // continuation uses the fresh provider pipeline while retaining persisted context.
            if (state.Store != null
                && state.Agent is MultiTurnAgentBase agentBase)
            {
                _ = await agentBase.RecoverAsync();
            }

            // Create new CTS and start the loop again
            state.Cts = new CancellationTokenSource();
            var cts = state.Cts;

            // Re-arm the lifecycle cancellation and open a new run generation for this epoch BEFORE the
            // restarted loop can report completion, so (a) injects into the new run link a fresh token
            // and (b) the Running publish below is generation-guarded against a fast completion.
            state.ResetLifecycleCts();
            var runGeneration = state.BeginRunGeneration();

            state.RunTask = state.Agent.RunAsync(cts.Token);

            // Re-subscribe BEFORE sending to avoid subscribe-after-send race
            state.MonitorTask = MonitorSubAgentAsync(state, gateGuard, runGeneration, cts.Token);

            _ = await state.Agent.SendAsync([message], ct: ct);

            // Publish Running as the final step of the restart transition, but skip it if the restarted
            // run already completed-and-disposed (a fast run can finish before this line executes):
            // resurrecting a terminal run to Running would let the next continuation inject through a
            // provider that terminal handling has already disposed.
            //
            // The collaboration directory is synced from the same guarded result. A restart that armed
            // Running while the directory still said "completed" would make the agent look terminal to
            // every other agent in the hierarchy, and a steer addressed to it would be refused for as
            // long as the restarted run lasted.
            if (state.TryArmRunning(runGeneration))
            {
                SyncCollaborationStatus(state.AgentId, AgentCollaborationStatuses.Running);
            }

            _logger.LogInformation(
                "Resumed sub-agent {AgentId} ({MessageLength} chars)",
                state.AgentId, (message as ICanGetText)?.GetText()?.Length ?? 0);
        }
        catch
        {
            // Cancel + observe any run/monitor tasks started before the failure (e.g.
            // agent.SendAsync threw after the new monitor was already subscribed) so they
            // don't leak as orphaned background work. Unlike a failed SpawnAsync, this agent
            // stays registered in _agents/_namesToIds: it is a pre-existing sub-agent whose
            // restart attempt failed, not a fresh, partially-registered one to roll back.
            try
            {
                await state.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by a racing path; nothing to cancel.
            }

            if (state.RunTask != null)
            {
                try { await state.RunTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RunTask faulted during restart cleanup for sub-agent {AgentId}", state.AgentId);
                }
            }

            if (state.MonitorTask != null)
            {
                try { await state.MonitorTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MonitorTask faulted during restart cleanup for sub-agent {AgentId}", state.AgentId);
                }
            }

            try { await state.Agent.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent dispose failed during restart cleanup for sub-agent {AgentId}", state.AgentId);
            }

            // The live loop is now dead but this sub-agent deliberately stays registered, so the NEXT
            // restart must rebuild the pipeline rather than send into it. The owned-provider flags do
            // not cover this: a sub-agent on a BORROWED provider has no owned provider to mark, and a
            // dispose that itself threw leaves the provider guard back at Idle.
            state.MarkAgentLoopDisposed();

            try { await state.DisposeOwnedProviderAgentAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider dispose failed during restart cleanup for sub-agent {AgentId}",
                    state.AgentId
                );
            }

            // Idempotent: a no-op if the (now-observed) monitor's own finally already
            // released the slot first (both hold the SAME gateGuard instance created above).
            gateGuard.ReleaseOnce(_concurrencyGate);
            throw;
        }
    }

    /// <summary>
    /// Returns a read-only, point-in-time snapshot of every registered sub-agent. Presentation-only:
    /// it reads the live registry without mutating any state, never blocks, and is safe to call
    /// concurrently with spawn/send/dispose. A sub-agent added or removed after the snapshot is taken
    /// is simply not reflected in the returned list.
    /// </summary>
    public IReadOnlyList<SubAgentSnapshot> ListAgents()
    {
        var snapshots = new List<SubAgentSnapshot>(_agents.Count + _queuedSpawns.Count);
        foreach (var queued in _queuedSpawns.Values)
        {
            snapshots.Add(new SubAgentSnapshot(
                AgentId: queued.AgentId,
                Name: queued.EffectiveName,
                TemplateName: queued.TemplateName,
                Task: queued.Task,
                Status: SubAgentStatus.Queued,
                ThreadId: $"subagent-{queued.AgentId}",
                LastActivityUtc: null,
                TerminalAtUtc: null,
                EffectiveModelId: null,
                EffectiveModelIntelligence: null,
                ModelSelectionSource: "pending"));
        }
        foreach (var state in _agents.Values)
        {
            snapshots.Add(new SubAgentSnapshot(
                AgentId: state.AgentId,
                Name: state.Name,
                TemplateName: state.TemplateName,
                Task: state.Task,
                Status: state.Status,
                ThreadId: state.Agent.ThreadId,
                LastActivityUtc: GetLastActivityUtc(state),
                TerminalAtUtc: state.TerminalAtUtc,
                EffectiveModelId: state.EffectiveModelId,
                EffectiveModelIntelligence: state.EffectiveModelIntelligence,
                ModelSelectionSource: state.ModelSelectionSource));
        }

        return snapshots;
    }

    /// <summary>
    /// Classifies whether <paramref name="status"/> is a terminal lifecycle status
    /// (<see cref="SubAgentStatus.Completed"/>, <see cref="SubAgentStatus.Error"/>, or
    /// <see cref="SubAgentStatus.Stopped"/>) as opposed to <see cref="SubAgentStatus.Running"/>. Gives
    /// callers reading <see cref="ListAgents"/> snapshots (e.g. a review-completion source) a single
    /// canonical classification instead of each re-deriving the 3-of-4-case terminal set.
    /// </summary>
    public static bool IsTerminal(SubAgentStatus status) =>
        status is SubAgentStatus.Completed or SubAgentStatus.Error or SubAgentStatus.Stopped;

    /// <summary>
    /// Derives the timestamp of the newest buffered turn for <paramref name="state"/>, or null when
    /// the buffer is empty. The buffer is a lock-free <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>;
    /// snapshotting it with <c>ToArray</c> gives a consistent view even while the monitor enqueues.
    /// </summary>
    private static DateTimeOffset? GetLastActivityUtc(SubAgentState state)
    {
        var turns = state.TurnBuffer.ToArray();
        if (turns.Length == 0)
        {
            return null;
        }

        var newest = turns[0].Timestamp;
        for (var i = 1; i < turns.Length; i++)
        {
            if (turns[i].Timestamp > newest)
            {
                newest = turns[i].Timestamp;
            }
        }

        return newest;
    }

    /// <summary>
    /// Non-throwing resolve of a single sub-agent by its id or caller-supplied name, for a
    /// presentation seam that needs the live instance (e.g. to subscribe to its output). Returns
    /// false (with <paramref name="agent"/> set to null) when the target matches no registered
    /// sub-agent, so a caller can distinguish "not ours" without catching exceptions. Read-only: it
    /// does not alter any sub-agent state.
    /// </summary>
    /// <param name="target">The sub-agent id or its caller-supplied name.</param>
    /// <param name="agent">The resolved live instance on success; null otherwise.</param>
    /// <returns>True if a registered sub-agent was resolved; false otherwise.</returns>
    public bool TryGetAgent(string target, out IMultiTurnAgent? agent)
    {
        if (TryResolveAgentId(target, out var agentId)
            && _agents.TryGetValue(agentId, out var state))
        {
            agent = state.Agent;
            return true;
        }

        agent = null;
        return false;
    }

    /// <summary>
    /// Presentation-only observation seam that streams a single sub-agent's output and transparently
    /// follows the child across an owned-provider restart's instance swap — so a focused viewer keeps
    /// receiving frames when a finished child is relayed a follow-up (which disposes the old loop and
    /// installs a fresh one). It NEVER drives, restarts, or otherwise mutates execution: it only
    /// subscribes to whatever the child's CURRENT live instance is and re-subscribes when that instance
    /// is replaced.
    /// <para>
    /// Each iteration captures the state's replacement signal BEFORE subscribing to the current instance
    /// (order matters: capturing the signal first means a swap racing between capture and subscribe is
    /// not lost), yields that instance's messages until its stream ends (an owned-provider restart
    /// disposes it, or cancellation), then awaits the replacement — re-subscribing to the new instance,
    /// or ending when the child was torn down (<c>null</c>), pruned, or the caller cancelled. A borrowed-
    /// provider child never swaps instances, so it simply streams the one loop until cancellation.
    /// </para>
    /// </summary>
    /// <param name="target">The sub-agent id or its caller-supplied name.</param>
    /// <param name="ct">Cancellation token; cancelling ends the enumeration.</param>
    /// <returns>An async stream of the child's messages spanning any owned-provider restart swaps.</returns>
    public async IAsyncEnumerable<IMessage> SubscribeToAgentAcrossRestartsAsync(
        string target,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Re-resolve at the loop top so a swap (or teardown) between iterations is observed against
            // the live registry rather than a stale handle.
            if (!TryResolveAgentId(target, out var id) || !_agents.TryGetValue(id, out var state))
            {
                yield break;
            }

            // Capture the live instance AND its replacement signal in ONE atomic snapshot so a swap that
            // lands between the two reads can never pair a signal from one restart epoch with an agent from
            // another (torn read). The signal is captured BEFORE subscribing so a swap between snapshot and
            // the SubscribeAsync below is delivered via `replaced` rather than lost.
            var (current, replaced) = state.SnapshotForObservation();

            await foreach (var msg in SubscribeUntilDisposedAsync(current, ct))
            {
                yield return msg;
            }

            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            // The current instance's stream ended without cancellation. Four causes:
            //  (a) an owned-provider restart disposed it (SignalRestartStarting set the restart flag
            //      BEFORE that dispose, and the swap completes `replaced` with the new instance),
            //  (b) the manager tore it down (completes `replaced` with null),
            //  (c) a slow-subscriber backpressure DROP removed our subscriber while the instance is
            //      still alive (MultiTurnAgentBase.PublishToSubscriber) — which never fires `replaced`
            //      and sets no restart flag, or
            //  (d) it was ALREADY disposed when we subscribed — the restart's dispose runs before its
            //      swap, so the registry hands out the old instance for that window and the admission
            //      gate rejects the subscribe (see SubscribeUntilDisposedAsync). That is the same
            //      transition as (a), one step earlier.
            // DecideAfterStreamEnd distinguishes these deterministically, under the same lock the swap and
            // teardown use, with NO elapsed-time heuristic: a restart/teardown (a)/(b)/(d) yields
            // AwaitReplacement (wait for the definitive signal, however long the dispose+cleanup takes); a
            // drop (c) yields EndStream so the socket closes and the client reconnects + replays.
            if (state.DecideAfterStreamEnd(replaced) == ObservationContinuation.EndStream)
            {
                yield break;
            }

            // A restart is in flight or already signalled: await the replacement bounded ONLY by the
            // caller's connection lifetime (ct), never by a fixed timeout. A slow restart therefore keeps
            // the focus stream attached instead of tearing a valid stream down.
            IMultiTurnAgent? next;
            try
            {
                next = await replaced.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (next is null)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Streams one instance's messages for the observation seam above, treating "this instance is
    /// disposed" as an ordinary END of that instance's stream rather than a failure.
    /// <para>
    /// A restart disposes the previous loop BEFORE swapping the replacement in, so for that window the
    /// registry still hands out the old instance and
    /// <see cref="MultiTurnAgentBase.SubscribeAsync"/>'s admission gate answers a subscribe with
    /// <see cref="ObjectDisposedException"/> — surfacing at the first <c>MoveNextAsync</c>, since the
    /// iterator is lazy. Letting that escape would reach the client as a hard stream failure (the
    /// WebSocket layer turns any non-cancellation fault into <c>subagent_stream_failed</c> plus an
    /// abnormal close) for a child that is merely between instances. Ending the stream instead feeds
    /// the caller's existing <c>DecideAfterStreamEnd</c>/replacement logic, which is already the right
    /// answer for a stream that ended because the instance went away.
    /// </para>
    /// <para>
    /// Only <see cref="ObjectDisposedException"/> is absorbed: cancellation and every other fault still
    /// propagate unchanged. The explicit enumerator loop exists because C# forbids <c>yield return</c>
    /// inside a <c>try</c> that has a <c>catch</c>.
    /// </para>
    /// </summary>
    private static async IAsyncEnumerable<IMessage> SubscribeUntilDisposedAsync(
        IMultiTurnAgent agent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var messages = agent.SubscribeAsync(ct).GetAsyncEnumerator(ct);

        while (true)
        {
            bool moved;
            try
            {
                moved = await messages.MoveNextAsync();
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            if (!moved)
            {
                yield break;
            }

            yield return messages.Current;
        }
    }

    /// <summary>
    /// Check the status and recent activity of a sub-agent.
    /// </summary>
    public string Peek(string agentId) =>
        TryPeek(agentId, out var status)
            ? status
            : throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));

    /// <summary>
    /// Non-throwing variant of <see cref="Peek"/>: returns <c>false</c> (with an empty status) when
    /// <paramref name="agentId"/> is not a tracked sub-agent, so a caller (e.g. the CheckAgent tool) can
    /// return a helpful "unknown agent" result to the model instead of surfacing a tool-execution error.
    /// </summary>
    public bool TryPeek(string agentId, out string status)
    {
        if (_queuedSpawns.TryGetValue(agentId, out var queued))
        {
            status = JsonSerializer.Serialize(new
            {
                agent_id = queued.AgentId,
                name = queued.EffectiveName,
                status = "queued",
                template = queued.TemplateName,
                task = queued.Task,
                recent_turns = Array.Empty<object>(),
                last_result = (string?)null,
                send_to_parent_failed = false,
                send_to_parent_error = (string?)null,
            });
            return true;
        }

        if (!_agents.TryGetValue(agentId, out var state))
        {
            status = string.Empty;
            return false;
        }

        // Get the last 3 turns from the buffer
        var recentTurns = state.TurnBuffer
            .ToArray()
            .TakeLast(3)
            .Select(t => new
            {
                type = t.MessageType,
                tool = t.ToolName,
                tool_args = t.ToolArgsPreview,
                text = t.TextPreview,
                time = t.Timestamp.ToString("o"),
            })
            .ToArray();

        status = JsonSerializer.Serialize(new
        {
            agent_id = agentId,
            name = state.Name,
            status = state.Status.ToString().ToLowerInvariant(),
            template = state.TemplateName,
            task = state.Task,
            recent_turns = recentTurns,
            last_result = state.LastResult,
            send_to_parent_failed = state.SendToParentFailed,
            send_to_parent_error = state.SendToParentError,
        });
        return true;
    }

    /// <summary>The ids of the sub-agents currently tracked, so an unknown-id CheckAgent can tell the model
    /// which ids are actually valid (the Agent tool returns short ids; a mismatched/hallucinated id is the
    /// common cause of an "unknown agent" check).</summary>
    public IReadOnlyCollection<string> KnownAgentIds() => [.. _agents.Keys, .. _queuedSpawns.Keys];

    /// <summary>
    /// Observes a direct child's completion by id OR name, including one still waiting in the defer
    /// queue (which <see cref="ObserveCompletionAsync"/> cannot see, because a queued spawn has no
    /// state yet). Non-destructive in the same way: cancelling the wait leaves the child running.
    /// </summary>
    /// <exception cref="ArgumentException">No child of this manager matches <paramref name="target"/>.</exception>
    public async Task<string> ObserveTargetCompletionAsync(string target, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!TryResolveAgentId(target, out var agentId))
        {
            throw new ArgumentException($"Unknown agent '{target}'.", nameof(target));
        }

        QueuedSpawn? queued;
        lock (_spawnQueue)
        {
            _ = _queuedSpawns.TryGetValue(agentId, out queued);
        }

        if (queued is null)
        {
            return await ObserveCompletionAsync(agentId, ct);
        }

        // Still queued: wait for the pump to start it, then for the run it starts. Both waits honour
        // the caller's token, so abandoning the wait never disturbs the spawn itself.
        var started = await queued.StateReady.Task.WaitAsync(ct);
        return await started.Completion.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Performs a batch observation of sub-agents matching the given targets (agent IDs or names).
    /// Returns one typed entry per input (in order, preserving duplicates and unknowns) with resolved
    /// identity, status, recent turn snapshots, and summary counts.
    /// </summary>
    public SubAgentObservationBatch CheckAgents(IReadOnlyList<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var entries = new List<SubAgentObservationEntry>(targets.Count);

        foreach (var target in targets)
        {
            var entry = SnapshotObservationEntry(target);
            entries.Add(entry);
        }

        return new SubAgentObservationBatch { Entries = entries.AsReadOnly() };
    }

    /// <summary>
    /// Creates a single typed observation entry for the given target (agent ID or name).
    /// Returns a populated entry if the target resolves to a known sub-agent; otherwise
    /// returns an entry with "not_found" status and null AgentId.
    /// </summary>
    private SubAgentObservationEntry SnapshotObservationEntry(string target)
    {
        // Try to resolve the target to an agent ID (ID first, then name)
        if (!TryResolveAgentId(target, out var agentId))
        {
            // Unknown target: return a minimal not_found entry
            return new SubAgentObservationEntry
            {
                Target = target,
                AgentId = null,
                Name = null,
                Status = "not_found",
                TemplateName = null,
                Task = null,
                RecentTurns = [],
                LastResult = null,
                SendToParentFailed = false,
                SendToParentError = null,
            };
        }

        if (_queuedSpawns.TryGetValue(agentId, out var queued))
        {
            return new SubAgentObservationEntry
            {
                Target = target,
                AgentId = queued.AgentId,
                Name = queued.EffectiveName,
                Status = "queued",
                TemplateName = queued.TemplateName,
                Task = queued.Task,
                RecentTurns = [],
                LastResult = null,
                SendToParentFailed = false,
                SendToParentError = null,
            };
        }

        // Resolved: fetch the state and build a complete entry
        if (!_agents.TryGetValue(agentId, out var state))
        {
            // Shouldn't happen (TryResolveAgentId checks both registries), but handle defensively
            return new SubAgentObservationEntry
            {
                Target = target,
                AgentId = null,
                Name = null,
                Status = "not_found",
                TemplateName = null,
                Task = null,
                RecentTurns = [],
                LastResult = null,
                SendToParentFailed = false,
                SendToParentError = null,
            };
        }

        // Build the typed snapshots for the recent turns
        var recentTurns = state.TurnBuffer
            .ToArray()
            .TakeLast(3)
            .Select(t => new SubAgentTurnSnapshot(
                MessageType: t.MessageType,
                ToolName: t.ToolName,
                ToolArgsPreview: t.ToolArgsPreview,
                TextPreview: t.TextPreview,
                Timestamp: t.Timestamp))
            .ToList()
            .AsReadOnly();

        return new SubAgentObservationEntry
        {
            Target = target,
            AgentId = agentId,
            Name = state.Name,
            Status = state.Status.ToString().ToLowerInvariant(),
            TemplateName = state.TemplateName,
            Task = state.Task,
            RecentTurns = recentTurns,
            LastResult = state.LastResult,
            SendToParentFailed = state.SendToParentFailed,
            SendToParentError = state.SendToParentError,
        };
    }

    /// <summary>
    /// Observes a sub-agent's completion by id, returning its final text (or throwing its
    /// <see cref="SubAgentExecutionException"/> on failure). Used by the sample-app
    /// SubAgentCompletionTriggerSource so a Wait can observe a background sub-agent.
    /// <para>
    /// Observation is NON-DESTRUCTIVE: if the caller's <paramref name="ct"/> fires, this stops
    /// observing (throws <see cref="OperationCanceledException"/>) but leaves the sub-agent's own
    /// run untouched — unlike <see cref="AwaitCompletionAsync"/> (the synchronous-spawn path), which
    /// cancels the sub-agent when the parent turn is abandoned. A trigger observing a
    /// fire-and-forget background sub-agent must leave it running so its automatic relay can resume
    /// if the wait is cancelled.
    /// </para>
    /// </summary>
    public Task<string> ObserveCompletionAsync(string agentId, CancellationToken ct)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        // Await the completion latch directly. On caller-cancel this throws without touching
        // state.Cts, so the sub-agent's run + monitor keep going and its relay resumes.
        return state.Completion.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Sets whether a specific sub-agent's completion is automatically relayed to the parent. A
    /// trigger source waiting on this sub-agent flips it to <c>false</c> at arm time (so the result
    /// arrives once, via the trigger envelope, not twice) and MUST restore it to <c>true</c> if the
    /// wait is cancelled before completion.
    /// </summary>
    public void SetNotifyParentOnCompletion(string agentId, bool value)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        state.NotifyParentOnCompletion = value;
    }

    /// <summary>
    /// Get the list of available template names.
    /// </summary>
    public IReadOnlyList<string> GetTemplateNames()
    {
        return _source.Templates.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Snapshot the tools this manager hands down on a spawn: the already-filtered inheritable
    /// contracts (they exclude the parent loop's <see cref="SubAgentOptions.NonInheritedToolNames"/>
    /// and the Agent-family tools) paired with the handler map they resolve against. Used by a
    /// WorkflowAgent controller to inherit a non-WorkflowAgent ancestor's tools transparently — see
    /// <see cref="InheritableToolSnapshot"/> and <see cref="SubAgentOptions.ExternalInheritableTools"/>.
    /// </summary>
    public InheritableToolSnapshot GetInheritableToolSnapshot() =>
        new(_parentContracts, new ReadOnlyDictionary<string, ToolHandler>(_parentHandlers));

    public async ValueTask DisposeAsync()
    {
        lock (_spawnQueue)
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }
        }

        // Stop the defer-queue pump FIRST so it can't register a new sub-agent into _agents while we
        // tear the collection down below. Cancelling _pumpCts unblocks the pump's WaitAsync calls; the
        // pump then faults any still-queued spawns (so a foreground caller unblocks) and exits.
        try { await _pumpCts.CancelAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel sub-agent spawn pump during disposal");
        }

        try { await _pumpTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sub-agent spawn pump faulted during disposal");
        }

        foreach (var (_, state) in _agents)
        {
            // Each step is isolated to prevent cascading failures:
            // if StopAsync throws, we still await tasks, dispose the agent, etc.
            try { await state.Cts.CancelAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CancelAsync failed for sub-agent {AgentId}", state.AgentId);
            }

            try { await state.Agent.StopAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StopAsync failed for sub-agent {AgentId}", state.AgentId);
            }

            // Await background tasks to ensure clean shutdown
            if (state.RunTask != null)
            {
                try { await state.RunTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RunTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            if (state.MonitorTask != null)
            {
                try { await state.MonitorTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MonitorTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            try { await state.Agent.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DisposeAsync failed for sub-agent {AgentId}", state.AgentId);
            }

            // Presentation-only: the agent's dispose above ends any external observer's current
            // subscription; signal null so an observer that then awaits the replacement unblocks and
            // ends cleanly instead of hanging for a swap that will never come. Never affects execution.
            state.SignalAgentReplaced(null);

            try { await state.DisposeOwnedProviderAgentAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider dispose failed for sub-agent {AgentId}", state.AgentId);
            }

            try { state.Cts.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CTS dispose failed for sub-agent {AgentId}", state.AgentId);
            }

            if (state.Store is IAsyncDisposable disposableStore)
            {
                try { await disposableStore.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Store dispose failed for sub-agent {AgentId}", state.AgentId);
                }
            }
        }

        _agents.Clear();
        _namesToIds.Clear();

        // The agents this manager owned no longer exist, so give the collaboration back their slots and
        // stop advertising them as reachable. Snapshot the keys first: retirement mutates _admissions.
        foreach (var agentId in _admissions.Keys.ToArray())
        {
            RetireFromCollaboration(agentId, AgentCollaborationStatuses.Stopped);
        }

        // Best-effort final dispose of providers whose in-restart retry disposal also failed; their state
        // slots were overwritten by replacements, so this is their last cleanup opportunity.
        foreach (var abandoned in _abandonedProviders)
        {
            try
            {
                if (abandoned is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (abandoned is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Abandoned owned-provider dispose failed at manager disposal");
            }
        }

        _concurrencyGate.Dispose();

        // Drain any spawns still queued at teardown (the pump above already faults those it dequeued;
        // this covers a race where an enqueue landed after the pump exited) so a foreground caller
        // blocked on StateReady unblocks with cancellation instead of hanging, then dispose the
        // defer-queue primitives. Routed through CancelQueuedSpawn for the same reason as the pump's
        // own tail drain: it must hand back the root-wide capacity lease too, not just unblock the
        // caller. RetireFromCollaboration is idempotent, so this is harmless even for an entry the
        // _admissions sweep above already retired.
        lock (_spawnQueue)
        {
            while (_spawnQueue.TryDequeue(out var pending))
            {
                CancelQueuedSpawn(pending, CancellationToken.None);
            }
        }

        _queueSignal.Dispose();
        _pumpCts.Dispose();
    }

    /// <summary>
    /// A spawn deferred because the concurrency pool was full when <see cref="SpawnAsync"/> ran. It
    /// carries everything the pump needs to build the agent later, plus <see cref="StateReady"/> — a
    /// bridge the pump completes with the live <see cref="SubAgentState"/> so a FOREGROUND (blocking)
    /// caller that is awaiting the queued spawn can then await its completion. A BACKGROUND caller does
    /// not await <see cref="StateReady"/> (it already returned a "queued" receipt); its result is relayed
    /// to the parent via the normal <c>NotifyParentOnCompletion</c> path once the pump starts it.
    /// </summary>
    private sealed record QueuedSpawn
    {
        public required string AgentId { get; init; }
        public required string EffectiveName { get; init; }
        public required string TemplateName { get; init; }
        public required SubAgentTemplate Template { get; init; }
        public required string Task { get; init; }
        public required bool RunInBackground { get; init; }
        public string? Model { get; init; }
        public string[]? AddTools { get; init; }
        public string[]? RemoveTools { get; init; }
        public int? ModelIntelligence { get; init; }
        public required AgentLineage Lineage { get; init; }
        public CancellationToken CallerCancellation { get; init; }

        // RunContinuationsAsynchronously so the pump thread that completes this never inline-runs a
        // foreground caller's AwaitCompletionAsync continuation while it should be moving to the next
        // queued spawn.
        public TaskCompletionSource<SubAgentState> StateReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private SubAgentModelRouting BuildRouting(
        SubAgentTemplate template,
        string? modelOverride,
        int? modelIntelligence,
        string? tierResolvedModel,
        string? effectiveModelId)
    {
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            return new SubAgentModelRouting(effectiveModelId, null, "spawn-model");
        }

        if (modelIntelligence is { } spawnTier && tierResolvedModel is not null)
        {
            return new SubAgentModelRouting(effectiveModelId, spawnTier, "spawn-tier");
        }

        if (template.IsModelExplicitlySelected)
        {
            return new SubAgentModelRouting(effectiveModelId, null, "template-model");
        }

        if (template.IsModelTierResolved)
        {
            return new SubAgentModelRouting(effectiveModelId, template.ModelIntelligence, "template-tier");
        }

        return new SubAgentModelRouting(effectiveModelId, null, "parent");
    }

    /// <summary>
    /// Creates a MultiTurnAgentLoop configured for a sub-agent with filtered tools.
    /// </summary>
    private async Task<(IMultiTurnAgent Agent, IConversationStore? Store, IStreamingAgent? OwnedProviderAgent, SubAgentModelRouting Routing)> CreateSubAgentAsync(
        string agentId,
        SubAgentTemplate template,
        string? modelOverride,
        string[]? addTools,
        string[]? removeTools,
        int? modelIntelligence,
        AgentLineage lineage)
    {
        // Guard the free-form `model` override before anything downstream consumes it. The Agent tool
        // exposes `model` as an unconstrained string, so a parent/controller LLM can fill it with an
        // invented id (e.g. "gpt-5", "o3-mini"), a value that belongs in another field ("general-purpose"
        // is a subagent_type; "none" is a placeholder), or a plain typo. Passed straight through, such a
        // value becomes the request model and hard-fails at the provider with a BadRequest — a wasted
        // spawn plus its tokens and a retry storm. When the host supplied a validator and the override
        // does not validate, DROP it (log once) and fall through to tier/parent resolution exactly as if
        // no override had been given. With no validator (the default) the override passes through
        // unchanged, so every non-host consumer keeps the previous behavior.
        if (!string.IsNullOrWhiteSpace(modelOverride)
            && _options.ModelOverrideValidator is { } isKnownModel
            && !isKnownModel(modelOverride))
        {
            _logger.LogWarning(
                "Sub-agent {AgentId} requested unknown model override {ModelOverride}; ignoring it and "
                + "falling back to the tier/parent model",
                agentId,
                modelOverride);
            modelOverride = null;
        }

        // A per-spawn model-intelligence tier resolves to a concrete model ONLY when the spawn set no
        // explicit model override (an explicit model always wins over a tier) AND the host supplied a
        // tier resolver. The resolved id is then fed into option resolution as if it were the requested
        // model, so model + budget inheritance treats it like any pinned model (override > tier > template
        // > parent). A null return (no resolver, unmapped tier, or no routable candidate) leaves the
        // sub-agent on its parent-inherited model, exactly as if no tier had been requested.
        var tierResolvedModel =
            string.IsNullOrWhiteSpace(modelOverride)
            && modelIntelligence is { } tier
            && _options.TierModelResolver is { } tierResolver
                ? tierResolver(tier)
                : null;
        var effectiveModel = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : tierResolvedModel;

        if (TestAgentFactoryOverride != null)
        {
            return (
                TestAgentFactoryOverride(agentId, template),
                TestConversationStoreOverride?.Invoke(agentId, template),
                TestOwnedProviderOverride?.Invoke(agentId, template),
                BuildRouting(
                    template,
                    modelOverride,
                    modelIntelligence,
                    tierResolvedModel,
                    ResolveSubAgentOptions(template.DefaultOptions, effectiveModel, _parentModelId, _parentMaxToken)?.ModelId));
        }

        // Resolve the sub-agent's options with model + budget inheritance (override > tier > template > parent).
        var defaultOptions = ResolveSubAgentOptions(template.DefaultOptions, effectiveModel, _parentModelId, _parentMaxToken);
        IStreamingAgent providerAgent;
        IStreamingAgent? ownedProviderAgent = null;
        IConversationStore? store = null;

        try
        {
            if (template.CharacteristicsAgentFactory is { } characteristicsFactory)
            {
                var modelId = string.IsNullOrWhiteSpace(defaultOptions?.ModelId)
                    ? null
                    : defaultOptions.ModelId;
                var modelExplicitlySelected =
                    !string.IsNullOrWhiteSpace(modelOverride)
                    || template.IsModelExplicitlySelected;
                // A per-spawn tier that resolved to a concrete model counts as a tier-resolved model for
                // this spawn (in addition to a template that was itself tier-authored), so the
                // characteristics gate builds a real provider for it rather than handing back the parent.
                var isModelTierResolved = template.IsModelTierResolved || tierResolvedModel is not null;
                // Inherit the parent's reasoning floor ONLY when this sub-agent made no model choice of its
                // own (parent-model reuse). A template that lowered its Effort keeps that value; one that
                // pins or tier-resolves a model is left un-nudged — "less thinking or a different model"
                // overrides the inherited floor (see SubAgentOptions.InheritedEffort).
                var effectiveEffort = template.Effort
                    ?? (modelExplicitlySelected || isModelTierResolved
                        ? null
                        : _options.InheritedEffort);
                var provider = characteristicsFactory(
                    new SubAgentCharacteristics(modelId, effectiveEffort)
                    {
                        IsModelExplicitlySelected = modelExplicitlySelected,
                        IsModelTierResolved = isModelTierResolved,
                    });
                providerAgent = provider.Agent;
                ownedProviderAgent = provider.OwnsAgent ? provider.Agent : null;

                if (provider.UseParentModel && defaultOptions is not null)
                {
                    defaultOptions = defaultOptions with { ModelId = _parentModelId ?? string.Empty };
                }

                if (provider.ExtraProperties.Count > 0)
                {
                    var requestExtraProperties =
                        defaultOptions?.ExtraProperties
                        ?? ImmutableDictionary<string, object?>.Empty;
                    defaultOptions = (defaultOptions ?? new GenerateReplyOptions()) with
                    {
                        // Template/request values intentionally win over generated reasoning metadata.
                        ExtraProperties = provider.ExtraProperties.SetItems(requestExtraProperties),
                    };
                }
            }
            else
            {
                // A concrete model chosen for this spawn — either a validated `model` override or a
                // per-spawn tier that resolved to a model — needs a provider whose TRANSPORT matches that
                // model. The plain template.AgentFactory() builds the parent/controller's transport, which
                // may differ from the chosen model's (e.g. an Anthropic-transport controller resolving a
                // Responses-transport model, or a Responses-transport controller handed an Anthropic-model
                // override), so it would POST the request to the wrong endpoint and hard-fail with a provider
                // BadRequest (unsupported_api_for_model) plus a retry storm. When the host supplied a tier
                // agent factory, build the transport-correct provider for the effective model and own it for
                // disposal; otherwise fall back to the template's provider (same-transport choices and every
                // parent-model-reuse spawn are unaffected).
                // A copied workflow-controller template may already carry a model resolved from its own
                // frontmatter tier. CharacteristicsAgentFactory is intentionally removed when rebinding that
                // template to the controller, so use the preserved DefaultOptions model as the plain-path
                // provider choice when no per-spawn override/tier supersedes it.
                var plainProviderModel = !string.IsNullOrWhiteSpace(effectiveModel)
                    ? effectiveModel
                    : (template.IsModelExplicitlySelected || template.IsModelTierResolved)
                        && !string.IsNullOrWhiteSpace(defaultOptions?.ModelId)
                        ? defaultOptions.ModelId
                        : null;
                if (!string.IsNullOrWhiteSpace(plainProviderModel)
                    && _options.TierAgentFactory is { } tierAgentFactory)
                {
                    providerAgent = tierAgentFactory(plainProviderModel);
                    ownedProviderAgent = providerAgent;
                }
                else
                {
                    providerAgent = template.AgentFactory();
                }

                // A plain-path delegate (a template with no characteristics factory — e.g. a WorkflowAgent
                // controller's transparent delegate) inherits the parent's PRE-SHAPED reasoning so it thinks
                // like the launching conversation. Applied only when the delegate reuses the parent model (no
                // explicit, per-spawn-tier, OR template-tier model — a different model may use a different
                // transport than the shaped metadata targets) and carries no reasoning of its own, so a template
                // that set ExtraProperties still wins.
                if (_options.InheritedReasoning is { Count: > 0 } inheritedReasoning
                    && string.IsNullOrWhiteSpace(plainProviderModel)
                    && (defaultOptions is null || defaultOptions.ExtraProperties.Count == 0))
                {
                    defaultOptions = (defaultOptions ?? new GenerateReplyOptions()) with
                    {
                        ExtraProperties = inheritedReasoning,
                    };
                }
            }

            // Determine conversation store
            var storeFactory =
                template.ConversationStoreFactory
                ?? _options.DefaultConversationStoreFactory;
            store = storeFactory?.Invoke($"subagent-{agentId}");

            // Build a fresh FunctionRegistry with filtered parent tools
            var registry = new FunctionRegistry();
            var enabledSet = BuildEnabledToolSet(
                template.EnabledTools, addTools, removeTools);
            var inheritedToolNames = new List<string>();

            foreach (var contract in _parentContracts)
            {
                // AskUserQuestion/NotifyClient (#246) are excluded from the ParentTools copy: every
                // MultiTurnAgentLoop constructor — including the child loop built below — registers
                // its OWN correctly-scoped instance of each unconditionally. Copying the parent's
                // entry here too would leave two registrations of the same tool name in the child's
                // fresh registry, and FunctionRegistry.Build()'s default (throwing) conflict
                // resolution would crash this sub-agent's construction.
                if (contract.Name is AskUserQuestionToolProvider.ToolName or NotifyClientToolProvider.ToolName)
                {
                    continue;
                }

                if (enabledSet != null && !enabledSet.Contains(contract.Name))
                {
                    continue;
                }

                if (!_parentHandlers.TryGetValue(contract.Name, out var handler))
                {
                    continue;
                }

                _ = registry.AddFunction(contract, handler, "ParentTools");
                inheritedToolNames.Add(contract.Name);
            }

            // Observability: the effective tool set this sub-agent inherited from its parent. Tool names
            // are content-free system identifiers (no task/prompt/EUII), and this is the boundary that
            // answers "did the delegate actually receive the tools?" — key for workflow transparency.
            _logger.LogDebug(
                "Sub-agent {AgentId} (template {Template}) inherited {InheritedToolCount} parent tool(s): {InheritedToolNames}",
                agentId,
                template.Name,
                inheritedToolNames.Count,
                inheritedToolNames
            );

            // Under collaboration the child ALWAYS gets its own manager, because messaging is not
            // delegation. SubAgentToolProvider already withholds the spawn tools when the child has no
            // delegation budget while still offering GetAgents/SendMessage — but that distinction was
            // unreachable while a depth-limited child was handed no options at all, since the loop only
            // builds the tool provider when it has them. The effect was a leaf registered in the
            // directory with an inbox and a write endpoint, addressable by anyone, and unable to answer:
            // with the default MaxDelegationDepth of 1 that silenced EVERY sub-agent. Handing the child
            // subAgentOptions gives it an independent SubAgentManager (own concurrency pool, own queue)
            // while the shared bundle keeps capacity and depth root-wide, and AdmitToCollaboration still
            // refuses an over-depth spawn defensively. Without collaboration this stays null — the
            // historical recursion guard, where exactly one level of ordinary sub-agents exists.
            var childCollaboration = GetChildCollaboration(agentId);
            var childParticipatesInCollaboration = childCollaboration is not null;

            // Tools that must be built per agent because they act AS that agent (the #244 transcript read
            // is the case in hand) cannot be inherited, so the host supplies a factory and the child gets
            // its OWN instance here — before the loop below snapshots what its own sub-agents inherit, so
            // the child advertises the tool while the grandchild is handed the same factory rather than
            // this instance. Only collaborating children have an agent id to be bound to.
            if (childCollaboration is { } childAgent
                && _options.ChildToolProviderFactory?.Invoke(childAgent.AgentId) is { } childToolProvider)
            {
                _ = registry.AddProvider(childToolProvider);
            }

            return (
                new MultiTurnAgentLoop(
                    providerAgent,
                    registry,
                    threadId: SubAgentThreadId(agentId),
                    // Explicit tool-control overload: a child always gets both browser-hosted client
                    // tools (matching the always-true behavior of the back-compat overload), but that
                    // overload has no descendantQuestionSink parameter — the child's questions must
                    // route to this manager's sink rather than the child's own persist-and-publish path.
                    includeAskUserQuestionTool: true,
                    includeNotifyClientTool: true,
                    systemPrompt: template.SystemPrompt,
                    defaultOptions: defaultOptions,
                    maxTurnsPerRun: template.MaxTurnsPerRun,
                    outputChannelCapacity: _options.OutputChannelCapacity,
                    store: store,
                    logger: _logger is NullLogger ? null : new SubAgentLoopLoggerAdapter(_logger),
                    // Not _options: a child runs its own delegations, so it must not inherit the spawn
                    // authority this level's host holds over ITS spawns (see ForChildLoop).
                    subAgentOptions: childParticipatesInCollaboration ? _childOptions : null,
                    subAgentTemplateSource: childParticipatesInCollaboration ? _source : null,
                    lifecycleServices: MultiTurnLifecycleServices.ForSpawnedAgent(
                        _lifecycleServices, lineage),
                    collaboration: childCollaboration,
                    descendantQuestionSink: _descendantQuestionSink
                ),
                store,
                ownedProviderAgent,
                // Capture the FINAL resolved model and the winning selection input together. This is the
                // authoritative presentation record; callers must not reconstruct routing from the LLM's raw
                // Agent arguments because workflow authority may have replaced placeholder values.
                BuildRouting(
                    template,
                    modelOverride,
                    modelIntelligence,
                    tierResolvedModel,
                    defaultOptions?.ModelId)
            );
        }
        catch
        {
            // Roll back partial construction. Attempt each cleanup INDEPENDENTLY so a failure in one
            // (e.g. store disposal throwing) does not skip the other (provider disposal), and let the
            // ORIGINAL construction exception propagate — cleanup failures are logged, never rethrown,
            // so they can't mask the real cause.
            if (store is IAsyncDisposable disposableStore)
            {
                try { await disposableStore.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Store dispose failed while rolling back sub-agent {AgentId} construction",
                        agentId
                    );
                }
            }

            try { await DisposeProviderAgentAsync(ownedProviderAgent); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider dispose failed while rolling back sub-agent {AgentId} construction",
                    agentId
                );
            }

            throw;
        }
    }

    /// <summary>
    /// Disposes a provider agent regardless of whether it exposes async or synchronous disposal.
    /// No-op when <paramref name="provider"/> is null or implements neither disposal interface.
    /// </summary>
    private static async ValueTask DisposeProviderAgentAsync(IStreamingAgent? provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Resolves the sub-agent's <see cref="GenerateReplyOptions"/> with model inheritance: an explicit
    /// per-spawn <paramref name="modelOverride"/> wins, else the template's own
    /// <see cref="GenerateReplyOptions.ModelId"/>, else the PARENT agent's model
    /// (<paramref name="parentModelId"/>). The parent fallback stops a template that sets no model
    /// (e.g. the built-in sub-agents) from letting the provider agent use its hardcoded default model,
    /// which often isn't valid on the parent's backend (observed: a sub-agent sending
    /// <c>claude-3-sonnet-20240229</c> to a backend that only serves the parent's model → HTTP 400
    /// <c>model_not_supported</c>). The per-turn output budget inherits the same way: the template's own
    /// <see cref="GenerateReplyOptions.MaxToken"/> wins, else the parent's effective budget
    /// (<paramref name="parentMaxToken"/>) — so a delegate gets the spawning conversation's headroom
    /// instead of the provider's 4096 default, which truncates a tool-call's argument JSON at
    /// <c>stop_reason=max_tokens</c>. Any other template option fields are preserved. Returns null only
    /// when nothing is available anywhere AND the template carried no options, so the previous
    /// "inherit the provider's own defaults" behavior is unchanged when there is genuinely nothing to set.
    /// </summary>
    internal static GenerateReplyOptions? ResolveSubAgentOptions(
        GenerateReplyOptions? templateDefaults,
        string? modelOverride,
        string? parentModelId,
        int? parentMaxToken = null)
    {
        var templateModel = templateDefaults?.ModelId;
        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : !string.IsNullOrWhiteSpace(templateModel)
                ? templateModel
                : parentModelId;

        var hasModel = !string.IsNullOrWhiteSpace(model);
        // Only apply an inherited budget when the template didn't set its own — the template always wins.
        var inheritBudget = templateDefaults?.MaxToken is null && parentMaxToken is not null;

        if (!hasModel && !inheritBudget)
        {
            // Nothing to set — preserve the exact previous behavior (return the template unchanged, null
            // included) so a genuinely empty resolution still yields the provider's own defaults.
            return templateDefaults;
        }

        var resolved = templateDefaults ?? new GenerateReplyOptions();
        if (hasModel)
        {
            // hasModel == !IsNullOrWhiteSpace(model), so model is non-null here (the compiler can't infer it).
            resolved = resolved with { ModelId = model! };
        }

        if (inheritBudget)
        {
            resolved = resolved with { MaxToken = parentMaxToken };
        }

        return resolved;
    }

    /// <summary>
    /// Builds the effective set of enabled tool names from template filter + overrides.
    /// Returns null when all tools should be available (no filtering).
    /// </summary>
    internal static HashSet<string>? BuildEnabledToolSet(
        IReadOnlyList<string>? templateEnabledTools,
        string[]? addTools,
        string[]? removeTools)
    {
        if (templateEnabledTools == null
            && addTools == null
            && removeTools == null)
        {
            return null; // No filtering
        }

        HashSet<string>? result = null;

        if (templateEnabledTools != null)
        {
            result = [.. templateEnabledTools];
        }

        if (addTools != null)
        {
            result ??= [];
            foreach (var tool in addTools)
            {
                _ = result.Add(tool);
            }
        }

        if (removeTools != null)
        {
            if (result == null)
            {
                // removeTools without a base set: cannot remove from "all tools"
                // since we don't know the full tool list here. Treat as error.
                throw new InvalidOperationException(
                    "Cannot specify removeTools without enabledTools or addTools. " +
                    "Use template EnabledTools to define the base set, then remove from it.");
            }

            foreach (var tool in removeTools)
            {
                _ = result.Remove(tool);
            }
        }

        return result;
    }

    /// <summary>
    /// Monitors a sub-agent's output, buffering turn summaries and detecting completion.
    /// Relays completion/error results back to the parent agent.
    /// </summary>
    /// <param name="state">The sub-agent's state.</param>
    /// <param name="gateGuard">
    /// The <see cref="GateReleaseGuard"/> for the gate-acquisition epoch this monitor was
    /// started for (created by <c>SpawnAsync</c> or <c>RestartRunAsync</c> right after their
    /// respective <c>_concurrencyGate.WaitAsync</c> succeeded). Passed explicitly, rather than
    /// read from a shared field on <paramref name="state"/>, so a later restart's own guard can
    /// never be conflated with this monitor's - see <see cref="GateReleaseGuard"/>.
    /// </param>
    /// <param name="runGeneration">
    /// The run generation this monitor belongs to (0 for the initial spawn; the restart's generation
    /// otherwise). On a monitor fault, the terminal Error is recorded against this generation so a
    /// racing restart's <c>TryArmRunning</c> cannot resurrect the faulted run to Running.
    /// </param>
    /// <param name="ct">Cancellation token for this run's lifetime.</param>
    private async Task MonitorSubAgentAsync(
        SubAgentState state,
        GateReleaseGuard gateGuard,
        long runGeneration,
        CancellationToken ct)
    {
        string? lastTextContent = null;

        // The concurrency slot is released exactly once per gate-acquisition epoch via
        // gateGuard.ReleaseOnce (an Interlocked-guarded no-op past the first call). A single
        // monitor can observe multiple RunCompletedMessages - a background sub-agent continued
        // in place via SendMessage runs again under the same monitor - but the slot was
        // acquired once (at spawn/restart), so it must be released once: on the first
        // completion, and never again. Releasing per-completion would over-release the
        // semaphore (eventually SemaphoreFullException) and break the concurrency limit. The
        // same gateGuard instance is also held by SpawnAsync/RestartRunAsync's own failure
        // cleanup, so whichever path notices termination first is the one that actually
        // releases it.

        // Subscribers receive raw streaming deltas: the publishing middleware runs
        // upstream of the joiner, so the consolidated TextMessage never reaches here —
        // only TextUpdateMessage deltas do. Reconstruct the sub-agent's final answer by
        // accumulating deltas per generation and keeping the latest generation's text.
        var textBuilder = new StringBuilder();
        string? textGenerationId = null;

        try
        {
            await foreach (var msg in state.Agent.SubscribeAsync(ct))
            {
                var summary = CreateTurnSummary(msg);
                if (summary != null)
                {
                    state.TurnBuffer.Enqueue(summary);
                    while (state.TurnBuffer.Count > 10)
                    {
                        _ = state.TurnBuffer.TryDequeue(out _);
                    }
                }

                // Fold this descendant's usage into the root conversation total (issue #196). Without
                // this, a sub-agent's (and workflow task's — same relay path) token spend was dropped.
                if (_usageSink is not null && msg is UsageMessage usageMessage)
                {
                    _usageSink.RecordUsage(BuildDescendantUsageRecord(usageMessage, state));

                    // Persist immediately so a late/background descendant's spend is durable even if no
                    // further primary usage event follows to flush it (#196).
                    if (_persistUsageAsync is not null)
                    {
                        _ = _persistUsageAsync();
                    }
                }

                // Track the last assistant text for the completion result. Subscribers
                // receive raw deltas, so accumulate TextUpdateMessage deltas per
                // generation; a consolidated TextMessage (non-streaming mock) is taken as-is.
                if (msg is TextUpdateMessage tu
                    && tu.Role == Role.Assistant
                    && !tu.IsThinking)
                {
                    // A new generation resets the accumulator so we keep only the most
                    // recent assistant message, not earlier turns' text.
                    if (!string.Equals(textGenerationId, tu.GenerationId, StringComparison.Ordinal))
                    {
                        textGenerationId = tu.GenerationId;
                        _ = textBuilder.Clear();
                    }

                    _ = textBuilder.Append(tu.Text);
                    lastTextContent = textBuilder.ToString();
                }
                else if (msg is TextMessage tm
                    && tm.Role == Role.Assistant
                    && !tm.IsThinking)
                {
                    textGenerationId = tm.GenerationId;
                    _ = textBuilder.Clear().Append(tm.Text);
                    lastTextContent = tm.Text;
                }

                if (msg is RunCompletedMessage rcm)
                {
                    state.LastResult = lastTextContent;

                    // A run reporting HasPendingMessages == false is not necessarily done: a child that
                    // just deferred on its own AskUserQuestion reports the exact same flag value (it only
                    // tracks queued NEXT-turn inputs — see MultiTurnAgentBase.CompleteRunAsync), yet its
                    // loop (state.Agent) still holds the deferred call live in its own registry. Compute
                    // that HERE, before deciding whether to release the concurrency slot, so the decision
                    // and HandleRunCompletionAsync's own terminal/non-terminal branching never disagree.
                    var awaitingQuestion = !rcm.HasPendingMessages
                        && !rcm.IsError
                        && await HasPendingAskUserQuestionAsync(state);

                    // Release the slot BEFORE the (possibly slow/backpressured) parent relay in
                    // HandleRunCompletionAsync — but ONLY for a genuinely TERMINAL completion. A
                    // nonterminal completion — either HasPendingMessages (another run will follow) or a
                    // parked AskUserQuestion (the SAME loop/provider stay live awaiting the human's
                    // answer) — keeps this sub-agent's resources busy, so releasing its permit now would
                    // let another sub-agent start while this one is still active, exceeding
                    // MaxConcurrentSubAgents. The permit is held until the run truly ends: the terminal
                    // completion here, or the monitor's finally if the stream ends first. Idempotent, so
                    // that fallback release is a safe no-op afterward.
                    if (!rcm.HasPendingMessages && !awaitingQuestion)
                    {
                        gateGuard.ReleaseOnce(_concurrencyGate);
                    }

                    await HandleRunCompletionAsync(state, rcm, lastTextContent, awaitingQuestion, ct);
                    lastTextContent = null;
                    textGenerationId = null;
                    _ = textBuilder.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (ChannelClosedException)
        {
            // Subscription channel closed - expected during disposal
        }
        catch (Exception ex)
        {
            // Record the fault as a generation-aware terminal Error (not a raw Status write): a restarted
            // run's monitor can fault before RestartRunAsync's TryArmRunning(runGeneration) executes, and
            // that publish must observe this generation's terminal record and refuse to overwrite Error
            // with Running (which would advertise a dead run).
            var faulted = state.MarkRunFaulted(runGeneration);
            state.SendToParentError = $"Monitor failed: {ex.Message}";
            _logger.LogError(
                ex,
                "Error monitoring sub-agent {AgentId}",
                state.AgentId);

            if (faulted)
            {
                // The run is terminally Error, so the collaboration directory must say so too. Left at
                // "running", every other agent in the hierarchy still sees this child as live: a steer
                // or a completion barrier addressed to it would wait on a run that can never answer.
                // Reached only when MarkRunFaulted(runGeneration) accepted THIS generation as the
                // terminal one, so a newer restart's Running publish is never clobbered.
                SyncCollaborationStatus(state.AgentId, AgentCollaborationStatuses.Error);

                // Same causal push HandleRunCompletionAsync performs for a graceful terminal: a
                // background child that never writes metadata again would otherwise leave its
                // persisted state claiming "running" forever. Skipped when a newer restart already
                // superseded this generation, so this can't race that restart's own Running publish.
                await PersistTerminalStateAsync(state);
            }

            // Fault the completion latch: the run ended here without ever producing a
            // RunCompletedMessage, so nothing else will resolve it, and an
            // AwaitCompletionAsync/ObserveCompletionAsync waiter would otherwise hang
            // forever. TryCompleteWithException is a no-op if already resolved.
            _ = state.TryCompleteWithException(ex);
        }
        finally
        {
            // Fallback: if no completion was ever handled (the stream ended, was
            // cancelled, or faulted before any RunCompletedMessage), the slot still
            // needs releasing. ReleaseOnce is idempotent, so this is a no-op when
            // a completion already released it.
            gateGuard.ReleaseOnce(_concurrencyGate);
        }
    }

    /// <summary>
    /// Handles a sub-agent run completion: resolves the synchronous completion signal
    /// and, for background spawns/continuations, relays the result to the parent.
    /// </summary>
    /// <param name="state">The sub-agent's state.</param>
    /// <param name="rcm">The run-completion message the monitor just observed.</param>
    /// <param name="lastTextContent">The run's last accumulated assistant text, if any.</param>
    /// <param name="awaitingQuestion">
    /// Precomputed by the monitor loop (via <see cref="HasPendingAskUserQuestionAsync"/>) BEFORE this
    /// call, so the concurrency-gate release decision and this method's terminal/non-terminal branching
    /// always agree on the same answer for the same <see cref="RunCompletedMessage"/>.
    /// </param>
    /// <param name="ct">Cancellation token for this run's lifetime.</param>
    private async Task HandleRunCompletionAsync(
        SubAgentState state,
        RunCompletedMessage rcm,
        string? lastTextContent,
        bool awaitingQuestion,
        CancellationToken ct)
    {
        // A run that still has queued messages is NOT terminal: another run will follow and reuse the
        // same loop/provider, so neither flip the sub-agent terminal nor dispose its owned provider
        // here — the final completion (HasPendingMessages == false) resolves and, if owned, disposes.
        if (rcm.HasPendingMessages)
        {
            return;
        }

        if (awaitingQuestion)
        {
            // A child that just deferred on its own AskUserQuestion reports the exact same
            // HasPendingMessages == false a genuinely finished run would (that flag only tracks queued
            // NEXT-turn inputs — see MultiTurnAgentBase.CompleteRunAsync), yet the loop itself
            // (state.Agent) still holds the deferred call live in its own registry — it is NOT done.
            // Treat this as explicitly non-terminal: never flip the sub-agent's status, persist a
            // Completed/Error state, or dispose its owned provider — the loop must stay exactly as it
            // is so that resolving the deferred call (whichever path does so) starts a new run against
            // the SAME live provider, not a rebuilt one. Above all, never resolve state.Completion here:
            // a foreground caller blocked on it must keep waiting for the REAL answer, and the answer's
            // eventual run is what performs the one true final completion (see the non-awaiting branch
            // below, invoked again for that later RunCompletedMessage).
            var awaitingResultText =
                $"<sub-agent name=\"{state.TemplateName}\" " +
                $"id=\"{state.AgentId}\">\n" +
                $"[AwaitingAnswer] Task: {state.Task}\n" +
                $"Result: (awaiting the human's answer to a pending question)\n" +
                $"</sub-agent>";

            // Surface a descendant's pending question to the root conversation immediately (#246): the
            // client navigates only on this distinct kind (never SubAgentCompletion/ClientNotification),
            // and this fires regardless of NotifyParentOnCompletion — a foreground (blocking) spawn's
            // caller is still parked awaiting the child's Task, so this is the ONLY way the human learns
            // the conversation needs their input rather than appearing to hang. SourceToolCallId is THIS
            // state's own agent id: HandleRunCompletionAsync runs once per level of nesting, so whichever
            // level's direct child actually parked is the one attributed here, however deep it sits.
            await _descendantQuestionSink(
                NotifyMessage.Create(
                    NotifyKinds.DescendantQuestion,
                    detail: awaitingResultText,
                    sourceToolName: "Agent",
                    sourceToolCallId: state.AgentId,
                    label: state.TemplateName),
                ct);

            if (state.NotifyParentOnCompletion)
            {
                await SendToParentAsync(state, awaitingResultText);
            }

            return;
        }

        // Transition out of Running BEFORE disposing the owned provider, atomically against a
        // concurrent SendMessageAsync (see SubAgentState.BeginContinuation). This blocks new inject
        // admissions and waits for any in-flight admitted send to finish, so the disposal below can
        // never overlap a send through the provider; a racing continuation then observes the finished
        // status and takes the restart path (which recreates a fresh provider).
        await state.BeginTerminalDisposalAsync(rcm.IsError);

        // Push the terminal transition through the child's OWN store now, causally, rather than
        // relying on the child's next metadata write — a background sub-agent that never receives
        // another message never writes metadata again, and the exact terminal status/timestamp
        // must still be persisted. See PersistTerminalStateAsync for why this is safe to layer on
        // top of the child's existing (unchanged) post-run metadata save.
        await PersistTerminalStateAsync(state);

        // Publish the terminal status but keep the directory entry live: a completed background
        // sub-agent is still addressable, and a collaboration message to it restarts it in place.
        SyncCollaborationStatus(
            state.AgentId,
            rcm.IsError ? AgentCollaborationStatuses.Error : AgentCollaborationStatuses.Completed);

        // The concurrency slot is released by the monitor (via its GateReleaseGuard), exactly
        // once per gate-acquisition epoch — not here, because a single monitor may handle
        // several completions when a background sub-agent is continued in place via SendMessage.
        // An explicit/tier provider is scoped to a single completed run. Dispose it before any
        // completion relay can block; a later continuation recreates its loop and provider through
        // the same characteristics factory, while borrowed parent/template agents remain untouched.
        // EndTerminalDisposal clears the terminating flag so a later restart's re-arm admits injects.
        try
        {
            try { await state.DisposeOwnedProviderAgentAsync(); }
            catch (Exception ex)
            {
                // Poison the run's provider: a continuation must rebuild a fresh one rather than reuse
                // this partially-disposed instance (the restart path retries disposing it). Clearing the
                // terminating flag below still lets a restart proceed — but against a fresh provider.
                state.MarkOwnedProviderTerminalDisposeFailed();
                _logger.LogWarning(
                    ex,
                    "Provider dispose failed at completion for sub-agent {AgentId}",
                    state.AgentId
                );
            }
        }
        finally
        {
            state.EndTerminalDisposal();
        }

        if (rcm.IsError)
        {
            var errorText =
                $"<sub-agent name=\"{state.TemplateName}\" " +
                $"id=\"{state.AgentId}\">\n" +
                $"[Error] Task: {state.Task}\n" +
                $"Error: {rcm.ErrorMessage}\n" +
                $"</sub-agent>";

            // Fault the synchronous waiter (if any); a background spawn observes
            // this fault via ObserveCompletionFaults so it is never unobserved.
            _ = state.TryCompleteWithException(
                new SubAgentExecutionException(
                    state.AgentId, state.TemplateName, rcm.ErrorMessage));

            if (state.NotifyParentOnCompletion)
            {
                await SendToParentAsync(state, errorText);
            }
        }
        else
        {
            // Genuinely terminal at this point: awaitingQuestion (precomputed by the caller) already
            // returned early above when true, so a run reaching here truly has nothing more to do.
            var result = lastTextContent ?? "(no text response)";

            var resultText =
                $"<sub-agent name=\"{state.TemplateName}\" " +
                $"id=\"{state.AgentId}\">\n" +
                $"[Completed] Task: {state.Task}\n" +
                $"Result: {result}\n" +
                $"</sub-agent>";

            _ = state.TryCompleteWithResult(result);

            if (state.NotifyParentOnCompletion)
            {
                await SendToParentAsync(state, resultText);
            }
        }

    }

    /// <summary>
    /// Mirrors the same pending-question check <c>MultiTurnAgentPool.HasPendingAskUserQuestionAsync</c>
    /// performs for pooled top-level agents: true when the child's own loop (not its now-possibly-disposed
    /// owned provider) still has a deferred <see cref="AskUserQuestionToolProvider.ToolName"/> call
    /// parked. Returns false for any non-<see cref="MultiTurnAgentLoop"/> agent (degrades gracefully
    /// rather than throwing).
    /// </summary>
    private static async Task<bool> HasPendingAskUserQuestionAsync(SubAgentState state)
    {
        if (state.Agent is not MultiTurnAgentLoop loop)
        {
            return false;
        }

        var deferred = await loop.GetDeferredToolCallsAsync();
        return deferred.Any(d =>
            string.Equals(d.FunctionName, AskUserQuestionToolProvider.ToolName, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when any live descendant in this manager's subtree — a direct child, or a further-nested
    /// descendant reached through a child's own <see cref="SubAgentManager"/> — has an unresolved
    /// <c>AskUserQuestion</c> parked. Used by <c>MultiTurnAgentPool.HasPendingAskUserQuestionAsync</c>
    /// (LmAgentInfra) to HARD-block a mode/provider switch (#246): recreating the primary agent disposes its ENTIRE
    /// live descendant tree, not just the primary's own deferred calls, so a switch could otherwise
    /// silently orphan a question the human hasn't answered yet just because it belongs to a child
    /// rather than the primary. Bounded to the CURRENT live tree — a snapshot of <see cref="_agents"/>
    /// keys taken at the start of the call; a descendant spawned or removed mid-traversal is simply not
    /// reflected. Recursion depth is naturally bounded because only a nested-root loop (e.g. a workflow
    /// controller) ever constructs its own <see cref="SubAgentManager"/> for its children — a plain
    /// Agent-spawned sub-agent never does (see <see cref="CreateSubAgentAsync"/>).
    /// </summary>
    public async Task<bool> HasPendingAskUserQuestionInDescendantsAsync(CancellationToken ct = default)
    {
        foreach (var agentId in _agents.Keys)
        {
            if (!TryGetAgent(agentId, out var agent) || agent is not MultiTurnAgentLoop loop)
            {
                continue;
            }

            var deferred = await loop.GetDeferredToolCallsAsync(ct);
            if (deferred.Any(d =>
                string.Equals(d.FunctionName, AskUserQuestionToolProvider.ToolName, StringComparison.Ordinal)))
            {
                return true;
            }

            if (loop.SubAgentManager is { } childManager
                && await childManager.HasPendingAskUserQuestionInDescendantsAsync(ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Actively persists a sub-agent's just-flipped terminal status through its OWN
    /// <see cref="IConversationStore"/>, at the moment of transition. This is the causal push:
    /// the child's own post-run metadata write (<c>MultiTurnAgentBase.UpdateMetadataAsync</c>) only
    /// ever calls <see cref="IConversationStore.SaveMetadataAsync"/>, and a background sub-agent that
    /// never receives another message never calls it again — so without this push, the exact
    /// terminal status/timestamp would only ever be known in memory. Uses
    /// <see cref="IConversationStore.UpdateMetadataAsync"/> so a concrete host integration (e.g. the
    /// sample's provenance-stamping store decorator) can layer its own metadata projection onto this
    /// touch exactly as it already does for the child's own writes; the manager itself stays
    /// completely unaware of any such projection — no second registry, just one extra write through
    /// the same per-child store the manager already holds.
    /// </summary>
    private async Task PersistTerminalStateAsync(SubAgentState state)
    {
        if (state.Store is null)
        {
            return;
        }

        try
        {
            var threadId = state.Agent.ThreadId;
            await state.Store.UpdateMetadataAsync(
                threadId,
                existing => existing
                    ?? new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist terminal state for sub-agent {AgentId}",
                state.AgentId);
        }
    }

    /// <summary>
    /// Observes faults on a completion the caller will not await (background path),
    /// so a faulted run never surfaces as an UnobservedTaskException during GC.
    /// </summary>
    private static void ObserveCompletionFaults(SubAgentState state)
    {
        _ = state.Completion.Task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Awaits a synchronous sub-agent run's completion. If the parent abandons the wait
    /// (its cancellation token fires), the sub-agent is cancelled too so it stops promptly
    /// and frees its concurrency slot instead of running on, orphaned.
    /// The wait has no independent timeout ceiling: it is bounded only by the caller's
    /// <paramref name="ct"/> (the parent turn's lifetime), while runaway sub-agent runs are
    /// independently bounded by the template's MaxTurnsPerRun.
    /// </summary>
    private static async Task<string> AwaitCompletionAsync(SubAgentState state, CancellationToken ct)
    {
        try
        {
            return await state.Completion.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                await state.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The sub-agent was already torn down (e.g. concurrent disposal); nothing to cancel.
            }

            throw;
        }
    }

    private async Task SendToParentAsync(SubAgentState state, string text)
    {
        try
        {
            // Deliver the completion as a typed notification, not a plain user turn: the parent LLM
            // reads it as an async response to the sub-agent spawn, and the UI renders a pill. The raw
            // <sub-agent …> block stays in Detail so downstream parsers (e.g. LmWorkflow) still find it.
            _ = await _parentAgent.SendAsync(
                [NotifyMessage.Create(
                    NotifyKinds.SubAgentCompletion,
                    detail: text,
                    sourceToolName: "Agent",
                    sourceToolCallId: state.AgentId,
                    label: state.TemplateName)]);
        }
        catch (Exception ex)
        {
            state.SendToParentFailed = true;
            state.SendToParentError = ex.Message;
            _logger.LogError(
                ex, "Failed to send sub-agent result to parent");
        }
    }

    /// <summary>
    /// Creates a lightweight turn summary from a message for the peek buffer.
    /// Returns null for internal/control messages that should be skipped.
    /// </summary>
    /// <summary>
    /// Maps a descendant's <see cref="UsageMessage"/> into a <see cref="UsageRecord"/> for the root
    /// ledger via the shared <see cref="UsageRecordMapper"/>. The model is the sub-agent's effective model
    /// captured at creation (<see cref="SubAgentState.EffectiveModelId"/> — the final resolved model after
    /// override/template/parent inheritance AND the characteristics path; without it a split-model sub-agent's
    /// spend would be mis-attributed to the parent model), falling back to the parent model only when creation
    /// recorded none. The <c>RootConversationId</c> placeholder is re-stamped to the ledger's root by
    /// <see cref="UsageLedger.RecordUsage"/>.
    /// </summary>
    private UsageRecord BuildDescendantUsageRecord(UsageMessage message, SubAgentState state) =>
        UsageRecordMapper.FromUsageMessage(
            message,
            // Use the sub-agent's OWN-loop thread id (not the bare agent id) so the relayed record shares
            // one canonical ProviderAttemptId with the sub-agent's own-loop usage capture, which keys under
            // this same thread id. Two id-spaces for one provider call would be a cross-conversation dedup
            // landmine (#196, BUG 3).
            SubAgentThreadId(state.AgentId),
            UsageExecutionKind.SubAgent,
            state.EffectiveModelId ?? _parentModelId);

    /// <summary>
    /// Builds the conversation thread id for a sub-agent's own loop from its agent id. Centralized so the
    /// sub-agent loop construction and the descendant usage relay stamp the SAME id, keeping one canonical
    /// <see cref="UsageRecord.ProviderAttemptId"/> per provider call across the own-loop and relay paths.
    /// </summary>
    internal static string SubAgentThreadId(string agentId) => $"subagent-{agentId}";

    /// <summary>
    /// Derives the fixed-width (8 hex) conversation tag that scopes a spawned sub-agent's id to the
    /// LAUNCHING conversation, from the parent agent's thread id. Deterministic (same parent thread id
    /// always yields the same tag, so it is stable across a conversation and its resumes) and content-free
    /// (a hash, not the raw id — sub-agent ids are compact handles that nest and are never resumed by id,
    /// so a compact digest is preferred over the raw conversation id used for the resumable controller
    /// thread). A null/empty parent thread id (e.g. a CLI-backed parent with no thread) yields a stable
    /// zero tag so the id shape is uniform. Uses FNV-1a/32 — no cryptographic strength is needed, only a
    /// deterministic, well-distributed short digest.
    /// </summary>
    internal static string ConversationTag(string? parentThreadId)
    {
        const uint FnvOffsetBasis = 2166136261;
        const uint FnvPrime = 16777619;

        var hash = FnvOffsetBasis;
        if (!string.IsNullOrEmpty(parentThreadId))
        {
            foreach (var c in parentThreadId)
            {
                hash ^= c;
                hash *= FnvPrime;
            }
        }

        return hash.ToString("x8");
    }

    private static SubAgentTurnSummary? CreateTurnSummary(IMessage msg)
    {
        return msg switch
        {
            TextMessage tm when tm.Role == Role.Assistant && !tm.IsThinking => new SubAgentTurnSummary
            {
                MessageType = "text",
                TextPreview = Truncate(tm.Text, 100),
            },
            ToolCallMessage tc => new SubAgentTurnSummary
            {
                MessageType = "tool_call",
                ToolName = tc.FunctionName,
                ToolArgsPreview = Truncate(tc.FunctionArgs, 80),
            },
            ToolCallResultMessage tcr => new SubAgentTurnSummary
            {
                MessageType = "tool_result",
                ToolName = tcr.ToolName,
                TextPreview = Truncate(tcr.Result, 100),
            },
            _ => null,
        };
    }

    private static string? Truncate(string? text, int maxLength)
    {
        return text == null
            ? null
            : text.Length <= maxLength
            ? text
            : text[..maxLength] + "...";
    }

    /// <summary>
    /// Adapts non-generic ILogger to ILogger&lt;MultiTurnAgentLoop&gt;
    /// so the sub-agent loop receives a properly typed logger.
    /// </summary>
    private sealed class SubAgentLoopLoggerAdapter(ILogger inner) : ILogger<MultiTurnAgentLoop>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return inner.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return inner.IsEnabled(logLevel);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
