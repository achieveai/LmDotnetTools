using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
///     Owns the lifecycle of workflows launched via <c>StartWorkflow</c>: validates the definition, bounds
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

    private readonly WorkflowValidator _validator = new();
    private readonly ConcurrentDictionary<string, WorkflowEntry> _workflows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrencyGate;

    /// <summary>Creates the manager.</summary>
    /// <param name="controllerAgentFactory">
    ///     Builds a FRESH controller <see cref="IStreamingAgent"/> per workflow run. The caller resolves the
    ///     fixed, pre-configured controller model once (a configured model id, else the conversation's own
    ///     default) and closes over it here — the controller model is never taken from the calling agent.
    /// </param>
    /// <param name="controllerSubAgentOptions">
    ///     The sub-agent templates a controller may delegate node work to. Every template MUST declare an
    ///     explicit <c>EnabledTools</c> allow-list that omits the workflow-state/launch tools, so a
    ///     node-delegate can never inherit the controller's own workflow tools; this is asserted here.
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
    public WorkflowManager(
        Func<IStreamingAgent> controllerAgentFactory,
        SubAgentOptions controllerSubAgentOptions,
        Func<NotifyMessage, CancellationToken, Task>? completionNotifier = null,
        int maxConcurrentWorkflows = 8,
        int controllerMaxTurnsPerRun = 150,
        TimeSpan? gateWaitTimeout = null,
        GenerateReplyOptions? controllerDefaultOptions = null,
        ILogger? logger = null,
        IJsonSchemaValidator? schemaValidator = null
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
        _concurrencyGate = new SemaphoreSlim(maxConcurrentWorkflows, maxConcurrentWorkflows);
    }

    /// <summary>
    ///     Validates <paramref name="definition"/> synchronously (before any background task starts, in both
    ///     modes), reserves the <paramref name="workflowId"/> slot, and launches an isolated controller loop.
    ///     Sync mode blocks until the workflow reaches a terminal state and returns the terminal result;
    ///     async mode returns a <c>started</c> receipt immediately.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The definition is invalid.</exception>
    /// <exception cref="DuplicateWorkflowException"><paramref name="workflowId"/> is already reserved.</exception>
    /// <exception cref="WorkflowCapacityException">No concurrency slot freed up within the wait window.</exception>
    public async Task<WorkflowRunResult> StartAsync(
        string workflowId,
        WorkflowDefinition definition,
        WorkflowStartMode mode,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(definition);

        // Validate BEFORE reserving a slot or starting anything, so an invalid definition never consumes a
        // workflowId or a concurrency slot and surfaces synchronously in both modes.
        _validator.ValidateAndThrow(definition);

        // Reserve the id slot atomically BEFORE the (effectively synchronous) start, closing the duplicate
        // TOCTOU: two concurrent starts for the same id cannot both pass the check.
        var entry = new WorkflowEntry();
        if (!_workflows.TryAdd(workflowId, entry))
        {
            throw new DuplicateWorkflowException(workflowId);
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

            handle = await WorkflowSession
                .StartAsync(
                    objective: StartObjective,
                    inputs: null,
                    definition: definition,
                    subAgentOptions: _controllerSubAgentOptions,
                    controllerAgent: _controllerAgentFactory(),
                    threadId: $"workflow-{workflowId}",
                    store: null,
                    instanceId: workflowId,
                    conversationStore: null,
                    logger: _logger is NullLogger ? null : _logger,
                    schemaValidator: _schemaValidator,
                    includeAuthoringTool: false,
                    controllerMaxTurnsPerRun: _controllerMaxTurnsPerRun,
                    controllerDefaultOptions: _controllerDefaultOptions,
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

        // Reserved but not yet started (a sub-millisecond, synchronous window inside StartAsync) → unknown.
        var handle = Volatile.Read(ref entry.Handle) ?? throw new UnknownWorkflowException(workflowId);

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

        if (!_workflows.TryGetValue(workflowId, out var entry))
        {
            throw new UnknownWorkflowException(workflowId);
        }

        // Already terminal → return the retained snapshot without blocking.
        if (Volatile.Read(ref entry.TerminalSnapshot) is { } cached)
        {
            return cached;
        }

        var handle = Volatile.Read(ref entry.Handle) ?? throw new UnknownWorkflowException(workflowId);

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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
    ///     Rejects a controller sub-agent template set that would let a node-delegate inherit the controller's
    ///     workflow tools: an <c>EnabledTools = null</c> (inherit-all) template, or one that explicitly enables
    ///     any workflow-state/launch tool. Called at construction so misconfiguration fails fast, before any
    ///     controller loop is built.
    /// </summary>
    internal static void AssertRestrictedControllerTemplates(SubAgentOptions options)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in WorkflowToolProvider.AllToolNames)
        {
            _ = forbidden.Add(name);
        }

        foreach (var name in StartWorkflowToolProvider.ToolNames)
        {
            _ = forbidden.Add(name);
        }

        foreach (var (templateName, template) in options.Templates)
        {
            if (template.EnabledTools is null)
            {
                throw new ArgumentException(
                    $"Controller sub-agent template '{templateName}' must declare an explicit EnabledTools "
                        + "allow-list; a null (inherit-all) list would let the node-delegate inherit the "
                        + "controller's workflow-state tools.",
                    nameof(options)
                );
            }

            var leaked = template.EnabledTools.Where(forbidden.Contains).ToList();
            if (leaked.Count > 0)
            {
                throw new ArgumentException(
                    $"Controller sub-agent template '{templateName}' must not enable workflow tools: "
                        + string.Join(", ", leaked)
                        + ".",
                    nameof(options)
                );
            }
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

            // Release the slot as soon as the run ends — BEFORE the possibly-slow notify — so a blocked
            // notification never holds up a fresh StartWorkflow waiting on the gate. Guarded against a
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
                    sourceToolCallId: workflowId,
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

        public Task? Observer;
        public int NotifySent;
        public int Disposed;
    }
}
