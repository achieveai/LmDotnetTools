using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>How a context-size number was obtained (spec 679 §0, §4.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeasurementProvenance
{
    /// <summary>The provider reported it.</summary>
    Measured,

    /// <summary>A local heuristic produced it.</summary>
    Estimated,

    /// <summary>Nothing could produce it.</summary>
    Unavailable,
}

/// <summary>
///     Expected prompt-cache reuse, derived from durable activity against the model's cache TTL (spec 679
///     §4.4). Computed at read time; never persisted as truth; never starts work.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheTemperature
{
    /// <summary>The last durable activity is inside the cache TTL.</summary>
    Hot,

    /// <summary>The last durable activity is outside the cache TTL, or there has been none.</summary>
    Cold,

    /// <summary>The route does not cache, so temperature is meaningless.</summary>
    Unknown,
}

/// <summary>One policy decision as stamped on the observation it was made for (spec 679 §5.5).</summary>
/// <remarks>
///     Defined here with the observation it rides on so the persisted shape is fixed once; the policy that
///     produces it (#684) is not part of this type's contract.
/// </remarks>
public sealed record CompactionDecisionSummary
{
    /// <summary>The decision: NoAction, Warn, Shadow, Compact, Skipped, Failed.</summary>
    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    /// <summary>The typed reason for a Skipped or Failed decision (§5.6).</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>Utilization the decision was made at.</summary>
    [JsonPropertyName("utilization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Utilization { get; init; }

    /// <summary>Tokens in the view the decision was made for.</summary>
    [JsonPropertyName("tokens")]
    public long Tokens { get; init; }

    /// <summary>The model window, when known.</summary>
    [JsonPropertyName("window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Window { get; init; }

    /// <summary>The reserve held back for output.</summary>
    [JsonPropertyName("reserve")]
    public long Reserve { get; init; }

    /// <summary>Cache temperature at decision time.</summary>
    [JsonPropertyName("cache_temperature")]
    public CacheTemperature CacheTemperature { get; init; } = CacheTemperature.Unknown;

    /// <summary>Generations of cooldown remaining, when a cooldown was active.</summary>
    [JsonPropertyName("cooldown_remaining")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CooldownRemaining { get; init; }

    /// <summary>Predicted savings in micros for an economic decision.</summary>
    [JsonPropertyName("predicted_savings_micros")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PredictedSavingsMicros { get; init; }

    /// <summary>The cut the decision proposed, when it proposed one.</summary>
    [JsonPropertyName("cut_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CutSeq { get; init; }
}

/// <summary>
///     A per-generation record of estimated or measured request size against the model's capacity, for one
///     agent loop (spec 679 §4.1). Persisted in the thread's metadata ring by
///     <c>ContextObservationProjection</c>; never a message row.
/// </summary>
public sealed record ContextObservation
{
    /// <summary>The persisted schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of this record.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The thread the observation belongs to.</summary>
    [JsonPropertyName("thread_id")]
    public required string ThreadId { get; init; }

    /// <summary><c>root</c> or <c>agent-N</c>.</summary>
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    /// <summary>The run the generation ran in.</summary>
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    /// <summary>The generation observed.</summary>
    [JsonPropertyName("generation_id")]
    public required string GenerationId { get; init; }

    /// <summary>Loop-local generation counter, for cooldown arithmetic.</summary>
    [JsonPropertyName("generation_ordinal")]
    public required long GenerationOrdinal { get; init; }

    /// <summary>When the observation was taken.</summary>
    [JsonPropertyName("observed_at_utc")]
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>The model the request was sent to.</summary>
    [JsonPropertyName("effective_model_id")]
    public required string EffectiveModelId { get; init; }

    /// <summary>Pre-send estimate of the request's input tokens (§4.2).</summary>
    [JsonPropertyName("estimated_input_tokens")]
    public long EstimatedInputTokens { get; init; }

    /// <summary>Post-response measurement: input + cache read + cache write of the generation's usage.</summary>
    [JsonPropertyName("measured_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MeasuredInputTokens { get; init; }

    /// <summary>How the size number was obtained.</summary>
    [JsonPropertyName("provenance")]
    public MeasurementProvenance Provenance { get; init; } = MeasurementProvenance.Unavailable;

    /// <summary>The model's context window, when the capacity resolver knows it.</summary>
    [JsonPropertyName("window_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WindowTokens { get; init; }

    /// <summary>Tokens held back for output (§5.2).</summary>
    [JsonPropertyName("reserve_tokens")]
    public long ReserveTokens { get; init; }

    /// <summary>
    ///     Fraction of the usable window the request occupies, or null when the window is unknown. Derived;
    ///     recomputed on read.
    /// </summary>
    [JsonIgnore]
    public double? Utilization =>
        WindowTokens is > 0 && WindowTokens.Value - ReserveTokens > 0
            ? (double)(MeasuredInputTokens ?? EstimatedInputTokens) / (WindowTokens.Value - ReserveTokens)
            : null;

    /// <summary>
    ///     Whether the request was sent with prompt caching on (#681; §4.4). Read back to decide whether a
    ///     cache temperature is meaningful for this loop; null when the loop did not say.
    /// </summary>
    [JsonPropertyName("prompt_caching_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PromptCachingEnabled { get; init; }

    /// <summary>The checkpoint the view was built on, when one was active.</summary>
    [JsonPropertyName("active_checkpoint_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveCheckpointId { get; init; }

    /// <summary>How many rows the execution view contained.</summary>
    [JsonPropertyName("rows_in_view")]
    public long RowsInView { get; init; }

    /// <summary>The policy decision made on this observation, when the policy ran.</summary>
    [JsonPropertyName("decision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompactionDecisionSummary? Decision { get; init; }
}
