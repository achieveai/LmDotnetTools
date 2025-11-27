using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
///     Signals completion of agent processing
/// </summary>
public sealed record RunFinishedEvent : AgUiEventBase
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "RUN_FINISHED";

    /// <summary>
    ///     Unique identifier for this run (should match RunStartedEvent.RunId)
    /// </summary>
    [JsonPropertyName("runId")]
    public new string RunId { get; init; } = string.Empty;

    /// <summary>
    ///     Timestamp when the run finished (UTC)
    /// </summary>
    [JsonPropertyName("finishedAt")]
    public DateTime FinishedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     Status of the completed run
    /// </summary>
    [JsonPropertyName("status")]
    public RunStatus Status { get; init; } = RunStatus.Success;

    /// <summary>
    ///     Error message if the run failed
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
