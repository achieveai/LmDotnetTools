using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Configuration for a language model including its capabilities and provider information.
/// </summary>
public record ModelConfig
{
    /// <summary>
    /// Unique model identifier (e.g., "gpt-4", "claude-3-sonnet").
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Whether this model has special reasoning capabilities.
    /// </summary>
    [JsonPropertyName("is_reasoning")]
    public bool IsReasoning { get; init; } = false;

    /// <summary>
    /// The date when this model was created/added to OpenRouter.
    /// Null if date information is not available.
    /// </summary>
    [JsonPropertyName("created_date")]
    public DateTime? CreatedDate { get; init; }

    /// <summary>
    /// Model capabilities defining what the model can do.
    /// Optional for backward compatibility with existing configurations.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public ModelCapabilities? Capabilities { get; init; }

    /// <summary>
    /// Ordered list of provider configurations for this model.
    /// Higher priority providers are tried first.
    /// </summary>
    [JsonPropertyName("providers")]
    public required IReadOnlyList<ProviderConfig> Providers { get; init; }

    /// <summary>
    /// Checks if this model has a specific capability.
    /// </summary>
    /// <param name="capability">The capability to check for.</param>
    /// <returns>True if the model has the capability, false otherwise.</returns>
    public bool HasCapability(string capability)
    {
        return Capabilities?.HasCapability(capability) ?? false;
    }

    /// <summary>
    /// Gets the highest priority provider for this model.
    /// </summary>
    /// <returns>The provider configuration with the highest priority.</returns>
    public ProviderConfig GetPrimaryProvider()
    {
        return Providers.OrderByDescending(p => p.Priority).First();
    }

    /// <summary>
    /// Gets providers that match the specified tags.
    /// </summary>
    /// <param name="requiredTags">Tags that providers must have.</param>
    /// <returns>List of matching providers ordered by priority.</returns>
    public IReadOnlyList<ProviderConfig> GetProvidersWithTags(IEnumerable<string> requiredTags)
    {
        var tags = requiredTags.ToList();
        return tags.Count == 0
            ? Providers
            : [.. Providers
                .Where(p => tags.All(tag => p.Tags?.Contains(tag) == true))
                .OrderByDescending(p => p.Priority)];
    }
}
