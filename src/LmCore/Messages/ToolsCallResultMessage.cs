using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(ToolsCallResultMessageJsonConverter))]
public record ToolsCallResultMessage : IMessage
{
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; } = null;

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.User;

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; } = null;

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; } = null;

    [JsonPropertyName("tool_call_results")]
    public ImmutableList<ToolCallResult> ToolCallResults { get; init; } = [];

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    public static string? GetText()
    {
        return null;
    }

    public static BinaryData? GetBinary()
    {
        return null;
    }

    public static ToolCall? GetToolCalls()
    {
        return null;
    }

    public static IEnumerable<IMessage>? GetMessages()
    {
        return null;
    }

    // Factory method for creating a ToolsCallResultMessage with a single result
    public static ToolsCallResultMessage Create(
        ToolCall toolCall,
        string? result,
        Role role = Role.User,
        string? fromAgent = null,
        ImmutableDictionary<string, object>? metadata = null,
        string? generationId = null
    )
    {
        return new ToolsCallResultMessage
        {
            Role = role,
            FromAgent = fromAgent,
            Metadata = metadata,
            GenerationId = generationId,
            ToolCallResults = [new ToolCallResult(toolCall.ToolCallId, result ?? string.Empty)],
        };
    }

    // Factory method for creating a ToolsCallResultMessage with multiple results
    public static ToolsCallResultMessage Create(
        IEnumerable<(ToolCall toolCall, string? result)> results,
        Role role = Role.User,
        string? fromAgent = null,
        ImmutableDictionary<string, object>? metadata = null,
        string? generationId = null
    )
    {
        return new ToolsCallResultMessage
        {
            Role = role,
            FromAgent = fromAgent,
            Metadata = metadata,
            GenerationId = generationId,
            ToolCallResults = [.. results.Select(r => new ToolCallResult(r.toolCall.ToolCallId, r.result ?? string.Empty))],
        };
    }
}

public class ToolsCallResultMessageJsonConverter : ShadowPropertiesJsonConverter<ToolsCallResultMessage>
{
    protected override ToolsCallResultMessage CreateInstance()
    {
        return new ToolsCallResultMessage();
    }
}
