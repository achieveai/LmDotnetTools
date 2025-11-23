using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
/// Result event containing final execution summary, usage statistics, and costs
/// </summary>
public record ResultEvent : JsonlEventBase
{
    [JsonPropertyName("subtype")]
    public string? Subtype { get; init; } // "success" or "error"

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

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
