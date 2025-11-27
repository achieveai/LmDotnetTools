using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Configuration for an MCP (Model Context Protocol) server
/// </summary>
public record McpServerConfig
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "stdio";

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("args")]
    public required List<string> Args { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }
}

/// <summary>
///     Root configuration object for MCP servers
/// </summary>
public record McpConfiguration
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; init; } = [];
}
