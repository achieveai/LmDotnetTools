using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Root configuration object for MCP servers, matching the <c>.mcp.json</c>
///     file shape used by the Claude Agent SDK.
/// </summary>
public record McpConfiguration
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; init; } = [];
}
