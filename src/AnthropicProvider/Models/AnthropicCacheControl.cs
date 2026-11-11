using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Cache control directive for Anthropic prompt caching.
/// </summary>
public record AnthropicCacheControl
{
    /// <summary>
    ///     The cache type. Currently only "ephemeral" is supported by Anthropic.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ephemeral";

    /// <summary>
    ///     How long the entry lives: "1h" for one hour. Null leaves the field off the wire, which means
    ///     Anthropic's 5-minute default.
    /// </summary>
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ttl { get; init; }
}
