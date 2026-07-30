using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.TurnCompleted"/>.
/// </summary>
/// <remarks>
/// <para>
/// Emitted exactly once per accepted generation id, at the turn's final state — including when the
/// turn ended in error, was cancelled, or was interrupted. Streaming fragments never appear here;
/// this reports final state only, which is what makes the event identical in shape across the raw
/// loop and every CLI-backed provider.
/// </para>
/// <para>
/// A run that deliberately performs no model turn — a non-final delayed-result sibling — emits no
/// turn event at all. See ADR 0004.
/// </para>
/// </remarks>
public sealed record TurnCompletedPayload
{
    /// <summary>The run this turn belongs to.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The turn that completed.</summary>
    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>This turn's ordinal within its run, starting at <c>1</c>.</summary>
    [JsonPropertyName("turn_index")]
    public int TurnIndex { get; set; }

    /// <summary>
    /// How the turn ended. See <see cref="LifecycleTurnOutcomes"/>. Open vocabulary.
    /// </summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = LifecycleTurnOutcomes.Completed;

    /// <summary>How many complete messages the turn produced.</summary>
    [JsonPropertyName("message_count")]
    public int MessageCount { get; set; }

    /// <summary>How many tool calls the turn requested.</summary>
    [JsonPropertyName("tool_call_count")]
    public int ToolCallCount { get; set; }

    /// <summary>
    /// Usage for this turn, when the provider reported any. Absent means none was reported.
    /// </summary>
    [JsonPropertyName("usage")]
    public LifecycleUsage? Usage { get; set; }

    /// <summary>
    /// The failure, when <see cref="Outcome"/> is not
    /// <see cref="LifecycleTurnOutcomes.Completed"/>. Absent on success.
    /// </summary>
    [JsonPropertyName("error")]
    public LifecycleError? Error { get; set; }
}
