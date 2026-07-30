using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.RunCompleted"/>.
/// </summary>
/// <remarks>
/// Emitted once per run at its terminal boundary, including on error, cancellation, interruption,
/// and turn-ceiling exhaustion. A run that started always completes: a consumer can pair every
/// <see cref="LifecycleEventTypes.RunStarted"/> with exactly one of these, or else observe the gap
/// that says the pairing was lost.
/// </remarks>
public sealed record RunCompletedPayload
{
    /// <summary>The run that completed.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The run's originating generation id.</summary>
    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>
    /// How the run ended. See <see cref="LifecycleRunOutcomes"/>. Open vocabulary.
    /// </summary>
    /// <remarks>
    /// <see cref="LifecycleRunOutcomes.AwaitingSiblingResults"/> identifies a delayed-result child
    /// that deliberately did no work because a sibling result was still outstanding. It is a
    /// success, not a failure — and it is reported with a turn count of zero.
    /// </remarks>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = LifecycleRunOutcomes.Completed;

    /// <summary>How many turns the run performed.</summary>
    [JsonPropertyName("turn_count")]
    public int TurnCount { get; set; }

    /// <summary>
    /// Usage accumulated across the run, flushed and stamped at the terminal boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="LifecycleUsage.Completeness"/> is meaningful here: it distinguishes a run whose
    /// every contributing response reported usage from one where some did not.
    /// </remarks>
    [JsonPropertyName("usage")]
    public LifecycleUsage? Usage { get; set; }

    /// <summary>
    /// The failure, when <see cref="Outcome"/> is
    /// <see cref="LifecycleRunOutcomes.Error"/>. Absent otherwise.
    /// </summary>
    [JsonPropertyName("error")]
    public LifecycleError? Error { get; set; }
}
