using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Represents the result of resolving which provider to use for a specific model.
/// Contains all necessary information to create and configure an agent for that provider.
/// </summary>
public record ProviderResolution
{
    /// <summary>
    /// The model configuration that was resolved.
    /// </summary>
    public required ModelConfig Model { get; init; }

    /// <summary>
    /// The provider configuration that was selected.
    /// </summary>
    public required ProviderConfig Provider { get; init; }

    /// <summary>
    /// The connection information for the selected provider.
    /// </summary>
    public required ProviderConnectionInfo Connection { get; init; }

    /// <summary>
    /// The sub-provider configuration if a sub-provider was selected.
    /// </summary>
    public SubProviderConfig? SubProvider { get; init; }

    /// <summary>
    /// The effective model name to use for API calls.
    /// This could be from the main provider or sub-provider.
    /// </summary>
    [JsonIgnore]
    public string EffectiveModelName => SubProvider?.ModelName ?? Provider.ModelName;

    /// <summary>
    /// The effective provider name to use.
    /// This could be from the main provider or sub-provider.
    /// </summary>
    [JsonIgnore]
    public string EffectiveProviderName => SubProvider?.Name ?? Provider.Name;

    /// <summary>
    /// The effective pricing configuration to use for cost calculation.
    /// This could be from the main provider or sub-provider.
    /// </summary>
    [JsonIgnore]
    public PricingConfig EffectivePricing => SubProvider?.Pricing ?? Provider.Pricing;

    /// <summary>
    /// The effective priority of this resolution.
    /// Sub-providers inherit the main provider's priority.
    /// </summary>
    [JsonIgnore]
    public int EffectivePriority => Provider.Priority;

    /// <summary>
    /// Gets a human-readable description of this resolution.
    /// </summary>
    /// <returns>A string describing the resolved provider configuration.</returns>
    public override string ToString()
    {
        var description = $"Model: {Model.Id}, Provider: {EffectiveProviderName}, ModelName: {EffectiveModelName}";
        if (SubProvider != null)
        {
            description += $" (via {Provider.Name})";
        }
        return description;
    }
}

/// <summary>
/// Criteria for selecting a provider when multiple options are available.
/// </summary>
public record ProviderSelectionCriteria
{
    /// <summary>
    /// Tags that the provider must have (all required).
    /// </summary>
    public IReadOnlyList<string>? RequiredTags { get; init; }

    /// <summary>
    /// Tags that the provider should preferably have (any preferred).
    /// </summary>
    public IReadOnlyList<string>? PreferredTags { get; init; }

    /// <summary>
    /// Whether to prefer providers with lower cost.
    /// </summary>
    public bool PreferLowerCost { get; init; } = false;

    /// <summary>
    /// Whether to prefer providers with higher performance/speed.
    /// </summary>
    public bool PreferHigherPerformance { get; init; } = false;

    /// <summary>
    /// Provider names to exclude from selection.
    /// </summary>
    public IReadOnlyList<string>? ExcludeProviders { get; init; }

    /// <summary>
    /// Provider names to include exclusively (if specified, only these providers will be considered).
    /// </summary>
    public IReadOnlyList<string>? IncludeOnlyProviders { get; init; }

    /// <summary>
    /// Maximum cost per million tokens for prompt processing.
    /// </summary>
    public decimal? MaxPromptCostPerMillion { get; init; }

    /// <summary>
    /// Maximum cost per million tokens for completion generation.
    /// </summary>
    public decimal? MaxCompletionCostPerMillion { get; init; }

    /// <summary>
    /// Creates a default criteria instance.
    /// </summary>
    public static ProviderSelectionCriteria Default => new();

    /// <summary>
    /// Creates criteria that prefers cost-effective providers.
    /// </summary>
    public static ProviderSelectionCriteria CostOptimized => new()
    {
        PreferLowerCost = true,
        PreferredTags = new[] { "cost-effective", "economical", "cheap" }
    };

    /// <summary>
    /// Creates criteria that prefers high-performance providers.
    /// </summary>
    public static ProviderSelectionCriteria PerformanceOptimized => new()
    {
        PreferHigherPerformance = true,
        PreferredTags = new[] { "fast", "ultra-fast", "high-performance", "speed-optimized" }
    };

    /// <summary>
    /// Creates criteria that prefers reliable providers.
    /// </summary>
    public static ProviderSelectionCriteria ReliabilityOptimized => new()
    {
        PreferredTags = new[] { "reliable", "stable", "official", "flagship" }
    };
}