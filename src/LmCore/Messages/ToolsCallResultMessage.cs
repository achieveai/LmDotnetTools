using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public record ToolsCallResultMessage : IMessage
{
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; } = null;

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.User;
    
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Metadata { get; init; } = null;

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; } = null;

    [JsonPropertyName("tool_call_results")]
    public ImmutableList<ToolCallResult> ToolCallResults { get; init; } = ImmutableList<ToolCallResult>.Empty;

    public string? GetText() => null;

    public BinaryData? GetBinary() => null;

    public ToolCall? GetToolCalls() => null;

    public IEnumerable<IMessage>? GetMessages() => null;

    // Factory method for creating a ToolsCallResultMessage with a single result
    public static ToolsCallResultMessage Create(ToolCall toolCall, string? result, Role role = Role.User, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        return new ToolsCallResultMessage
        {
            Role = role,
            FromAgent = fromAgent,
            Metadata = metadata,
            GenerationId = generationId,
            ToolCallResults = ImmutableList.Create(new ToolCallResult(toolCall.ToolCallId, result ?? string.Empty))
        };
    }

    // Factory method for creating a ToolsCallResultMessage with multiple results
    public static ToolsCallResultMessage Create(IEnumerable<(ToolCall toolCall, string? result)> results, Role role = Role.User, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        return new ToolsCallResultMessage
        {
            Role = role,
            FromAgent = fromAgent,
            Metadata = metadata,
            GenerationId = generationId,
            ToolCallResults = results.Select(r => new ToolCallResult(r.toolCall.ToolCallId, r.result ?? string.Empty)).ToImmutableList()
        };
    }
}