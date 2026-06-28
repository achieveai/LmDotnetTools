using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

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

    /// <summary>Creates a runtime. A custom <paramref name="schemaValidator"/> may be injected for tests.</summary>
    public WorkflowRuntime(IJsonSchemaValidator? schemaValidator = null)
    {
        _schemaValidator = schemaValidator ?? new JsonSchemaValidator();
    }

    /// <summary>The loaded definition, or <c>null</c> before <see cref="LoadDefinition"/>.</summary>
    public WorkflowDefinition? Definition { get; private set; }

    /// <summary>The id of the node the controller is currently positioned on.</summary>
    public string? CurrentNodeId { get; private set; }

    /// <summary>Whether the workflow has advanced into a terminal node.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>The validated final result captured when the workflow completed, or <c>null</c>.</summary>
    public JsonNode? Result { get; private set; }

    /// <summary>The global controller step counter, incremented on every transition.</summary>
    public int Step { get; private set; }

    /// <summary>The workflow inputs channel (<c>inputs.&lt;...&gt;</c>).</summary>
    public JsonObject Inputs { get; private set; } = [];

    /// <summary>The mutable state channel (<c>state.&lt;...&gt;</c>).</summary>
    public JsonObject State { get; private set; } = [];

    /// <summary>The per-node task outputs channel (<c>{ nodeId: { taskId: value } }</c>).</summary>
    public JsonObject Outputs { get; private set; } = [];

    /// <summary>The scoped notes channel (<c>{ scope: { key: value } }</c>).</summary>
    public JsonObject Notes { get; private set; } = [];

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

        lock (_lock)
        {
            Definition = def;
            Inputs = CloneObject(def.Inputs);
            State = CloneObject(def.State);
            Outputs = [];
            Notes = [];
            foreach (var node in def.Nodes)
            {
                Outputs[node.Id] = new JsonObject();
            }

            var start = def.Nodes.OfType<StartNode>().Single();
            CurrentNodeId = start.Id;
            _visits.Clear();
            _visits[start.Id] = 1;
            Step = 0;
            IsComplete = false;
            Result = null;

            _tasksByName.Clear();
            _tasksByToolCallId.Clear();
            _tasksByAgentId.Clear();
            _status.Clear();
            _composed.Clear();
            _attempts.Clear();
            _lastError.Clear();
            _unmatched.Clear();
        }
    }

    /// <summary>Shallow-merges <paramref name="inputs"/> into the inputs channel (caller-supplied seed).</summary>
    public void MergeInputs(JsonObject inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (_lock)
        {
            foreach (var (key, value) in inputs)
            {
                Inputs[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    ///     Builds a <see cref="BindingContext"/> over the current channels (plus optional loop locals) for
    ///     template rendering and condition evaluation.
    /// </summary>
    public BindingContext BuildContext(JsonNode? item = null, int? index = null, int? count = null)
    {
        lock (_lock)
        {
            return BuildContextNoLock(item, index, count);
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

        lock (_lock)
        {
            if (!_tasksByToolCallId.TryGetValue(toolCallId, out var taskRef))
            {
                _unmatched.Add(toolCallId);
                return;
            }

            ObserveAnswerNoLock(taskRef, resultText, isError);
        }
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

        lock (_lock)
        {
            if (!_tasksByToolCallId.TryGetValue(toolCallId, out var taskRef))
            {
                _unmatched.Add(toolCallId);
                return;
            }

            if (isError)
            {
                HandleFailureNoLock(taskRef, $"sub-agent returned an error: {resultText}");
                return;
            }

            if (TryReadSpawnReceipt(resultText, out var agentId))
            {
                // Background spawn: correlate by the receipt agent_id and wait for the injected result.
                _tasksByAgentId[agentId] = taskRef;
                return;
            }

            ValidateAndRecordNoLock(taskRef, resultText);
        }
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

        lock (_lock)
        {
            if (!_tasksByAgentId.TryGetValue(agentId, out var taskRef))
            {
                _unmatched.Add(agentId);
                return;
            }

            ObserveAnswerNoLock(taskRef, resultText, isError);
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
                    Result = capturedResult;
                }

                IsComplete = true;
            }
        }
    }

    /// <summary>Writes <paramref name="value"/> into the state channel at <paramref name="path"/> using the given mode.</summary>
    public void SetState(string path, JsonNode? value, string? mode, string? key)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_lock)
        {
            var spec = new WriteSpec
            {
                To = path,
                Mode = ParseWriteMode(mode),
                Key = key,
            };
            StateWriter.Apply(State, spec, value);
        }
    }

    /// <summary>Sets a scoped note (<c>notes[scope][key] = value</c>).</summary>
    public void SetNotes(string scope, string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (_lock)
        {
            if (Notes[scope] is not JsonObject scopeObject)
            {
                scopeObject = [];
                Notes[scope] = scopeObject;
            }

            scopeObject[key] = JsonValue.Create(value);
        }
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
                result["state"] = State.DeepClone();
            }

            if (IncludesChannel(projection, "outputs"))
            {
                result["outputs"] = Outputs.DeepClone();
            }

            if (IncludesChannel(projection, "notes"))
            {
                result["notes"] = Notes.DeepClone();
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

    private BindingContext BuildContextNoLock(JsonNode? item, int? index, int? count) =>
        new()
        {
            Inputs = Inputs,
            State = State,
            Outputs = Outputs,
            Notes = Notes,
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
            StateWriter.Apply(State, writes, parsed);
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
        if (Outputs[taskRef.NodeId] is not JsonObject nodeOutputs)
        {
            nodeOutputs = [];
            Outputs[taskRef.NodeId] = nodeOutputs;
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
        var satisfied = mode == JoinMode.Any ? validated >= 1 : total > 0 && validated == total;

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
