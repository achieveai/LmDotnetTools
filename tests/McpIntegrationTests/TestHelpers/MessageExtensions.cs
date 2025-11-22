using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;

/// <summary>
/// Extension methods for working with messages in tests
/// </summary>
public static class MessageExtensions
{
    /// <summary>
    /// Extracts text content from various message types
    /// </summary>
    /// <param name="message">The message to extract text from</param>
    /// <returns>The extracted text or null if the message is null</returns>
    public static string? GetText(this IMessage? message)
    {
        if (message == null)
        {
            return null;
        }

        return message switch
        {
            TextMessage textMessage => textMessage.Text,
            ToolsCallResultMessage toolCallResult => string.Join(
                Environment.NewLine,
                toolCallResult.ToolCallResults.Select(tcr => tcr.Result)
            ),
            ToolsCallAggregateMessage toolCallAggregate => string.Join(
                Environment.NewLine,
                toolCallAggregate.ToolsCallResult.ToolCallResults.Select(tcr => tcr.Result)
            ),
            _ => message.ToString(),
        };
    }
}
