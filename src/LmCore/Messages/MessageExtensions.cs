using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Extension methods for working with message capabilities
/// </summary>
public static class MessageExtensions
{
    /// <summary>
    /// Gets all text content from a collection of messages that support text content
    /// </summary>
    public static IEnumerable<string> GetAllTextContent(this IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is ICanGetText textMessage)
            {
                var text = textMessage.GetText();
                if (text != null)
                {
                    yield return text;
                }
            }
        }
    }

    /// <summary>
    /// Gets all binary content from a collection of messages that support binary content
    /// </summary>
    public static IEnumerable<BinaryData> GetAllBinaryContent(this IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is ICanGetBinary binaryMessage)
            {
                var binary = binaryMessage.GetBinary();
                if (binary != null)
                {
                    yield return binary;
                }
            }
        }
    }

    /// <summary>
    /// Gets all tool calls from a collection of messages that support tool calls
    /// </summary>
    public static IEnumerable<ToolCall> GetAllToolCalls(this IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is ICanGetToolCalls toolCallMessage)
            {
                var toolCalls = toolCallMessage.GetToolCalls();
                if (toolCalls != null)
                {
                    foreach (var toolCall in toolCalls)
                    {
                        yield return toolCall;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks if a message can provide text content
    /// </summary>
    public static bool CanGetText(this IMessage message)
    {
        return message is ICanGetText && ((ICanGetText)message).GetText() != null;
    }

    /// <summary>
    /// Checks if a message can provide binary content
    /// </summary>
    public static bool CanGetBinary(this IMessage message)
    {
        return message is ICanGetBinary && ((ICanGetBinary)message).GetBinary() != null;
    }

    /// <summary>
    /// Checks if a message can provide tool calls
    /// </summary>
    public static bool CanGetToolCalls(this IMessage message)
    {
        return message is ICanGetToolCalls && ((ICanGetToolCalls)message).GetToolCalls() != null;
    }

    /// <summary>
    /// Gets all usage data from a collection of messages that support usage data
    /// </summary>
    public static IEnumerable<Usage> GetAllUsage(this IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is ICanGetUsage usageMessage)
            {
                var usage = usageMessage.GetUsage();
                if (usage != null)
                {
                    yield return usage;
                }
            }
        }
    }

    /// <summary>
    /// Checks if a message can provide usage information
    /// </summary>
    public static bool CanGetUsage(this IMessage message)
    {
        return message is ICanGetUsage && ((ICanGetUsage)message).GetUsage() != null;
    }

    /// <summary>
    /// Transforms a ToolsCallAggregateMessage to natural language format with XML-style tool calls.
    /// Returns the message unchanged if it's not a ToolsCallAggregateMessage or if transformation fails.
    /// </summary>
    /// <param name="message">The message to transform</param>
    /// <returns>A TextMessage with natural tool use format, or the original message if transformation is not applicable</returns>
    public static IMessage ToNaturalToolUse(this IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            if (message is ToolsCallAggregateMessage aggregateMessage)
            {
                return ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);
            }
        }
        catch
        {
            // Graceful fallback - return original message if transformation fails
        }

        return message;
    }

    /// <summary>
    /// Transforms all ToolsCallAggregateMessage instances in a collection to natural language format.
    /// Messages that are not ToolsCallAggregateMessage or fail transformation are left unchanged.
    /// </summary>
    /// <param name="messages">The collection of messages to transform</param>
    /// <returns>A collection with ToolsCallAggregateMessage instances transformed to natural format</returns>
    public static IEnumerable<IMessage> ToNaturalToolUse(this IEnumerable<IMessage> messages)
    {
        if (messages == null)
        {
            yield break;
        }

        foreach (var message in messages)
        {
            yield return message.ToNaturalToolUse();
        }
    }

    /// <summary>
    /// Combines a sequence of messages into a single TextMessage with natural tool use format.
    /// ToolsCallAggregateMessage instances are transformed to XML format and combined with surrounding text.
    /// </summary>
    /// <param name="messages">The sequence of messages to combine</param>
    /// <returns>A single TextMessage containing all content in natural format</returns>
    public static TextMessage CombineAsNaturalToolUse(this IEnumerable<IMessage> messages)
    {
        if (messages == null)
        {
            return new TextMessage { Text = string.Empty, Role = Role.Assistant };
        }

        try
        {
            return ToolsCallAggregateTransformer.CombineMessageSequence(messages);
        }
        catch
        {
            // Graceful fallback - combine text content from messages that support it
            var textContent = string.Join(
                Environment.NewLine,
                messages
                    .Where(m => m is ICanGetText)
                    .Cast<ICanGetText>()
                    .Select(m => m.GetText())
                    .Where(text => !string.IsNullOrEmpty(text))
            );

            return new TextMessage { Text = textContent, Role = messages.FirstOrDefault()?.Role ?? Role.Assistant };
        }
    }

    /// <summary>
    /// Checks if a message sequence contains any ToolsCallAggregateMessage instances that can be transformed.
    /// </summary>
    /// <param name="messages">The sequence of messages to check</param>
    /// <returns>True if the sequence contains transformable ToolsCallAggregateMessage instances</returns>
    public static bool ContainsTransformableToolCalls(this IEnumerable<IMessage> messages)
    {
        return messages == null ? false : messages.Any(m => m is ToolsCallAggregateMessage);
    }

    /// <summary>
    /// Checks if a single message is a ToolsCallAggregateMessage that can be transformed.
    /// </summary>
    /// <param name="message">The message to check</param>
    /// <returns>True if the message is a transformable ToolsCallAggregateMessage</returns>
    public static bool IsTransformableToolCall(this IMessage message)
    {
        return message is ToolsCallAggregateMessage;
    }

    /// <summary>
    /// Updates the message with run, parent run, and thread IDs from the options.
    /// </summary>
    public static IMessage WithIds(this IMessage message, GenerateReplyOptions? options)
    {
        if (options == null)
        {
            return message;
        }

        return message.WithIds(options.RunId, options.ParentRunId, options.ThreadId);
    }

    /// <summary>
    /// Updates the message with run, parent run, and thread IDs.
    /// </summary>
    public static IMessage WithIds(this IMessage message, string? runId, string? parentRunId, string? threadId)
    {
        if (message is TextMessage textMessage)
        {
            return textMessage with { RunId = runId, ParentRunId = parentRunId, ThreadId = threadId };
        }
        else if (message is ToolsCallMessage toolsCallMessage)
        {
            return toolsCallMessage with { RunId = runId, ParentRunId = parentRunId, ThreadId = threadId };
        }
        else if (message is ReasoningMessage reasoningMessage)
        {
            return reasoningMessage with { RunId = runId, ParentRunId = parentRunId, ThreadId = threadId };
        }
        else if (message is TextUpdateMessage textUpdateMessage)
        {
            return textUpdateMessage with { RunId = runId, ParentRunId = parentRunId, ThreadId = threadId };
        }
        else if (message is ToolsCallUpdateMessage toolsCallUpdateMessage)
        {
            return toolsCallUpdateMessage with { RunId = runId, ParentRunId = parentRunId, ThreadId = threadId };
        }
        else if (message is ReasoningUpdateMessage reasoningUpdateMessage)
        {
            return reasoningUpdateMessage with { RunId = runId, ParentRunId = parentRunId, ThreadId = threadId };
        }

        return message;
    }
}
