using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// A message that can contain a mix of different content types (text, binary data, tool call results)
/// </summary>
public record CompositeMessage : IMessage, ICanGetText, ICanGetBinary, ICanGetToolCalls
{
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;
    
    public JsonObject? Metadata { get; init; } = null;

    public string? GenerationId { get; init; } = null;

    public ImmutableList<Union<string, BinaryData, ToolCallResult>> Contents { get; init; } = 
        ImmutableList<Union<string, BinaryData, ToolCallResult>>.Empty;

    public string? GetText()
    {
        // Only return text if there's exactly one text item
        int textCount = 0;
        string? textContent = null;

        foreach (var content in Contents)
        {
            if (content.Is<string>())
            {
                textCount++;
                textContent = content.Get<string>();
                if (textCount > 1)
                {
                    return null; // More than one text item, return null
                }
            }
        }

        return textCount == 1 ? textContent : null;
    }

    public BinaryData? GetBinary()
    {
        // Only return binary data if there's exactly one binary item
        int binaryCount = 0;
        BinaryData? binaryContent = null;

        foreach (var content in Contents)
        {
            if (content.Is<BinaryData>())
            {
                binaryCount++;
                binaryContent = content.Get<BinaryData>();
                if (binaryCount > 1)
                {
                    return null; // More than one binary item, return null
                }
            }
        }

        return binaryCount == 1 ? binaryContent : null;
    }

    public ToolCall? GetToolCalls()
    {
        // Only return tool call if there's exactly one tool call result item
        int toolCallCount = 0;
        ToolCall? toolCall = null;

        foreach (var content in Contents)
        {
            if (content.Is<ToolCallResult>())
            {
                toolCallCount++;
                toolCall = content.Get<ToolCallResult>().ToolCall;
                if (toolCallCount > 1)
                {
                    return null; // More than one tool call result, return null
                }
            }
        }

        return toolCallCount == 1 ? toolCall : null;
    }

    // Implementation for ICanGetToolCalls
    IEnumerable<ToolCall>? ICanGetToolCalls.GetToolCalls()
    {
        var toolCalls = new List<ToolCall>();
        
        foreach (var content in Contents)
        {
            if (content.Is<ToolCallResult>())
            {
                toolCalls.Add(content.Get<ToolCallResult>().ToolCall);
            }
        }
        
        return toolCalls.Count > 0 ? toolCalls : null;
    }

    public IEnumerable<IMessage>? GetMessages() => null;

    // Factory methods for creating CompositeMessages

    /// <summary>
    /// Creates a CompositeMessage containing a single text content
    /// </summary>
    public static CompositeMessage FromText(string text, Role role = Role.Assistant, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        return new CompositeMessage
        {
            FromAgent = fromAgent,
            Role = role,
            Metadata = metadata,
            GenerationId = generationId,
            Contents = ImmutableList.Create(new Union<string, BinaryData, ToolCallResult>(text))
        };
    }

    /// <summary>
    /// Creates a CompositeMessage containing a single binary data content
    /// </summary>
    public static CompositeMessage FromBinary(BinaryData data, Role role = Role.Assistant, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        return new CompositeMessage
        {
            FromAgent = fromAgent,
            Role = role,
            Metadata = metadata,
            GenerationId = generationId,
            Contents = ImmutableList.Create(new Union<string, BinaryData, ToolCallResult>(data))
        };
    }

    /// <summary>
    /// Creates a CompositeMessage containing a single tool call result
    /// </summary>
    public static CompositeMessage FromToolCallResult(ToolCallResult result, Role role = Role.User, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        return new CompositeMessage
        {
            FromAgent = fromAgent,
            Role = role,
            Metadata = metadata,
            GenerationId = generationId,
            Contents = ImmutableList.Create(new Union<string, BinaryData, ToolCallResult>(result))
        };
    }

    /// <summary>
    /// Creates a CompositeMessage with multiple text contents
    /// </summary>
    public static CompositeMessage CreateFromTexts(IEnumerable<string> texts, Role role = Role.Assistant, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        var builder = ImmutableList.CreateBuilder<Union<string, BinaryData, ToolCallResult>>();
        foreach (var text in texts)
        {
            builder.Add(new Union<string, BinaryData, ToolCallResult>(text));
        }

        return new CompositeMessage
        {
            FromAgent = fromAgent,
            Role = role,
            Metadata = metadata,
            GenerationId = generationId,
            Contents = builder.ToImmutable()
        };
    }

    /// <summary>
    /// Creates a CompositeMessage with multiple binary data contents
    /// </summary>
    public static CompositeMessage CreateFromBinaries(IEnumerable<BinaryData> binaries, Role role = Role.Assistant, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        var builder = ImmutableList.CreateBuilder<Union<string, BinaryData, ToolCallResult>>();
        foreach (var binary in binaries)
        {
            builder.Add(new Union<string, BinaryData, ToolCallResult>(binary));
        }

        return new CompositeMessage
        {
            FromAgent = fromAgent,
            Role = role,
            Metadata = metadata,
            GenerationId = generationId,
            Contents = builder.ToImmutable()
        };
    }

    /// <summary>
    /// Creates a CompositeMessage with multiple tool call results
    /// </summary>
    public static CompositeMessage CreateFromToolCallResults(IEnumerable<ToolCallResult> results, Role role = Role.User, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        var builder = ImmutableList.CreateBuilder<Union<string, BinaryData, ToolCallResult>>();
        foreach (var result in results)
        {
            builder.Add(new Union<string, BinaryData, ToolCallResult>(result));
        }

        return new CompositeMessage
        {
            FromAgent = fromAgent,
            Role = role,
            Metadata = metadata,
            GenerationId = generationId,
            Contents = builder.ToImmutable()
        };
    }
    
    /// <summary>
    /// Creates a CompositeMessage by extracting content from an array of different message types
    /// </summary>
    public static CompositeMessage CreateFromMessages(IEnumerable<IMessage> messages, Role? role = null, string? fromAgent = null, JsonObject? metadata = null, string? generationId = null)
    {
        var builder = ImmutableList.CreateBuilder<Union<string, BinaryData, ToolCallResult>>();
        Role effectiveRole = Role.None;
        string? effectiveFromAgent = null;
        JsonObject? effectiveMetadata = metadata?.DeepClone() as JsonObject;
        
        foreach (var message in messages)
        {
            // Use the first message's role and fromAgent if not provided
            if (effectiveRole == Role.None && role == null)
            {
                effectiveRole = message.Role;
            }
            
            if (effectiveFromAgent == null && fromAgent == null)
            {
                effectiveFromAgent = message.FromAgent;
            }
            
            // Merge metadata from each message
            if (message.Metadata != null)
            {
                if (effectiveMetadata == null)
                {
                    effectiveMetadata = message.Metadata.DeepClone() as JsonObject;
                }
                else
                {
                    // Merge metadata, with message's metadata taking precedence
                    foreach (var prop in message.Metadata)
                    {
                        effectiveMetadata[prop.Key] = prop.Value?.DeepClone();
                    }
                }
            }
            
            // Extract text if available
            string? text = (message as ICanGetText)?.GetText();
            if (text != null)
            {
                builder.Add(new Union<string, BinaryData, ToolCallResult>(text));
            }
            
            // Extract binary data if available
            BinaryData? binary = (message as ICanGetBinary)?.GetBinary();
            if (binary != null)
            {
                builder.Add(new Union<string, BinaryData, ToolCallResult>(binary));
            }
            
            // Extract tool call if available
            var toolCalls = (message as ICanGetToolCalls)?.GetToolCalls();
            if (toolCalls != null)
            {
                foreach (var toolCall in toolCalls)
                {
                    builder.Add(new Union<string, BinaryData, ToolCallResult>(new ToolCallResult(toolCall, string.Empty)));
                }
            }
        }
        
        return new CompositeMessage
        {
            FromAgent = effectiveFromAgent ?? fromAgent,
            Role = role ?? effectiveRole,
            Metadata = effectiveMetadata,
            GenerationId = generationId,
            Contents = builder.ToImmutable()
        };
    }
}
