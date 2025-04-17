using System.Collections.Generic;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;

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
    public static bool CanGetText(this IMessage message) => 
        message is ICanGetText && ((ICanGetText)message).GetText() != null;

    /// <summary>
    /// Checks if a message can provide binary content
    /// </summary>
    public static bool CanGetBinary(this IMessage message) => 
        message is ICanGetBinary && ((ICanGetBinary)message).GetBinary() != null;

    /// <summary>
    /// Checks if a message can provide tool calls
    /// </summary>
    public static bool CanGetToolCalls(this IMessage message) => 
        message is ICanGetToolCalls && ((ICanGetToolCalls)message).GetToolCalls() != null;

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
    public static bool CanGetUsage(this IMessage message) => 
        message is ICanGetUsage && ((ICanGetUsage)message).GetUsage() != null;
} 