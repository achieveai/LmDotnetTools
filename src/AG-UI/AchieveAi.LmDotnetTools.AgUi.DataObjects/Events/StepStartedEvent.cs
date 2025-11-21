using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Event representing the start of a sub-agent step or task
/// Used for multi-step agent workflows or sub-agent delegation
/// </summary>
public sealed record StepStartedEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => AgUiEventTypes.STEP_STARTED;

    /// <summary>
    /// Unique identifier for this step
    /// </summary>
    [JsonPropertyName("stepId")]
    public string StepId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name of the step
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of what this step will accomplish
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Parent step ID if this is a nested step
    /// </summary>
    [JsonPropertyName("parentStepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentStepId { get; init; }

    /// <summary>
    /// Type/category of the step (e.g., "tool_call", "reasoning", "sub_agent")
    /// </summary>
    [JsonPropertyName("stepType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepType { get; init; }

    /// <summary>
    /// Timestamp when the step started
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}
