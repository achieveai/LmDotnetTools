using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.ContextMeasured"/>: one agent loop's request sized
/// against its model's window (#681; spec 679 §4.1).
/// </summary>
/// <remarks>
/// <para>
/// Emitted twice per generation: once <c>Estimated</c>, from a local heuristic over the request about
/// to go out, and once <c>Measured</c>, from the usage the provider reported for it. The pair shares a
/// <see cref="GenerationId"/> and <see cref="GenerationOrdinal"/>; the measured one supersedes.
/// </para>
/// <para>
/// Content-free by construction — counts, ratios and ids only — so it needs no capability gate.
/// </para>
/// </remarks>
public sealed record ContextMeasuredPayload
{
    /// <summary>The run the generation ran in.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The generation observed.</summary>
    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>Loop-local generation counter, monotonic across restarts.</summary>
    [JsonPropertyName("generation_ordinal")]
    public long GenerationOrdinal { get; set; }

    /// <summary><c>root</c> or the sub-agent id.</summary>
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The model the request was sent to.</summary>
    [JsonPropertyName("effective_model_id")]
    public string EffectiveModelId { get; set; } = string.Empty;

    /// <summary>How the size number was obtained: <c>Measured</c>, <c>Estimated</c>, <c>Unavailable</c>.</summary>
    [JsonPropertyName("provenance")]
    public string Provenance { get; set; } = string.Empty;

    /// <summary>Pre-send estimate of the request's input tokens.</summary>
    [JsonPropertyName("estimated_input_tokens")]
    public long EstimatedInputTokens { get; set; }

    /// <summary>Post-response measurement, when the provider reported usage.</summary>
    [JsonPropertyName("measured_input_tokens")]
    public long? MeasuredInputTokens { get; set; }

    /// <summary>The model's context window, when known.</summary>
    [JsonPropertyName("window_tokens")]
    public long? WindowTokens { get; set; }

    /// <summary>Tokens held back for output.</summary>
    [JsonPropertyName("reserve_tokens")]
    public long ReserveTokens { get; set; }

    /// <summary>Fraction of the usable window the request occupies, or null when the window is unknown.</summary>
    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }

    /// <summary>The checkpoint the view was built on, when one was active.</summary>
    [JsonPropertyName("active_checkpoint_id")]
    public string? ActiveCheckpointId { get; set; }

    /// <summary>How many rows the request carried.</summary>
    [JsonPropertyName("rows_in_view")]
    public long RowsInView { get; set; }
}
