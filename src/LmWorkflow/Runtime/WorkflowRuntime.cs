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
///         element), <b>fail-fast handling of background spawns</b> (a <c>run_in_background</c> spawn receipt
///         is terminally, non-retryably failed in V1 because its injected completion is not observable
///         end-to-end — the dormant correlation is forward-built), <b>join surfacing</b> (an <c>all</c>/
///         <c>any</c> progress view for the controller), and <b>bounded validation retries</b>.
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

    // The authored-task lifecycle (compose / spawn correlation / result recording) and the per-task
    // status/attempt/error bookkeeping are owned by the coordinator. It holds no lock of its own: the runtime
    // invokes every coordinator method while holding _lock, so all its state is mutated under the one lock.
    private readonly TaskCoordinator _coordinator;

    private readonly Dictionary<string, int> _visits = new(StringComparer.Ordinal);

    // O(1) node-id -> node lookup rebuilt whenever Definition changes; it replaces a linear node-list scan
    // that ran 4-5x per GetProjection/AdvanceTo. Empty when no definition is loaded.
    private readonly Dictionary<string, WorkflowNode> _nodeIndex = new(StringComparer.Ordinal);

    // The live data channels the runtime mutates IN PLACE under _lock. They are exposed only through the
    // public getters below, which hand back a deep copy taken under the lock so a host can never observe a
    // half-mutated channel or mutate internal state by writing into the returned node.
    private JsonObject _inputs = [];
    private JsonObject _state = [];
    private JsonObject _outputs = [];
    private JsonObject _notes = [];
    private JsonNode? _result;

    // Optional best-effort persistence: when attached, a fresh snapshot is saved after every state-mutating
    // public method so the run can be resumed (single-root) after a restart. No store attached => no-op.
    private IWorkflowStore? _store;
    private string? _instanceId;

    // Persistence sequencing is delegated to a collaborator that owns its OWN save-chain lock (independent of
    // _lock): the runtime captures the snapshot under _lock, releases it, then enqueues the save. The
    // collaborator serializes saves in capture order and swallows/logs faults, so persistence never blocks or
    // faults the live run.
    private readonly SnapshotPersister _persister = new();

    // Optional logger so a swallowed best-effort persistence fault is at least visible (surfaced at Warning).
    private readonly ILogger? _logger;

    /// <summary>
    ///     Creates a runtime. A custom <paramref name="schemaValidator"/> may be injected for tests, and an
    ///     optional <paramref name="logger"/> surfaces otherwise-swallowed best-effort persistence faults.
    /// </summary>
    public WorkflowRuntime(IJsonSchemaValidator? schemaValidator = null, ILogger? logger = null)
    {
        _schemaValidator = schemaValidator ?? new JsonSchemaValidator();
        _logger = logger;

        // The coordinator reads/mutates runtime state through these delegates. They capture the runtime's live
        // fields (read each call), so a re-load/restore that reassigns a channel is observed without staleness.
        // Every delegate is only invoked from a coordinator method called by the runtime under _lock.
        _coordinator = new TaskCoordinator(
            _schemaValidator,
            definition: () => Definition,
            currentNodeId: () => CurrentNodeId,
            findNode: FindNodeNoLock,
            currentVisit: nodeId => _visits.TryGetValue(nodeId, out var count) ? count : 1,
            liveState: () => _state,
            liveOutputs: () => _outputs,
            buildContext: (item, index, count) => BuildContextNoLock(item, index, count)
        );
    }

    /// <summary>
    ///     Attaches a persistence <paramref name="store"/> and the <paramref name="instanceId"/> to save under.
    ///     Once attached, every state-mutating method persists a fresh snapshot (best-effort). Idempotent and
    ///     safe to call before any state exists; it does not itself persist.
    /// </summary>
    internal void AttachStore(IWorkflowStore store, string instanceId)
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
    ///     copy taken under the lock, so a host reading it never aliases live runtime state.
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
    ///     mutating the returned object does not change runtime state.
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
    ///     mutating the returned object does not change runtime state.
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
    ///     under the lock; mutating the returned object does not change runtime state.
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
    ///     lock; mutating the returned object does not change runtime state.
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
                return _coordinator.SnapshotComposed();
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
            RebuildNodeIndexNoLock();
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

            _coordinator.Reset();

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>Shallow-merges <paramref name="inputs"/> into the inputs channel (caller-supplied seed).</summary>
    internal void MergeInputs(JsonObject inputs)
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
    ///     returned context never aliases live runtime state.
    /// </summary>
    internal BindingContext BuildContext(JsonNode? item = null, int? index = null, int? count = null)
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
    internal IReadOnlyList<SpawnUnit> ComposeNextExpectedAction()
    {
        lock (_lock)
        {
            return _coordinator.Compose();
        }
    }

    /// <summary>
    ///     Correlates an observed <c>Agent</c> tool call (by its <paramref name="toolCallId"/>) to the
    ///     expected unit named <paramref name="name"/> and marks the task in-flight. No-ops when the name is
    ///     not an expected unit (so the caller can pass through every <c>Agent</c> call unconditionally).
    /// </summary>
    internal void RegisterSpawn(string toolCallId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_lock)
        {
            _coordinator.RegisterSpawn(toolCallId, name);
        }
    }

    /// <summary>Whether <paramref name="name"/> is a currently-expected (composed) unit name.</summary>
    internal bool IsExpectedUnit(string name)
    {
        lock (_lock)
        {
            return _coordinator.IsExpectedUnit(name);
        }
    }

    /// <summary>Whether <paramref name="toolCallId"/> has been correlated to a task via <see cref="RegisterSpawn"/>.</summary>
    internal bool IsRegisteredSpawn(string toolCallId)
    {
        lock (_lock)
        {
            return _coordinator.IsRegisteredSpawn(toolCallId);
        }
    }

    /// <summary>
    ///     Observes the result of a correlated <b>blocking</b> spawn (P3 path): an error result, non-JSON, or
    ///     a schema-invalid result is routed through the validation-retry policy (re-surfaced as pending while
    ///     the attempt budget allows, otherwise terminally failed with an <c>{ "_error": ... }</c> marker); a
    ///     valid result is recorded into <c>outputs[nodeId][taskId]</c>, its writes applied to state, and the
    ///     task marked validated. An unknown <paramref name="toolCallId"/> is surfaced as "unmatched".
    /// </summary>
    internal void ObserveResult(string toolCallId, string resultText, bool isError)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            _coordinator.ObserveResult(toolCallId, resultText, isError);
            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Observes the result of a correlated <c>Agent</c> tool call, distinguishing a <b>background spawn
    ///     receipt</b> from a <b>blocking answer</b>. An error is failed; a background receipt
    ///     (<c>{ "status": "spawned", "agent_id": ... }</c>) is NOT supported in V1 and is FAILED FAST
    ///     (terminally, non-retryably) instead of deferred, because its injected completion is not observable
    ///     end-to-end; anything else is treated as a blocking answer and validated/recorded immediately. An
    ///     unknown <paramref name="toolCallId"/> is surfaced as "unmatched".
    /// </summary>
    internal void ObserveSpawnResult(string toolCallId, string resultText, bool isError)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            _coordinator.ObserveSpawnResult(toolCallId, resultText, isError);
            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Observes the injected background-completion result for a sub-agent, correlated by its receipt
    ///     <paramref name="agentId"/>. DORMANT in V1: the receipt path now fails fast (see
    ///     <see cref="ObserveSpawnResult"/>) and never records an <c>agent_id → task</c> correlation, so in V1
    ///     this finds no mapping and surfaces any injected result as "unmatched". Forward-built for when
    ///     injected completions become observable, where it validates/records (or fails) like the blocking path.
    /// </summary>
    internal void ObserveInjectedResult(string agentId, string resultText, bool isError)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            _coordinator.ObserveInjectedResult(agentId, resultText, isError);
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
    public void SetState(string path, JsonNode? value, string? mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            // WriteSpec.Key is only meaningful for the deferred upsert mode (out of V1 scope); set/append/
            // merge never read it, so the controller-facing SetState carries no key.
            var spec = new WriteSpec { To = path, Mode = ParseWriteMode(mode) };
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
            // Compose first (the side-effecting registration stays in the coordinator), capture the read
            // state under the lock, then hand it to the pure renderer.
            var nextActions = _coordinator.Compose();
            return WorkflowProjectionBuilder.Build(BuildProjectionInputsNoLock(nextActions, projection));
        }
    }

    /// <summary>
    ///     Assembles the read-only <see cref="ProjectionInputs"/> snapshot the projection renderer consumes.
    ///     Caller MUST hold <see cref="_lock"/>; the live channels/maps are passed by reference and read
    ///     synchronously by the renderer under the lock (the visit ceiling's <c>maxVisits</c>/<c>onMaxVisits</c>
    ///     are pre-resolved here so the node-policy lookup stays in one place, shared with <c>AdvanceTo</c>).
    /// </summary>
    private ProjectionInputs BuildProjectionInputsNoLock(
        IReadOnlyList<SpawnUnit> nextActions,
        string? projection
    )
    {
        var activeNode = CurrentNodeId is { } nodeId ? FindNodeNoLock(nodeId) : null;
        IReadOnlyList<ProjectionActiveUnit> activeUnits = [];
        if (activeNode is ProceduralNode procedural)
        {
            activeUnits = _coordinator.ActiveUnits(procedural.Id);
        }

        return new ProjectionInputs
        {
            CurrentNodeId = CurrentNodeId,
            IsComplete = IsComplete,
            Step = Step,
            Definition = Definition,
            ActiveNode = activeNode,
            ActiveNodeMaxVisits = MaxVisitsOf(activeNode),
            ActiveNodeOnMaxVisits = OnMaxVisitsOf(activeNode),
            Visits = _visits,
            Statuses = _coordinator.Statuses,
            NextActions = nextActions,
            ActiveUnits = activeUnits,
            ContextFactory = () => BuildContextNoLock(null, null, null),
            LastErrors = _coordinator.LastErrors,
            Unmatched = _coordinator.Unmatched,
            State = _state,
            Outputs = _outputs,
            Notes = _notes,
            Projection = projection,
        };
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
    internal WorkflowInstanceSnapshot Snapshot()
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
    ///     still holding a live <c>Agent</c> tool-call correlation but no recorded output — cannot be resumed,
    ///     because its sub-agent no longer exists after a restart. Such a task is reset to <c>pending</c> and
    ///     its tool-call correlation is dropped, so the resumed controller re-spawns it. An in-flight
    ///     background spawn (whose live agent id is not persisted) is reset purely on its <c>in_flight</c>
    ///     status. Its attempt budget is preserved so bounded validation retries survive the restart. A
    ///     <c>validated</c> or terminally <c>failed</c> task keeps its status and recorded output untouched.
    /// </remarks>
    public static WorkflowRuntime FromSnapshot(
        WorkflowInstanceSnapshot snapshot,
        IJsonSchemaValidator? validator = null,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var runtime = new WorkflowRuntime(validator, logger);
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
            RebuildNodeIndexNoLock();
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

            // The coordinator rebuilds its task maps (applying orphan reset per occurrence).
            _coordinator.Restore(snapshot.Tasks);
        }
    }

    /// <summary>Builds a snapshot of the current state. Caller MUST hold <see cref="_lock"/>.</summary>
    private WorkflowInstanceSnapshot BuildSnapshotNoLock(string instanceId) =>
        new()
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
            Tasks = _coordinator.BuildTaskSnapshots(),
        };

    /// <summary>Builds a snapshot for persistence, or <c>null</c> when no store is attached. Caller holds the lock.</summary>
    private WorkflowInstanceSnapshot? CaptureSnapshotNoLock() =>
        _store is not null && _instanceId is not null ? BuildSnapshotNoLock(_instanceId) : null;

    /// <summary>
    ///     Best-effort persistence of a captured snapshot (no-op when not attached). The captured snapshot is
    ///     handed to the <see cref="SnapshotPersister"/>, which serializes saves on a per-instance chain so a
    ///     stale save never overwrites a newer one. Called AFTER releasing <see cref="_lock"/>; use
    ///     <see cref="DrainPersistAsync"/> to flush pending saves before disposal.
    /// </summary>
    private void Persist(WorkflowInstanceSnapshot? snapshot)
    {
        if (snapshot is null || _store is null || _instanceId is null)
        {
            return;
        }

        _persister.Enqueue(_store, _instanceId, snapshot, _logger);
    }

    /// <summary>
    ///     Awaits the current per-instance save chain so a host can flush every pending best-effort save
    ///     before disposing the run. Best-effort saves never fault the chain, so this never throws; it is a
    ///     no-op (already-completed task) when no store is attached.
    /// </summary>
    internal Task DrainPersistAsync() => _persister.DrainAsync();

    /// <summary>
    ///     Builds a binding context over the live channels. Internal callers use it read-only under the lock
    ///     and may keep aliasing the live channels (<paramref name="clone"/> = <c>false</c>); the public
    ///     <see cref="BuildContext"/> passes <paramref name="clone"/> = <c>true</c> so its returned context is
    ///     isolated from later runtime mutation.
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
            // Internal (clone:false) callers read the context synchronously under _lock and never let it
            // escape, so the live _visits map can be aliased; the public (clone:true) BuildContext keeps the
            // defensive copy so its returned context is isolated from later runtime mutation.
            Visits = clone ? new Dictionary<string, int>(_visits, StringComparer.Ordinal) : _visits,
            Step = Step,
            Item = item,
            Index = index,
            Count = count,
        };

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

    private WorkflowNode? FindNodeNoLock(string id) => _nodeIndex.GetValueOrDefault(id);

    /// <summary>
    ///     Rebuilds the O(1) node-id lookup index from the current <see cref="Definition"/>. Cleared first so a
    ///     re-load/restore never retains stale nodes; <see cref="Dictionary{TKey,TValue}.TryAdd"/> preserves the
    ///     first-wins resolution of the previous <c>FirstOrDefault</c> lookup. Caller MUST hold <see cref="_lock"/>.
    /// </summary>
    private void RebuildNodeIndexNoLock()
    {
        _nodeIndex.Clear();
        if (Definition is null)
        {
            return;
        }

        foreach (var node in Definition.Nodes)
        {
            _ = _nodeIndex.TryAdd(node.Id, node);
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

}
