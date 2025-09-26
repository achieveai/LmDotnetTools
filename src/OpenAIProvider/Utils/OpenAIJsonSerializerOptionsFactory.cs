using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Utils;

/// <summary>
/// Factory for creating JsonSerializerOptions with OpenAI-specific converters.
/// Extends LmCore's base factory with OpenAI Union types and enums.
/// </summary>
public static class OpenAIJsonSerializerOptionsFactory
{
    /// <summary>
    /// Creates JsonSerializerOptions with all LmCore converters plus OpenAI-specific converters.
    /// This includes Union converters for OpenAI content types and OpenAI enum converters.
    /// </summary>
    /// <param name="writeIndented">Whether to format JSON with indentation</param>
    /// <param name="namingPolicy">JSON property naming policy</param>
    /// <param name="caseInsensitive">Whether property names should be case-insensitive</param>
    /// <param name="allowTrailingCommas">Whether to allow trailing commas in JSON</param>
    /// <returns>JsonSerializerOptions with LmCore and OpenAI converters registered</returns>
    public static JsonSerializerOptions CreateForOpenAI(
        bool writeIndented = false,
        JsonNamingPolicy? namingPolicy = null,
        bool caseInsensitive = false,
        bool allowTrailingCommas = true
    )
    {
        var options = JsonSerializerOptionsFactory.CreateBase(
            writeIndented,
            namingPolicy,
            caseInsensitive,
            allowTrailingCommas
        );

        AddOpenAIConverters(options);
        return options;
    }

    /// <summary>
    /// Adds OpenAI-specific converters to the provided JsonSerializerOptions.
    /// This includes only the essential Union converters for OpenAI content types that are not already in LmCore.
    /// Enum converters are not added to avoid conflicts with JsonPropertyName attributes.
    /// </summary>
    /// <param name="options">The JsonSerializerOptions to add OpenAI converters to</param>
    public static void AddOpenAIConverters(JsonSerializerOptions options)
    {
        // OpenAI-specific Union converters for content types (not in LmCore)
        options.Converters.Add(
            new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>()
        );
        options.Converters.Add(new UnionJsonConverter<TextContent, ImageContent>());

        // Note: UnionJsonConverter<string, IReadOnlyList<string>> is already registered in LmCore
        // Note: Enum converters are not added here to avoid conflicts with JsonPropertyName attributes
        // on OpenAI models. The models use JsonPropertyName attributes for proper JSON mapping.
    }

    /// <summary>
    /// Creates JsonSerializerOptions optimized for OpenAI API production use.
    /// Uses the standard OpenAI configuration with compact formatting.
    /// </summary>
    public static JsonSerializerOptions CreateForProduction() =>
        CreateForOpenAI(writeIndented: false);

    /// <summary>
    /// Creates JsonSerializerOptions optimized for OpenAI testing and debugging.
    /// Uses indented formatting for better readability.
    /// </summary>
    public static JsonSerializerOptions CreateForTesting() => CreateForOpenAI(writeIndented: true);

    /// <summary>
    /// Creates JsonSerializerOptions for cross-provider scenarios.
    /// Uses case-insensitive matching for robustness across different providers.
    /// </summary>
    public static JsonSerializerOptions CreateUniversal() => CreateForOpenAI(caseInsensitive: true);
}
