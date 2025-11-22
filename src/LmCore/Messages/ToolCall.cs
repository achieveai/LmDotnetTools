using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public record struct ToolCall(
    [property: JsonPropertyName("function_name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FunctionName,
    [property: JsonPropertyName("function_args")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FunctionArgs
)
{
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Index { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Order index of this tool call within its containing ToolCallMessage.
    /// Enables deterministic reconstruction of tool call order for KV cache optimization.
    /// </summary>
    [JsonPropertyName("toolCallIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ToolCallIdx { get; init; }
}

public record struct ToolCallResult(
    [property: JsonPropertyName("tool_call_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ToolCallId,
    [property: JsonPropertyName("result")] string Result
);

public record ToolCallUpdate
{
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Index { get; init; }

    [JsonPropertyName("function_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionName { get; init; }

    [JsonPropertyName("function_args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionArgs { get; init; }

    /// <summary>
    /// Structured JSON fragment updates generated from the function arguments
    /// </summary>
    [JsonPropertyName("json_update_fragments")]
    public IList<JsonFragmentUpdate>? JsonFragmentUpdates { get; init; }
}
