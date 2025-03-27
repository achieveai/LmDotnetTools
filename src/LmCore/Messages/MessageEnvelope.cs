using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Envelope that wraps any message type, providing additional metadata or context
/// while preserving the original message's capabilities.
/// </summary>
public class MessageEnvelope : IMessage, ICanGetText, ICanGetBinary, ICanGetToolCalls, ICanGetMessages
{
    /// <summary>
    /// The wrapped message
    /// </summary>
    public IMessage InnerMessage { get; }
    
    /// <summary>
    /// Optional: Additional metadata that can be attached to the envelope itself
    /// </summary>
    public JsonObject? EnvelopeMetadata { get; init; }
    
    /// <summary>
    /// Optional: Tracks which system/middleware added this envelope
    /// </summary>
    public string? AddedBy { get; init; }
    
    /// <summary>
    /// Creates a new MessageEnvelope wrapping the specified message
    /// </summary>
    public MessageEnvelope(IMessage message)
    {
        InnerMessage = message;
    }
    
    /// <summary>
    /// Creates a new MessageEnvelope with custom metadata
    /// </summary>
    public MessageEnvelope(IMessage message, JsonObject? envelopeMetadata, string? addedBy = null)
    {
        InnerMessage = message;
        EnvelopeMetadata = envelopeMetadata;
        AddedBy = addedBy;
    }
    
    // IMessage implementation - delegate to inner message
    
    public string? FromAgent => InnerMessage.FromAgent;
    
    public Role Role => InnerMessage.Role;
    
    public JsonObject? Metadata => InnerMessage.Metadata;
    
    public string? GenerationId => InnerMessage.GenerationId;
    
    // Capability implementations
    
    public string? GetText()
    {
        // If inner message implements ICanGetText, delegate to it
        if (InnerMessage is ICanGetText textMessage)
        {
            return textMessage.GetText();
        }
        
        // Otherwise, delegate to the standard IMessage.GetText
        return null;
    }
    
    public BinaryData? GetBinary()
    {
        // If inner message implements ICanGetBinary, delegate to it
        if (InnerMessage is ICanGetBinary binaryMessage)
        {
            return binaryMessage.GetBinary();
        }
        
        // Otherwise, delegate to the standard IMessage.GetBinary
        return null;
    }
    
    IEnumerable<ToolCall>? ICanGetToolCalls.GetToolCalls()
    {
        // If inner message implements ICanGetToolCalls, delegate to it
        if (InnerMessage is ICanGetToolCalls toolCallMessage)
        {
            return toolCallMessage.GetToolCalls();
        }
        
        return null;
    }
    
    public IEnumerable<IMessage>? GetMessages()
    {
        // If inner message implements ICanGetMessages, delegate to it
        if (InnerMessage is ICanGetMessages messageContainer)
        {
            return messageContainer.GetMessages();
        }
        
        // Otherwise, delegate to the standard IMessage.GetMessages
        return null;
    }
    
    /// <summary>
    /// Unwraps the envelope chain until reaching a non-envelope message or a specific type
    /// </summary>
    /// <typeparam name="T">Optional specific message type to look for</typeparam>
    /// <returns>The innermost non-envelope message or the first message of type T</returns>
    public IMessage Unwrap<T>() where T : IMessage
    {
        IMessage current = this;
        
        while (current is MessageEnvelope envelope)
        {
            // If we're looking for a specific type and found it, return it
            if (current is T)
            {
                return current;
            }
            
            current = envelope.InnerMessage;
            
            // If the inner message is the specific type we're looking for, return it
            if (current is T)
            {
                return current;
            }
        }
        
        return current;
    }
    
    /// <summary>
    /// Unwraps the envelope chain until reaching a non-envelope message
    /// </summary>
    /// <returns>The innermost non-envelope message</returns>
    public IMessage Unwrap()
    {
        IMessage current = this;
        
        while (current is MessageEnvelope envelope)
        {
            current = envelope.InnerMessage;
        }
        
        return current;
    }
    
    /// <summary>
    /// Creates a new envelope with the same envelope properties but wrapping a different message
    /// </summary>
    public MessageEnvelope WithMessage(IMessage newInnerMessage)
    {
        return new MessageEnvelope(newInnerMessage, EnvelopeMetadata, AddedBy);
    }
} 