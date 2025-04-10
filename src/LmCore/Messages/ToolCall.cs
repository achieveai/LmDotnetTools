using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public record struct ToolCall(string FunctionName, string FunctionArgs)
{
    public int? Index { get; init; }

    public string? ToolCallId { get; init; }
}
public record struct ToolCallResult(string? ToolCallId, string Result);

public record ToolCallUpdate
{
    public string? ToolCallId { get; init; }

    public int? Index { get; init; }

    public string? FunctionName { get; init; }

    public string? FunctionArgs { get; init; }
}