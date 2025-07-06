using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Represents a model's internal chain-of-thought or reasoning text. Some providers (OpenAI o-series, DeepSeek-R, Anthropic Claude) return this
/// alongside the assistant's answer. Keeping it in a dedicated message lets callers decide whether to display, log or
/// omit the content.
/// </summary>
[JsonConverter(typeof(ReasoningMessageJsonConverter))]
public record ReasoningMessage : IMessage, ICanGetText
{
    /// <summary>
    /// The raw reasoning text provided by the model.  If <see cref="Visibility"/> is <see cref="ReasoningVisibility.Encrypted"/>,
    /// the value is an opaque blob that must be preserved verbatim but should NOT be displayed to users.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public required string Reasoning { get; init; }

    /// <summary>
    /// Indicates whether the reasoning is plain-text or an encrypted / redacted blob that should not be surfaced.
    /// </summary>
    [JsonPropertyName("visibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReasoningVisibility Visibility { get; init; } = ReasoningVisibility.Plain;

    public string? GetText() => Visibility == ReasoningVisibility.Encrypted ? null : Reasoning;

    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    public BinaryData? GetBinary() => null;
    public ToolCall? GetToolCalls() => null;
    public IEnumerable<IMessage>? GetMessages() => null;
}

/// <summary>
/// When streaming providers emit partial reasoning tokens we accumulate them using this message type.
/// </summary>
[JsonConverter(typeof(ReasoningUpdateMessageJsonConverter))]
public record ReasoningUpdateMessage : IMessage, ICanGetText
{
    [JsonPropertyName("reasoning")]
    public required string Reasoning { get; init; }

    public string? GetText() => Reasoning;

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    [JsonPropertyName("isUpdate")]
    public bool IsUpdate { get; init; } = true;

    public BinaryData? GetBinary() => null;
    public ToolCall? GetToolCalls() => null;
    public IEnumerable<IMessage>? GetMessages() => null;
}

/// <summary>
/// Indicates how the reasoning text may be surfaced.
/// </summary>
public enum ReasoningVisibility
{
    /// <summary>Reasoning is plain text (full chain-of-thought) and may be displayed to end-users or logs.</summary>
    Plain,

    /// <summary>Reasoning is the provider-generated short summary of the chain-of-thought. It is safe to surface and
    /// may be echoed back when encrypted reasoning is not available.</summary>
    Summary,

    /// <summary>Reasoning is an encrypted / redacted blob and must be preserved but hidden.</summary>
    Encrypted,
}

public class ReasoningMessageJsonConverter : ShadowPropertiesJsonConverter<ReasoningMessage>
{
    protected override ReasoningMessage CreateInstance()
    {
        return new ReasoningMessage { Reasoning = string.Empty };
    }
}

public class ReasoningUpdateMessageJsonConverter : ShadowPropertiesJsonConverter<ReasoningUpdateMessage>
{
    protected override ReasoningUpdateMessage CreateInstance()
    {
        return new ReasoningUpdateMessage { Reasoning = string.Empty };
    }
}

public class ReasoningMessageBuilder : IMessageBuilder<ReasoningMessage, ReasoningUpdateMessage>
{
    private readonly System.Text.StringBuilder _builder = new();

    public string? FromAgent { get; set; }
    public Role Role { get; set; } = Role.Assistant;
    public string? GenerationId { get; set; }
    public ReasoningVisibility Visibility { get; set; } = ReasoningVisibility.Plain;

    public void Add(ReasoningUpdateMessage streamingMessageUpdate)
    {
        _builder.Append(streamingMessageUpdate.Reasoning);
    }

    IMessage IMessageBuilder.Build() => this.Build();

    public ReasoningMessage Build()
    {
        return new ReasoningMessage
        {
            Reasoning = _builder.ToString(),
            FromAgent = FromAgent,
            Role = Role,
            GenerationId = GenerationId,
            Visibility = Visibility,
        };
    }
} 