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
}
