using System.Collections.Generic;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using System.Text.Json;
using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public record OpenMessage
{
    public string CompletionId { get; init; } = string.Empty;

    public required ChatMessage ChatMessage { get; init; }

    public OpenUsage? Usage { get; init; }

    public IMessage ToMessage()
    {
        var baseMessage = ChatMessage.ToMessage(ChatMessage.Name);
        
        // Create metadata with OpenMessage-specific properties
        var metadata = baseMessage.Metadata?.DeepClone() as JsonObject ?? new JsonObject();
        metadata["completion_id"] = CompletionId;
        if (Usage != null)
        {
            metadata["usage"] = JsonSerializer.SerializeToNode(Usage);
        }
        
        // Update the message with the metadata containing OpenMessage properties
        if (baseMessage is TextMessage textMessage)
        {
            return textMessage with { Metadata = metadata };
        }
        else if (baseMessage is ToolsCallMessage toolCallMessage)
        {
            return toolCallMessage with { Metadata = metadata };
        }
        else if (baseMessage is ImageMessage imageMessage)
        {
            // Create a new instance since ImageMessage is not a record type
            return new ImageMessage
            {
                FromAgent = imageMessage.FromAgent,
                Role = imageMessage.Role,
                Metadata = metadata,
                GenerationId = imageMessage.GenerationId,
                ImageData = imageMessage.ImageData
            };
        }
        else if (baseMessage is CompositeMessage compositeMessage)
        {
            return compositeMessage with { Metadata = metadata };
        }
        
        return baseMessage;
    }

    public IMessage ToOpenMessage()
    {
        var baseMessage = ChatMessage.ToMessage(ChatMessage.Name);

        // Fill in the OpenMessage specific fields
        if (baseMessage is ToolsCallMessage toolCallMessage)
        {
            return new OpenToolMessage
            {
                ToolCalls = toolCallMessage.ToolCalls.ToList(),
                CompletionId = CompletionId,
                FromAgent = ChatMessage.Name,
                Role = toolCallMessage.Role,
                Usage = Usage
            };
        }
        else if (baseMessage is TextMessage textMessage)
        {
            return new OpenTextMessage
            {
                Text = textMessage.Text,
                Role = textMessage.Role,
                FromAgent = ChatMessage.Name,
                CompletionId = CompletionId,
                Usage = Usage
            };
        }

        return baseMessage;
    }

    public IMessage ToStreamingMessage()
    {
        var baseMessage = ChatMessage.ToStreamingMessage(ChatMessage.Name);

        // Fill in the OpenMessage specific fields
        if (baseMessage is ToolsCallMessage toolCallMessage)
        {
            return new OpenToolMessage
            {
                ToolCalls = toolCallMessage.ToolCalls.ToList(),
                CompletionId = CompletionId,
                FromAgent = ChatMessage.Name,
                Role = toolCallMessage.Role,
                Usage = Usage
            };
        }
        else if (baseMessage is TextMessage textMessage)
        {
            // For streaming text, return a TextUpdateMessage
            return new TextUpdateMessage
            {
                Text = textMessage.Text,
                Role = textMessage.Role,
                FromAgent = ChatMessage.Name,
                GenerationId = CompletionId,
                Metadata = new JsonObject
                {
                    ["completion_id"] = CompletionId,
                    ["is_streaming"] = true,
                    ["usage"] = Usage != null ? JsonSerializer.SerializeToNode(Usage) : null
                }
            };
        }

        return baseMessage;
    }
}

public record OpenUsage
{
    public required string? ModelId { get; init; }

    public required int CompletionTokens { get; init; }

    public required int PromptTokens { get; init; }

    public double? TotalCost { get; init; }

    public bool IsCached { get; init; } = false;

    public static OpenUsage operator +(OpenUsage a, OpenUsage b)
    {
        return new OpenUsage
        {
            ModelId = a.ModelId,
            CompletionTokens = a.CompletionTokens + b.CompletionTokens,
            PromptTokens = a.PromptTokens + b.PromptTokens,
            TotalCost = a.TotalCost + b.TotalCost,
            IsCached = a.IsCached
        };
    }
}

public interface ICanGetUsage
{
    string CompletionId { get; }

    OpenUsage? Usage { get; }
}

public class OpenTextMessage : IMessage, ICanGetText, ICanGetUsage
{
    public bool IsStreaming { get; set; } = false;

    public required string Text { get; set; }

    public Role Role { get; set; }

    public string? FromAgent { get; set; }

    public JsonObject? Metadata { get; set; }

    public string? GenerationId { get; set; }

    public string CompletionId { get; set; } = string.Empty;

    public OpenUsage? Usage { get; set; }

    public string? GetText() => Text;

    public BinaryData? GetBinary() => null;

    public ToolCall? GetToolCalls() => null;

    public IEnumerable<IMessage>? GetMessages() => null;

    public TextMessage ToTextMessage()
    {
        return new TextMessage
        {
            Text = Text,
            Role = Role,
            FromAgent = FromAgent,
            Metadata = Metadata,
            GenerationId = GenerationId
        };
    }
}

public class OpenToolMessage : IMessage, ICanGetToolCalls, ICanGetUsage
{
    public List<ToolCall> ToolCalls { get; set; } = new List<ToolCall>();

    public string CompletionId { get; set; } = string.Empty;

    public string? FromAgent { get; set; }

    public Role Role { get; set; }

    public JsonObject? Metadata { get; set; }

    public string? GenerationId { get; set; }

    public OpenUsage? Usage { get; set; }

    public string? GetText() => null;

    public BinaryData? GetBinary() => null;

    public ToolCall? GetToolCalls() => ToolCalls.Count > 0 ? ToolCalls[0] : null;

    IEnumerable<ToolCall>? ICanGetToolCalls.GetToolCalls() => ToolCalls.Count > 0 ? ToolCalls : null;

    public IEnumerable<IMessage>? GetMessages() => null;
}

public class OpenToolCallAggregateMessage : ToolsCallAggregateMessage, ICanGetUsage
{
    public OpenToolCallAggregateMessage(
        string completionId,
        ICanGetToolCalls toolCallMsg,
        ToolsCallResultMessage toolCallResult,
        string? fromAgent = null,
        OpenUsage? usage = null)
        : base(
            toolCallMsg,
            toolCallResult, fromAgent)
    {
        CompletionId = completionId;
        Usage = usage;
    }

    public string CompletionId { get; set; }

    public OpenUsage? Usage { get; set; }
}
