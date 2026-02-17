using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

public sealed record CodexBridgeInitOptions
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("approval_policy")]
    public string? ApprovalPolicy { get; init; }

    [JsonPropertyName("sandbox_mode")]
    public string? SandboxMode { get; init; }

    [JsonPropertyName("skip_git_repo_check")]
    public bool SkipGitRepoCheck { get; init; }

    [JsonPropertyName("network_access_enabled")]
    public bool NetworkAccessEnabled { get; init; }

    [JsonPropertyName("web_search_mode")]
    public string? WebSearchMode { get; init; }

    [JsonPropertyName("working_directory")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("additional_directories")]
    public IReadOnlyList<string>? AdditionalDirectories { get; init; }

    [JsonPropertyName("mcp_servers")]
    public IReadOnlyDictionary<string, CodexMcpServerConfig>? McpServers { get; init; }

    [JsonPropertyName("base_instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseInstructions { get; init; }

    [JsonPropertyName("developer_instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeveloperInstructions { get; init; }

    [JsonPropertyName("model_instructions_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelInstructionsFile { get; init; }

    [JsonPropertyName("dynamic_tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CodexDynamicToolSpec>? DynamicTools { get; init; }

    [JsonPropertyName("tool_bridge_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolBridgeMode { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; init; }
}

public sealed record CodexTurnEventEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("thread_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("turn_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }
}

public sealed record CodexDynamicToolSpec
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; init; }
}

public sealed record CodexDynamicToolCallRequest
{
    [JsonPropertyName("thread_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("turn_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }

    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; init; }

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

public sealed record CodexDynamicToolCallResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("content_items")]
    public IReadOnlyList<CodexDynamicToolContentItem> ContentItems { get; init; } = [];
}

public sealed record CodexDynamicToolContentItem
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }
}
