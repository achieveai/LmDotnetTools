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

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; init; }
}

public sealed record CodexBridgeRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexBridgeInitOptions? Options { get; init; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; init; }
}

public sealed record CodexBridgeResponse
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    [JsonPropertyName("event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Event { get; init; }

    [JsonPropertyName("thread_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
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
}
