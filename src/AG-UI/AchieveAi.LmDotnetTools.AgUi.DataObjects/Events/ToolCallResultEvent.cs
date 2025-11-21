using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Event representing the result of a tool/function call execution
/// Delivers the return value from the tool after it completes
/// </summary>
public sealed record ToolCallResultEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => AgUiEventTypes.TOOL_CALL_RESULT;

    /// <summary>
    /// Unique identifier linking this result to the corresponding tool call
    /// Matches the toolCallId from TOOL_CALL_START event
    /// </summary>
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the tool/function that was executed
    /// </summary>
    [JsonPropertyName("toolName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    /// <summary>
    /// Result content from the tool execution
    /// Can be JSON object, string, or other serializable data
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Whether the tool execution was successful
    /// </summary>
    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Error message if the tool execution failed
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }
}
