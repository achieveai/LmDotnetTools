using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>
///     The single source of truth for one running workflow. It owns the data channels
///     (<c>inputs</c>/<c>state</c>/<c>outputs</c>/<c>notes</c>), the loop bookkeeping
///     (<c>currentNode</c>/<c>visits</c>/<c>step</c>), the task-correlation maps, and the completion
///     signal. The controller tools and the run observer drive it; <b>every</b> mutation is serialized by
///     a single lock so the loop-thread tool handlers and the stream-observer task never corrupt state.
/// </summary>
/// <remarks>
///     <para>
///         Beyond the linear P3 slice this surface supports <b>forEach fan-out</b> (one unit per array
///         element), <b>background sub-agent correlation</b> (a spawn receipt is correlated by its
///         <c>agent_id</c>, and the eventual injected result validated when it arrives), <b>join surfacing</b>
///         (an <c>all</c>/<c>any</c> progress view for the controller), and <b>bounded validation retries</b>.
///     </para>
///     <para>
///         The runtime stays deliberately light: it never initiates its own tool calls. Validation retries
///         follow a "controller re-spawns; runtime bounds the attempts" model — when a unit's result fails
///         and its attempt budget is not yet exhausted the runtime marks the unit <c>pending</c> again and
///         surfaces the error, which re-surfaces the unit in <c>nextExpectedAction</c> so the controller
///         re-spawns it; only when the budget is exhausted is the unit terminally failed.
///     </para>
/// </remarks>
public sealed class WorkflowRuntime
{
    private readonly object _lock = new();
    private readonly WorkflowValidator _validator = new();
    private readonly IJsonSchemaValidator _schemaValidator;
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // Correlation: the surfaced unit name -> its task ref (populated by ComposeNextExpectedAction), the
    // observed Agent tool_call_id -> the same task ref (populated by RegisterSpawn), and the background
    // spawn receipt agent_id -> the same task ref (populated by ObserveSpawnResult).
    private readonly Dictionary<string, TaskRef> _tasksByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskRef> _tasksByToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskRef> _tasksByAgentId = new(StringComparer.Ordinal);

    // Per-task status and the last composed spawn unit, both keyed by the unit name.
    private readonly Dictionary<string, WorkflowTaskStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpawnUnit> _composed = new(StringComparer.Ordinal);

    // Per-unit validation-retry bookkeeping: how many times a unit's result has failed (attempts) and the
    // most recent failure reason (surfaced to the controller so it can re-author the re-spawned unit).
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastError = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _visits = new(StringComparer.Ordinal);
    private readonly List<string> _unmatched = [];

    // The live data channels the runtime mutates IN PLACE under _lock. They are exposed only through the
    // public getters below, which hand back a deep copy taken under the lock so a host can never observe a
    // half-mutated channel or mutate internal state by writing into the returned node (Fix H1).
    private JsonObject _inputs = [];
    private JsonObject _state = [];
    private JsonObject _outputs = [];
    private JsonObject _notes = [];
    private JsonNode? _result;

    // Optional best-effort persistence: when attached, a fresh snapshot is saved after every state-mutating
    // public method so the run can be resumed (single-root) after a restart. No store attached => no-op.
    private IWorkflowStore? _store;
    private string? _instanceId;

    // Persistence is serialized so saves apply in capture order (save N completes before save N+1 starts),
    // preventing a slow stale save from overwriting a newer one (Fix M2). The chain is advanced under a
    // dedicated lock so it never blocks the main runtime lock.
    private readonly object _saveLock = new();
    private Task _saveChain = Task.CompletedTask;

    // Optional logger so a swallowed best-effort persistence fault is at least visible (Fix M3).
    private readonly ILogger? _logger;

    /// <summary>
    ///     Creates a runtime. A custom <paramref name="schemaValidator"/> may be injected for tests, and an
    ///     optional <paramref name="logger"/> surfaces otherwise-swallowed best-effort persistence faults.
    /// </summary>
    public WorkflowRuntime(IJsonSchemaValidator? schemaValidator = null, ILogger? logger = null)
    {
        _schemaValidator = schemaValidator ?? new JsonSchemaValidator();
        _logger = logger;
    }

    /// <summary>
    ///     Attaches a persistence <paramref name="store"/> and the <paramref name="instanceId"/> to save under.
    ///     Once attached, every state-mutating method persists a fresh snapshot (best-effort). Idempotent and
    ///     safe to call before any state exists; it does not itself persist.
    /// </summary>
    public void AttachStore(IWorkflowStore store, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        lock (_lock)
        {
            _store = store;
            _instanceId = instanceId;
        }
    }

    /// <summary>The loaded definition, or <c>null</c> before <see cref="LoadDefinition"/>.</summary>
    public WorkflowDefinition? Definition { get; private set; }

    /// <summary>The id of the node the controller is currently positioned on.</summary>
    public string? CurrentNodeId { get; private set; }

    /// <summary>Whether the workflow has advanced into a terminal node.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    ///     The validated final result captured when the workflow completed, or <c>null</c>. Returns a deep
    ///     copy taken under the lock, so a host reading it never aliases live runtime state (Fix H1).
    /// </summary>
    public JsonNode? Result
    {
        get
        {
            lock (_lock)
            {
                return _result?.DeepClone();
            }
        }
    }

    /// <summary>The global controller step counter, incremented on every transition.</summary>
    public int Step { get; private set; }

    /// <summary>
    ///     The workflow inputs channel (<c>inputs.&lt;...&gt;</c>). Returns a deep copy taken under the lock;
    ///     mutating the returned object does not change runtime state (Fix H1).
    /// </summary>
    public JsonObject Inputs
    {
        get
        {
            lock (_lock)
            {
                return CloneObject(_inputs);
            }
        }
    }

    /// <summary>
    ///     The mutable state channel (<c>state.&lt;...&gt;</c>). Returns a deep copy taken under the lock;
    ///     mutating the returned object does not change runtime state (Fix H1).
    /// </summary>
    public JsonObject State
    {
        get
        {
            lock (_lock)
            {
                return CloneObject(_state);
            }
        }
    }

    /// <summary>
    ///     The per-node task outputs channel (<c>{ nodeId: { taskId: value } }</c>). Returns a deep copy taken
    ///     under the lock; mutating the returned object does not change runtime state (Fix H1).
    /// </summary>
    public JsonObject Outputs
    {
        get
        {
            lock (_lock)
            {
                return CloneObject(_outputs);
            }
        }
    }

    /// <summary>
    ///     The scoped notes channel (<c>{ scope: { key: value } }</c>). Returns a deep copy taken under the
    ///     lock; mutating the returned object does not change runtime state (Fix H1).
    /// </summary>
    public JsonObject Notes
    {
        get
        {
            lock (_lock)
            {
                return CloneObject(_notes);
            }
        }
    }

    /// <summary>
    ///     Completes when the workflow reaches a terminal node and the run observer has drained the stream
    ///     up to that point (signalled by the host via <see cref="SignalCompletion"/>). Faults via
    ///     <see cref="SignalFailure"/> when the controller run throws.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>A snapshot of the per-node visit counts.</summary>
    public IReadOnlyDictionary<string, int> Visits
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, int>(_visits, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>A snapshot of the last spawn units composed by name (used by hosts/tests for inspection).</summary>
    internal IReadOnlyDictionary<string, SpawnUnit> ComposedUnits
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, SpawnUnit>(_composed, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    ///     Validates and loads a definition: seeds the inputs/state channels (cloned), an empty outputs
    ///     bag per node, sets the current node to the start node, records the first visit, and resets the
    ///     step counter and correlation maps.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The definition failed validation.</exception>
    public void LoadDefinition(WorkflowDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        _validator.ValidateAndThrow(def);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            Definition = def;
            _inputs = CloneObject(def.Inputs);
            _state = CloneObject(def.State);
            _outputs = [];
            _notes = [];
            foreach (var node in def.Nodes)
            {
                _outputs[node.Id] = new JsonObject();
            }

            var start = def.Nodes.OfType<StartNode>().Single();
            CurrentNodeId = start.Id;
            _visits.Clear();
            _visits[start.Id] = 1;
            Step = 0;
            IsComplete = false;
            _result = null;

            _tasksByName.Clear();
            _tasksByToolCallId.Clear();
            _tasksByAgentId.Clear();
            _status.Clear();
            _composed.Clear();
            _attempts.Clear();
            _lastError.Clear();
            _unmatched.Clear();

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>Shallow-merges <paramref name="inputs"/> into the inputs channel (caller-supplied seed).</summary>
    public void MergeInputs(JsonObject inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (_lock)
        {
            foreach (var (key, value) in inputs)
            {
                _inputs[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    ///     Builds a <see cref="BindingContext"/> over the current channels (plus optional loop locals) for
    ///     template rendering and condition evaluation. The channels are deep-copied under the lock so the
    ///     returned context never aliases live runtime state (Fix H1).
    /// </summary>
    public BindingContext BuildContext(JsonNode? item = null, int? index = null, int? count = null)
    {
        lock (_lock)
        {
            return BuildContextNoLock(item, index, count, clone: true);
        }
    }

    /// <summary>
    ///     Composes the next expected sub-agent action(s) for the active node. In the V1-minimal slice this
    ///     is exactly one <see cref="SpawnUnit"/> when the active node is a procedural node with a single
    ///     authored, non-forEach agent task; start/terminal/conditional nodes (and anything outside the
    ///     minimal shape) return an empty list because the controller routes those itself. As a side effect
    ///     it registers the composed unit by name so a later <see cref="RegisterSpawn"/> can correlate it.
    /// </summary>
    public IReadOnlyList<SpawnUnit> ComposeNextExpectedAction()
    {
        lock (_lock)
        {
            return ComposeNoLock();
        }
    }

    /// <summary>
    ///     Correlates an observed <c>Agent</c> tool call (by its <paramref name="toolCallId"/>) to the
    ///     expected unit named <paramref name="name"/> and marks the task in-flight. No-ops when the name is
    ///     not an expected unit (so the caller can pass through every <c>Agent</c> call unconditionally).
    /// </summary>
    public void RegisterSpawn(string toolCallId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_lock)
        {
            if (!_tasksByName.TryGetValue(name, out var taskRef))
            {
                return;
            }

            // A unit that has already validated or terminally failed is SETTLED. A controller re-issuing an
            // Agent call for it (e.g. after completion) must not reset its status or re-map the toolCallId,
            // or the eventual ObserveResult would re-run ValidateAndRecordNoLock and re-apply its
            // append/merge writes — silently corrupting state (Fix M1). Only a Pending unit transitions to
            // InFlight; an already-InFlight unit re-correlates as before.
            var status = StatusOfNoLock(name);
            if (status is WorkflowTaskStatus.Validated or WorkflowTaskStatus.Failed)
            {
                return;
            }

            _tasksByToolCallId[toolCallId] = taskRef;
            _status[name] = WorkflowTaskStatus.InFlight;
        }
    }

    /// <summary>Whether <paramref name="name"/> is a currently-expected (composed) unit name.</summary>
    public bool IsExpectedUnit(string name)
    {
        lock (_lock)
        {
            return _tasksByName.ContainsKey(name);
        }
    }

    /// <summary>Whether <paramref name="toolCallId"/> has been correlated to a task via <see cref="RegisterSpawn"/>.</summary>
    public bool IsRegisteredSpawn(string toolCallId)
    {
        lock (_lock)
        {
            return _tasksByToolCallId.ContainsKey(toolCallId);
        }
    }

    /// <summary>
    ///     Observes the result of a correlated <b>blocking</b> spawn (P3 path): an error result, non-JSON, or
    ///     a schema-invalid result is routed through the validation-retry policy (re-surfaced as pending while
    ///     the attempt budget allows, otherwise terminally failed with an <c>{ "_error": ... }</c> marker); a
    ///     valid result is recorded into <c>outputs[nodeId][taskId]</c>, its writes applied to state, and the
    ///     task marked validated. An unknown <paramref name="toolCallId"/> is surfaced as "unmatched".
    /// </summary>
    public void ObserveResult(string toolCallId, string resultText, bool isError)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (_tasksByToolCallId.TryGetValue(toolCallId, out var taskRef))
            {
                ObserveAnswerNoLock(taskRef, resultText, isError);
            }
            else
            {
                _unmatched.Add(toolCallId);
            }

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Observes the result of a correlated <c>Agent</c> tool call, distinguishing a <b>background spawn
    ///     receipt</b> from a <b>blocking answer</b>. An error is failed; a receipt
    ///     (<c>{ "status": "spawned", "agent_id": ... }</c>) records the <c>agent_id → task</c> correlation and
    ///     defers validation until the injected result arrives (<see cref="ObserveInjectedResult"/>); anything
    ///     else is treated as a blocking answer and validated/recorded immediately. An unknown
    ///     <paramref name="toolCallId"/> is surfaced as "unmatched".
    /// </summary>
    public void ObserveSpawnResult(string toolCallId, string resultText, bool isError)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (!_tasksByToolCallId.TryGetValue(toolCallId, out var taskRef))
            {
                _unmatched.Add(toolCallId);
            }
            else if (isError)
            {
                HandleFailureNoLock(taskRef, $"sub-agent returned an error: {resultText}");
            }
            else if (TryReadSpawnReceipt(resultText, out var agentId))
            {
                // Background spawn: correlate by the receipt agent_id and wait for the injected result.
                _tasksByAgentId[agentId] = taskRef;
            }
            else
            {
                ValidateAndRecordNoLock(taskRef, resultText);
            }

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Observes the injected background-completion result for a sub-agent, correlated by the receipt
    ///     <paramref name="agentId"/> recorded in <see cref="ObserveSpawnResult"/>. Validates and records (or
    ///     fails) exactly as the blocking path does. An unknown <paramref name="agentId"/> is surfaced as
    ///     "unmatched" rather than dropped.
    /// </summary>
    public void ObserveInjectedResult(string agentId, string resultText, bool isError)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (_tasksByAgentId.TryGetValue(agentId, out var taskRef))
            {
                ObserveAnswerNoLock(taskRef, resultText, isError);
            }
            else
            {
                _unmatched.Add(agentId);
            }

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Advances the controller from the current node to <paramref name="nextNodeId"/>, which must be a
    ///     declared edge target (or the definition's <c>onBudgetExhausted</c> escape). Increments the next
    ///     node's visit count and the step counter. Enforces the safety rails by REFUSING out-of-policy
    ///     moves: a node at its <c>maxVisits</c> ceiling may not be re-entered (route to its
    ///     <c>onMaxVisits</c>), and once the step budget is exhausted only the <c>onBudgetExhausted</c>
    ///     escape is allowed. When the next node is terminal, the result is taken from the supplied
    ///     <paramref name="result"/> or, when none is supplied, deep-rendered from the terminal's
    ///     <c>resultTemplate</c>; either way it is validated against the terminal's (or the definition's)
    ///     final output schema, captured, and the workflow completed. The runtime never auto-advances.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     No definition is loaded; the transition is not a declared edge; the next node has reached its
    ///     <c>maxVisits</c> ceiling; the step budget is exhausted and the move is not the
    ///     <c>onBudgetExhausted</c> escape; or the terminal result fails its final output schema.
    /// </exception>
    public void AdvanceTo(string? completedNodeId, string nextNodeId, JsonNode? result)
    {
        ArgumentException.ThrowIfNullOrEmpty(nextNodeId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (Definition is null || CurrentNodeId is null)
            {
                throw new InvalidOperationException("No workflow definition is loaded.");
            }

            // Safety rail: once the global step budget is reached, the only permitted move is the
            // definition's onBudgetExhausted escape, which acts as a global edge and bypasses the
            // declared-edge check below. When no escape is defined the advance is allowed so the
            // controller is never deadlocked.
            var budgetExhausted = Step >= Definition.MaxStepBudget;
            var budgetEscape =
                budgetExhausted
                && !string.IsNullOrEmpty(Definition.OnBudgetExhausted)
                && nextNodeId == Definition.OnBudgetExhausted;
            if (
                budgetExhausted
                && !string.IsNullOrEmpty(Definition.OnBudgetExhausted)
                && nextNodeId != Definition.OnBudgetExhausted
            )
            {
                throw new InvalidOperationException(
                    $"Step budget {Definition.MaxStepBudget} exhausted; route to onBudgetExhausted "
                        + $"'{Definition.OnBudgetExhausted}' instead."
                );
            }

            if (!budgetEscape && !IsDeclaredEdgeNoLock(CurrentNodeId, nextNodeId))
            {
                throw new InvalidOperationException(
                    $"Undeclared transition: '{CurrentNodeId}' has no edge to '{nextNodeId}'."
                );
            }

            var nextNode = FindNodeNoLock(nextNodeId);

            // Safety rail: a node with maxVisits = N may be ENTERED N times; the (N+1)-th entry is
            // refused so the controller routes to its onMaxVisits instead. The rejected path mutates
            // nothing.
            if (MaxVisitsOf(nextNode) is { } maxVisits)
            {
                var entered = _visits.TryGetValue(nextNodeId, out var priorVisits) ? priorVisits : 0;
                if (entered >= maxVisits)
                {
                    var onMaxVisits = OnMaxVisitsOf(nextNode);
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(onMaxVisits)
                            ? $"Node '{nextNodeId}' reached maxVisits {maxVisits} and no onMaxVisits is defined."
                            : $"Node '{nextNodeId}' reached maxVisits {maxVisits}; route to onMaxVisits '{onMaxVisits}' instead."
                    );
                }
            }

            // Validate any terminal result BEFORE mutating, so a schema failure leaves the runtime
            // untouched (no half-advanced node/visit/step state to recover from). When the controller
            // does not pass an explicit result, the terminal's resultTemplate (if any) is deep-rendered
            // from state to compose one.
            var terminal = nextNode as TerminalNode;
            JsonNode? capturedResult = null;
            if (terminal is not null)
            {
                var composed =
                    result
                    ?? (
                        terminal.ResultTemplate is { } template
                            ? TemplateNodeRenderer.Render(
                                template,
                                BuildContextNoLock(null, null, null)
                            )
                            : null
                    );

                if (composed is not null)
                {
                    var schema = terminal.FinalOutputSchema ?? Definition.FinalOutputSchema;
                    if (schema is not null)
                    {
                        var validation = _schemaValidator.ValidateDetailed(
                            composed.ToJsonString(),
                            schema.ToJsonString()
                        );
                        if (!validation.IsValid)
                        {
                            throw new InvalidOperationException(
                                "Final result failed schema validation: "
                                    + string.Join("; ", validation.Errors)
                            );
                        }
                    }

                    capturedResult = composed.DeepClone();
                }
            }

            CurrentNodeId = nextNodeId;
            _visits[nextNodeId] = (_visits.TryGetValue(nextNodeId, out var visits) ? visits : 0) + 1;
            Step++;

            if (terminal is not null)
            {
                if (capturedResult is not null)
                {
                    _result = capturedResult;
                }

                IsComplete = true;
            }

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>Writes <paramref name="value"/> into the state channel at <paramref name="path"/> using the given mode.</summary>
    public void SetState(string path, JsonNode? value, string? mode, string? key)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            var spec = new WriteSpec
            {
                To = path,
                Mode = ParseWriteMode(mode),
                Key = key,
            };
            StateWriter.Apply(_state, spec, value);

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>Sets a scoped note (<c>notes[scope][key] = value</c>).</summary>
    public void SetNotes(string scope, string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(key);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (_notes[scope] is not JsonObject scopeObject)
            {
                scopeObject = [];
                _notes[scope] = scopeObject;
            }

            scopeObject[key] = JsonValue.Create(value);

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Returns the controller-facing projection of runtime state. The default projection always carries
    ///     the ready-to-spawn <c>nextExpectedAction</c> unit(s); passing a projection that mentions
    ///     <c>state</c>, <c>outputs</c>, <c>notes</c>, or <c>all</c>/<c>full</c> includes those channels too.
    /// </summary>
    public JsonObject GetProjection(string? projection)
    {
        lock (_lock)
        {
            var nextActions = ComposeNoLock();
            var result = new JsonObject
            {
                ["currentNodeId"] = CurrentNodeId,
                ["isComplete"] = IsComplete,
                ["step"] = Step,
                ["visits"] = VisitsToJsonNoLock(),
                ["tasks"] = StatusToJsonNoLock(),
                ["nextExpectedAction"] = NextActionsToJsonNoLock(nextActions),
            };

            // For the active procedural node, surface the join progress (a SURFACE for the controller — the
            // runtime never auto-advances) and, if a unit has terminally failed, the node's onFailure route.
            if (CurrentNodeId is { } nodeId && FindNodeNoLock(nodeId) is ProceduralNode procedural)
            {
                var activeUnits = ActiveUnitsNoLock(procedural);
                result["join"] = BuildJoinNoLock(procedural, activeUnits);
                if (OnFailureRouteNoLock(procedural, activeUnits) is { } route)
                {
                    result["onFailure"] = route;
                }
            }

            // For the active conditional node, surface the deterministic recommended branch plus a
            // per-branch evaluation view (read-only hints; AdvanceTo still accepts any declared edge).
            if (
                CurrentNodeId is { } conditionalId
                && FindNodeNoLock(conditionalId) is ConditionalNode conditional
            )
            {
                AddConditionalRoutingNoLock(result, conditional);
            }

            // Safety-rail surfaces for the active node: a reached visit ceiling (route to onMaxVisits)
            // and a global step-budget exhaustion (route to onBudgetExhausted).
            AddVisitCeilingNoLock(result);
            AddBudgetSurfaceNoLock(result);

            // Surface per-unit validation/parse errors so the controller can re-author a re-surfaced unit.
            if (_lastError.Count > 0)
            {
                var errors = new JsonObject();
                foreach (var (name, error) in _lastError)
                {
                    errors[name] = error;
                }

                result["taskErrors"] = errors;
            }

            if (IncludesChannel(projection, "state"))
            {
                result["state"] = _state.DeepClone();
            }

            if (IncludesChannel(projection, "outputs"))
            {
                result["outputs"] = _outputs.DeepClone();
            }

            if (IncludesChannel(projection, "notes"))
            {
                result["notes"] = _notes.DeepClone();
            }

            if (_unmatched.Count > 0)
            {
                var unmatched = new JsonArray();
                foreach (var id in _unmatched)
                {
                    unmatched.Add(id);
                }

                result["unmatched"] = unmatched;
            }

            return result;
        }
    }

    /// <summary>Signals normal completion of the controller run to <see cref="Completion"/> waiters.</summary>
    internal void SignalCompletion() => _completion.TrySetResult();

    /// <summary>Faults <see cref="Completion"/> with <paramref name="ex"/> (controller run threw).</summary>
    internal void SignalFailure(Exception ex) => _completion.TrySetException(ex);

    /// <summary>
    ///     Captures a deep-cloned <see cref="WorkflowInstanceSnapshot"/> of all mutable runtime state under
    ///     the lock. The snapshot is self-contained (definition + channels + bookkeeping + per-task
    ///     correlation) so <see cref="FromSnapshot"/> can rebuild an equivalent runtime without re-running the
    ///     definition's ingest mutation of the channels.
    /// </summary>
    public WorkflowInstanceSnapshot Snapshot()
    {
        lock (_lock)
        {
            return BuildSnapshotNoLock(_instanceId ?? string.Empty);
        }
    }

    /// <summary>
    ///     Rebuilds a runtime from <paramref name="snapshot"/> with its state restored verbatim — the
    ///     definition is set WITHOUT re-running ingest (channels are restored as captured, not re-seeded), and
    ///     <c>currentNodeId</c>/<c>visits</c>/<c>step</c>/<c>outputs</c>/<c>state</c>/<c>notes</c>/<c>result</c>/
    ///     <c>isComplete</c> are all restored.
    /// </summary>
    /// <remarks>
    ///     <b>Orphan handling.</b> A task that was <c>in_flight</c> at snapshot time — or <c>pending</c> while
    ///     still holding a live <c>Agent</c> tool-call / background agent correlation but no recorded output —
    ///     cannot be resumed, because its sub-agent no longer exists after a restart. Such a task is reset to
    ///     <c>pending</c> and its tool-call/agent-id correlation is dropped, so the resumed controller
    ///     re-spawns it. Its attempt budget is preserved so bounded validation retries survive the restart. A
    ///     <c>validated</c> or terminally <c>failed</c> task keeps its status and recorded output untouched.
    /// </remarks>
    public static WorkflowRuntime FromSnapshot(
        WorkflowInstanceSnapshot snapshot,
        IJsonSchemaValidator? validator = null
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var runtime = new WorkflowRuntime(validator);
        runtime.RestoreFromSnapshot(snapshot);
        return runtime;
    }

    private void RestoreFromSnapshot(WorkflowInstanceSnapshot snapshot)
    {
        lock (_lock)
        {
            // Restore the definition verbatim (no validator, no channel re-seeding) and the channels as
            // captured — a deep copy so the runtime never aliases the caller's snapshot nodes.
            Definition = snapshot.Definition;
            CurrentNodeId = snapshot.CurrentNodeId;
            IsComplete = snapshot.IsComplete;
            _result = snapshot.Result?.DeepClone();
            Step = snapshot.Step;
            _inputs = CloneObject(snapshot.Inputs);
            _state = CloneObject(snapshot.State);
            _outputs = CloneObject(snapshot.Outputs);
            _notes = CloneObject(snapshot.Notes);

            _visits.Clear();
            foreach (var (nodeId, count) in snapshot.Visits)
            {
                _visits[nodeId] = count;
            }

            _tasksByName.Clear();
            _tasksByToolCallId.Clear();
            _tasksByAgentId.Clear();
            _status.Clear();
            _attempts.Clear();
            _lastError.Clear();
            _composed.Clear();
            _unmatched.Clear();

            foreach (var task in snapshot.Tasks)
            {
                RestoreTaskNoLock(task);
            }
        }
    }

    /// <summary>Rebuilds one task occurrence's correlation/status from its snapshot, applying orphan reset.</summary>
    private void RestoreTaskNoLock(WorkflowTaskSnapshot task)
    {
        var taskRef = new TaskRef
        {
            NodeId = task.NodeId,
            Visit = task.Visit,
            TaskId = task.TaskId,
            Index = task.Index,
            OutputSchema = task.OutputSchema?.DeepClone(),
            Writes = task.Writes,
            OnFailure = task.OnFailure,
            MaxValidationRetries = task.MaxValidationRetries,
        };
        _tasksByName[task.Name] = taskRef;

        // An in-flight unit (or a pending one still holding a live spawn correlation, hence no recorded
        // output) is an orphan after a restart: reset it to pending and drop its correlation so the
        // controller re-spawns it. Everything else keeps its status and (for completeness) its correlation.
        var hasLiveCorrelation = task.ToolCallId is not null || task.AgentId is not null;
        var isOrphan =
            task.Status == WorkflowTaskStatus.InFlight
            || (task.Status == WorkflowTaskStatus.Pending && hasLiveCorrelation);

        if (isOrphan)
        {
            _status[task.Name] = WorkflowTaskStatus.Pending;
        }
        else
        {
            _status[task.Name] = task.Status;
            if (task.ToolCallId is { } toolCallId)
            {
                _tasksByToolCallId[toolCallId] = taskRef;
            }

            if (task.AgentId is { } agentId)
            {
                _tasksByAgentId[agentId] = taskRef;
            }
        }

        // Preserve the validation-retry budget and last error across the restart.
        if (task.Attempts > 0)
        {
            _attempts[task.Name] = task.Attempts;
        }

        if (task.LastError is { } error)
        {
            _lastError[task.Name] = error;
        }
    }

    /// <summary>Builds a snapshot of the current state. Caller MUST hold <see cref="_lock"/>.</summary>
    private WorkflowInstanceSnapshot BuildSnapshotNoLock(string instanceId)
    {
        // Reverse-index the live spawn correlation so each task carries its own tool-call / agent id.
        var toolCallByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (toolCallId, taskRef) in _tasksByToolCallId)
        {
            toolCallByName[taskRef.Name] = toolCallId;
        }

        var agentByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (agentId, taskRef) in _tasksByAgentId)
        {
            agentByName[taskRef.Name] = agentId;
        }

        var tasks = new List<WorkflowTaskSnapshot>(_tasksByName.Count);
        foreach (var (name, taskRef) in _tasksByName)
        {
            tasks.Add(
                new WorkflowTaskSnapshot
                {
                    Name = name,
                    NodeId = taskRef.NodeId,
                    Visit = taskRef.Visit,
                    TaskId = taskRef.TaskId,
                    Index = taskRef.Index,
                    OutputSchema = taskRef.OutputSchema?.DeepClone(),
                    Writes = taskRef.Writes,
                    OnFailure = taskRef.OnFailure,
                    MaxValidationRetries = taskRef.MaxValidationRetries,
                    Status = StatusOfNoLock(name),
                    Attempts = _attempts.TryGetValue(name, out var attempts) ? attempts : 0,
                    LastError = _lastError.TryGetValue(name, out var error) ? error : null,
                    ToolCallId = toolCallByName.TryGetValue(name, out var toolCallId) ? toolCallId : null,
                    AgentId = agentByName.TryGetValue(name, out var agentId) ? agentId : null,
                }
            );
        }

        return new WorkflowInstanceSnapshot
        {
            SchemaVersion = WorkflowInstanceSnapshot.CurrentSchemaVersion,
            InstanceId = instanceId,
            Definition = Definition,
            CurrentNodeId = CurrentNodeId,
            IsComplete = IsComplete,
            Result = _result?.DeepClone(),
            Step = Step,
            Inputs = CloneObject(_inputs),
            State = CloneObject(_state),
            Outputs = CloneObject(_outputs),
            Notes = CloneObject(_notes),
            Visits = new Dictionary<string, int>(_visits, StringComparer.Ordinal),
            Tasks = tasks,
        };
    }

    /// <summary>Builds a snapshot for persistence, or <c>null</c> when no store is attached. Caller holds the lock.</summary>
    private WorkflowInstanceSnapshot? CaptureSnapshotNoLock() =>
        _store is not null && _instanceId is not null ? BuildSnapshotNoLock(_instanceId) : null;

    /// <summary>
    ///     Best-effort persistence of a captured snapshot (no-op when not attached). Saves are SERIALIZED on a
    ///     per-instance chain so they apply in capture order — save N completes before save N+1 starts — which
    ///     prevents a slow, stale save from overwriting a newer snapshot (Fix M2). Use
    ///     <see cref="DrainPersistAsync"/> to flush pending saves before disposal.
    /// </summary>
    private void Persist(WorkflowInstanceSnapshot? snapshot)
    {
        if (snapshot is null || _store is null || _instanceId is null)
        {
            return;
        }

        var store = _store;
        var instanceId = _instanceId;
        lock (_saveLock)
        {
            // ExecuteSynchronously keeps a save inline when its antecedent is ALREADY complete (the common
            // case for a synchronous store), so persistence stays as prompt as the prior fire-and-forget for
            // such stores; an async store's save simply chains the next save behind its still-running task.
            _saveChain = _saveChain
                .ContinueWith(
                    _ => SaveBestEffortAsync(store, instanceId, snapshot),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
                .Unwrap();
        }
    }

    /// <summary>
    ///     Awaits the current per-instance save chain so a host can flush every pending best-effort save
    ///     before disposing the run. Best-effort saves never fault the chain, so this never throws; it is a
    ///     no-op (already-completed task) when no store is attached.
    /// </summary>
    public Task DrainPersistAsync()
    {
        lock (_saveLock)
        {
            return _saveChain;
        }
    }

    private async Task SaveBestEffortAsync(
        IWorkflowStore store,
        string instanceId,
        WorkflowInstanceSnapshot snapshot
    )
    {
        try
        {
            await store.SaveAsync(instanceId, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: a persistence fault must never corrupt or fail the live run. Surface it at Warning
            // (Fix M3) so a store outage is visible; the next mutation re-attempts with a fresh snapshot.
            _logger?.LogWarning(ex, "Workflow {InstanceId} snapshot persistence failed", instanceId);
        }
    }

    /// <summary>
    ///     Builds a binding context over the live channels. Internal callers use it read-only under the lock
    ///     and may keep aliasing the live channels (<paramref name="clone"/> = <c>false</c>); the public
    ///     <see cref="BuildContext"/> passes <paramref name="clone"/> = <c>true</c> so its returned context is
    ///     isolated from later runtime mutation (Fix H1).
    /// </summary>
    private BindingContext BuildContextNoLock(
        JsonNode? item,
        int? index,
        int? count,
        bool clone = false
    ) =>
        new()
        {
            Inputs = clone ? CloneObject(_inputs) : _inputs,
            State = clone ? CloneObject(_state) : _state,
            Outputs = clone ? CloneObject(_outputs) : _outputs,
            Notes = clone ? CloneObject(_notes) : _notes,
            Visits = new Dictionary<string, int>(_visits, StringComparer.Ordinal),
            Step = Step,
            Item = item,
            Index = index,
            Count = count,
        };

    private IReadOnlyList<SpawnUnit> ComposeNoLock()
    {
        if (
            Definition is null
            || CurrentNodeId is null
            || FindNodeNoLock(CurrentNodeId) is not ProceduralNode node
            || node.TasksMode != TasksMode.Authored
            || node.TaskList is not { Count: > 0 } tasks
        )
        {
            return [];
        }

        var visit = _visits.TryGetValue(node.Id, out var count) ? count : 1;
        var units = new List<SpawnUnit>();
        foreach (var task in tasks)
        {
            if (task.Delegate != DelegateKind.Agent || string.IsNullOrEmpty(task.SubagentType))
            {
                continue;
            }

            if (string.IsNullOrEmpty(task.ForEach))
            {
                ComposeUnitNoLock(node, visit, task, index: null, item: null, count: null, units);
                continue;
            }

            // forEach fan-out: resolve the source binding to an array and spawn one unit per element.
            if (BuildContextNoLock(null, null, null).Resolve(task.ForEach) is not JsonArray source)
            {
                continue;
            }

            for (var i = 0; i < source.Count; i++)
            {
                ComposeUnitNoLock(node, visit, task, i, source[i], source.Count, units);
            }
        }

        return units;
    }

    /// <summary>
    ///     Registers one task occurrence (a non-forEach unit when <paramref name="index"/> is <c>null</c>, or a
    ///     single forEach element otherwise): records its <see cref="TaskRef"/> and seeds a <c>pending</c>
    ///     status so the unit counts toward the node's join totals, then surfaces it (rendering its prompt with
    ///     the loop locals) only when it is still <c>pending</c> — an <c>in_flight</c>/<c>validated</c>/terminal
    ///     <c>failed</c> unit is registered but not re-surfaced, so polling <c>GetWorkflow</c> never re-spawns it.
    /// </summary>
    private void ComposeUnitNoLock(
        ProceduralNode node,
        int visit,
        WorkflowTask task,
        int? index,
        JsonNode? item,
        int? count,
        List<SpawnUnit> units
    )
    {
        var taskRef = new TaskRef
        {
            NodeId = node.Id,
            Visit = visit,
            TaskId = task.Id,
            Index = index,
            OutputSchema = task.OutputSchema,
            Writes = task.Writes,
            OnFailure = task.OnFailure,
            MaxValidationRetries = task.MaxValidationRetries,
        };

        _tasksByName[taskRef.Name] = taskRef;
        _ = _status.TryAdd(taskRef.Name, WorkflowTaskStatus.Pending);

        if (_status[taskRef.Name] != WorkflowTaskStatus.Pending)
        {
            return;
        }

        var unit = new SpawnUnit
        {
            Name = taskRef.Name,
            SubagentType = task.SubagentType!,
            Prompt = ComposePrompt(task, BuildContextNoLock(item, index, count)),
            OutputSchema = task.OutputSchema?.DeepClone(),
        };
        _composed[taskRef.Name] = unit;
        units.Add(unit);
    }

    private string ComposePrompt(WorkflowTask task, BindingContext context)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(Definition!.SharedContext))
        {
            parts.Add(Definition.SharedContext);
        }

        parts.Add(TemplateRenderer.Render(task.PromptTemplate, context));

        if (task.OutputSchema is { } schema)
        {
            parts.Add(
                "Return ONLY a JSON object that conforms to this schema:\n" + schema.ToJsonString()
            );
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    ///     Shared "blocking answer" handling for the P3 tool-result path and the injected-result path: an
    ///     error fails the task; otherwise the answer is validated and recorded.
    /// </summary>
    private void ObserveAnswerNoLock(TaskRef taskRef, string resultText, bool isError)
    {
        if (isError)
        {
            HandleFailureNoLock(taskRef, $"sub-agent returned an error: {resultText}");
            return;
        }

        ValidateAndRecordNoLock(taskRef, resultText);
    }

    /// <summary>
    ///     Parses, schema-validates, records and applies the writes of a successful task answer. Any
    ///     parse/validation failure is routed through <see cref="HandleFailureNoLock"/> (which decides between
    ///     a retry and a terminal failure). On success the unit is marked validated and its last error cleared.
    /// </summary>
    private void ValidateAndRecordNoLock(TaskRef taskRef, string resultText)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(resultText);
        }
        catch (JsonException ex)
        {
            HandleFailureNoLock(taskRef, $"task output is not valid JSON: {ex.Message}");
            return;
        }

        if (parsed is null)
        {
            HandleFailureNoLock(taskRef, "task output parsed to a null JSON value.");
            return;
        }

        if (taskRef.OutputSchema is { } schema)
        {
            var validation = _schemaValidator.ValidateDetailed(resultText, schema.ToJsonString());
            if (!validation.IsValid)
            {
                HandleFailureNoLock(
                    taskRef,
                    "task output failed schema validation: " + string.Join("; ", validation.Errors)
                );
                return;
            }
        }

        WriteOutputSlotNoLock(taskRef, parsed);
        if (taskRef.Writes is { } writes)
        {
            StateWriter.Apply(_state, writes, parsed);
        }

        _status[taskRef.Name] = WorkflowTaskStatus.Validated;
        _ = _lastError.Remove(taskRef.Name);
    }

    /// <summary>
    ///     Applies the "controller re-spawns; runtime bounds the attempts" retry policy. The attempt counter
    ///     for the unit is incremented; while it stays within <see cref="WorkflowTask.MaxValidationRetries"/>
    ///     the unit is marked <c>pending</c> again (so it re-surfaces in <c>nextExpectedAction</c>), its error
    ///     surfaced, and its spawn correlation cleared so the next spawn re-correlates. Once the budget is
    ///     exhausted the unit is terminally failed and an <c>{ "_error": ... }</c> marker recorded into outputs.
    /// </summary>
    private void HandleFailureNoLock(TaskRef taskRef, string error)
    {
        var attempts = (_attempts.TryGetValue(taskRef.Name, out var prior) ? prior : 0) + 1;
        _attempts[taskRef.Name] = attempts;
        _lastError[taskRef.Name] = error;

        if (attempts <= taskRef.MaxValidationRetries)
        {
            _status[taskRef.Name] = WorkflowTaskStatus.Pending;
            ClearCorrelationNoLock(taskRef.Name);
            return;
        }

        WriteOutputSlotNoLock(taskRef, new JsonObject { ["_error"] = error });
        _status[taskRef.Name] = WorkflowTaskStatus.Failed;
    }

    /// <summary>
    ///     Writes <paramref name="value"/> into the output slot for <paramref name="taskRef"/>: a forEach unit
    ///     (one with an <see cref="TaskRef.Index"/>) lands at its index in an array under
    ///     <c>outputs[nodeId][taskId]</c> (the array is grown/padded with nulls so out-of-order completions
    ///     land correctly); a non-forEach unit is written directly as the scalar/object value.
    /// </summary>
    private void WriteOutputSlotNoLock(TaskRef taskRef, JsonNode value)
    {
        if (_outputs[taskRef.NodeId] is not JsonObject nodeOutputs)
        {
            nodeOutputs = [];
            _outputs[taskRef.NodeId] = nodeOutputs;
        }

        if (taskRef.Index is not { } index)
        {
            nodeOutputs[taskRef.TaskId] = value.DeepClone();
            return;
        }

        if (nodeOutputs[taskRef.TaskId] is not JsonArray array)
        {
            array = [];
            nodeOutputs[taskRef.TaskId] = array;
        }

        while (array.Count <= index)
        {
            array.Add(null);
        }

        array[index] = value.DeepClone();
    }

    /// <summary>Removes any tool-call-id / agent-id correlation pointing at the unit named <paramref name="unitName"/>.</summary>
    private void ClearCorrelationNoLock(string unitName)
    {
        RemoveByUnit(_tasksByToolCallId, unitName);
        RemoveByUnit(_tasksByAgentId, unitName);

        static void RemoveByUnit(Dictionary<string, TaskRef> map, string unitName)
        {
            foreach (var key in map.Where(kv => kv.Value.Name == unitName).Select(kv => kv.Key).ToList())
            {
                _ = map.Remove(key);
            }
        }
    }

    /// <summary>
    ///     Detects a background spawn receipt (<c>{ "status": "spawned", "agent_id": "..." }</c>) and extracts
    ///     its <paramref name="agentId"/>. Anything else (including a blocking JSON answer that happens to be an
    ///     object) returns <c>false</c>.
    /// </summary>
    private static bool TryReadSpawnReceipt(string resultText, out string agentId)
    {
        agentId = string.Empty;
        if (string.IsNullOrWhiteSpace(resultText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (
                !doc.RootElement.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || status.GetString() != "spawned"
            )
            {
                return false;
            }

            if (
                !doc.RootElement.TryGetProperty("agent_id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(id.GetString())
            )
            {
                return false;
            }

            agentId = id.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsDeclaredEdgeNoLock(string from, string to) =>
        FindNodeNoLock(from) switch
        {
            StartNode start => start.Next.Contains(to),
            ProceduralNode procedural =>
                procedural.Next.Contains(to)
                || procedural.OnFailure == to
                || procedural.OnMaxVisits == to
                || (procedural.TaskList?.Any(t => t.OnFailure == to) ?? false),
            ConditionalNode conditional =>
                (conditional.Branches?.Any(b => b.To == to) ?? false)
                || conditional.Else == to
                || conditional.OnMaxVisits == to,
            _ => false,
        };

    private WorkflowNode? FindNodeNoLock(string id) =>
        Definition?.Nodes.FirstOrDefault(n => n.Id == id);

    private JsonObject VisitsToJsonNoLock()
    {
        var visits = new JsonObject();
        foreach (var (key, value) in _visits)
        {
            visits[key] = value;
        }

        return visits;
    }

    private JsonObject StatusToJsonNoLock()
    {
        var statuses = new JsonObject();
        foreach (var (key, value) in _status)
        {
            statuses[key] = Wire(value);
        }

        return statuses;
    }

    /// <summary>The registered units for the active visit of <paramref name="node"/> (the join's working set).</summary>
    private List<TaskRef> ActiveUnitsNoLock(ProceduralNode node)
    {
        var visit = _visits.TryGetValue(node.Id, out var count) ? count : 1;
        return [.. _tasksByName.Values.Where(t => t.NodeId == node.Id && t.Visit == visit)];
    }

    /// <summary>
    ///     Computes the controller-facing join surface for the active node: counts by status plus a
    ///     <c>satisfied</c> flag (mode <c>all</c> → every unit validated; mode <c>any</c> → at least one).
    /// </summary>
    private JsonObject BuildJoinNoLock(ProceduralNode node, IReadOnlyList<TaskRef> units)
    {
        var validated = units.Count(u => StatusOfNoLock(u.Name) == WorkflowTaskStatus.Validated);
        var failed = units.Count(u => StatusOfNoLock(u.Name) == WorkflowTaskStatus.Failed);
        var inFlight = units.Count(u => StatusOfNoLock(u.Name) == WorkflowTaskStatus.InFlight);

        var mode = node.JoinPolicy?.Mode ?? JoinMode.All;
        var total = units.Count;

        // An all-join is satisfied once every COMPOSED unit validated — including the vacuous total == 0
        // case: a node whose working set is empty (a forEach over an empty array, or a procedural node whose
        // TaskList yields no spawnable units) has nothing to spawn and is therefore vacuously satisfied, so
        // the controller can route on instead of livelocking until the budget rail trips (Fix M5). This is
        // only ever evaluated AFTER compose has run for the node — GetProjection calls ComposeNoLock before
        // ActiveUnitsNoLock/BuildJoinNoLock — so a not-yet-composed node is never falsely reported satisfied.
        var satisfied = mode == JoinMode.Any ? validated >= 1 : validated == total;

        return new JsonObject
        {
            ["mode"] = mode == JoinMode.Any ? "any" : "all",
            ["total"] = total,
            ["validated"] = validated,
            ["failed"] = failed,
            ["inFlight"] = inFlight,
            ["satisfied"] = satisfied,
        };
    }

    /// <summary>
    ///     The node id the controller should route to when a unit of the active node has terminally failed:
    ///     the failed task's <see cref="WorkflowTask.OnFailure"/> if set, else the node's
    ///     <see cref="ProceduralNode.OnFailure"/>; <c>null</c> when nothing has failed or no route exists.
    /// </summary>
    private string? OnFailureRouteNoLock(ProceduralNode node, IReadOnlyList<TaskRef> units)
    {
        var failed = units.FirstOrDefault(u => StatusOfNoLock(u.Name) == WorkflowTaskStatus.Failed);
        if (failed is null)
        {
            return null;
        }

        var route = failed.OnFailure ?? node.OnFailure;
        return string.IsNullOrEmpty(route) ? null : route;
    }

    /// <summary>
    ///     Surfaces the deterministic recommended branch for an active conditional node: the first branch
    ///     whose structured condition holds (prose <c>when</c> branches have no structured form and are
    ///     skipped), else the <c>else</c> target. A per-branch <c>{ to, matched }</c> view is surfaced too.
    /// </summary>
    private void AddConditionalRoutingNoLock(JsonObject result, ConditionalNode conditional)
    {
        var context = BuildContextNoLock(null, null, null);
        var evaluations = new JsonArray();
        string? recommended = null;
        foreach (var branch in conditional.Branches)
        {
            var matched =
                branch.StructuredCondition is { } condition
                && ConditionEvaluator.Evaluate(condition, context);
            evaluations.Add(new JsonObject { ["to"] = branch.To, ["matched"] = matched });
            recommended ??= matched ? branch.To : null;
        }

        result["recommendedBranch"] = recommended ?? conditional.Else;
        result["branchEvaluations"] = evaluations;
    }

    /// <summary>
    ///     Surfaces <c>atVisitCeiling</c>/<c>onMaxVisits</c> when the active node has been entered its
    ///     declared <c>maxVisits</c> times, so the controller routes to its <c>onMaxVisits</c> instead of
    ///     re-entering it.
    /// </summary>
    private void AddVisitCeilingNoLock(JsonObject result)
    {
        if (CurrentNodeId is not { } nodeId)
        {
            return;
        }

        var node = FindNodeNoLock(nodeId);
        if (
            MaxVisitsOf(node) is { } maxVisits
            && (_visits.TryGetValue(nodeId, out var entered) ? entered : 0) >= maxVisits
        )
        {
            result["atVisitCeiling"] = true;
            if (OnMaxVisitsOf(node) is { } onMaxVisits && !string.IsNullOrEmpty(onMaxVisits))
            {
                result["onMaxVisits"] = onMaxVisits;
            }
        }
    }

    /// <summary>
    ///     Surfaces <c>budgetExhausted</c>/<c>onBudgetExhausted</c> when the global step counter has reached
    ///     the definition's <c>maxStepBudget</c>, so the controller routes to the escape.
    /// </summary>
    private void AddBudgetSurfaceNoLock(JsonObject result)
    {
        if (Definition is { } definition && Step >= definition.MaxStepBudget)
        {
            result["budgetExhausted"] = true;
            if (!string.IsNullOrEmpty(definition.OnBudgetExhausted))
            {
                result["onBudgetExhausted"] = definition.OnBudgetExhausted;
            }
        }
    }

    /// <summary>The visit ceiling declared on a node (procedural/conditional), or <c>null</c> when unbounded.</summary>
    private static int? MaxVisitsOf(WorkflowNode? node) =>
        node switch
        {
            ProceduralNode procedural => procedural.MaxVisits,
            ConditionalNode conditional => conditional.MaxVisits,
            _ => null,
        };

    /// <summary>The <c>onMaxVisits</c> route declared on a node (procedural/conditional), or <c>null</c>.</summary>
    private static string? OnMaxVisitsOf(WorkflowNode? node) =>
        node switch
        {
            ProceduralNode procedural => procedural.OnMaxVisits,
            ConditionalNode conditional => conditional.OnMaxVisits,
            _ => null,
        };

    private WorkflowTaskStatus StatusOfNoLock(string unitName) =>
        _status.TryGetValue(unitName, out var status) ? status : WorkflowTaskStatus.Pending;

    private static JsonArray NextActionsToJsonNoLock(IReadOnlyList<SpawnUnit> units)
    {
        var array = new JsonArray();
        foreach (var unit in units)
        {
            array.Add(
                new JsonObject
                {
                    ["name"] = unit.Name,
                    ["subagentType"] = unit.SubagentType,
                    ["prompt"] = unit.Prompt,
                    ["outputSchema"] = unit.OutputSchema?.DeepClone(),
                }
            );
        }

        return array;
    }

    private static bool IncludesChannel(string? projection, string channel) =>
        projection is not null
        && (
            projection.Contains("all", StringComparison.OrdinalIgnoreCase)
            || projection.Contains("full", StringComparison.OrdinalIgnoreCase)
            || projection.Contains(channel, StringComparison.OrdinalIgnoreCase)
        );

    private static JsonObject CloneObject(JsonObject? source) =>
        source?.DeepClone() as JsonObject ?? [];

    private static WriteMode ParseWriteMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            null or "" or "set" => WriteMode.Set,
            "append" => WriteMode.Append,
            "merge" => WriteMode.Merge,
            _ => throw new ArgumentException(
                $"Unsupported state write mode '{mode}' (expected set, append, or merge).",
                nameof(mode)
            ),
        };

    private static string Wire(WorkflowTaskStatus status) =>
        status switch
        {
            WorkflowTaskStatus.Pending => "pending",
            WorkflowTaskStatus.InFlight => "in_flight",
            WorkflowTaskStatus.Validated => "validated",
            WorkflowTaskStatus.Failed => "failed",
            _ => status.ToString().ToLowerInvariant(),
        };

    /// <summary>
    ///     Correlation record for one authored task occurrence: which node/visit/task it belongs to, the
    ///     optional forEach index (<c>null</c> for a non-forEach unit, the element index for a fan-out unit),
    ///     the schema/writes the observed result is validated against and applied through, the per-task
    ///     <see cref="WorkflowTask.OnFailure"/> route, and the validation-retry budget.
    /// </summary>
    private sealed record TaskRef
    {
        public required string NodeId { get; init; }
        public required int Visit { get; init; }
        public required string TaskId { get; init; }
        public int? Index { get; init; }
        public JsonNode? OutputSchema { get; init; }
        public WriteSpec? Writes { get; init; }
        public string? OnFailure { get; init; }
        public int MaxValidationRetries { get; init; }

        public string Name =>
            Index is { } index ? $"{NodeId}:{Visit}:{TaskId}:{index}" : $"{NodeId}:{Visit}:{TaskId}";
    }
}
