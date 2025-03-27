using System.Collections.Generic;
using System.Text.Json.Nodes;

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
    /// Wraps a message in an envelope
    /// </summary>
    public static MessageEnvelope AsEnvelope(this IMessage message, JsonObject? metadata = null, string? addedBy = null)
    {
        // If it's already an envelope and we're not adding anything new, just return it
        if (message is MessageEnvelope existingEnvelope && 
            metadata == null && 
            addedBy == null)
        {
            return existingEnvelope;
        }
        
        // Otherwise create a new envelope
        return new MessageEnvelope(message, metadata, addedBy);
    }
    
    /// <summary>
    /// Tries to unwrap a message if it's an envelope, otherwise returns the original message
    /// </summary>
    public static IMessage TryUnwrap(this IMessage message)
    {
        return message is MessageEnvelope envelope ? envelope.Unwrap() : message;
    }
    
    /// <summary>
    /// Tries to unwrap a message to a specific type, returns null if not found
    /// </summary>
    public static T? TryUnwrapAs<T>(this IMessage message) where T : class, IMessage
    {
        if (message is T directMatch)
        {
            return directMatch;
        }
        
        if (message is MessageEnvelope envelope)
        {
            var unwrapped = envelope.Unwrap<T>();
            return unwrapped as T;
        }
        
        return null;
    }
    
    /// <summary>
    /// Checks if this message or any wrapped message is of the specified type
    /// </summary>
    public static bool ContainsMessageOfType<T>(this IMessage message) where T : IMessage
    {
        if (message is T)
        {
            return true;
        }
        
        if (message is MessageEnvelope envelope)
        {
            return envelope.InnerMessage is T || envelope.InnerMessage.ContainsMessageOfType<T>();
        }
        
        return false;
    }
} 