namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Enumeration of Anthropic model types.
/// </summary>
public enum AnthropicModelType
{
    /// <summary>
    /// Claude 3 Opus model - highest capabilities model
    /// </summary>
    Claude3Opus,

    /// <summary>
    /// Claude 3 Sonnet model - balanced performance and efficiency
    /// </summary>
    Claude3Sonnet,

    /// <summary>
    /// Claude 3 Haiku model - fastest and most compact model
    /// </summary>
    Claude3Haiku,

    /// <summary>
    /// Claude 3.7 Sonnet model - latest high-performance model with extended thinking capabilities
    /// </summary>
    Claude37Sonnet,
}

/// <summary>
/// Provides constants for Anthropic model names used in API requests.
/// </summary>
public static class AnthropicModelNames
{
    /// <summary>
    /// Claude 3 Opus model ID.
    /// </summary>
    public const string Claude3Opus = "claude-3-opus-20240229";

    /// <summary>
    /// Claude 3 Sonnet model ID.
    /// </summary>
    public const string Claude3Sonnet = "claude-3-sonnet-20240229";

    /// <summary>
    /// Claude 3 Haiku model ID.
    /// </summary>
    public const string Claude3Haiku = "claude-3-haiku-20240307";

    /// <summary>
    /// Claude 3.7 Sonnet model ID, as seen in example requests.
    /// </summary>
    public const string Claude37Sonnet = "claude-3-7-sonnet-20250219";

    /// <summary>
    /// Gets the model name for a given model type.
    /// </summary>
    /// <param name="modelType">The model type to get the name for.</param>
    /// <returns>The model name used in API requests.</returns>
    public static string GetModelName(AnthropicModelType modelType) =>
        modelType switch
        {
            AnthropicModelType.Claude3Opus => Claude3Opus,
            AnthropicModelType.Claude3Sonnet => Claude3Sonnet,
            AnthropicModelType.Claude3Haiku => Claude3Haiku,
            AnthropicModelType.Claude37Sonnet => Claude37Sonnet,
            _ => Claude3Sonnet, // Default to Claude 3 Sonnet
        };
}
