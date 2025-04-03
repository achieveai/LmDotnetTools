using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Configuration for an MCP client
/// </summary>
/// <param name="Id">Unique identifier for the client</param>
/// <param name="Name">Display name for the client</param>
/// <param name="TransportType">Transport type (e.g., StdIo, Http)</param>
/// <param name="TransportOptions">Options specific to the transport type</param>
public record McpClientConfiguration(
    string Id,
    string Name,
    string TransportType,
    Dictionary<string, string>? TransportOptions = null);

/// <summary>
/// Configuration for MCP middleware
/// </summary>
public class McpMiddlewareConfiguration
{
    /// <summary>
    /// Dictionary of MCP client configurations
    /// </summary>
    [JsonPropertyName("clients")]
    public Dictionary<string, McpClientConfiguration> Clients { get; set; } = new();
}
