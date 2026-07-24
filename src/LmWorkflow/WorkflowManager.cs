using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmWorkflow;

/// <summary>How <see cref="WorkflowManager.StartAsync"/> should return to the caller.</summary>
public enum WorkflowStartMode
{
    /// <summary>Block until the workflow reaches a terminal state, then return the terminal result inline.</summary>
    Sync,

    /// <summary>Return immediately once validation passes; the run continues on a background task.</summary>
    Async,
}

/// <summary>
///     The well-known <see cref="WorkflowRunResult.Status"/> values a workflow run reports. Kept as named
///     constants (like <c>NotifyKinds</c>) so producers and the <c>BuildNotifyDetail</c> dispatch never drift
///     on a raw string literal.
/// </summary>
public static class WorkflowStatuses
{
    /// <summary>An async run was accepted and is now running on a background task.</summary>
    public const string Started = "started";

    /// <summary>The run has not yet reached a terminal state.</summary>
    public const string Running = "running";

    /// <summary>The run reached a terminal node; <see cref="WorkflowRunResult.Result"/> carries the outcome.</summary>
    public const string Completed = "completed";

    /// <summary>The run ended without a terminal node (fault, cancellation, or turn-budget exhaustion).</summary>
    public const string Failed = "failed";

    /// <summary>A <c>WaitWorkflow</c> call returned before the run finished; the run is still going.</summary>
    public const string Timeout = "timeout";
}

/// <summary>
///     The status/result of a workflow run, returned by <see cref="WorkflowManager.StartAsync"/> (sync mode
///     terminal, or an async <c>started</c> receipt), <see cref="WorkflowManager.Check"/>, and
///     <see cref="WorkflowManager.WaitAsync"/>. Not every field is populated in every status — e.g. a
///     <c>started</c>/<c>running</c>/<c>timeout</c> result carries no terminal <see cref="Result"/>.
/// </summary>
public sealed record WorkflowRunResult
{
    /// <summary>The caller-supplied opaque workflow handle.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>One of the <see cref="WorkflowStatuses"/> values (started/running/completed/failed/timeout).</summary>
    public required string Status { get; init; }

    /// <summary>The validated final result when <see cref="Status"/> is <c>completed</c>; otherwise <c>null</c>.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>A human-readable failure reason when <see cref="Status"/> is <c>failed</c>; otherwise <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>The node the controller is currently positioned on, when known.</summary>
    public string? CurrentNodeId { get; init; }

    /// <summary>Whether the workflow reached a terminal node.</summary>
    public bool IsComplete { get; init; }

    /// <summary>The per-node task outputs channel snapshot (a deep copy), when populated.</summary>
    public JsonObject? Outputs { get; init; }

    /// <summary>The scoped notes channel snapshot (a deep copy), when populated.</summary>
    public JsonObject? Notes { get; init; }
}

/// <summary>
///     A lightweight, presentation-only snapshot of one tracked workflow run, returned by
///     <see cref="WorkflowManager.ListRuns"/>. Carries no live handles, so it is safe to hand to a host UI
///     enumerating active/finished runs (e.g. as tabs) without exposing the run graph.
/// </summary>
public sealed record WorkflowRunSummary
{
    /// <summary>The caller-supplied opaque workflow handle.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>The run's objective (from its definition), or the <see cref="WorkflowId"/> when none is set.</summary>
    public required string Objective { get; init; }

    /// <summary>One of the <see cref="WorkflowStatuses"/> values (running/completed/failed).</summary>
    public required string Status { get; init; }

    /// <summary>The node the controller is currently (or was last) positioned on, when known.</summary>
    public string? CurrentNodeId { get; init; }

    /// <summary>When the run was reserved/started.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    ///     The run's last observed activity — its start while running, or the terminal transition once it
    ///     finishes. The manager does not observe intermediate controller turns, so a running run reports its
    ///     start time as the recency floor.
    /// </summary>
    public DateTimeOffset? LastActivityUtc { get; init; }
}

/// <summary>Thrown when a <c>workflowId</c> is already reserved (in flight or completed but still queryable).</summary>
public sealed class DuplicateWorkflowException(string workflowId)
    : Exception($"A workflow with id '{workflowId}' already exists. Choose a distinct workflowId.")
{
    /// <summary>The conflicting workflow id.</summary>
    public string WorkflowId { get; } = workflowId;
}

/// <summary>Thrown by <c>CheckWorkflow</c>/<c>WaitWorkflow</c> for an unrecognized or expired <c>workflowId</c>.</summary>
public sealed class UnknownWorkflowException(string workflowId)
    : Exception($"No workflow with id '{workflowId}' is known (it was never started, or was lost to a restart).")
{
    /// <summary>The unrecognized workflow id.</summary>
    public string WorkflowId { get; } = workflowId;
}

/// <summary>Thrown when the concurrent-workflow cap is reached and no slot frees up within the wait window.</summary>
public sealed class WorkflowCapacityException(int maxConcurrentWorkflows)
    : Exception(
        $"The concurrent-workflow limit ({maxConcurrentWorkflows}) is reached. "
            + "Wait for a running workflow to finish, or increase maxConcurrentWorkflows."
    )
{
    /// <summary>The configured concurrency cap.</summary>
    public int MaxConcurrentWorkflows { get; } = maxConcurrentWorkflows;
}

/// <summary>
///     Owns the lifecycle of workflows launched via <c>StartWorkflowAgent</c>: validates the definition, bounds
///     concurrency, spins up an isolated controller loop (via <see cref="WorkflowSession"/>) with a
///     restricted tool surface (no <c>SetWorkflow</c>; a controller always gets a pre-authored definition),
///     and exposes non-blocking status (<see cref="Check"/>) and blocking wait (<see cref="WaitAsync"/>). An
///     async run proactively notifies the originating caller on completion, while its result stays queryable
///     via <see cref="Check"/> even if that notification fails.
/// </summary>
/// <remarks>
///     Mirrors the shape of <c>SubAgentManager</c>/<c>SubAgentToolProvider</c>. V1 is in-memory only: a
///     workflow is lost on process restart (surfaced as <see cref="UnknownWorkflowException"/>), and a
///     completed entry is retained (so its result stays queryable) rather than evicted — so a given
///     <c>workflowId</c> cannot be reused once started.
/// </remarks>
public sealed class WorkflowManager : IAsyncDisposable
{
    /// <summary>The default bounded wait for a concurrency slot before signalling backpressure.</summary>
    private static readonly TimeSpan DefaultGateWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The largest wait <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/> accepts (~24.8 days).</summary>
    private static readonly TimeSpan MaxWaitTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>The initial message handed to a controller whose definition is already loaded.</summary>
    internal const string StartObjective =
        "A workflow definition has been loaded for you. Call GetWorkflow to read the current node and its "
        + "ready-to-spawn nextExpectedAction unit(s), then drive it to a terminal node.";

    private readonly Func<IStreamingAgent> _controllerAgentFactory;
    private readonly SubAgentOptions _controllerSubAgentOptions;
    private readonly Func<NotifyMessage, CancellationToken, Task>? _completionNotifier;
    private readonly int _maxConcurrentWorkflows;
    private readonly int _controllerMaxTurnsPerRun;
    private readonly TimeSpan _gateWaitTimeout;
    private readonly GenerateReplyOptions? _controllerDefaultOptions;
    private readonly ILogger _logger;
    private readonly IJsonSchemaValidator? _schemaValidator;
    private readonly Func<IUsageSink?>? _rootUsageSink;
    private readonly Func<InheritableToolSnapshot?>? _inheritedToolSnapshot;
    private readonly IConversationStore? _controllerConversationStore;

    private readonly WorkflowValidator _validator = new();
    private readonly ConcurrentDictionary<string, WorkflowEntry> _workflows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrencyGate;
    private bool _disposed;

    /// <summary>Creates the manager.</summary>
    /// <param name="controllerAgentFactory">
    ///     Builds a FRESH controller <see cref="IStreamingAgent"/> per workflow run. The caller resolves the
    ///     fixed, pre-configured controller model once (a configured model id, else the conversation's own
    ///     default) and closes over it here — the controller model is never taken from the calling agent.
    /// </param>
    /// <param name="controllerSubAgentOptions">
    ///     The sub-agent templates a controller may delegate node work to. The options MUST exclude the
    ///     workflow-state/launch tools from inheritance via
    ///     <see cref="SubAgentOptions.NonInheritedToolNames"/>, so a node-delegate can never inherit the
    ///     controller's own workflow tools even with an inherit-all (<c>EnabledTools = null</c>)
    ///     template; this is asserted here. Delegates otherwise inherit the launching conversation's
    ///     tools transparently — see <paramref name="inheritedToolSnapshot"/>.
    /// </param>
    /// <param name="completionNotifier">
    ///     Optional sink that delivers the proactive completion <see cref="NotifyMessage"/> to the originating
    ///     caller for async runs. When null, async runs still complete and stay queryable via
    ///     <see cref="Check"/> — only the proactive push is skipped.
    /// </param>
    /// <param name="maxConcurrentWorkflows">The concurrent-workflow cap (default 8). Must be >= 1.</param>
    /// <param name="controllerMaxTurnsPerRun">The controller loop's per-run turn budget (default 150). Must be >= 1.</param>
    /// <param name="gateWaitTimeout">How long <see cref="StartAsync"/> waits for a slot before backpressure. Null = 5s.</param>
    /// <param name="controllerDefaultOptions">
    ///     Optional request defaults (notably <c>ModelId</c>) for the controller loop, so it runs on the fixed,
    ///     pre-configured controller model rather than the provider agent's hardcoded default.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="schemaValidator">Optional JSON-Schema validator forwarded to the runtime.</param>
    /// <param name="rootUsageSink">
    ///     Optional LATE-BOUND getter for the originating conversation's root usage sink (issue #196). It is
    ///     resolved once per run at <see cref="StartAsync"/> time (not at construction), so a host that creates
    ///     the manager BEFORE its root conversation loop exists can pass e.g. <c>() =&gt; agent?.UsageSink</c>.
    ///     When it resolves to a non-null sink, that run's controller loop folds BOTH its own driving turns AND
    ///     its task sub-agents' usage into it, so an isolated StartWorkflowAgent run's token spend rolls up into
    ///     the originating conversation's total. Null (or a getter returning null) keeps each run's usage scoped
    ///     to its own controller loop.
    /// </param>
    /// <param name="inheritedToolSnapshot">
    ///     Optional LATE-BOUND getter for the launching conversation's inheritable tool snapshot (the tools its
    ///     own sub-agents inherit). Resolved once per run at <see cref="StartAsync"/> time — same late-binding
    ///     rationale as <paramref name="rootUsageSink"/> — and threaded onto the run's controller
    ///     <see cref="SubAgentOptions.ExternalInheritableTools"/> so the controller's delegate sub-agents
    ///     inherit those tools transparently (the workflow-state/launch tools are still excluded structurally
    ///     via <see cref="SubAgentOptions.NonInheritedToolNames"/>). A host with sandbox tools passes e.g.
    ///     <c>() =&gt; agent?.SubAgentManager?.GetInheritableToolSnapshot()</c>. Null keeps delegates
    ///     reasoning-only (the controller registry carries no domain tools to inherit).
    /// </param>
    /// <param name="controllerConversationStore">
    ///     Optional conversation store for the controller loop so the workflow agent's OWN conversation (its
    ///     orchestration turns — GetWorkflow / SetCurrentNode / Agent spawns) is persisted under the
    ///     <c>workflow-{id}</c> thread and can be viewed (via the messages endpoint / the ⚙ workflow tab) after
    ///     the run completes and the loop is disposed. Null (the default) keeps the controller conversation
    ///     live-only (streamable during the run, empty afterwards). The host should pass a NON-OWNING wrapper
    ///     over its shared store so controller teardown never disposes it.
    /// </param>
    public WorkflowManager(
        Func<IStreamingAgent> controllerAgentFactory,
        SubAgentOptions controllerSubAgentOptions,
        Func<NotifyMessage, CancellationToken, Task>? completionNotifier = null,
        int maxConcurrentWorkflows = 8,
        int controllerMaxTurnsPerRun = 150,
        TimeSpan? gateWaitTimeout = null,
        GenerateReplyOptions? controllerDefaultOptions = null,
        ILogger? logger = null,
        IJsonSchemaValidator? schemaValidator = null,
        Func<IUsageSink?>? rootUsageSink = null,
        Func<InheritableToolSnapshot?>? inheritedToolSnapshot = null,
        IConversationStore? controllerConversationStore = null
    )
    {
        ArgumentNullException.ThrowIfNull(controllerAgentFactory);
        ArgumentNullException.ThrowIfNull(controllerSubAgentOptions);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentWorkflows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(controllerMaxTurnsPerRun, 1);

        AssertRestrictedControllerTemplates(controllerSubAgentOptions);

        _controllerAgentFactory = controllerAgentFactory;
        _controllerSubAgentOptions = controllerSubAgentOptions;
        _completionNotifier = completionNotifier;
        _maxConcurrentWorkflows = maxConcurrentWorkflows;
        _controllerMaxTurnsPerRun = controllerMaxTurnsPerRun;
        _gateWaitTimeout = gateWaitTimeout ?? DefaultGateWaitTimeout;
        _controllerDefaultOptions = controllerDefaultOptions;
        _logger = logger ?? NullLogger.Instance;
        _schemaValidator = schemaValidator;
        _rootUsageSink = rootUsageSink;
        _inheritedToolSnapshot = inheritedToolSnapshot;
        _controllerConversationStore = controllerConversationStore;
        _concurrencyGate = new SemaphoreSlim(maxConcurrentWorkflows, maxConcurrentWorkflows);
    }

    /// <summary>
    ///     Validates <paramref name="definition"/> synchronously (before any background task starts, in both
    ///     modes), reserves the <paramref name="workflowId"/> slot, and launches an isolated controller loop.
    ///     Sync mode blocks until the workflow reaches a terminal state and returns the terminal result;
    ///     async mode returns a <c>started</c> receipt immediately.
    /// </summary>
    /// <param name="workflowId">The opaque, caller-supplied workflow handle.</param>
    /// <param name="definition">The pre-authored workflow definition to run.</param>
    /// <param name="mode">Whether to block for the terminal result (sync) or return a started receipt (async).</param>
    /// <param name="ct">Cancels the caller's wait (sync mode); the run itself is not cancelled by this token.</param>
    /// <param name="originatingToolCallId">
    ///     The <c>StartWorkflowAgent</c> tool-call id, so an async run's completion notification can be correlated
    ///     back to the initiating call. Null falls back to <paramref name="workflowId"/>.
    /// </param>
    /// <exception cref="WorkflowValidationException">The definition is invalid.</exception>
    /// <exception cref="DuplicateWorkflowException"><paramref name="workflowId"/> is already reserved.</exception>
    /// <exception cref="WorkflowCapacityException">No concurrency slot freed up within the wait window.</exception>
    /// <exception cref="ObjectDisposedException">The manager is disposing/disposed.</exception>
    public async Task<WorkflowRunResult> StartAsync(
        string workflowId,
        WorkflowDefinition definition,
        WorkflowStartMode mode,
        CancellationToken ct = default,
        string? originatingToolCallId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(definition);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        // Validate BEFORE reserving a slot or starting anything, so an invalid definition never consumes a
        // workflowId or a concurrency slot and surfaces synchronously in both modes.
        _validator.ValidateAndThrow(definition);

        // Reserve the id slot atomically BEFORE the (effectively synchronous) start, closing the duplicate
        // TOCTOU: two concurrent starts for the same id cannot both pass the check.
        var startedUtc = DateTimeOffset.UtcNow;
        var entry = new WorkflowEntry
        {
            OriginatingToolCallId = originatingToolCallId,
            Objective = definition.Objective,
            StartedUtc = startedUtc,
            LastActivityUtcTicks = startedUtc.UtcTicks,
        };
        if (!_workflows.TryAdd(workflowId, entry))
        {
            throw new DuplicateWorkflowException(workflowId);
        }

        // Reject a start that races DisposeAsync: if disposal began after our TryAdd, roll the reservation
        // back so we never return "started" backed by a run the disposing manager won't observe.
        if (Volatile.Read(ref _disposed))
        {
            _ = _workflows.TryRemove(workflowId, out _);
            throw new ObjectDisposedException(nameof(WorkflowManager));
        }

        var gateAcquired = false;
        WorkflowRunHandle handle;
        try
        {
            if (!await _concurrencyGate.WaitAsync(_gateWaitTimeout, ct).ConfigureAwait(false))
            {
                throw new WorkflowCapacityException(_maxConcurrentWorkflows);
            }

            gateAcquired = true;

            // Resolve the originating conversation's root usage sink ONCE per run, now that a start is
            // actually proceeding. Late-bound (issue #196): a host may construct this manager before its root
            // conversation loop exists, so the sink is fetched here rather than at construction. A null getter
            // (or one that resolves to null) leaves this run's usage scoped to its own controller loop.
            var rootUsageSink = _rootUsageSink?.Invoke();

            // Resolve the launching conversation's inheritable tool snapshot ONCE per run (same
            // late-binding rationale as rootUsageSink above). Threaded onto the controller's
            // SubAgentOptions so the controller's delegate sub-agents inherit those tools
            // transparently; the workflow-state/launch tools stay excluded via NonInheritedToolNames.
            var inheritedTools = _inheritedToolSnapshot?.Invoke();
            var controllerSubAgentOptions = inheritedTools is null
                ? _controllerSubAgentOptions
                : _controllerSubAgentOptions with { ExternalInheritableTools = inheritedTools };

            // Observability: record how many tools this run's delegates will transparently inherit from the
            // launching conversation (content-free — a count, no tool arguments or task text).
            _logger.LogDebug(
                "Workflow {WorkflowId}: resolved {InheritedToolCount} inherited tool(s) for its delegates "
                    + "from the launching conversation.",
                workflowId,
                inheritedTools?.Contracts.Count ?? 0
            );

            handle = await WorkflowSession
                .StartAsync(
                    objective: StartObjective,
                    inputs: null,
                    definition: definition,
                    subAgentOptions: controllerSubAgentOptions,
                    controllerAgent: _controllerAgentFactory(),
                    threadId: $"workflow-{workflowId}",
                    store: null,
                    instanceId: workflowId,
                    conversationStore: _controllerConversationStore,
                    logger: _logger is NullLogger ? null : _logger,
                    schemaValidator: _schemaValidator,
                    includeAuthoringTool: false,
                    controllerMaxTurnsPerRun: _controllerMaxTurnsPerRun,
                    controllerDefaultOptions: _controllerDefaultOptions,
                    usageSink: rootUsageSink,
                    ct: CancellationToken.None
                )
                .ConfigureAwait(false);

            entry.Handle = handle;

            // Past this point the completion handler owns the concurrency slot and the handle's disposal.
            // Track the observer task so DisposeAsync can await it before disposing the gate (otherwise its
            // fire-and-forget Release could land on an already-disposed semaphore).
            entry.Observer = ObserveCompletionAsync(workflowId, entry, mode);
        }
        catch
        {
            // Start failed before the completion handler took ownership: roll back the reservation and
            // release the slot if it was acquired.
            _ = _workflows.TryRemove(workflowId, out _);
            if (gateAcquired)
            {
                _ = _concurrencyGate.Release();
            }

            throw;
        }

        if (mode == WorkflowStartMode.Async)
        {
            return new WorkflowRunResult { WorkflowId = workflowId, Status = WorkflowStatuses.Started };
        }

        // Sync: block until terminal, then return the outcome inline. Gate release + disposal are handled by
        // the completion handler; this path only reads the terminal state. Note: cancelling the caller here
        // rethrows OperationCanceledException but deliberately does NOT cancel the run (it stays queryable via
        // Check/Wait and finishes on its own); the run is bounded instead by controllerMaxTurnsPerRun.
        try
        {
            await handle.Completion.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A faulted run is surfaced as a Failed result below; the fault itself is observed by the
            // completion handler.
        }

        return BuildResult(workflowId, handle);
    }

    /// <summary>
    ///     Returns the current status and a state snapshot for <paramref name="workflowId"/> WITHOUT blocking.
    ///     Works for a running, completed, or failed workflow — after completion it answers from a retained
    ///     lightweight snapshot (the heavy run graph is released).
    /// </summary>
    /// <exception cref="UnknownWorkflowException">No such workflow.</exception>
    public WorkflowRunResult Check(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        if (!_workflows.TryGetValue(workflowId, out var entry))
        {
            throw new UnknownWorkflowException(workflowId);
        }

        // A captured terminal snapshot is authoritative once present (the handle may already be released).
        if (Volatile.Read(ref entry.TerminalSnapshot) is { } terminal)
        {
            return terminal;
        }

        var handle = Volatile.Read(ref entry.Handle);
        if (handle is null)
        {
            // The completion observer captures the snapshot BEFORE nulling the handle, so a null handle here
            // means either that handoff is mid-flight (re-read the snapshot) or the entry is still in its
            // admitted-but-starting window (a coherent "running", never "unknown" for a known id).
            return Volatile.Read(ref entry.TerminalSnapshot) ?? Pending(workflowId);
        }

        return handle.Completion.IsCompleted
            ? BuildResult(workflowId, handle)
            : Running(workflowId, handle);
    }

    /// <summary>
    ///     Blocks until <paramref name="workflowId"/> reaches a terminal state or <paramref name="timeout"/>
    ///     elapses, then returns the terminal result (or a <c>timeout</c> result). NON-DESTRUCTIVE: a timeout
    ///     leaves the workflow running so a later <see cref="Check"/>/<see cref="WaitAsync"/> can still observe
    ///     it. Unlike <c>Agent</c>'s bounded-turn wait, the timeout here is open-ended, so a long wait suspends
    ///     the calling loop's dispatch cycle for its duration.
    /// </summary>
    /// <exception cref="UnknownWorkflowException">No such workflow.</exception>
    public async Task<WorkflowRunResult> WaitAsync(
        string workflowId,
        TimeSpan? timeout,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        // Reject an invalid negative timeout at the public API (the tool handler already rejects it earlier);
        // Timeout.InfiniteTimeSpan (-1ms) is the one allowed negative — it means "no timeout".
        if (timeout is { } requested && requested < TimeSpan.Zero && requested != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Timeout must be non-negative (or Timeout.InfiniteTimeSpan)."
            );
        }

        // Treat the infinite sentinel as "no timeout" so the wait blocks on ct only.
        if (timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            timeout = null;
        }

        if (!_workflows.TryGetValue(workflowId, out var entry))
        {
            throw new UnknownWorkflowException(workflowId);
        }

        // Already terminal → return the retained snapshot without blocking.
        if (Volatile.Read(ref entry.TerminalSnapshot) is { } cached)
        {
            return cached;
        }

        var handle = Volatile.Read(ref entry.Handle);
        if (handle is null)
        {
            // Mid-completion-handoff (re-read snapshot) or admitted-but-starting (coherent "running").
            return Volatile.Read(ref entry.TerminalSnapshot) ?? Pending(workflowId);
        }

        if (!handle.Completion.IsCompleted)
        {
            if (timeout is { } wait)
            {
                // Clamp to a range Task.WaitAsync accepts (it throws for a timeout beyond ~49.7 days); ~24.8
                // days is effectively "wait until completion" while staying in range. A direct library caller
                // passing an out-of-range TimeSpan is handled here, not just the tool's own clamp.
                var bounded = wait > MaxWaitTimeout ? MaxWaitTimeout : wait;
                try
                {
                    // WaitAsync(timeout, ct) is non-destructive: it stops waiting without cancelling the
                    // underlying run, and observes the source task internally.
                    await handle.Completion.WaitAsync(bounded, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return Timeout(workflowId, handle);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Faulted run → surfaced as Failed by the terminal read below.
                }
            }
            else
            {
                try
                {
                    await handle.Completion.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Faulted run → surfaced as Failed by the terminal read below.
                }
            }
        }

        // Prefer a snapshot the observer may have captured while we waited; otherwise only report a terminal
        // outcome once Completion has actually resolved — a still-running workflow is reported as waiting, not
        // misread as "failed".
        if (Volatile.Read(ref entry.TerminalSnapshot) is { } terminal)
        {
            return terminal;
        }

        return handle.Completion.IsCompleted ? BuildResult(workflowId, handle) : Timeout(workflowId, handle);
    }

    /// <summary>
    ///     Returns a lightweight per-run summary for every tracked workflow (running, completed, or failed)
    ///     WITHOUT blocking — a presentation seam for a host listing active runs. Order is unspecified, and no
    ///     live run graph is exposed; a completed run is answered from its retained terminal snapshot.
    /// </summary>
    public IReadOnlyList<WorkflowRunSummary> ListRuns()
    {
        var summaries = new List<WorkflowRunSummary>(_workflows.Count);
        foreach (var (workflowId, entry) in _workflows)
        {
            // A captured terminal snapshot is authoritative once present (the handle may already be released);
            // otherwise a known entry is running — whether its handle is published yet or the completion
            // handoff is mid-flight — never "unknown" for a genuinely tracked id.
            var terminal = Volatile.Read(ref entry.TerminalSnapshot);
            var handle = Volatile.Read(ref entry.Handle);
            var status = terminal?.Status ?? WorkflowStatuses.Running;
            var currentNodeId = terminal?.CurrentNodeId ?? handle?.CurrentNodeId;

            summaries.Add(
                new WorkflowRunSummary
                {
                    WorkflowId = workflowId,
                    Objective = string.IsNullOrWhiteSpace(entry.Objective) ? workflowId : entry.Objective,
                    Status = status,
                    CurrentNodeId = currentNodeId,
                    StartedUtc = entry.StartedUtc,
                    LastActivityUtc = new DateTimeOffset(
                        Volatile.Read(ref entry.LastActivityUtcTicks),
                        TimeSpan.Zero
                    ),
                }
            );
        }

        return summaries;
    }

    /// <summary>
    ///     Exposes the live controller <see cref="MultiTurnAgentLoop"/> for a running workflow so a host can
    ///     subscribe to and stream its conversation. Returns <c>false</c> for an unknown id OR a run that has
    ///     completed and had its heavy graph released (the loop is no longer live). The returned loop is owned
    ///     by the run — the caller MUST NOT dispose it.
    /// </summary>
    public bool TryGetRunLoop(string workflowId, out MultiTurnAgentLoop? loop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        if (_workflows.TryGetValue(workflowId, out var entry)
            && Volatile.Read(ref entry.Handle) is { } handle)
        {
            loop = handle.Loop;
            return true;
        }

        loop = null;
        return false;
    }

    /// <summary>
    ///     The delegate sub-agents of a run, for surfacing as nested tabs. Returns the LIVE controller loop's
    ///     delegates while running, or the snapshot retained at completion once the loop is released — so a
    ///     completed run's delegate tabs (and their persisted transcripts) remain listable, matching how a
    ///     conversation's own sub-agent tabs persist. Empty for an unknown run or one that spawned no delegates.
    /// </summary>
    /// <param name="workflowId">The run whose delegate sub-agents to list.</param>
    public IReadOnlyList<SubAgentSnapshot> ListRunDelegates(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        if (!_workflows.TryGetValue(workflowId, out var entry))
        {
            return [];
        }

        // Prefer the live loop (running run) so in-flight status/activity is current; fall back to the
        // snapshot retained at completion once the heavy graph has been released.
        if (Volatile.Read(ref entry.Handle) is { } handle
            && handle.Loop.SubAgentManager is { } manager)
        {
            return manager.ListAgents();
        }

        return Volatile.Read(ref entry.RetainedDelegates) ?? [];
    }

    /// <summary>
    ///     Finds the running controller loop whose <see cref="SubAgentManager"/> owns the given delegate
    ///     sub-agent id, so a host can stream a nested workflow delegate (spawned BY a controller) the same
    ///     way it streams a top-level sub-agent. Symmetric with <see cref="TryGetRunLoop"/>, but keyed by a
    ///     delegate agent id rather than a workflow id. Returns false when no live run owns the id.
    /// </summary>
    /// <param name="subAgentId">The delegate sub-agent id (as listed by the controller's SubAgentManager).</param>
    /// <param name="loop">The owning controller loop when found; otherwise null.</param>
    public bool TryGetRunLoopOwningSubAgent(string subAgentId, out MultiTurnAgentLoop? loop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        foreach (var entry in _workflows.Values)
        {
            if (Volatile.Read(ref entry.Handle) is { } handle
                && handle.Loop.SubAgentManager is { } subAgentManager
                && subAgentManager.TryGetAgent(subAgentId, out _))
            {
                loop = handle.Loop;
                return true;
            }
        }

        loop = null;
        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Reject any new starts that race this disposal (StartAsync re-checks the flag after reserving).
        Volatile.Write(ref _disposed, true);

        foreach (var (id, entry) in _workflows)
        {
            await DisposeHandleOnceAsync(id, entry).ConfigureAwait(false);

            // Await the completion observer (its Completion is now resolved by the disposal above) so its
            // one-shot gate Release runs BEFORE the gate is disposed below — otherwise a still-in-flight
            // observer could Release an already-disposed semaphore and fault as unobserved background work.
            if (entry.Observer is { } observer)
            {
                try
                {
                    await observer.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Workflow {WorkflowId} completion observer faulted during dispose", id);
                }
            }
        }

        _workflows.Clear();
        _concurrencyGate.Dispose();
    }

    /// <summary>
    ///     Asserts the controller sub-agent options exclude the workflow-state/launch tools from inheritance
    ///     via <see cref="SubAgentOptions.NonInheritedToolNames"/>. That structural exclusion is what keeps a
    ///     node-delegate from inheriting the controller's own workflow tools even under an inherit-all
    ///     (<c>EnabledTools = null</c>) template — the tools are filtered out of the controller's inheritable
    ///     snapshot before any template allow-list is applied, and before any transparently-inherited ancestor
    ///     tools are merged. Called at construction so misconfiguration fails fast, before any controller loop
    ///     is built.
    /// </summary>
    internal static void AssertRestrictedControllerTemplates(SubAgentOptions options)
    {
        // No delegate templates ⇒ no sub-agent can be spawned ⇒ nothing can inherit the workflow tools,
        // so the exclusion is moot. This keeps a controller that never delegates (e.g. a purely
        // graph-driving run) valid without forcing a redundant NonInheritedToolNames.
        if (options.Templates.Count == 0)
        {
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in WorkflowToolProvider.AllToolNames)
        {
            _ = required.Add(name);
        }

        foreach (var name in StartWorkflowToolProvider.ToolNames)
        {
            _ = required.Add(name);
        }

        var excluded = options.NonInheritedToolNames is { } names
            ? new HashSet<string>(names, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var missing = required.Where(n => !excluded.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                "Controller sub-agent options must exclude the workflow-state/launch tools from inheritance "
                    + "(via SubAgentOptions.NonInheritedToolNames) so a node-delegate can never inherit the "
                    + "controller's own workflow tools: "
                    + string.Join(", ", missing)
                    + ".",
                nameof(options)
            );
        }
    }

    /// <summary>
    ///     Awaits the run's completion, then (once, in order): captures the lightweight terminal snapshot,
    ///     releases the concurrency slot, sends the proactive completion notification for an async run
    ///     (exception-isolated, so a delivery failure never masks the result), disposes the handle, and
    ///     releases the heavy run graph. Fully guarded so it never faults as background work.
    /// </summary>
    private async Task ObserveCompletionAsync(string workflowId, WorkflowEntry entry, WorkflowStartMode mode)
    {
        var handle = entry.Handle!;
        try
        {
            try
            {
                await handle.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The terminal fault is also read back from the handle by BuildResult (→ a Failed result);
                // surface it here at Warning so a controller run fault is never entirely silent.
                _logger.LogWarning(ex, "Workflow {WorkflowId} controller run faulted", workflowId);
            }

            // Capture the lightweight terminal snapshot BEFORE releasing/notifying/disposing so Check/Wait can
            // answer from it and the heavy WorkflowRunHandle → WorkflowRuntime graph can be released below.
            var result = BuildResult(workflowId, handle);
            Volatile.Write(ref entry.TerminalSnapshot, result);

            // Snapshot the controller's delegate sub-agents BEFORE the loop is disposed below, so their tabs
            // (and their already-persisted subagent-{id} transcripts) stay listable after the run completes —
            // parity with a conversation's own sub-agent tabs, which persist for the conversation's lifetime.
            var delegates = handle.Loop.SubAgentManager?.ListAgents();
            if (delegates is { Count: > 0 })
            {
                Volatile.Write(ref entry.RetainedDelegates, delegates);
            }

            // Observability: how many delegates this run produced (retained for post-completion tab
            // surfacing). Content-free — a count only.
            _logger.LogDebug(
                "Workflow {WorkflowId} completed with {DelegateCount} delegate(s) retained for tab surfacing.",
                workflowId,
                delegates?.Count ?? 0
            );

            // Stamp the terminal transition as the run's last activity so ListRuns reports a meaningful
            // recency for a finished run (the manager does not observe intermediate controller turns).
            Volatile.Write(ref entry.LastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

            // Release the slot as soon as the run ends — BEFORE the possibly-slow notify — so a blocked
            // notification never holds up a fresh StartWorkflowAgent call waiting on the gate. Guarded against a
            // concurrent DisposeAsync having already disposed the gate (belt-and-suspenders: DisposeAsync
            // now awaits this observer before disposing, so this is normally unreachable).
            try
            {
                _ = _concurrencyGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // The manager was disposed; the slot no longer matters.
            }

            if (mode == WorkflowStartMode.Async
                && _completionNotifier is not null
                && Interlocked.Exchange(ref entry.NotifySent, 1) == 0)
            {
                // Revision #4: the terminal outcome is fully determined BEFORE the notify, and the notify is
                // isolated so a failure never loses the result (still queryable via Check).
                var notify = NotifyMessage.Create(
                    NotifyKinds.WorkflowCompletion,
                    detail: BuildNotifyDetail(result),
                    sourceToolName: StartWorkflowToolProvider.StartWorkflowToolName,
                    // Correlate to the originating StartWorkflowAgent tool call when known; the workflowId is still
                    // carried in the label + detail body.
                    sourceToolCallId: entry.OriginatingToolCallId ?? workflowId,
                    label: workflowId
                );

                try
                {
                    await _completionNotifier(notify, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Proactive completion notification for workflow {WorkflowId} failed; the result "
                            + "remains queryable via CheckWorkflow",
                        workflowId
                    );
                }
            }
        }
        finally
        {
            await DisposeHandleOnceAsync(workflowId, entry).ConfigureAwait(false);

            // Release the heavy run graph now that a lightweight terminal snapshot answers Check/Wait. The
            // entry itself is retained (see the class remarks on the accepted v1 no-eviction limitation), but
            // it now holds only the small result, not the disposed loop + runtime + definition.
            Volatile.Write(ref entry.Handle, null);
        }
    }

    /// <summary>Disposes the entry's handle at most once (guards against the completion handler racing DisposeAsync).</summary>
    private async Task DisposeHandleOnceAsync(string workflowId, WorkflowEntry entry)
    {
        if (entry.Handle is not { } handle || Interlocked.Exchange(ref entry.Disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the handle for workflow {WorkflowId} failed", workflowId);
        }
    }

    /// <summary>Builds the terminal outcome from a handle whose <c>Completion</c> has already resolved.</summary>
    private static WorkflowRunResult BuildResult(string workflowId, WorkflowRunHandle handle)
    {
        if (handle.Completion.IsFaulted)
        {
            var ex = handle.Completion.Exception?.GetBaseException();
            return Failed(workflowId, handle, ex?.Message ?? "The workflow controller run failed.");
        }

        if (handle.Completion.IsCanceled)
        {
            return Failed(workflowId, handle, "The workflow run was cancelled.");
        }

        // Ran to completion. The controller loop can end WITHOUT the workflow reaching a terminal node (turn
        // budget exhausted, or a task failed with no matching edge and no onBudgetExhausted escape) — treat
        // that as an explicit terminal failure rather than reporting a phantom success.
        return handle.IsComplete
            ? new WorkflowRunResult
            {
                WorkflowId = workflowId,
                Status = WorkflowStatuses.Completed,
                Result = handle.Result,
                CurrentNodeId = handle.CurrentNodeId,
                IsComplete = true,
                Outputs = handle.Outputs,
                Notes = handle.Notes,
            }
            : Failed(
                workflowId,
                handle,
                "The workflow controller ended without reaching a terminal node "
                    + "(turn budget exhausted, or no available transition)."
            );
    }

    private static WorkflowRunResult Failed(string workflowId, WorkflowRunHandle handle, string error) =>
        new()
        {
            WorkflowId = workflowId,
            Status = WorkflowStatuses.Failed,
            Error = error,
            CurrentNodeId = handle.CurrentNodeId,
            IsComplete = handle.IsComplete,
            Outputs = handle.Outputs,
            Notes = handle.Notes,
        };

    private static WorkflowRunResult Running(string workflowId, WorkflowRunHandle handle) =>
        new()
        {
            WorkflowId = workflowId,
            Status = WorkflowStatuses.Running,
            CurrentNodeId = handle.CurrentNodeId,
            IsComplete = false,
            Outputs = handle.Outputs,
            Notes = handle.Notes,
        };

    /// <summary>A known workflow that has been admitted but whose controller handle is not yet published
    /// (queued at the concurrency gate, or the completion handoff is mid-flight) — reported as running, not
    /// unknown, since the id is genuinely known.</summary>
    private static WorkflowRunResult Pending(string workflowId) =>
        new() { WorkflowId = workflowId, Status = WorkflowStatuses.Running, IsComplete = false };

    private static WorkflowRunResult Timeout(string workflowId, WorkflowRunHandle handle) =>
        new()
        {
            WorkflowId = workflowId,
            Status = WorkflowStatuses.Timeout,
            CurrentNodeId = handle.CurrentNodeId,
            IsComplete = false,
        };

    /// <summary>Renders the notification payload body dropped into the completion envelope.</summary>
    private static string BuildNotifyDetail(WorkflowRunResult result)
    {
        // XML-escape the model-supplied id/status into the inner markup. The OUTER envelope
        // (NotifyMessage.BuildEnvelope) already escapes + sanitizes, so this is robustness against malformed
        // inner markup (a workflowId containing " or <) the model would otherwise read, not a security fix.
        var sb = new StringBuilder();
        _ = sb.Append("<workflow id=\"")
            .Append(EscapeXml(result.WorkflowId))
            .Append("\" status=\"")
            .Append(EscapeXml(result.Status))
            .Append("\">\n");

        if (result.Status == WorkflowStatuses.Completed)
        {
            _ = sb.Append("Result: ").Append(result.Result?.ToJsonString() ?? "(no result)").Append('\n');
        }
        else if (result.Error is { } error)
        {
            _ = sb.Append("Error: ").Append(error).Append('\n');
        }

        _ = sb.Append("</workflow>");
        return sb.ToString();
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    /// <summary>A tracked workflow: a lightweight terminal snapshot plus the live run handle (released once
    /// terminal) and one-shot notify/dispose guards. Fields are published/read via <see cref="Volatile"/>.</summary>
    private sealed class WorkflowEntry
    {
        /// <summary>The live run handle while running; nulled once terminal so its heavy graph can be collected.</summary>
        public WorkflowRunHandle? Handle;

        /// <summary>The lightweight terminal result, captured at completion. Authoritative once non-null.</summary>
        public WorkflowRunResult? TerminalSnapshot;

        /// <summary>The originating StartWorkflowAgent tool-call id for completion-notify correlation, or null.</summary>
        public string? OriginatingToolCallId;

        /// <summary>The run's objective (from its definition), for presentation via ListRuns. Set once at
        /// reservation, before the entry is published; immutable thereafter.</summary>
        public string? Objective;

        /// <summary>When the run was reserved/started. Set once at reservation, before the entry is published;
        /// immutable thereafter.</summary>
        public DateTimeOffset StartedUtc;

        /// <summary>UTC ticks of the run's last observed activity: the start time while running, then the
        /// terminal transition once it finishes. Read/written via <see cref="Volatile"/>.</summary>
        public long LastActivityUtcTicks;

        /// <summary>The controller's delegate sub-agents, snapshotted at completion (before the loop is
        /// released) so a finished run's delegate tabs — and their persisted transcripts — remain listable,
        /// matching how a conversation's own sub-agent tabs persist. Read/written via <see cref="Volatile"/>.</summary>
        public IReadOnlyList<SubAgentSnapshot>? RetainedDelegates;

        public Task? Observer;
        public int NotifySent;
        public int Disposed;
    }
}
