using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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

    // Per-tool-call reassembly buffer for STREAMING Agent-call args. ObserveMessage runs on the host's
    // out-of-band observer thread (not under _lock), and a real provider delivers the Agent call's args as
    // incremental fragments across many ToolCallUpdateMessages; this concurrent map reassembles them by
    // tool-call id until the spawn name is parseable. Dropped as soon as the args parse, capped for a stream
    // that never parses, and cleared on the tool result — so it cannot leak in a long-lived conversation.
    private const int MaxSpawnArgBufferChars = 256 * 1024;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _spawnArgBuffers =
        new(StringComparer.Ordinal);

    // ---- Route-away observation barrier -------------------------------------------------------------
    // A transition (AdvanceTo) runs INLINE on the controller loop's thread, but the sub-agent results that
    // feed it arrive only through the out-of-band observer, which lags the loop by however long the
    // subscriber channel takes to drain. Routing into a terminal renders its resultTemplate from the state
    // AS OF THAT MOMENT, so an unobserved result silently produced a terminal result missing the work the
    // controller had already seen complete. The barrier closes that window: because every message is
    // PUBLISHED before the loop executes it (MessagePublishingMiddleware wraps the provider stream) and the
    // observer consumes in publish order, waiting until the routing tool call's OWN message has been
    // observed proves every earlier message — including every prior tool result — is already correlated.
    // Keyed on the tool-call id rather than task status: "join satisfied"/"nothing in flight" predicates
    // all mis-handle the legitimate cases (a terminally failed unit routing to onFailure, a composed but
    // deliberately unspawned unit, an as-yet-unobserved spawn registration).
    private const int ObservedToolCallHistory = 256;
    private static readonly TimeSpan ObservationBarrierTimeout = TimeSpan.FromSeconds(10);

    // Guards ONLY the barrier bookkeeping. Deliberately NOT _lock: the observer thread signals waiters from
    // inside ObserveMessage while a loop thread may be inside AdvanceTo holding _lock, so the two must not
    // be nested. Waiter TCSs also use RunContinuationsAsynchronously so completing one never runs the
    // routing handler's continuation on the observer thread.
    private readonly object _observationLock = new();
    private readonly HashSet<string> _observedToolCallIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _observedToolCallOrder = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _observationWaiters =
        new(StringComparer.Ordinal);
    private bool _orderedObserverAttached;
    private bool _observationBarrierDisabled;

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

    // Transient (never persisted): a terminal whose result was composed from its resultTemplate rather than
    // an explicit result. The eager render in AdvanceTo can run on the controller-pump task BEFORE the drive
    // task applies the last observed sub-agent write, so the result is re-rendered from final state when the
    // drive signals completion (see SignalCompletion). Null once completion has re-rendered it, or when the
    // terminal supplied an explicit result / declared no template.
    private TerminalNode? _pendingTemplateTerminal;

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
    ///     Internal: a runtime is only constructed inside the library (via <see cref="WorkflowSession"/> /
    ///     <see cref="WorkflowManager"/> / <see cref="FromSnapshot"/> / <see cref="CreateNew"/>) so
    ///     construction always goes through one of those blessed, public entry points.
    /// </summary>
    internal WorkflowRuntime(IJsonSchemaValidator? schemaValidator = null, ILogger? logger = null)
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

    /// <summary>
    ///     Splices <paramref name="node"/> into the graph: if <paramref name="previousNodeId"/> is given, it is
    ///     appended to that node's own outgoing edge(s); if <paramref name="nextNodeId"/> is given, it is
    ///     appended to <paramref name="node"/>'s own outgoing edge(s). At least one of the two is required — an
    ///     unreachable node is never useful. The candidate definition is validated (see <see cref="LoadDefinition"/>)
    ///     before it is committed; on failure <see cref="Definition"/> is left untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     No definition is loaded; <paramref name="node"/>'s id already exists; both
    ///     <paramref name="previousNodeId"/> and <paramref name="nextNodeId"/> are omitted; either id does not
    ///     resolve to an existing node; or splicing targets a node type whose outgoing edges aren't a plain
    ///     "next" list (conditional/terminal) — use <c>SetWorkflow</c> to edit those directly instead.
    /// </exception>
    /// <exception cref="WorkflowValidationException">The resulting definition failed validation.</exception>
    public void AddNode(WorkflowNode node, string? previousNodeId, string? nextNodeId)
    {
        ArgumentNullException.ThrowIfNull(node);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (Definition is null)
            {
                throw new InvalidOperationException("No workflow definition is loaded.");
            }

            if (FindNodeNoLock(node.Id) is not null)
            {
                throw new InvalidOperationException($"Node id '{node.Id}' already exists.");
            }

            if (string.IsNullOrEmpty(previousNodeId) && string.IsNullOrEmpty(nextNodeId))
            {
                throw new InvalidOperationException(
                    "At least one of 'previousNodeId' or 'nextNodeId' is required so the new node is reachable."
                );
            }

            WorkflowNode? mutatedPrevious = null;
            if (!string.IsNullOrEmpty(previousNodeId))
            {
                var previous =
                    FindNodeNoLock(previousNodeId)
                    ?? throw new InvalidOperationException($"Node '{previousNodeId}' does not exist.");

                mutatedPrevious = previous switch
                {
                    StartNode start => start with { Next = [.. start.Next, node.Id] },
                    ProceduralNode procedural => procedural with { Next = [.. procedural.Next, node.Id] },
                    _ => throw new InvalidOperationException(
                        $"Node '{previousNodeId}' is a {previous.Type} node; splicing via 'previousNodeId' is "
                            + "only supported for start/procedural nodes. Use SetWorkflow to edit its "
                            + "branches/else directly."
                    ),
                };
            }

            var newNode = node;
            if (!string.IsNullOrEmpty(nextNodeId))
            {
                if (FindNodeNoLock(nextNodeId) is null)
                {
                    throw new InvalidOperationException($"Node '{nextNodeId}' does not exist.");
                }

                newNode = node switch
                {
                    StartNode start => start.Next.Contains(nextNodeId)
                        ? start
                        : start with { Next = [.. start.Next, nextNodeId] },
                    ProceduralNode procedural => procedural.Next.Contains(nextNodeId)
                        ? procedural
                        : procedural with { Next = [.. procedural.Next, nextNodeId] },
                    _ => throw new InvalidOperationException(
                        $"'{node.Id}' is a {node.Type} node; 'nextNodeId' auto-wiring is only supported for "
                            + "start/procedural nodes. Fully specify branches/else (or omit nextNodeId for a "
                            + "terminal) on the node payload."
                    ),
                };
            }

            var candidateNodes = Definition
                .Nodes.Select(n => mutatedPrevious is not null && n.Id == mutatedPrevious.Id ? mutatedPrevious : n)
                .Append(newNode)
                .ToList();
            var candidate = Definition with { Nodes = candidateNodes };
            _validator.ValidateAndThrow(candidate);

            Definition = candidate;
            RebuildNodeIndexNoLock();
            _outputs[newNode.Id] = new JsonObject();

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     "Removes" the node <paramref name="nodeId"/> by neutering it into a no-op pass-through IN PLACE
    ///     rather than deleting it from the graph. The node keeps its id and every INBOUND edge, so removal
    ///     itself can never leave a dangling edge or orphan a predecessor. Only the node's own work is
    ///     stripped: a <see cref="ProceduralNode"/> loses its task list and its failure/visit-cap edges (a
    ///     no-op can neither fail nor loop, so its loop cycle becomes 0) and simply advances along its
    ///     existing <c>next</c>; a <see cref="ConditionalNode"/> "defaults to true", collapsing to a
    ///     procedural pass-through whose single <c>next</c> is its FIRST branch's target (its <c>else</c>
    ///     fallback when it declares no branch), dropping the other branches. The candidate definition is
    ///     validated (see <see cref="LoadDefinition"/>) before it is committed; on failure
    ///     <see cref="Definition"/> is left untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     No definition is loaded; <paramref name="nodeId"/> does not resolve to an existing node; it is the
    ///     node the controller is currently positioned on; or it is the start node (the entry point — re-root
    ///     via <c>SetWorkflow</c>) or a terminal node (no successor to pass through to — edit via
    ///     <c>SetWorkflow</c>).
    /// </exception>
    /// <exception cref="WorkflowValidationException">
    ///     Neutering the node produced an invalid definition — e.g. dropping a procedural node's
    ///     <c>onFailure</c> edge, or collapsing a conditional to its first branch, orphaned a node that was
    ///     reachable only through the dropped edge.
    /// </exception>
    public void RemoveNode(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        WorkflowInstanceSnapshot? snapshot;
        lock (_lock)
        {
            if (Definition is null)
            {
                throw new InvalidOperationException("No workflow definition is loaded.");
            }

            var node =
                FindNodeNoLock(nodeId)
                ?? throw new InvalidOperationException($"Node '{nodeId}' does not exist.");

            if (nodeId == CurrentNodeId)
            {
                throw new InvalidOperationException(
                    $"Cannot remove node '{nodeId}': the workflow is currently positioned on it."
                );
            }

            var neutered = NeuterNode(node);

            var candidateNodes = Definition.Nodes.Select(n => n.Id == nodeId ? neutered : n).ToList();
            var candidate = Definition with { Nodes = candidateNodes };
            _validator.ValidateAndThrow(candidate);

            Definition = candidate;
            RebuildNodeIndexNoLock();
            // The node persists (its id + inbound edges are unchanged); it just no longer produces output.
            _outputs[nodeId] = new JsonObject();

            snapshot = CaptureSnapshotNoLock();
        }

        Persist(snapshot);
    }

    /// <summary>
    ///     Builds the no-op replacement for <paramref name="node"/>: a procedural node keeps its id/title and
    ///     <c>next</c> but loses its task list and failure/visit-cap edges; a conditional collapses to a
    ///     procedural whose single <c>next</c> is its first branch's target (its "defaults to true" path).
    ///     Start and terminal nodes have no meaningful no-op form and are rejected.
    /// </summary>
    private static WorkflowNode NeuterNode(WorkflowNode node) =>
        node switch
        {
            ProceduralNode procedural => procedural with
            {
                TaskList = [],
                OnFailure = null,
                MaxVisits = null,
                OnMaxVisits = null,
                MaxParallel = null,
            },
            ConditionalNode conditional => new ProceduralNode
            {
                Id = conditional.Id,
                Title = conditional.Title,
                ControllerInstructions = conditional.ControllerInstructions,
                Next = [FirstConditionalTarget(conditional)],
                TaskList = [],
            },
            StartNode => throw new InvalidOperationException(
                $"Cannot remove the start node '{node.Id}': it is the workflow entry point. "
                    + "Re-root the workflow via SetWorkflow instead."
            ),
            TerminalNode => throw new InvalidOperationException(
                $"Cannot remove the terminal node '{node.Id}': it has no successor to pass through to. "
                    + "Edit the graph via SetWorkflow instead."
            ),
            _ => throw new InvalidOperationException(
                $"Cannot remove node '{node.Id}' of unsupported type {node.Type}."
            ),
        };

    /// <summary>
    ///     The target a no-op'd conditional collapses to: its first branch with a resolvable target,
    ///     otherwise its <c>else</c> fallback (guaranteed non-empty for a valid conditional).
    /// </summary>
    private static string FirstConditionalTarget(ConditionalNode conditional)
    {
        var firstBranch = conditional
            .Branches?.FirstOrDefault(b => !string.IsNullOrEmpty(b.To))
            ?.To;
        return !string.IsNullOrEmpty(firstBranch) ? firstBranch : conditional.Else;
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

    /// <summary>
    ///     Self-correcting spawn-name gate for the controller loop (Option A). Given the <c>name</c> argument
    ///     of an <c>Agent</c> spawn, returns <c>null</c> when the name matches a unit the runtime can correlate
    ///     (so the spawn proceeds), or an actionable corrective message when it does not — so the
    ///     <c>Agent</c>-tool boundary can reject the mis-named spawn as a recoverable tool error instead of
    ///     letting it run and be silently discarded (its result never correlates, the unit stays pending, and
    ///     the controller re-spawns the same wrong name in a loop). The active node's authored units are
    ///     composed on demand (idempotent, exactly as <see cref="RegisterSpawn"/> does) so a FIRST spawn issued
    ///     before any <c>GetWorkflow</c> poll is judged against the real unit set rather than rejected
    ///     spuriously. An already-settled unit is still "expected" (its name stays known), so a harmless
    ///     re-issue passes through — <see cref="RegisterSpawn"/> no-ops on a settled unit.
    /// </summary>
    internal string? DescribeSpawnNameRejection(string? name)
    {
        lock (_lock)
        {
            // Compose the active node's units on demand so the gate never depends on the controller having
            // polled GetWorkflow first, and so a not-yet-polled unit is registered in the coordinator's
            // name map exactly as RegisterSpawn would (Compose is idempotent).
            _ = _coordinator.Compose();

            // Allow FIRST when the name matches a known unit — Pending, in-flight, or already-settled all
            // correlate (this mirrors RegisterSpawn, which correlates by name regardless of lifecycle status).
            // This MUST precede the "no composable units" steer below: the run observer marks a unit in-flight
            // BEFORE the tool-boundary gate runs for that same spawn, so a legitimate unit is frequently already
            // non-pending here — and Compose() returns only PENDING units, so relying on its count would make a
            // valid, already-in-flight spawn look unroutable and reject it (its result would then be recorded as
            // a failure and its state write dropped).
            if (!string.IsNullOrWhiteSpace(name) && _coordinator.IsExpectedUnit(name))
            {
                return null;
            }

            // The name is not a known unit. List the active node's authored units for the current visit (any
            // lifecycle status, via ActiveUnits — NOT Compose()'s pending-only view) so the controller can
            // re-issue the exact name. A node that composes no units at all (start/terminal/conditional, or a
            // non-authored procedural node) is routed by the controller, not spawned into — steer it there.
            var activeNames =
                CurrentNodeId is { } nodeId
                    ? _coordinator.ActiveUnits(nodeId).Select(u => u.Name).ToList()
                    : [];
            if (activeNames.Count == 0)
            {
                return "The current workflow node has no sub-agent units to spawn. Advance the workflow with "
                    + "SetCurrentNode (route to the next node) instead of calling Agent.";
            }

            var expected = string.Join(", ", activeNames);
            var got = string.IsNullOrWhiteSpace(name) ? "(no name)" : $"'{name}'";
            return $"No workflow unit named {got} for the current node. Re-call Agent with the exact unit name "
                + "— copy the nextExpectedAction[].name value character-for-character (do NOT build it from the "
                + $"node id). The unit name(s) for the current node are: {expected}.";
        }
    }

    /// <summary>
    ///     Returns the workflow unit's authoritative model selection for an exact spawn name. A returned
    ///     record may intentionally contain two nulls: that means the authored unit omitted a per-spawn
    ///     choice and the delegate must keep its template/default model rather than accepting placeholder
    ///     optional values generated by the controller LLM. Null return means the name is not a workflow unit.
    /// </summary>
    internal SubAgentSpawnModelSelection? ResolveSpawnModelSelection(string? name)
    {
        lock (_lock)
        {
            _ = _coordinator.Compose();
            if (string.IsNullOrWhiteSpace(name) || CurrentNodeId is null)
            {
                return null;
            }

            var unit = _coordinator.FindComposedUnit(name);
            return unit is null
                ? null
                : new SubAgentSpawnModelSelection(Model: null, unit.ModelIntelligence);
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
    ///     Thread-safe, side-effect-free wrapper over <see cref="TaskCoordinator.CheckSpawnResult"/>: reports
    ///     whether a correlated spawn's result carries an output schema and already satisfies it (and, when it
    ///     does not, the schema to repair against). Takes the runtime lock because the controller loop can be
    ///     mutating task correlation concurrently, but records NOTHING — the drive pump calls it to gate the
    ///     best-effort JSON-repair pass without touching durable/projected state.
    /// </summary>
    internal SpawnSchemaCheck CheckSpawnResult(string toolCallId, string resultText)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        lock (_lock)
        {
            return _coordinator.CheckSpawnResult(toolCallId, resultText);
        }
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
    ///     Correlates the stream events that matter to a run observer: an <c>Agent</c> tool call (registers
    ///     the spawn by the runtime-surfaced unit name); its tool result (a blocking answer is
    ///     validated/recorded, a background spawn receipt is failed fast — unsupported in V1); and an injected
    ///     <c>&lt;sub-agent&gt;</c> user message (dormant / forward-built). Every other message is ignored.
    ///     This is the single entry point a host wires a <c>MultiTurnAgentLoop</c>'s message stream
    ///     into to drive workflow correlation from outside a dedicated <c>WorkflowSession</c>.
    /// </summary>
    public void ObserveMessage(IMessage message)
    {
        switch (message)
        {
            // A finalized Agent tool call (what ExecuteRunAsync / a mocked agent yields as one message).
            case ToolCallMessage { FunctionName: "Agent", ToolCallId: { } toolCallId } call:
                RegisterSpawnByName(toolCallId, call.FunctionArgs);
                break;

            // A streaming Agent tool-call update. A REAL provider publishes the Agent call to subscribers
            // ONLY as these updates (the finalized ToolCallMessage is added to loop history but NOT published
            // to the subscriber stream, so the sample's out-of-band observer never sees it). Critically, each
            // update carries an INCREMENTAL args FRAGMENT, not the full accumulated args — verified live: the
            // fragments arrive as {"subagent_t / ype": "gen / eral-pu / ... / "name": "work:1:task1"}. So the
            // spawn name only becomes readable after the fragments are reassembled; accumulate per toolCallId
            // and retry the parse on each fragment.
            case ToolCallUpdateMessage { FunctionName: "Agent", ToolCallId: { } updateId } update:
                AccumulateAndRegisterSpawn(updateId, update.FunctionArgs);
                break;

            case ToolCallResultMessage { ToolCallId: { } resultId } result:
                // Any tool result SETTLES its call: drop the streamed-args buffer unconditionally, whether or
                // not the spawn correlated. Non-workflow Agent calls, malformed streams, and names that never
                // matched a workflow unit must not retain fragments for the life of a long conversation.
                _ = _spawnArgBuffers.TryRemove(resultId, out _);
                if (IsRegisteredSpawn(resultId))
                {
                    ObserveSpawnResult(resultId, result.Result, result.IsError);
                }

                break;

            // Injected sub-agent completion. As of #171, MultiTurnAgentLoop DOES publish injected
            // NotifyMessages (including sub-agent completions) to its subscribers, so — unlike before —
            // this case is reachable if a host wires the loop's message stream into ObserveMessage. It
            // stays safe in V1 because the background receipt path fails fast (ObserveSpawnResult) and
            // never records an agent_id correlation, so ObserveInjectedResult finds no mapping and surfaces
            // the result as a harmless "unmatched"; full correlation is re-enabled in the follow-up.
            // The completion arrives as a NotifyMessage whose Detail carries the raw <sub-agent …> block
            // (its envelope Text wraps that block), so parse Detail rather than GetText().
            case NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion, Detail: { } detail }:
                if (SubAgentResultParser.TryParse(detail, out var agentId, out var payload, out var isError))
                {
                    ObserveInjectedResult(agentId, payload, isError);
                }

                break;

            default:
                break;
        }

        // Watermark LAST, so a waiter is only released once this message has been fully correlated. Both
        // shapes are recorded because which one reaches the subscriber stream depends on the provider: a
        // mocked agent yields the finalized ToolCallMessage, a real provider publishes only the streaming
        // update fragments (see the Agent cases above).
        switch (message)
        {
            case ToolCallMessage { ToolCallId: { } callId }:
                MarkObserved(callId);
                break;

            case ToolCallUpdateMessage { ToolCallId: { } updateWatermark }:
                MarkObserved(updateWatermark);
                break;

            default:
                break;
        }
    }

    /// <summary>
    ///     Declares that a single ordered consumer feeds <b>every</b> published message of the controller run
    ///     into <see cref="ObserveMessage"/>, in publish order. Only then can a transition wait for the
    ///     observer to catch up (see <see cref="WaitForObservationAsync"/>); without it the barrier is a no-op
    ///     so a host driving the runtime directly — with no observer at all — never stalls.
    /// </summary>
    internal void AttachOrderedObserver()
    {
        lock (_observationLock)
        {
            _orderedObserverAttached = true;
        }
    }

    /// <summary>
    ///     Waits until the observer has processed the message carrying <paramref name="toolCallId"/> — the
    ///     routing tool call's own message, published before the loop invoked its handler. Returns
    ///     immediately when no ordered observer is attached, when the id is unknown, or when the message has
    ///     already been observed. If the watermark never arrives within
    ///     <see cref="ObservationBarrierTimeout"/> the barrier logs once and DISABLES itself for the rest of
    ///     the run, degrading to the previous (racy but non-blocking) behaviour rather than stalling every
    ///     subsequent transition.
    /// </summary>
    internal async Task WaitForObservationAsync(string? toolCallId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(toolCallId))
        {
            return;
        }

        TaskCompletionSource<bool> waiter;
        lock (_observationLock)
        {
            if (
                !_orderedObserverAttached
                || _observationBarrierDisabled
                || _observedToolCallIds.Contains(toolCallId)
            )
            {
                return;
            }

            if (!_observationWaiters.TryGetValue(toolCallId, out waiter!))
            {
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _observationWaiters[toolCallId] = waiter;
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(ObservationBarrierTimeout, timeoutCts.Token);
        var winner = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);

        // Always cancel the timer: when the watermark won, leaving it armed would keep a timer per transition
        // alive for the full timeout.
        timeoutCts.Cancel();

        if (winner == waiter.Task)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();

        lock (_observationLock)
        {
            _ = _observationWaiters.Remove(toolCallId);
            _observationBarrierDisabled = true;
        }

        _logger?.LogWarning(
            "Workflow transition for tool call {ToolCallId} timed out after {TimeoutSeconds}s waiting for the "
                + "run observer to reach it; the observation barrier is disabled for the rest of this run and "
                + "a transition may now render from state that lags the controller.",
            toolCallId,
            ObservationBarrierTimeout.TotalSeconds
        );
    }

    /// <summary>
    ///     Records that the observer has fully processed the message carrying <paramref name="toolCallId"/>
    ///     and releases any transition waiting on it. The observed-id history is capped: the gap between a
    ///     message being published and its handler awaiting the watermark is a handful of messages, so a
    ///     bounded window is sufficient and cannot leak across a long-lived controller conversation.
    /// </summary>
    private void MarkObserved(string toolCallId)
    {
        if (string.IsNullOrEmpty(toolCallId))
        {
            return;
        }

        TaskCompletionSource<bool>? waiter = null;
        lock (_observationLock)
        {
            if (_observedToolCallIds.Add(toolCallId))
            {
                _observedToolCallOrder.Enqueue(toolCallId);
                while (_observedToolCallOrder.Count > ObservedToolCallHistory)
                {
                    _ = _observedToolCallIds.Remove(_observedToolCallOrder.Dequeue());
                }
            }

            if (_observationWaiters.Remove(toolCallId, out var pending))
            {
                waiter = pending;
            }
        }

        // Outside the lock: even with RunContinuationsAsynchronously, never complete a waiter while holding it.
        _ = waiter?.TrySetResult(true);
    }

    /// <summary>
    ///     Reads the spawn <c>name</c> from a FINALIZED Agent tool call's complete args and registers the
    ///     correlation. No-ops when the args do not name a known unit.
    /// </summary>
    private void RegisterSpawnByName(string toolCallId, string? functionArgs)
    {
        var name = TryReadSpawnName(functionArgs);
        if (name is not null)
        {
            RegisterSpawn(toolCallId, name);
        }
    }

    /// <summary>
    ///     Appends a streaming Agent-call args fragment to this tool call's buffer and, once the accumulated
    ///     buffer parses as JSON carrying a <c>name</c>, registers the spawn. Fragments arrive as raw byte
    ///     slices of the args JSON (e.g. <c>{"subagent_t</c> then <c>ype": "gen</c> …), so no single fragment
    ///     is parseable — only the reassembled buffer is. Idempotent: <see cref="RegisterSpawn"/> keys on
    ///     tool-call id, so repeated parses after the name first appears are harmless.
    /// </summary>
    /// <remarks>
    ///     Buffer lifecycle is bounded so a long-lived Workspace Agent conversation cannot leak: the buffer is
    ///     dropped the moment the args become parseable (the name is then known — later fragments are noise),
    ///     is capped at <see cref="MaxSpawnArgBufferChars"/> for a stream that never parses, and is cleared on
    ///     the tool result (see <see cref="ObserveMessage"/>).
    /// </remarks>
    private void AccumulateAndRegisterSpawn(string toolCallId, string? fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            return;
        }

        var buffer = _spawnArgBuffers.AddOrUpdate(
            toolCallId,
            fragment,
            (_, existing) => existing + fragment
        );

        if (TryReadSpawnName(buffer) is { } name)
        {
            RegisterSpawn(toolCallId, name);
            // The args are complete enough to parse — the name is known and further fragments are noise.
            // Drop the buffer now (before the sub-agent even finishes) rather than waiting for the result.
            _ = _spawnArgBuffers.TryRemove(toolCallId, out _);
            return;
        }

        // A well-formed Agent call yields a parseable name well within the cap; a stream that blows past it
        // without ever parsing is malformed or not a workflow spawn — drop it so it cannot grow unbounded.
        if (buffer.Length > MaxSpawnArgBufferChars)
        {
            _ = _spawnArgBuffers.TryRemove(toolCallId, out _);
        }
    }

    private static string? TryReadSpawnName(string? functionArgs)
    {
        if (string.IsNullOrEmpty(functionArgs))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(functionArgs);
            return doc.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

                // The eager render above may have run against state that is not yet final (the last observed
                // sub-agent write can be applied on the drive task AFTER the controller pump reaches here).
                // Remember a template-composed terminal so SignalCompletion re-renders it from the drained
                // final state; an explicit result needs no refresh.
                _pendingTemplateTerminal =
                    result is null && terminal.ResultTemplate is not null ? terminal : null;

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

    /// <summary>
    ///     Signals normal completion of the controller run to <see cref="Completion"/> waiters. When the
    ///     workflow completed into a terminal whose result was composed from a <c>resultTemplate</c>, the
    ///     result is re-rendered from the now-final state first: the drive calls this only after the whole
    ///     message stream has drained (every observed sub-agent write applied), so this captures the complete
    ///     result rather than the mid-flight snapshot the eager render in <see cref="AdvanceTo"/> may have
    ///     frozen when the pump reached the terminal ahead of the drive. A re-render that fails its final
    ///     output schema faults <see cref="Completion"/> instead of completing it.
    /// </summary>
    internal void SignalCompletion()
    {
        WorkflowInstanceSnapshot? snapshot = null;
        Exception? renderFailure = null;
        lock (_lock)
        {
            if (_pendingTemplateTerminal is { ResultTemplate: { } template } terminal)
            {
                _pendingTemplateTerminal = null;
                try
                {
                    var composed = TemplateNodeRenderer.Render(
                        template,
                        BuildContextNoLock(null, null, null)
                    );
                    if (composed is not null)
                    {
                        var schema = terminal.FinalOutputSchema ?? Definition?.FinalOutputSchema;
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

                        _result = composed.DeepClone();
                        snapshot = CaptureSnapshotNoLock();
                    }
                }
                catch (Exception ex)
                {
                    renderFailure = ex;
                }
            }
        }

        // Persist the refreshed result AFTER releasing the lock (mirrors AdvanceTo); the persister serializes
        // saves per instance, so this later save supersedes AdvanceTo's eager-result save for resume.
        if (snapshot is not null)
        {
            Persist(snapshot);
        }

        if (renderFailure is not null)
        {
            _ = _completion.TrySetException(renderFailure);
            return;
        }

        _ = _completion.TrySetResult();
    }

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

    /// <summary>
    ///     Creates a fresh, empty runtime for a host that wants a chat agent to author and drive a workflow
    ///     directly — via <see cref="Tools.WorkflowToolProvider"/> registered on the SAME conversation loop as
    ///     the agent, rather than inside an isolated <see cref="WorkflowSession"/> controller loop. This is a
    ///     blessed public entry point alongside <see cref="FromSnapshot"/>.
    /// </summary>
    public static WorkflowRuntime CreateNew(IJsonSchemaValidator? schemaValidator = null, ILogger? logger = null) =>
        new(schemaValidator, logger);

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
