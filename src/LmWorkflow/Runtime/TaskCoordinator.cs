using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>
///     Owns the authored-task lifecycle: composing the next-expected-action unit(s) for the active node,
///     correlating observed sub-agent spawns/results back to the task they fulfill (by surfaced unit name,
///     <c>Agent</c> tool-call id, or background-receipt agent id), validating/recording results with the
///     bounded validation-retry policy, and the per-task status/attempt/error bookkeeping the projection and
///     snapshot read from.
/// </summary>
/// <remarks>
///     <para>
///         This collaborator owns NO lock. Every method is invoked by the <see cref="WorkflowRuntime"/> while
///         the runtime holds its single state lock, so all the maps below are mutated under that one lock.
///     </para>
///     <para>
///         The runtime state the coordinator reads or mutates (the loaded definition, the current node, the
///         visit counts, and the live <c>state</c>/<c>outputs</c> channels) is supplied as delegates created
///         once by the runtime and capturing its live fields, so a re-load or restore that reassigns a channel
///         is observed transparently without the coordinator holding a stale reference.
///     </para>
/// </remarks>
internal sealed class TaskCoordinator
{
    private readonly IJsonSchemaValidator _schemaValidator;
    private readonly Func<WorkflowDefinition?> _definition;
    private readonly Func<string?> _currentNodeId;
    private readonly Func<string, WorkflowNode?> _findNode;
    private readonly Func<string, int> _currentVisit;
    private readonly Func<JsonObject> _liveState;
    private readonly Func<JsonObject> _liveOutputs;
    private readonly Func<JsonNode?, int?, int?, BindingContext> _buildContext;

    // Correlation: the surfaced unit name -> its task ref (populated by Compose), the observed Agent
    // tool_call_id -> the same task ref (populated by RegisterSpawn), and the background spawn receipt
    // agent_id -> the same task ref (populated by ObserveSpawnResult).
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

    private readonly List<string> _unmatched = [];

    // The cap on the surfaced "unmatched" diagnostic list: a misbehaving controller emitting an unbounded
    // stream of uncorrelated ids must not inflate the projection (and the LLM context) without limit. The
    // most recent entries are kept (Fix: bound the diagnostic list).
    private const int MaxUnmatchedDiagnostics = 50;

    // The cap on the VARIABLE, potentially-EUII tail embedded in a task failure reason (the raw sub-agent
    // output or the joined schema-validation errors). A failure reason flows into _lastError, the
    // {"_error": ...} output marker, the GetWorkflow taskErrors projection AND the persisted
    // WorkflowTaskSnapshot.LastError, so the raw tail is truncated to bound data-retention / EUII exposure
    // (and keep the controller context small). The stable prefix is always kept so the controller still has
    // enough signal to decide retry/route (Fix: bound the failure reason).
    private const int MaxFailureReasonChars = 300;

    /// <summary>Creates a coordinator over the supplied schema validator and runtime-state accessors.</summary>
    public TaskCoordinator(
        IJsonSchemaValidator schemaValidator,
        Func<WorkflowDefinition?> definition,
        Func<string?> currentNodeId,
        Func<string, WorkflowNode?> findNode,
        Func<string, int> currentVisit,
        Func<JsonObject> liveState,
        Func<JsonObject> liveOutputs,
        Func<JsonNode?, int?, int?, BindingContext> buildContext
    )
    {
        _schemaValidator = schemaValidator;
        _definition = definition;
        _currentNodeId = currentNodeId;
        _findNode = findNode;
        _currentVisit = currentVisit;
        _liveState = liveState;
        _liveOutputs = liveOutputs;
        _buildContext = buildContext;
    }

    /// <summary>The per-unit lifecycle statuses, keyed by unit name (live, read-only view for the projection).</summary>
    public IReadOnlyDictionary<string, WorkflowTaskStatus> Statuses => _status;

    /// <summary>The most recent failure reason per unit (live, read-only view for the projection).</summary>
    public IReadOnlyDictionary<string, string> LastErrors => _lastError;

    /// <summary>The bounded list of uncorrelated tool-call / agent ids (live, read-only view for the projection).</summary>
    public IReadOnlyList<string> Unmatched => _unmatched;

    /// <summary>A snapshot of the last spawn units composed by name (used by hosts/tests for inspection).</summary>
    public IReadOnlyDictionary<string, SpawnUnit> SnapshotComposed() =>
        new Dictionary<string, SpawnUnit>(_composed, StringComparer.Ordinal);

    /// <summary>The active node's working set (name + onFailure) for the projection's join / onFailure surfaces.</summary>
    public IReadOnlyList<ProjectionActiveUnit> ActiveUnits(string nodeId)
    {
        var visit = _currentVisit(nodeId);
        return
        [
            .. _tasksByName
                .Values.Where(t => t.NodeId == nodeId && t.Visit == visit)
                .Select(t => new ProjectionActiveUnit { Name = t.Name, OnFailure = t.OnFailure }),
        ];
    }

    /// <summary>Whether <paramref name="name"/> is a currently-expected (composed) unit name.</summary>
    public bool IsExpectedUnit(string name) => _tasksByName.ContainsKey(name);

    /// <summary>Whether <paramref name="toolCallId"/> has been correlated to a task via <see cref="RegisterSpawn"/>.</summary>
    public bool IsRegisteredSpawn(string toolCallId) => _tasksByToolCallId.ContainsKey(toolCallId);

    /// <summary>
    ///     Composes the next expected sub-agent action(s) for the active node. In the V1-minimal slice this is
    ///     exactly one <see cref="SpawnUnit"/> per authored, non-forEach agent task (one per element for a
    ///     forEach task); start/terminal/conditional nodes (and anything outside the minimal shape) return an
    ///     empty list because the controller routes those itself. As a side effect it registers the composed
    ///     unit by name so a later <see cref="RegisterSpawn"/> can correlate it.
    /// </summary>
    public IReadOnlyList<SpawnUnit> Compose()
    {
        var definition = _definition();
        var currentNodeId = _currentNodeId();
        if (
            definition is null
            || currentNodeId is null
            || _findNode(currentNodeId) is not ProceduralNode node
            || node.TasksMode != TasksMode.Authored
            || node.TaskList is not { Count: > 0 } tasks
        )
        {
            return [];
        }

        var visit = _currentVisit(node.Id);
        var units = new List<SpawnUnit>();
        foreach (var task in tasks)
        {
            if (task.Delegate != DelegateKind.Agent || string.IsNullOrEmpty(task.SubagentType))
            {
                continue;
            }

            if (string.IsNullOrEmpty(task.ForEach))
            {
                ComposeUnit(node, visit, task, index: null, item: null, count: null, units);
                continue;
            }

            // forEach fan-out: resolve the source binding to an array and spawn one unit per element.
            if (_buildContext(null, null, null).Resolve(task.ForEach) is not JsonArray source)
            {
                continue;
            }

            for (var i = 0; i < source.Count; i++)
            {
                ComposeUnit(node, visit, task, i, source[i], source.Count, units);
            }
        }

        return units;
    }

    /// <summary>
    ///     Correlates an observed <c>Agent</c> tool call (by its <paramref name="toolCallId"/>) to the expected
    ///     unit named <paramref name="name"/> and marks the task in-flight. No-ops when the name is not an
    ///     expected unit (so the caller can pass through every <c>Agent</c> call unconditionally).
    /// </summary>
    public void RegisterSpawn(string toolCallId, string name)
    {
        if (!_tasksByName.TryGetValue(name, out var taskRef))
        {
            return;
        }

        // A unit that has already validated or terminally failed is SETTLED. A controller re-issuing an Agent
        // call for it (e.g. after completion) must not reset its status or re-map the toolCallId, or the
        // eventual ObserveResult would re-run ValidateAndRecord and re-apply its append/merge writes — silently
        // corrupting state (Fix M1). Only a Pending unit transitions to InFlight; an already-InFlight unit
        // re-correlates as before.
        var status = StatusOf(name);
        if (status is WorkflowTaskStatus.Validated or WorkflowTaskStatus.Failed)
        {
            return;
        }

        _tasksByToolCallId[toolCallId] = taskRef;
        _status[name] = WorkflowTaskStatus.InFlight;
    }

    /// <summary>
    ///     Observes the result of a correlated <b>blocking</b> spawn (P3 path): an error / non-JSON /
    ///     schema-invalid result is routed through the validation-retry policy; a valid result is recorded and
    ///     its writes applied. An unknown <paramref name="toolCallId"/> is surfaced as "unmatched".
    /// </summary>
    public void ObserveResult(string toolCallId, string resultText, bool isError)
    {
        if (_tasksByToolCallId.TryGetValue(toolCallId, out var taskRef))
        {
            ObserveAnswer(taskRef, resultText, isError);
        }
        else
        {
            AddUnmatched(toolCallId);
        }
    }

    /// <summary>
    ///     Observes the result of a correlated <c>Agent</c> tool call, distinguishing a <b>background spawn
    ///     receipt</b> (<c>{ "status": "spawned", "agent_id": ... }</c>, which records the <c>agent_id → task</c>
    ///     correlation and defers validation) from a <b>blocking answer</b> (validated/recorded immediately).
    ///     An error is failed; an unknown <paramref name="toolCallId"/> is surfaced as "unmatched".
    /// </summary>
    public void ObserveSpawnResult(string toolCallId, string resultText, bool isError)
    {
        if (!_tasksByToolCallId.TryGetValue(toolCallId, out var taskRef))
        {
            AddUnmatched(toolCallId);
        }
        else if (isError)
        {
            HandleFailure(taskRef, $"sub-agent returned an error: {Sanitize(resultText)}");
        }
        else if (TryReadSpawnReceipt(resultText, out var agentId))
        {
            // Background spawn: correlate by the receipt agent_id and wait for the injected result.
            _tasksByAgentId[agentId] = taskRef;
        }
        else
        {
            ValidateAndRecord(taskRef, resultText);
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
        if (_tasksByAgentId.TryGetValue(agentId, out var taskRef))
        {
            ObserveAnswer(taskRef, resultText, isError);
        }
        else
        {
            AddUnmatched(agentId);
        }
    }

    /// <summary>Clears all correlation/status/bookkeeping maps (on definition (re)load).</summary>
    public void Reset()
    {
        _tasksByName.Clear();
        _tasksByToolCallId.Clear();
        _tasksByAgentId.Clear();
        _status.Clear();
        _composed.Clear();
        _attempts.Clear();
        _lastError.Clear();
        _unmatched.Clear();
    }

    /// <summary>Rebuilds the task maps from a snapshot's task list, applying orphan reset per occurrence.</summary>
    public void Restore(IReadOnlyList<WorkflowTaskSnapshot> tasks)
    {
        Reset();
        foreach (var task in tasks)
        {
            RestoreTask(task);
        }
    }

    /// <summary>
    ///     Builds the per-task snapshot list (one entry per surfaced occurrence), reverse-indexing the live
    ///     blocking-spawn correlation so each task carries its own tool-call id. The background-spawn agent id
    ///     is intentionally NOT persisted: an orphaned in-flight task is detected on resume by its
    ///     <see cref="WorkflowTaskStatus.InFlight"/> status alone, so persisting the agent id would be inert.
    /// </summary>
    public IReadOnlyList<WorkflowTaskSnapshot> BuildTaskSnapshots()
    {
        var toolCallByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (toolCallId, taskRef) in _tasksByToolCallId)
        {
            toolCallByName[taskRef.Name] = toolCallId;
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
                    Status = StatusOf(name),
                    Attempts = _attempts.TryGetValue(name, out var attempts) ? attempts : 0,
                    LastError = _lastError.TryGetValue(name, out var error) ? error : null,
                    ToolCallId = toolCallByName.TryGetValue(name, out var toolCallId) ? toolCallId : null,
                }
            );
        }

        return tasks;
    }

    /// <summary>Rebuilds one task occurrence's correlation/status from its snapshot, applying orphan reset.</summary>
    private void RestoreTask(WorkflowTaskSnapshot task)
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
        // output) is an orphan after a restart: reset it to pending and drop its correlation so the controller
        // re-spawns it. Everything else keeps its status and (for completeness) its correlation. Only the
        // blocking-spawn tool-call correlation is persisted; the background-spawn agent id is not, because the
        // in-flight status alone drives the orphan reset (a pending task never carries a live agent id).
        var hasLiveCorrelation = task.ToolCallId is not null;
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

    /// <summary>
    ///     Registers one task occurrence (a non-forEach unit when <paramref name="index"/> is <c>null</c>, or a
    ///     single forEach element otherwise): records its <see cref="TaskRef"/> and seeds a <c>pending</c>
    ///     status so the unit counts toward the node's join totals, then surfaces it (rendering its prompt with
    ///     the loop locals) only when it is still <c>pending</c> — an <c>in_flight</c>/<c>validated</c>/terminal
    ///     <c>failed</c> unit is registered but not re-surfaced, so polling <c>GetWorkflow</c> never re-spawns it.
    /// </summary>
    private void ComposeUnit(
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
            Prompt = ComposePrompt(task, _buildContext(item, index, count)),
            OutputSchema = task.OutputSchema?.DeepClone(),
        };
        _composed[taskRef.Name] = unit;
        units.Add(unit);
    }

    private string ComposePrompt(WorkflowTask task, BindingContext context)
    {
        var parts = new List<string>();

        var definition = _definition();
        if (!string.IsNullOrEmpty(definition!.SharedContext))
        {
            parts.Add(definition.SharedContext);
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
    ///     Shared "blocking answer" handling for the P3 tool-result path and the injected-result path: an error
    ///     fails the task; otherwise the answer is validated and recorded.
    /// </summary>
    private void ObserveAnswer(TaskRef taskRef, string resultText, bool isError)
    {
        if (isError)
        {
            HandleFailure(taskRef, $"sub-agent returned an error: {Sanitize(resultText)}");
            return;
        }

        ValidateAndRecord(taskRef, resultText);
    }

    /// <summary>
    ///     Parses, schema-validates, records and applies the writes of a successful task answer. Any
    ///     parse/validation failure is routed through <see cref="HandleFailure"/> (which decides between a retry
    ///     and a terminal failure). On success the unit is marked validated and its last error cleared.
    /// </summary>
    private void ValidateAndRecord(TaskRef taskRef, string resultText)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(resultText);
        }
        catch (JsonException ex)
        {
            HandleFailure(taskRef, $"task output is not valid JSON: {Sanitize(ex.Message)}");
            return;
        }

        if (parsed is null)
        {
            HandleFailure(taskRef, "task output parsed to a null JSON value.");
            return;
        }

        if (taskRef.OutputSchemaJson is { } schemaJson)
        {
            var validation = _schemaValidator.ValidateDetailed(resultText, schemaJson);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Errors);
                HandleFailure(taskRef, "task output failed schema validation: " + Sanitize(errors));
                return;
            }
        }

        WriteOutputSlot(taskRef, parsed);
        if (taskRef.Writes is { } writes)
        {
            // A validated output can still be un-writable (e.g. a merge whose value is not an object). Route
            // that through the failure policy instead of letting it propagate out of the observe path and
            // fault the ENTIRE workflow, mirroring the JSON-parse handling above and the SetState handler's
            // catch (Fix: guard the task-output state write).
            try
            {
                StateWriter.Apply(_liveState(), writes, parsed);
            }
            catch (Exception ex)
                when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                HandleFailure(
                    taskRef,
                    $"task output could not be written to state: {Sanitize(ex.Message)}"
                );
                return;
            }
        }

        _status[taskRef.Name] = WorkflowTaskStatus.Validated;
        _ = _lastError.Remove(taskRef.Name);
    }

    /// <summary>
    ///     Truncates the variable, potentially-EUII tail of a failure reason (the raw sub-agent output or the
    ///     joined schema-validation errors) to <see cref="MaxFailureReasonChars"/> characters. Failure reasons
    ///     are persisted and surfaced (see <see cref="HandleFailure"/>), so capping the raw tail bounds the
    ///     data-retention / EUII exposure while keeping a short, useful prefix for the controller.
    /// </summary>
    private static string Sanitize(string raw) =>
        raw.Length <= MaxFailureReasonChars ? raw : raw[..MaxFailureReasonChars] + "…[truncated]";

    /// <summary>
    ///     Applies the "controller re-spawns; runtime bounds the attempts" retry policy. The attempt counter
    ///     for the unit is incremented; while it stays within <see cref="WorkflowTask.MaxValidationRetries"/>
    ///     the unit is marked <c>pending</c> again (so it re-surfaces in <c>nextExpectedAction</c>), its error
    ///     surfaced, and its spawn correlation cleared so the next spawn re-correlates. Once the budget is
    ///     exhausted the unit is terminally failed and an <c>{ "_error": ... }</c> marker recorded into outputs.
    ///     The <paramref name="error"/> reason is already truncated by <see cref="Sanitize"/> at every call
    ///     site that embeds raw text, bounding what is persisted/surfaced.
    /// </summary>
    private void HandleFailure(TaskRef taskRef, string error)
    {
        var attempts = (_attempts.TryGetValue(taskRef.Name, out var prior) ? prior : 0) + 1;
        _attempts[taskRef.Name] = attempts;
        _lastError[taskRef.Name] = error;

        if (attempts <= taskRef.MaxValidationRetries)
        {
            _status[taskRef.Name] = WorkflowTaskStatus.Pending;
            ClearCorrelation(taskRef.Name);
            return;
        }

        WriteOutputSlot(taskRef, new JsonObject { ["_error"] = error });
        _status[taskRef.Name] = WorkflowTaskStatus.Failed;
    }

    /// <summary>
    ///     Writes <paramref name="value"/> into the output slot for <paramref name="taskRef"/>: a forEach unit
    ///     (one with an <see cref="TaskRef.Index"/>) lands at its index in an array under
    ///     <c>outputs[nodeId][taskId]</c> (the array is grown/padded with nulls so out-of-order completions
    ///     land correctly); a non-forEach unit is written directly as the scalar/object value.
    /// </summary>
    private void WriteOutputSlot(TaskRef taskRef, JsonNode value)
    {
        var outputs = _liveOutputs();
        if (outputs[taskRef.NodeId] is not JsonObject nodeOutputs)
        {
            nodeOutputs = [];
            outputs[taskRef.NodeId] = nodeOutputs;
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

    /// <summary>
    ///     Records an uncorrelated <paramref name="id"/> in the bounded diagnostic list surfaced by the
    ///     projection. At most <see cref="MaxUnmatchedDiagnostics"/> entries are retained — when adding would
    ///     exceed the cap the oldest entry is dropped, keeping the most recent ids so a misbehaving controller
    ///     cannot inflate the projection (and the LLM context) without bound.
    /// </summary>
    private void AddUnmatched(string id)
    {
        _unmatched.Add(id);
        if (_unmatched.Count > MaxUnmatchedDiagnostics)
        {
            _unmatched.RemoveAt(0);
        }
    }

    /// <summary>Removes any tool-call-id / agent-id correlation pointing at the unit named <paramref name="unitName"/>.</summary>
    private void ClearCorrelation(string unitName)
    {
        RemoveByUnit(_tasksByToolCallId, unitName);
        RemoveByUnit(_tasksByAgentId, unitName);

        static void RemoveByUnit(Dictionary<string, TaskRef> map, string unitName)
        {
            foreach (
                var key in map.Where(kv => kv.Value.Name == unitName).Select(kv => kv.Key).ToList()
            )
            {
                _ = map.Remove(key);
            }
        }
    }

    private WorkflowTaskStatus StatusOf(string unitName) =>
        _status.TryGetValue(unitName, out var status) ? status : WorkflowTaskStatus.Pending;

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

    /// <summary>
    ///     Correlation record for one authored task occurrence: which node/visit/task it belongs to, the
    ///     optional forEach index (<c>null</c> for a non-forEach unit, the element index for a fan-out unit),
    ///     the schema/writes the observed result is validated against and applied through, the per-task
    ///     <see cref="WorkflowTask.OnFailure"/> route, and the validation-retry budget.
    /// </summary>
    private sealed record TaskRef
    {
        // Lazily-computed caches: Name and the serialized OutputSchema are derived purely from the immutable
        // init properties, so caching them on first access is behavior-identical while avoiding a fresh
        // allocation/serialization on every access (Name is read many times per unit; the schema JSON is
        // re-validated for every forEach element sharing one schema).
        private string? _name;
        private string? _outputSchemaJson;

        public required string NodeId { get; init; }
        public required int Visit { get; init; }
        public required string TaskId { get; init; }
        public int? Index { get; init; }
        public JsonNode? OutputSchema { get; init; }
        public WriteSpec? Writes { get; init; }
        public string? OnFailure { get; init; }
        public int MaxValidationRetries { get; init; }

        /// <summary>The stable unit name, computed once and cached.</summary>
        public string Name =>
            _name ??=
                Index is { } index
                    ? $"{NodeId}:{Visit}:{TaskId}:{index}"
                    : $"{NodeId}:{Visit}:{TaskId}";

        /// <summary>The serialized <see cref="OutputSchema"/>, serialized once and cached; <c>null</c> when there is no schema.</summary>
        public string? OutputSchemaJson =>
            OutputSchema is null ? null : (_outputSchemaJson ??= OutputSchema.ToJsonString());
    }
}
