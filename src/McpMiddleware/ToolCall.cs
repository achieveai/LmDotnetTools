using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Represents a tool call
/// </summary>
public class ToolCall
{
    /// <summary>
    /// Gets or sets the ID of the tool call
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type of the tool call
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the function
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the arguments for the function
    /// </summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}
