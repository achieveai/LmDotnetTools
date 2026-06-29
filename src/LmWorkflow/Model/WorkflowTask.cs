using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     A single authored task within a <see cref="ProceduralNode"/>. In V1 every task delegates to an
///     agent (<see cref="DelegateKind.Agent"/>) identified by <see cref="SubagentType"/>.
/// </summary>
public sealed record WorkflowTask
{
    /// <summary>Task id, unique within its owning node.</summary>
    public required string Id { get; init; }

    /// <summary>Optional human-readable label.</summary>
    public string? Label { get; init; }

    /// <summary>The delegate target. Defaults to <see cref="DelegateKind.Agent"/>.</summary>
    public DelegateKind Delegate { get; init; } = DelegateKind.Agent;

    /// <summary>
    ///     The sub-agent template key to spawn (mirrors the <c>subagent_type</c> argument of the Agent
    ///     tool). Required when <see cref="Delegate"/> is <see cref="DelegateKind.Agent"/>.
    /// </summary>
    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>An optional dotted path whose array elements fan the task out (one spawn per item).</summary>
    public string? ForEach { get; init; }

    /// <summary>Whether the fan-out spawns run in parallel.</summary>
    public bool Parallel { get; init; }

    /// <summary>The prompt template handed to the spawned agent.</summary>
    public required string PromptTemplate { get; init; }

    /// <summary>An optional JSON-Schema fragment the task output is validated against.</summary>
    public JsonNode? OutputSchema { get; init; }

    /// <summary>How and where the task output is written into workflow state.</summary>
    public WriteSpec? Writes { get; init; }

    /// <summary>Optional node id to transition to when the task fails.</summary>
    public string? OnFailure { get; init; }

    /// <summary>How many times the task output may be re-requested when it fails schema validation.</summary>
    public int MaxValidationRetries { get; init; }
}

/// <summary>
///     Describes how a task output is merged into workflow state. In V1 the supported modes are
///     <see cref="WriteMode.Set"/>, <see cref="WriteMode.Append"/> and <see cref="WriteMode.Merge"/>.
/// </summary>
public sealed record WriteSpec
{
    /// <summary>An optional property name to extract from the task output before writing.</summary>
    public string? From { get; init; }

    /// <summary>The destination state path; must start with <c>state.</c>.</summary>
    public required string To { get; init; }

    /// <summary>The merge strategy. Defaults to <see cref="WriteMode.Set"/>.</summary>
    public WriteMode Mode { get; init; } = WriteMode.Set;

    /// <summary>An optional key used by keyed merges.</summary>
    public string? Key { get; init; }
}
