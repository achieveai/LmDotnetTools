using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Signals the beginning of agent processing
/// </summary>
public sealed record RunStartedEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "RUN_STARTED";

    /// <summary>
    /// Unique identifier for this run
    /// </summary>
    [JsonPropertyName("runId")]
    public string RunId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Timestamp when the run started (UTC)
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional metadata associated with this run
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, object>? Metadata { get; init; }
}
