using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Result event containing final execution summary, usage statistics, and costs.
/// </summary>
/// <remarks>
///     Per the upstream SDK type definition (<c>SDKResultMessage</c>), a result is either
///     a <c>success</c> variant carrying <see cref="Result"/> and full usage stats, or an
///     error variant carrying <see cref="Errors"/>. Inspect <see cref="Subtype"/> to
///     distinguish — <see cref="IsError"/> alone is unreliable (the CLI sets it to
///     <c>false</c> on <c>error_during_execution</c>).
/// </remarks>
public record ResultEvent : JsonlEventBase
{
    /// <summary>
    ///     One of: <c>success</c>, <c>error_during_execution</c>, <c>error_max_turns</c>,
    ///     <c>error_max_budget_usd</c>, <c>error_max_structured_output_retries</c>.
    ///     Treat anything other than <c>success</c> as a failed turn — the CLI emits
    ///     non-success subtypes when an internal exception prevents the API call from
    ///     being issued.
    /// </summary>
    [JsonPropertyName("subtype")]
    public string? Subtype { get; init; }

    /// <summary>
    ///     The CLI's raw <c>is_error</c> flag. Not a reliable failure signal — it remains
    ///     <c>false</c> on <c>error_during_execution</c>. Use <see cref="Subtype"/> instead.
    /// </summary>
    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    /// <summary>
    ///     Error messages reported by the SDK on non-success subtypes (typically captured
    ///     stack traces from internal exceptions that aborted the run before the API call).
    /// </summary>
    [JsonPropertyName("errors")]
    public List<string>? Errors { get; init; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; init; }

    [JsonPropertyName("duration_api_ms")]
    public int? DurationApiMs { get; init; }

    [JsonPropertyName("num_turns")]
    public int? NumTurns { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("total_cost_usd")]
    public double? TotalCostUsd { get; init; }

    [JsonPropertyName("usage")]
    public JsonElement? Usage { get; init; }

    [JsonPropertyName("modelUsage")]
    public JsonElement? ModelUsage { get; init; }

    [JsonPropertyName("permission_denials")]
    public List<string>? PermissionDenials { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }
}
