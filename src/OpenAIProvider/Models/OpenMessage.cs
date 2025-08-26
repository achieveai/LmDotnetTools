using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public record OpenMessage
{
    public string CompletionId { get; init; } = string.Empty;

    public required ChatMessage ChatMessage { get; init; }

    public OpenUsage? Usage { get; init; }

    public IEnumerable<IMessage> ToMessages()
    {
        var messages = new List<IMessage>();
        var chatMessage = ChatMessage with { Id = CompletionId };

        // First, add all content messages
        foreach (var baseMessage in chatMessage.ToMessages(ChatMessage.Name))
        {
            // Create metadata without usage data
            var metadata = baseMessage.Metadata ?? ImmutableDictionary<string, object>.Empty;
            metadata = metadata.Add("completion_id", CompletionId);

            // Update the message with the metadata containing OpenMessage properties
            if (baseMessage is TextMessage textMessage)
            {
                messages.Add(textMessage with { Metadata = metadata });
            }
            else if (baseMessage is ToolsCallMessage toolCallMessage)
            {
                messages.Add(toolCallMessage with { Metadata = metadata });
            }
            else if (baseMessage is ImageMessage imageMessage)
            {
                // Create a new instance since ImageMessage is not a record type
                messages.Add(
                    new ImageMessage
                    {
                        FromAgent = imageMessage.FromAgent,
                        Role = imageMessage.Role,
                        Metadata = metadata,
                        GenerationId = imageMessage.GenerationId,
                        ImageData = imageMessage.ImageData,
                    }
                );
            }
            else
            {
                messages.Add(baseMessage);
            }
        }

        // Then, if we have usage data, add a dedicated UsageMessage
        if (Usage != null)
        {
            messages.Add(
                new UsageMessage
                {
                    Usage = new Usage
                    {
                        PromptTokens = Usage.PromptTokens,
                        CompletionTokens = Usage.CompletionTokens,
                        TotalTokens = Usage.PromptTokens + Usage.CompletionTokens,
                    },
                    Role = Role.Assistant,
                    FromAgent = ChatMessage.Name,
                    GenerationId = CompletionId,
                }
            );
        }

        return messages;
    }

    public IEnumerable<IMessage> ToStreamingMessage()
    {
        var messages = new List<IMessage>();
        var chatMessage = ChatMessage with { Id = CompletionId };

        // First, add all content update messages
        foreach (var baseMessage in chatMessage.ToStreamingMessages(ChatMessage.Name))
        {
            var metadata = ImmutableDictionary<string, object>
                .Empty.Add("completion_id", CompletionId)
                .Add("is_streaming", true);

            // Fill in the OpenMessage specific fields
            if (baseMessage is ToolsCallMessage toolCallMessage)
            {
                messages.Add(
                    toolCallMessage with
                    {
                        GenerationId = CompletionId,
                        FromAgent = ChatMessage.Name,
                        Role = toolCallMessage.Role,
                        Metadata = metadata,
                    }
                );
            }
            else if (baseMessage is TextMessage textMessage)
            {
                messages.Add(
                    new TextUpdateMessage
                    {
                        Text = textMessage.Text,
                        Role = textMessage.Role,
                        FromAgent = ChatMessage.Name,
                        GenerationId = CompletionId,
                        Metadata = metadata,
                    }
                );
            }
            else
            {
                messages.Add(baseMessage);
            }
        }

        // Then, if we have usage data with actual token counts, add a dedicated UsageMessage.
        // Streaming scenarios often emit an intermediate usage delta with zero tokens; this
        // is filtered upstream. The final usage update may still have zero tokens for some
        // providers – in that case we omit the UsageMessage entirely to avoid noise.
        if (Usage != null && (Usage.PromptTokens > 0 || Usage.CompletionTokens > 0))
        {
            messages.Add(
                new UsageMessage
                {
                    Usage = new Usage
                    {
                        PromptTokens = Usage.PromptTokens,
                        CompletionTokens = Usage.CompletionTokens,
                        TotalTokens = Usage.PromptTokens + Usage.CompletionTokens,
                    },
                    Role = Role.Assistant,
                    FromAgent = ChatMessage.Name,
                    GenerationId = CompletionId,
                }
            );
        }

        return messages;
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
            IsCached = a.IsCached,
        };
    }
}
