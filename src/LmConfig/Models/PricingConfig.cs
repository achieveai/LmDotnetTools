using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Pricing configuration for token-based costs.
/// </summary>
public record PricingConfig
{
    /// <summary>
    /// Cost per million prompt tokens.
    /// </summary>
    [JsonPropertyName("prompt_per_million")]
    public required double PromptPerMillion { get; init; }

    /// <summary>
    /// Cost per million completion tokens.
    /// </summary>
    [JsonPropertyName("completion_per_million")]
    public required double CompletionPerMillion { get; init; }

    /// <summary>
    /// Calculates the total cost for a request.
    /// </summary>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <returns>Total cost in dollars</returns>
    public decimal CalculateTotalCost(int promptTokens, int completionTokens)
    {
        var promptCost = (decimal)((promptTokens * PromptPerMillion) / 1_000_000.0);
        var completionCost = (decimal)((completionTokens * CompletionPerMillion) / 1_000_000.0);
        return promptCost + completionCost;
    }

    /// <summary>
    /// Calculates the prompt cost for a request.
    /// </summary>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <returns>Prompt cost in dollars</returns>
    public decimal CalculatePromptCost(int promptTokens)
    {
        return (decimal)((promptTokens * PromptPerMillion) / 1_000_000.0);
    }

    /// <summary>
    /// Calculates the completion cost for a request.
    /// </summary>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <returns>Completion cost in dollars</returns>
    public decimal CalculateCompletionCost(int completionTokens)
    {
        return (decimal)((completionTokens * CompletionPerMillion) / 1_000_000.0);
    }
}

/// <summary>
/// Enhanced cost estimation with provider and subprovider details.
/// </summary>
public record CostEstimation
{
    public required decimal EstimatedPromptCost { get; init; }
    public required decimal EstimatedCompletionCost { get; init; }
    public required decimal TotalEstimatedCost { get; init; }
    public required string ModelId { get; init; }
    public required string Provider { get; init; }
    public string? SubProvider { get; init; } // For aggregators like OpenRouter
    public required string SelectedModel { get; init; } // Provider-specific model name
    public required int EstimatedPromptTokens { get; init; }
    public required int EstimatedCompletionTokens { get; init; }
    public required PricingConfig PricingInfo { get; init; }
    public DateTime EstimatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Full provider path for aggregators (e.g., "OpenRouter -> OpenAI")
    /// </summary>
    public string ProviderPath => SubProvider != null ? $"{Provider} -> {SubProvider}" : Provider;
}

/// <summary>
/// Actual cost report with provider and subprovider tracking.
/// </summary>
public record CostReport
{
    public required decimal ActualPromptCost { get; init; }
    public required decimal ActualCompletionCost { get; init; }
    public required decimal TotalActualCost { get; init; }
    public required string ModelId { get; init; }
    public required string Provider { get; init; }
    public string? SubProvider { get; init; } // For aggregators like OpenRouter
    public required string UsedModel { get; init; } // Provider-specific model name used
    public required int PromptTokens { get; init; }
    public required int CompletionTokens { get; init; }
    public required PricingConfig PricingInfo { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public TimeSpan? ResponseTime { get; init; } // How long the request took
    public string? RequestId { get; init; } // For tracking and debugging

    /// <summary>
    /// Full provider path for aggregators (e.g., "OpenRouter -> Together AI")
    /// </summary>
    public string ProviderPath => SubProvider != null ? $"{Provider} -> {SubProvider}" : Provider;
}

/// <summary>
/// Cost comparison between different provider options.
/// </summary>
public record CostComparison
{
    public required string ModelId { get; init; }
    public required IReadOnlyList<CostOption> Options { get; init; }
    public required int PromptTokens { get; init; }
    public required int CompletionTokens { get; init; }
    public DateTime ComparedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the cheapest cost option.
    /// </summary>
    public CostOption Cheapest => Options.OrderBy(o => o.TotalCost).First();

    /// <summary>
    /// Gets the most expensive cost option.
    /// </summary>
    public CostOption MostExpensive => Options.OrderByDescending(o => o.TotalCost).First();

    /// <summary>
    /// Gets options grouped by reliability tier.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CostOption>> ByReliability =>
        Options
            .GroupBy(o => o.ReliabilityTier ?? "Unknown")
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CostOption>)g.OrderBy(o => o.TotalCost).ToList());
}

/// <summary>
/// Individual cost option in a comparison.
/// </summary>
public record CostOption
{
    public required string Provider { get; init; }
    public string? SubProvider { get; init; }
    public required string ModelName { get; init; }
    public required decimal PromptCost { get; init; }
    public required decimal CompletionCost { get; init; }
    public required decimal TotalCost { get; init; }
    public required PricingConfig Pricing { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? ReliabilityTier { get; init; } // "high", "medium", "low"
    public double? UptimePercentage { get; init; } // 99.9, 99.5, etc.

    /// <summary>
    /// Full provider path for aggregators.
    /// </summary>
    public string ProviderPath => SubProvider != null ? $"{Provider} -> {SubProvider}" : Provider;

    /// <summary>
    /// Cost savings compared to a reference cost.
    /// </summary>
    public decimal CalculateSavings(decimal referenceCost) => referenceCost - TotalCost;

    /// <summary>
    /// Percentage savings compared to a reference cost.
    /// </summary>
    public double CalculateSavingsPercentage(decimal referenceCost) =>
        referenceCost > 0 ? (double)((referenceCost - TotalCost) / referenceCost * 100) : 0;
}

/// <summary>
/// Provider selection strategy for cost optimization.
/// </summary>
public enum ProviderSelectionStrategy
{
    Priority, // Use priority-based selection (default)
    Economic, // Prefer lowest cost providers
    Fast, // Prefer fastest providers
    Balanced, // Balance cost and performance
    HighQuality, // Prefer highest quality providers
    HighReliability, // Prefer most reliable providers (high uptime)
}
