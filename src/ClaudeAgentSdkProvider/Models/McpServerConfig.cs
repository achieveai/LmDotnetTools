using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Configuration for an MCP (Model Context Protocol) server.
///     Supports both stdio (command-based) and http (URL-based) transports.
/// </summary>
public record McpServerConfig
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "stdio";

    // For stdio type
    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Args { get; init; }

    // For http type
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; init; }

    // Shared
    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>
    ///     Creates a stdio-based MCP server configuration.
    /// </summary>
    public static McpServerConfig CreateStdio(
        string command,
        List<string> args,
        Dictionary<string, string>? env = null)
    {
        return new McpServerConfig { Type = "stdio", Command = command, Args = args, Env = env };
    }

    /// <summary>
    ///     Creates an HTTP-based MCP server configuration.
    /// </summary>
    public static McpServerConfig CreateHttp(
        string url,
        Dictionary<string, string>? headers = null)
    {
        return new McpServerConfig { Type = "http", Url = url, Headers = headers };
    }
}

/// <summary>
///     Root configuration object for MCP servers
/// </summary>
public record McpConfiguration
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; init; } = [];
}
