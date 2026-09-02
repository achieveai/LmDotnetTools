using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// Payload of <see cref="LifecycleEventTypes.CompactionDecided"/>,
/// <see cref="LifecycleEventTypes.CompactionApplied"/> and <see cref="LifecycleEventTypes.CompactionFailed"/>:
/// the just-in-time policy's typed decision record (spec 679 §5.5). It carries numbers and identifiers
/// only — never the summary text or a row — so a subscriber without content capability sees all of it.
/// </summary>
public sealed record CompactionPayload
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>One of the decision kinds: <c>no_action</c>, <c>warn</c>, <c>shadow</c>, <c>compact</c>, <c>skipped</c>, <c>failed</c>.</summary>
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    /// <summary>The typed skip, trigger or failure reason.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }

    [JsonPropertyName("checkpoint_id")]
    public string? CheckpointId { get; set; }

    [JsonPropertyName("boundary_seq")]
    public long? BoundarySeq { get; set; }

    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }

    [JsonPropertyName("tokens")]
    public long Tokens { get; set; }

    [JsonPropertyName("window")]
    public long? Window { get; set; }

    [JsonPropertyName("reserve")]
    public long Reserve { get; set; }

    [JsonPropertyName("cache_temperature")]
    public string CacheTemperature { get; set; } = string.Empty;

    [JsonPropertyName("cooldown_remaining")]
    public long? CooldownRemaining { get; set; }

    [JsonPropertyName("predicted_savings_micros")]
    public long? PredictedSavingsMicros { get; set; }

    [JsonPropertyName("cut_seq")]
    public long? CutSeq { get; set; }

    [JsonPropertyName("tokens_after")]
    public long? TokensAfter { get; set; }

    [JsonPropertyName("rows_covered")]
    public long? RowsCovered { get; set; }

    [JsonPropertyName("latency_ms")]
    public long? LatencyMilliseconds { get; set; }
}
