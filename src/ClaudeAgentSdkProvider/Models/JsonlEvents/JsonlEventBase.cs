using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Base class for all JSONL events from claude-agent-sdk CLI
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SummaryEvent), "summary")]
[JsonDerivedType(typeof(UserMessageEvent), "user")]
[JsonDerivedType(typeof(AssistantMessageEvent), "assistant")]
[JsonDerivedType(typeof(FileHistorySnapshotEvent), "file-history-snapshot")]
[JsonDerivedType(typeof(SystemInitEvent), "system")]
[JsonDerivedType(typeof(ResultEvent), "result")]
public abstract record JsonlEventBase
{
    // Note: The 'type' property is handled by JsonPolymorphic discriminator,
    // so we don't need an explicit property here. The discriminator will
    // automatically read/write the type during serialization/deserialization.
}
