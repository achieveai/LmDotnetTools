using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;

using AchieveAi.LmDotnetTools.LmCore.Models;
namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

/// <summary>
///     Provider-specific usage model that supports both OpenAI and OpenRouter formats
/// </summary>
public record OpenAIProviderUsage
{
    // Standard token counts (both OpenAI and OpenRouter)
    [JsonPropertyName("prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TotalTokens { get; init; }

    [JsonPropertyName("total_cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? TotalCost { get; init; }

    // OpenAI-style nested token details
    [JsonPropertyName("input_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIInputTokenDetails? InputTokenDetails { get; init; }

    [JsonPropertyName("output_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIOutputTokenDetails? OutputTokenDetails { get; init; }

    // OpenRouter-style direct fields (for compatibility)
    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReasoningTokens { get; init; }

    [JsonPropertyName("cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CachedTokens { get; init; }

    [JsonExtensionData]
    public IDictionary<string, object> ExtraProperties { get; init; } = new Dictionary<string, object>();

    // Unified access properties with precedence logic
    [JsonIgnore]
    public int TotalReasoningTokens =>
        // OpenRouter direct field takes precedence
        ReasoningTokens != 0
            ? ReasoningTokens
            :
            // Fallback to OpenAI nested structure
            OutputTokenDetails?.ReasoningTokens ?? 0;

    [JsonIgnore]
    public int TotalCachedTokens =>
        // OpenRouter direct field takes precedence
        CachedTokens != 0
            ? CachedTokens
            :
            // Fallback to OpenAI nested structure
            InputTokenDetails?.CachedTokens ?? 0;

    /// <summary>
    ///     Convert to core Usage model
    /// </summary>
    public Usage ToCoreUsage()
    {
        var usage = new Usage
        {
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
            TotalTokens = TotalTokens,
            TotalCost = TotalCost,
            ExtraProperties = ExtraProperties.ToImmutableDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
        };

        // Convert nested token details if present
        if (InputTokenDetails != null)
        {
            usage = usage with
            {
                InputTokenDetails = new InputTokenDetails { CachedTokens = InputTokenDetails.CachedTokens },
            };
        }

        if (OutputTokenDetails != null)
        {
            usage = usage with
            {
                OutputTokenDetails = new OutputTokenDetails { ReasoningTokens = OutputTokenDetails.ReasoningTokens },
            };
        }

        return usage;
    }

    /// <summary>
    ///     Create from core Usage model
    /// </summary>
    public static OpenAIProviderUsage FromCoreUsage(Usage coreUsage)
    {
        ArgumentNullException.ThrowIfNull(coreUsage);

        return new OpenAIProviderUsage
        {
            PromptTokens = coreUsage.PromptTokens,
            CompletionTokens = coreUsage.CompletionTokens,
            TotalTokens = coreUsage.TotalTokens,
            TotalCost = coreUsage.TotalCost,
            InputTokenDetails =
                coreUsage.InputTokenDetails != null
                    ? new OpenAIInputTokenDetails { CachedTokens = coreUsage.InputTokenDetails.CachedTokens }
                    : null,
            OutputTokenDetails =
                coreUsage.OutputTokenDetails != null
                    ? new OpenAIOutputTokenDetails { ReasoningTokens = coreUsage.OutputTokenDetails.ReasoningTokens }
                    : null,
        };
    }
}

/// <summary>
///     OpenAI-style input token details for provider-specific model
/// </summary>
public record OpenAIInputTokenDetails
{
    [JsonPropertyName("cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CachedTokens { get; init; }
}

/// <summary>
///     OpenAI-style output token details for provider-specific model
/// </summary>
public record OpenAIOutputTokenDetails
{
    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReasoningTokens { get; init; }
}
