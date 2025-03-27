using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public record struct ToolCall(string FunctionName, string FunctionArgs)
{
    public string? ToolCallId { get; init; }
}

public record struct ToolCallResult(ToolCall ToolCall, string Result);

public record ToolCallUpdate
{
    public string? FunctionName { get; init; }

    public string? FunctionArgs { get; init; }
}