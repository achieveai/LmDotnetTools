using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Utils;

/// <summary>
///     Factory for creating JsonSerializerOptions with Anthropic-specific settings.
///     Extends LmCore's base factory with Anthropic API requirements.
/// </summary>
public static class AnthropicJsonSerializerOptionsFactory
{
    /// <summary>
    ///     Creates JsonSerializerOptions with all LmCore converters plus Anthropic-specific settings.
    ///     Uses camelCase naming policy as required by the Anthropic API.
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
        var options = JsonSerializerOptionsFactory.CreateBase(
            writeIndented,
            JsonNamingPolicy.CamelCase,
            caseInsensitive,
            allowTrailingCommas
        );

        // Register StableJsonElementConverter globally so that all JsonElement properties
        // are eagerly cloned during deserialization. This prevents stale references when
        // SSE streaming disposes the per-line JsonDocument after each event.
        options.Converters.Add(new StableJsonElementConverter());

        return options;
    }

    /// <summary>
    ///     Creates JsonSerializerOptions optimized for Anthropic API production use.
    ///     Uses the standard Anthropic configuration with compact formatting.
    /// </summary>
    public static JsonSerializerOptions CreateForProduction()
    {
        return CreateForAnthropic(false);
    }

    /// <summary>
    ///     Creates JsonSerializerOptions optimized for Anthropic testing and debugging.
    ///     Uses indented formatting for better readability.
    /// </summary>
    public static JsonSerializerOptions CreateForTesting()
    {
        return CreateForAnthropic(true);
    }

    /// <summary>
    ///     Creates JsonSerializerOptions for cross-provider scenarios.
    ///     Uses case-insensitive matching for robustness across different providers.
    /// </summary>
    public static JsonSerializerOptions CreateUniversal()
    {
        return CreateForAnthropic(caseInsensitive: true);
    }
}
