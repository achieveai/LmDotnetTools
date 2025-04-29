using System.Text;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(TextMessageJsonConverter))]
public record TextMessage : IMessage, ICanGetText
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    public string? GetText() => Text;

    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    [JsonPropertyName("role")]
    public Role Role { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    [JsonPropertyName("isThinking")]
    public bool IsThinking { get; init; }

    public BinaryData? GetBinary() => null;

    public ToolCall? GetToolCalls() => null;

    public IEnumerable<IMessage>? GetMessages() => null;
}

public class TextMessageJsonConverter : ShadowPropertiesJsonConverter<TextMessage>
{
    protected override TextMessage CreateInstance()
    {
        return new TextMessage { Text = string.Empty };
    }
}

public class TextMessageBuilder : IMessageBuilder<TextMessage, TextUpdateMessage>
{
    private readonly StringBuilder _textBuilder = new StringBuilder();

    public string? FromAgent { get; set; }

    public Role Role { get; set; }

    public ImmutableDictionary<string, object>? Metadata { get; private set; }

    public string? GenerationId { get; set; }

    public bool IsThinking { get; set; }

    IMessage IMessageBuilder.Build()
    {
        return this.Build();
    }

    public void Add(TextUpdateMessage streamingMessageUpdate)
    {
        _textBuilder.Append(streamingMessageUpdate.Text);

        // Set IsThinking from the update
        IsThinking = streamingMessageUpdate.IsThinking;

        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata;
            }
            else
            {
                // Merge metadata, with message's metadata taking precedence
                foreach (var prop in streamingMessageUpdate.Metadata)
                {
                    Metadata = Metadata.Add(prop.Key, prop.Value);
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
            GenerationId = GenerationId,
            IsThinking = IsThinking
        };
    }
}
