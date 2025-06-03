using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Sub-provider configuration for tiered failover scenarios.
/// </summary>
public record SubProviderConfig
{
    /// <summary>
    /// Sub-provider name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Sub-provider-specific model name.
    /// </summary>
    [JsonPropertyName("model_name")]
    public required string ModelName { get; init; }

    /// <summary>
    /// Sub-provider priority.
    /// </summary>
    [JsonPropertyName("priority")]
    public required int Priority { get; init; }

    /// <summary>
    /// Pricing configuration for this sub-provider.
    /// </summary>
    [JsonPropertyName("pricing")]
    public required PricingConfig Pricing { get; init; }
} 