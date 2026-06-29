using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     A complete LLM-authored workflow: an objective, optional shared context, typed nodes, and the
///     JSON-Schema definitions they reference. This is the in-memory shape produced by
///     <see cref="WorkflowJson.Deserialize"/> and validated by the ingest validator.
/// </summary>
public sealed record WorkflowDefinition
{
    /// <summary>The workflow schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>The high-level objective the workflow pursues.</summary>
    public required string Objective { get; init; }

    /// <summary>Optional shared context prepended to every delegated task.</summary>
    public string? SharedContext { get; init; }

    /// <summary>Optional initial inputs object.</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>Optional initial mutable state object.</summary>
    public JsonObject? State { get; init; }

    /// <summary>The workflow nodes.</summary>
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }

    /// <summary>The shared JSON-Schema definitions referenced via <c>$ref</c> (<c>#/$defs/&lt;name&gt;</c>).</summary>
    [JsonPropertyName("$defs")]
    public JsonObject? Defs { get; init; }

    /// <summary>An optional JSON-Schema fragment describing the workflow's final output.</summary>
    public JsonNode? FinalOutputSchema { get; init; }

    /// <summary>The maximum number of controller steps before the budget is exhausted. Must be &gt; 0.</summary>
    public int MaxStepBudget { get; init; } = 100;

    /// <summary>Optional node id to transition to when <see cref="MaxStepBudget"/> is exhausted.</summary>
    public string? OnBudgetExhausted { get; init; }
}
