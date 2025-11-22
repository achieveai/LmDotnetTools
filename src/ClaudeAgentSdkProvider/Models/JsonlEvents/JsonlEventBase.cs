using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
/// Base class for all JSONL events from claude-agent-sdk CLI
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SummaryEvent), "summary")]
[JsonDerivedType(typeof(UserMessageEvent), "user")]
[JsonDerivedType(typeof(AssistantMessageEvent), "assistant")]
[JsonDerivedType(typeof(FileHistorySnapshotEvent), "file-history-snapshot")]
public abstract record JsonlEventBase
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}
