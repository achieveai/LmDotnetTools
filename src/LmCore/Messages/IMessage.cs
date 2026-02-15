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
    Role Role { get; }

    string? FromAgent { get; }

    string? GenerationId { get; }

    ImmutableDictionary<string, object>? Metadata { get; }

    /// <summary>
    ///     Run identifier for this specific execution (used with AG-UI protocol)
    ///     Tracks individual runs within a conversation thread
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RunId => null;

    /// <summary>
    ///     Parent Run identifier for branching/time travel (creates git-like lineage)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentRunId => null;

    /// <summary>
    ///     Thread identifier for conversation continuity (used with AG-UI protocol)
    ///     Maps to a persistent conversation thread across multiple runs
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ThreadId => null;

    /// <summary>
    ///     Order index of this message within its generation (same GenerationId)
    ///     Enables deterministic reconstruction of message order for KV cache optimization
    ///     Restarts at 0 for each new generation. Null for messages without ordering (e.g., user messages)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MessageOrderIdx => null;

    ImmutableDictionary<string, object?>? GetMetaTools()
    {
        return null;
    }
}
