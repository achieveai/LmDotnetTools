using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
/// System initialization event from claude-agent-sdk CLI
/// Contains session info, available tools, MCP servers status, etc.
/// </summary>
public record SystemInitEvent : JsonlEventBase
{
    [JsonPropertyName("subtype")]
    public string Subtype { get; init; } = "init";

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; init; }

    [JsonPropertyName("mcp_servers")]
    public List<McpServerStatus>? McpServers { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    [JsonPropertyName("slash_commands")]
    public List<string>? SlashCommands { get; init; }

    [JsonPropertyName("api_key_source")]
    public string? ApiKeySource { get; init; }

    [JsonPropertyName("claude_code_version")]
    public string? ClaudeCodeVersion { get; init; }

    [JsonPropertyName("output_style")]
    public string? OutputStyle { get; init; }

    [JsonPropertyName("agents")]
    public List<string>? Agents { get; init; }

    [JsonPropertyName("skills")]
    public List<string>? Skills { get; init; }

    [JsonPropertyName("plugins")]
    public List<string>? Plugins { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }
}

/// <summary>
/// MCP server status information
/// </summary>
public record McpServerStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
