using AchieveAi.LmDotnetTools.LmCore.Messages;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Converter between LmCore.Messages.ToolCall and McpMiddleware.ToolCall
/// </summary>
public static class ToolCallConverter
{
    /// <summary>
    /// Converts a collection of LmCore.Messages.ToolCall to McpMiddleware.ToolCall
    /// </summary>
    /// <param name="toolCalls">The tool calls to convert</param>
    /// <returns>The converted tool calls</returns>
    public static IEnumerable<ToolCall> Convert(IEnumerable<LmCore.Messages.ToolCall> toolCalls)
    {
        if (toolCalls == null)
        {
            yield break;
        }

        foreach (var toolCall in toolCalls)
        {
            yield return new ToolCall
            {
                Id = toolCall.ToolCallId ?? string.Empty,
                Type = "function", // Assuming all tool calls are function calls
                Name = toolCall.FunctionName,
                Arguments = !string.IsNullOrEmpty(toolCall.FunctionArgs)
                    ? JsonSerializer.Deserialize<JsonElement>(toolCall.FunctionArgs)
                    : null
            };
        }
    }
}
