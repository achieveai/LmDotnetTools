using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Persistence;

/// <summary>
///     A durable, round-trippable capture of <b>all</b> the mutable runtime state needed to resume a single
///     (non-nested) workflow instance: the loaded definition, the data channels
///     (<c>inputs</c>/<c>state</c>/<c>outputs</c>/<c>notes</c>), the loop bookkeeping
///     (<c>currentNodeId</c>/<c>visits</c>/<c>step</c>), the completion signal/result, and the per-task
///     status / attempt / correlation bookkeeping that lets a resumed controller detect and re-spawn orphaned
///     work. Produced by <see cref="WorkflowRuntime.Snapshot"/> and consumed by
///     <see cref="WorkflowRuntime.FromSnapshot"/>.
/// </summary>
/// <remarks>
///     The record is serialized and deserialized exclusively through <see cref="WorkflowJson.Options"/> so the
///     polymorphic node converter (and the tolerant condition converter) round-trip the embedded
///     <see cref="Definition"/>, and so the persistence wire contract lives in exactly one place. Nested
///     workflows are out of scope for this phase — a snapshot describes one root instance only.
/// </remarks>
public sealed record WorkflowInstanceSnapshot
{
    /// <summary>The current persistence schema version (bumped when the snapshot shape changes).</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The snapshot schema version, stored so a future loader can migrate older shapes.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The stable instance id this snapshot belongs to (the store key).</summary>
    public required string InstanceId { get; init; }

    /// <summary>The loaded workflow definition, or <c>null</c> if none has been authored yet.</summary>
    public WorkflowDefinition? Definition { get; init; }

    /// <summary>The id of the node the controller is positioned on.</summary>
    public string? CurrentNodeId { get; init; }

    /// <summary>Whether the workflow has advanced into a terminal node.</summary>
    public bool IsComplete { get; init; }

    /// <summary>The validated final result captured at completion, or <c>null</c>.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>The global controller step counter.</summary>
    public int Step { get; init; }

    /// <summary>The inputs channel (<c>inputs.&lt;...&gt;</c>).</summary>
    public JsonObject Inputs { get; init; } = [];

    /// <summary>The mutable state channel (<c>state.&lt;...&gt;</c>).</summary>
    public JsonObject State { get; init; } = [];

    /// <summary>The per-node task outputs channel (<c>{ nodeId: { taskId: value } }</c>).</summary>
    public JsonObject Outputs { get; init; } = [];

    /// <summary>The scoped notes channel (<c>{ scope: { key: value } }</c>).</summary>
    public JsonObject Notes { get; init; } = [];

    /// <summary>The per-node visit counts.</summary>
    public IReadOnlyDictionary<string, int> Visits { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The per-task correlation/status bookkeeping (one entry per surfaced task occurrence).</summary>
    public IReadOnlyList<WorkflowTaskSnapshot> Tasks { get; init; } = [];

    /// <summary>Serializes this snapshot to its canonical JSON form using <see cref="WorkflowJson.Options"/>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WorkflowJson.Options);

    /// <summary>Deserializes a snapshot from its canonical JSON form.</summary>
    /// <exception cref="JsonException">The JSON is invalid or deserializes to a null snapshot.</exception>
    public static WorkflowInstanceSnapshot FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<WorkflowInstanceSnapshot>(json, WorkflowJson.Options)
            ?? throw new JsonException("Workflow snapshot JSON deserialized to a null snapshot.");
    }

    /// <summary>
    ///     Returns a fully-isolated deep copy by serializing then deserializing through
    ///     <see cref="WorkflowJson.Options"/>. Used by stores to defend against <see cref="JsonNode"/>
    ///     aliasing so a later mutation of the live runtime never leaks into a persisted copy.
    /// </summary>
    public WorkflowInstanceSnapshot DeepCopy() => FromJson(ToJson());
}

/// <summary>
///     The persisted bookkeeping for one surfaced task occurrence (a non-forEach unit, or a single forEach
///     element). It carries both the correlation identity the runtime rebuilds its task maps from
///     (<see cref="NodeId"/>/<see cref="Visit"/>/<see cref="TaskId"/>/<see cref="Index"/> plus the
///     schema/writes/retry policy) and the live lifecycle state (<see cref="Status"/>, <see cref="Attempts"/>,
///     <see cref="LastError"/>) plus any in-flight spawn correlation (<see cref="ToolCallId"/>/
///     <see cref="AgentId"/>) needed to detect an orphan on resume.
/// </summary>
public sealed record WorkflowTaskSnapshot
{
    /// <summary>The unit correlation name, formatted <c>nodeId:visit:taskId[:index]</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The owning node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>The node visit this occurrence belongs to.</summary>
    public int Visit { get; init; }

    /// <summary>The authored task id within the node.</summary>
    public required string TaskId { get; init; }

    /// <summary>The forEach element index, or <c>null</c> for a non-forEach unit.</summary>
    public int? Index { get; init; }

    /// <summary>The output schema the result is validated against, if any.</summary>
    public JsonNode? OutputSchema { get; init; }

    /// <summary>How and where the validated output is written into state, if any.</summary>
    public WriteSpec? Writes { get; init; }

    /// <summary>The per-task failure route, if any.</summary>
    public string? OnFailure { get; init; }

    /// <summary>The validation-retry budget for this task.</summary>
    public int MaxValidationRetries { get; init; }

    /// <summary>The lifecycle status at snapshot time.</summary>
    public WorkflowTaskStatus Status { get; init; }

    /// <summary>How many times this unit's result has failed validation so far.</summary>
    public int Attempts { get; init; }

    /// <summary>The most recent failure reason, if any.</summary>
    public string? LastError { get; init; }

    /// <summary>The correlated <c>Agent</c> tool-call id of an in-flight spawn, if any.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>The correlated background-spawn receipt agent id, if any.</summary>
    public string? AgentId { get; init; }
}
