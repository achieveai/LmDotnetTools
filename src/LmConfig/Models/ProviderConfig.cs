using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
///     Configuration for a specific provider of a language model.
/// </summary>
public record ProviderConfig
{
    /// <summary>
    ///     Provider name (e.g., "OpenAI", "Anthropic", "AzureAI").
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    ///     Provider-specific model name.
    /// </summary>
    [JsonPropertyName("model_name")]
    public required string ModelName { get; init; }

    /// <summary>
    ///     Provider priority (higher values = preferred).
    ///     Default is 1.
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 1;

    /// <summary>
    ///     Pricing configuration for cost estimation and tracking.
    /// </summary>
    [JsonPropertyName("pricing")]
    public required PricingConfig Pricing { get; init; }

    /// <summary>
    ///     Performance metrics for this provider.
    /// </summary>
    [JsonPropertyName("performance")]
    public PerformanceConfig? Performance { get; init; }

    /// <summary>
    ///     Sub-providers for tiered failover (optional).
    /// </summary>
    [JsonPropertyName("sub_providers")]
    public IReadOnlyList<SubProviderConfig>? SubProviders { get; init; }

    /// <summary>
    ///     Tags for provider categorization and filtering (e.g., "economic", "fast", "reliable").
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    ///     Checks if this provider has all the specified tags.
    /// </summary>
    /// <param name="requiredTags">Tags to check for.</param>
    /// <returns>True if the provider has all required tags, false otherwise.</returns>
    public bool HasAllTags(IEnumerable<string> requiredTags)
    {
        return Tags != null && requiredTags.All(tag => Tags.Contains(tag));
    }

    /// <summary>
    ///     Checks if this provider has any of the specified tags.
    /// </summary>
    /// <param name="tags">Tags to check for.</param>
    /// <returns>True if the provider has at least one of the tags, false otherwise.</returns>
    public bool HasAnyTag(IEnumerable<string> tags)
    {
        return Tags != null && tags.Any(tag => Tags.Contains(tag));
    }
}
