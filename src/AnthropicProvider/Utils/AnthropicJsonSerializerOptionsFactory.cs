using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Utils;

/// <summary>
/// Factory for creating JsonSerializerOptions with Anthropic-specific settings.
/// Extends LmCore's base factory with Anthropic API requirements.
/// </summary>
public static class AnthropicJsonSerializerOptionsFactory
{
    /// <summary>
    /// Creates JsonSerializerOptions with all LmCore converters plus Anthropic-specific settings.
    /// Uses camelCase naming policy as required by the Anthropic API.
    /// </summary>
    /// <param name="writeIndented">Whether to format JSON with indentation</param>
    /// <param name="caseInsensitive">Whether property names should be case-insensitive</param>
    /// <param name="allowTrailingCommas">Whether to allow trailing commas in JSON</param>
    /// <returns>JsonSerializerOptions with LmCore converters and Anthropic settings</returns>
    public static JsonSerializerOptions CreateForAnthropic(
        bool writeIndented = false,
        bool caseInsensitive = false,
        bool allowTrailingCommas = true
    )
    {
        // Anthropic API requires camelCase property naming
        return JsonSerializerOptionsFactory.CreateBase(
            writeIndented: writeIndented,
            namingPolicy: JsonNamingPolicy.CamelCase,
            caseInsensitive: caseInsensitive,
            allowTrailingCommas: allowTrailingCommas
        );
    }

    /// <summary>
    /// Creates JsonSerializerOptions optimized for Anthropic API production use.
    /// Uses the standard Anthropic configuration with compact formatting.
    /// </summary>
    public static JsonSerializerOptions CreateForProduction()
    {
        return CreateForAnthropic(writeIndented: false);
    }

    /// <summary>
    /// Creates JsonSerializerOptions optimized for Anthropic testing and debugging.
    /// Uses indented formatting for better readability.
    /// </summary>
    public static JsonSerializerOptions CreateForTesting()
    {
        return CreateForAnthropic(writeIndented: true);
    }

    /// <summary>
    /// Creates JsonSerializerOptions for cross-provider scenarios.
    /// Uses case-insensitive matching for robustness across different providers.
    /// </summary>
    public static JsonSerializerOptions CreateUniversal()
    {
        return CreateForAnthropic(caseInsensitive: true);
    }
}
