using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs;

/// <summary>
///     Configuration options for an agent run
/// </summary>
public record RunConfiguration
{
    /// <summary>
    ///     Temperature for LLM sampling (0.0 to 2.0)
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>
    ///     Maximum number of tokens to generate
    /// </summary>
    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }

    /// <summary>
    ///     Model identifier (e.g., "gpt-4", "claude-3-opus")
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>
    ///     List of enabled tool names
    /// </summary>
    [JsonPropertyName("enabledTools")]
    public List<string>? EnabledTools { get; init; }

    /// <summary>
    ///     Additional model-specific parameters
    /// </summary>
    [JsonPropertyName("modelParameters")]
    public Dictionary<string, object>? ModelParameters { get; init; }
}
