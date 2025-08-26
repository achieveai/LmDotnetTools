using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
/// Represents the token limits and context capabilities of a model.
/// </summary>
public record TokenLimits
{
    /// <summary>
    /// Maximum number of tokens the model can process in its context window.
    /// </summary>
    [JsonPropertyName("max_context_tokens")]
    public int MaxContextTokens { get; init; }

    /// <summary>
    /// Maximum number of tokens the model can generate in a single response.
    /// </summary>
    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; }

    /// <summary>
    /// Recommended maximum number of tokens to use for the prompt to ensure optimal performance.
    /// </summary>
    [JsonPropertyName("recommended_max_prompt_tokens")]
    public int? RecommendedMaxPromptTokens { get; init; }

    /// <summary>
    /// Minimum number of tokens required for the model to function properly.
    /// </summary>
    [JsonPropertyName("min_context_tokens")]
    public int? MinContextTokens { get; init; }

    /// <summary>
    /// Default number of output tokens if not specified in the request.
    /// </summary>
    [JsonPropertyName("default_output_tokens")]
    public int? DefaultOutputTokens { get; init; }

    /// <summary>
    /// Whether the model supports dynamic context window sizing.
    /// </summary>
    [JsonPropertyName("supports_dynamic_context")]
    public bool SupportsDynamicContext { get; init; } = false;

    /// <summary>
    /// Whether the model supports context window extensions beyond the base limit.
    /// </summary>
    [JsonPropertyName("supports_context_extension")]
    public bool SupportsContextExtension { get; init; } = false;

    /// <summary>
    /// Maximum context window size when using extensions (if supported).
    /// </summary>
    [JsonPropertyName("max_extended_context_tokens")]
    public int? MaxExtendedContextTokens { get; init; }

    /// <summary>
    /// Whether the model supports accurate token counting for cost estimation.
    /// </summary>
    [JsonPropertyName("supports_token_counting")]
    public bool SupportsTokenCounting { get; init; } = true;

    /// <summary>
    /// Tokenizer information for accurate token counting.
    /// </summary>
    [JsonPropertyName("tokenizer")]
    public string? Tokenizer { get; init; }

    /// <summary>
    /// Whether token limits vary based on the input type (text, images, etc.).
    /// </summary>
    [JsonPropertyName("variable_token_limits")]
    public bool VariableTokenLimits { get; init; } = false;
}
