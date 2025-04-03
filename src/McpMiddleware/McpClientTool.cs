using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Represents a tool available from an MCP server
/// </summary>
public class McpClientTool
{
    /// <summary>
    /// Gets or sets the name of the tool
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the tool
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the input schema for the tool
    /// </summary>
    [JsonPropertyName("inputSchema")]
    public object? InputSchema { get; set; }
}
