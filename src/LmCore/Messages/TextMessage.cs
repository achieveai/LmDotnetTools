using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public record TextMessage : IMessage, ICanGetText
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    public string? GetText() => Text;

    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    [JsonPropertyName("role")]
    public Role Role { get; init; }
    
    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; init; }
    
    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }
    
    public BinaryData? GetBinary() => null;
    
    public ToolCall? GetToolCalls() => null;
    
    public IEnumerable<IMessage>? GetMessages() => null;
}

public class TextMessageBuilder : IMessageBuilder<TextMessage, TextUpdateMessage>
{
    private readonly StringBuilder _textBuilder = new StringBuilder();

    public string? FromAgent { get; set; }

    public Role Role { get; set; }
    
    public JsonObject? Metadata { get; private set; }

    public string? GenerationId { get; set; }

    IMessage IMessageBuilder.Build()
    {
        return this.Build();
    }

    public void Add(TextUpdateMessage streamingMessageUpdate)
    {
        _textBuilder.Append(streamingMessageUpdate.Text);
        
        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata.DeepClone() as JsonObject;
            }
            else
            {
                // Merge metadata, with message's metadata taking precedence
                foreach (var prop in streamingMessageUpdate.Metadata)
                {
                    Metadata[prop.Key] = prop.Value?.DeepClone();
                }
            }
        }
    }

    public TextMessage Build()
    {
        return new TextMessage
        {
            Text = _textBuilder.ToString(),
            FromAgent = FromAgent,
            Role = Role,
            Metadata = Metadata,
            GenerationId = GenerationId
        };
    }
}
