using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

public sealed record CodexMcpServerConfig
{
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Args { get; init; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }

    [JsonPropertyName("enabled_tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EnabledTools { get; init; }

    [JsonPropertyName("disabled_tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DisabledTools { get; init; }

    [JsonPropertyName("tool_timeout_sec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToolTimeoutSec { get; init; }

    [JsonPropertyName("startup_timeout_sec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StartupTimeoutSec { get; init; }
}
