using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonDerivedType(typeof(TextMessage), "text")]
[JsonDerivedType(typeof(ImageMessage), "image")]
[JsonDerivedType(typeof(ToolsCallMessage), "tools_call")]
[JsonDerivedType(typeof(ToolsCallAggregateMessage), "tools_call_aggregate")]

public interface IMessage
{
    public string? FromAgent { get; }

    public Role Role { get; }

    public JsonObject? Metadata { get; }
    
    public string? GenerationId { get; }

    public ImmutableDictionary<string, object?>? GetMetaTools() => null;
}