using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(IMessageJsonConverter))]
// Use registrations inside the IMessageJsonConverter
// [JsonDerivedType(typeof(ToolsCallMessage), "tools_call_message")]
// [JsonDerivedType(typeof(TextUpdateMessage), "text_update_message")]
// [JsonDerivedType(typeof(ToolsCallResultMessage), "tools_call_result_message")]
// [JsonDerivedType(typeof(ToolsCallUpdateMessage), "tools_call_update_message")]
// [JsonDerivedType(typeof(ToolsCallAggregateMessage), "tools_call_aggregate_message")]
// [JsonDerivedType(typeof(UsageMessage), "usage_message")]
public interface IMessage
{
    public Role Role { get; }

    public string? FromAgent { get; }

    public string? GenerationId { get; }

    public ImmutableDictionary<string, object>? Metadata { get; }

    /// <summary>
    /// Thread identifier for conversation continuity (used with AG-UI protocol)
    /// Maps to a persistent conversation thread across multiple runs
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId => null;

    /// <summary>
    /// Run identifier for this specific execution (used with AG-UI protocol)
    /// Tracks individual runs within a conversation thread
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId => null;

    public ImmutableDictionary<string, object?>? GetMetaTools() => null;
}
