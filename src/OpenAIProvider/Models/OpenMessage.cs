using AchieveAi.LmDotnetTools.LmCore.Messages;
using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public record OpenMessage
{
    public string CompletionId { get; init; } = string.Empty;

    public required ChatMessage ChatMessage { get; init; }

    public OpenUsage? Usage { get; init; }

    public IEnumerable<IMessage> ToMessages()
    {
        foreach (var baseMessage in ChatMessage.ToMessages(ChatMessage.Name))
        {
            // Create metadata with OpenMessage-specific properties
            var metadata = baseMessage.Metadata ?? ImmutableDictionary<string, object>.Empty;
            metadata = metadata.Add("completion_id", CompletionId);
            if (Usage != null)
            {
                metadata = metadata.Add("usage", Usage);
            }
        
            // Update the message with the metadata containing OpenMessage properties
            if (baseMessage is TextMessage textMessage)
            {
                yield return textMessage with { Metadata = metadata };
            }
            else if (baseMessage is ToolsCallMessage toolCallMessage)
            {
                yield return toolCallMessage with { Metadata = metadata };
            }
            else if (baseMessage is ImageMessage imageMessage)
            {
                // Create a new instance since ImageMessage is not a record type
                yield return new ImageMessage
                {
                    FromAgent = imageMessage.FromAgent,
                    Role = imageMessage.Role,
                    Metadata = metadata,
                    GenerationId = imageMessage.GenerationId,
                    ImageData = imageMessage.ImageData
                };
            }
            else
            {
                yield return baseMessage;
            }
        }
    }

    public IEnumerable<IMessage> ToStreamingMessage()
    {
        foreach (var baseMessage in ChatMessage.ToStreamingMessages(ChatMessage.Name))
        {
            var metadata = ImmutableDictionary<string, object>.Empty
                .Add("completion_id", CompletionId)
                .Add("is_streaming", true);

            if (Usage != null)
            {
                metadata = metadata.Add("usage", Usage);
            }

            // Fill in the OpenMessage specific fields
            if (baseMessage is ToolsCallMessage toolCallMessage)
            {
                yield return toolCallMessage with {
                    GenerationId = CompletionId,
                    FromAgent = ChatMessage.Name,
                    Role = toolCallMessage.Role,
                    Metadata = metadata
                };
            }
            else if (baseMessage is TextMessage textMessage)
            {
                yield return new TextUpdateMessage {
                    Text = textMessage.Text,
                    Role = textMessage.Role,
                    FromAgent = ChatMessage.Name,
                    GenerationId = CompletionId,
                    Metadata = metadata
                };
            }
            else
            {
                yield return baseMessage;
            }
        }
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
