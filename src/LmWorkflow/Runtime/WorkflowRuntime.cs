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
///     This is the minimal V1 surface: a linear <c>start → procedural(single authored agent task) →
///     terminal</c> path with a single blocking spawn. forEach fan-out, background spawns, joins, loops,
///     and validation re-prompts are intentionally out of scope (P4) but the shape (per-task status,
///     correlation maps, error markers) is built so P4 can extend it.
/// </remarks>
public sealed class WorkflowRuntime
{
    private readonly object _lock = new();
    private readonly WorkflowValidator _validator = new();
    private readonly IJsonSchemaValidator _schemaValidator;
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // Correlation: the surfaced unit name -> its task ref (populated by ComposeNextExpectedAction), and
    // the observed Agent tool_call_id -> the same task ref (populated by RegisterSpawn).
    private readonly Dictionary<string, TaskRef> _tasksByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskRef> _tasksByToolCallId = new(StringComparer.Ordinal);

    // Per-task status and the last composed spawn unit, both keyed by the unit name.
    private readonly Dictionary<string, WorkflowTaskStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpawnUnit> _composed = new(StringComparer.Ordinal);

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
            _status.Clear();
            _composed.Clear();
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
    ///     Observes the result of a correlated spawn: an error result, non-JSON, or a schema-invalid result
    ///     records an <c>{ "_error": ... }</c> marker into outputs and marks the task failed; a valid result
    ///     is recorded into <c>outputs[nodeId][taskId]</c>, its writes applied to state, and the task marked
    ///     validated. An unknown <paramref name="toolCallId"/> is surfaced as "unmatched" rather than dropped.
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

            if (isError)
            {
                MarkFailedNoLock(taskRef, $"sub-agent returned an error: {resultText}");
                return;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(resultText);
            }
            catch (JsonException ex)
            {
                MarkFailedNoLock(taskRef, $"task output is not valid JSON: {ex.Message}");
                return;
            }

            if (parsed is null)
            {
                MarkFailedNoLock(taskRef, "task output parsed to a null JSON value.");
                return;
            }

            if (taskRef.OutputSchema is { } schema)
            {
                var validation = _schemaValidator.ValidateDetailed(resultText, schema.ToJsonString());
                if (!validation.IsValid)
                {
                    MarkFailedNoLock(
                        taskRef,
                        "task output failed schema validation: " + string.Join("; ", validation.Errors)
                    );
                    return;
                }
            }

            RecordOutputNoLock(taskRef, parsed);
            if (taskRef.Writes is { } writes)
            {
                StateWriter.Apply(State, writes, parsed);
            }

            _status[taskRef.Name] = WorkflowTaskStatus.Validated;
        }
    }

    /// <summary>
    ///     Advances the controller from the current node to <paramref name="nextNodeId"/>, which must be a
    ///     declared edge target. Increments the next node's visit count and the step counter. When the next
    ///     node is terminal and a <paramref name="result"/> is supplied, the result is validated against the
    ///     terminal's (or the definition's) final output schema, captured, and the workflow is completed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     No definition is loaded, the transition is not a declared edge, or the terminal result fails its
    ///     final output schema.
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

            if (!IsDeclaredEdgeNoLock(CurrentNodeId, nextNodeId))
            {
                throw new InvalidOperationException(
                    $"Undeclared transition: '{CurrentNodeId}' has no edge to '{nextNodeId}'."
                );
            }

            // Validate any terminal result BEFORE mutating, so a schema failure leaves the runtime
            // untouched (no half-advanced node/visit/step state to recover from).
            var terminal = FindNodeNoLock(nextNodeId) as TerminalNode;
            JsonNode? capturedResult = null;
            if (terminal is not null && result is not null)
            {
                var schema = terminal.FinalOutputSchema ?? Definition.FinalOutputSchema;
                if (schema is not null)
                {
                    var validation = _schemaValidator.ValidateDetailed(
                        result.ToJsonString(),
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

                capturedResult = result.DeepClone();
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
            || node.TaskList is not { Count: 1 } tasks
        )
        {
            return [];
        }

        var task = tasks[0];
        if (
            task.Delegate != DelegateKind.Agent
            || string.IsNullOrEmpty(task.SubagentType)
            || !string.IsNullOrEmpty(task.ForEach)
        )
        {
            return [];
        }

        var visit = _visits.TryGetValue(node.Id, out var count) ? count : 1;
        var name = $"{node.Id}:{visit}:{task.Id}";
        var prompt = ComposePrompt(task, BuildContextNoLock(null, null, null));
        var unit = new SpawnUnit
        {
            Name = name,
            SubagentType = task.SubagentType,
            Prompt = prompt,
            OutputSchema = task.OutputSchema?.DeepClone(),
        };

        _tasksByName[name] = new TaskRef
        {
            NodeId = node.Id,
            Visit = visit,
            TaskId = task.Id,
            Index = null,
            OutputSchema = task.OutputSchema,
            Writes = task.Writes,
        };
        _ = _status.TryAdd(name, WorkflowTaskStatus.Pending);
        _composed[name] = unit;

        return [unit];
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

    private void RecordOutputNoLock(TaskRef taskRef, JsonNode value)
    {
        if (Outputs[taskRef.NodeId] is not JsonObject nodeOutputs)
        {
            nodeOutputs = [];
            Outputs[taskRef.NodeId] = nodeOutputs;
        }

        nodeOutputs[taskRef.TaskId] = value.DeepClone();
    }

    private void MarkFailedNoLock(TaskRef taskRef, string error)
    {
        if (Outputs[taskRef.NodeId] is not JsonObject nodeOutputs)
        {
            nodeOutputs = [];
            Outputs[taskRef.NodeId] = nodeOutputs;
        }

        nodeOutputs[taskRef.TaskId] = new JsonObject { ["_error"] = error };
        _status[taskRef.Name] = WorkflowTaskStatus.Failed;
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
    ///     optional forEach index (always <c>null</c> in the V1-minimal slice), and the schema/writes the
    ///     observed result is validated against and applied through.
    /// </summary>
    private sealed record TaskRef
    {
        public required string NodeId { get; init; }
        public required int Visit { get; init; }
        public required string TaskId { get; init; }
        public int? Index { get; init; }
        public JsonNode? OutputSchema { get; init; }
        public WriteSpec? Writes { get; init; }

        public string Name =>
            Index is { } index ? $"{NodeId}:{Visit}:{TaskId}:{index}" : $"{NodeId}:{Visit}:{TaskId}";
    }
}
