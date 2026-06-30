using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

/// <summary>
/// Gateway → app payload for a single discovered context item. Field names are the gateway's
/// wire contract (snake_case), pinned via <see cref="JsonPropertyNameAttribute"/> so they bind
/// regardless of the app's JSON naming defaults. Unknown fields are tolerated by System.Text.Json
/// by default so the gateway can add new fields without breaking older app builds.
/// </summary>
public sealed record ContextDiscoveryPayload
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Body of a discovered context file (CLAUDE.md / AGENTS.md). Sent by the gateway only for
    /// <c>kind == "context_file"</c> deliveries; the sub-agent path resolves the markdown by
    /// reading it from the workspace host directory instead and ignores this field.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Set by the gateway when <see cref="Content"/> was truncated to fit a delivery size cap.
    /// The injector surfaces a tag in the injected message so the model knows it isn't seeing
    /// the full file. Optional + defaults to false when absent.
    /// </summary>
    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}
