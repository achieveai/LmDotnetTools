using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
///     Event representing the completion of a sub-agent step or task
///     Marks the end of a step started by STEP_STARTED event
/// </summary>
public sealed record StepFinishedEvent : AgUiEventBase
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => AgUiEventTypes.STEP_FINISHED;

    /// <summary>
    ///     Unique identifier for this step
    ///     Must match the stepId from the corresponding STEP_STARTED event
    /// </summary>
    [JsonPropertyName("stepId")]
    public string StepId { get; init; } = string.Empty;

    /// <summary>
    ///     Status of step completion
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "success";

    /// <summary>
    ///     Result or output from the step
    /// </summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; init; }

    /// <summary>
    ///     Error message if the step failed
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Timestamp when the step finished
    /// </summary>
    [JsonPropertyName("finishedAt")]
    public DateTime FinishedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     Duration of step execution in milliseconds
    /// </summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }
}
