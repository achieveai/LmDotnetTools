using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     System initialization event from claude-agent-sdk CLI
///     Contains session info, available tools, MCP servers status, etc.
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
    public List<NamedRef>? Agents { get; init; }

    [JsonPropertyName("skills")]
    public List<NamedRef>? Skills { get; init; }

    [JsonPropertyName("plugins")]
    public List<NamedRef>? Plugins { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }
}

/// <summary>
///     MCP server status information
/// </summary>
public record McpServerStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>
///     Named reference emitted by claude-agent-sdk CLI for entries in
///     <c>system.init.plugins</c>, <c>system.init.skills</c>, and
///     <c>system.init.agents</c>. The CLI has historically alternated between
///     <c>string</c> and <c>{name, ...}</c> shapes for these fields
///     (see #42 / PR #43 for the plugin drift); <see cref="NamedRefJsonConverter"/>
///     accepts either at deserialisation time and <see cref="Extra"/> captures any
///     additional fields without forcing another schema chase.
/// </summary>
[JsonConverter(typeof(NamedRefJsonConverter))]
public record NamedRef
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
