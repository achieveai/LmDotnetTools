using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Represents the response from calling a tool on an MCP server
/// </summary>
public class CallToolResponse
{
    /// <summary>
    /// Gets or sets the content of the response
    /// </summary>
    [JsonPropertyName("content")]
    public IList<Content>? Content { get; set; }
}

/// <summary>
/// Represents a content item in a tool response
/// </summary>
public class Content
{
    /// <summary>
    /// Gets or sets the type of the content
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text of the content
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
