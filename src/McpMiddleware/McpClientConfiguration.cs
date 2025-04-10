using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Configuration for MCP middleware
/// </summary>
public class McpMiddlewareConfiguration
{
    /// <summary>
    /// Dictionary of MCP client configurations
    /// </summary>
    [JsonPropertyName("clients")]
    public Dictionary<string, McpServerConfig> Clients { get; set; } = new();
}
