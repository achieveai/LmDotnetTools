using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Represents a model's internal chain-of-thought or reasoning text. Some providers (OpenAI o-series, DeepSeek-R,
///     Anthropic Claude) return this
///     alongside the assistant's answer. Keeping it in a dedicated message lets callers decide whether to display, log or
///     omit the content.
/// </summary>
[JsonConverter(typeof(ReasoningMessageJsonConverter))]
public record ReasoningMessage : IMessage, ICanGetText
{
    /// <summary>
    ///     The raw reasoning text provided by the model.  If <see cref="Visibility" /> is
    ///     <see cref="ReasoningVisibility.Encrypted" />,
    ///     the value is an opaque blob that must be preserved verbatim but should NOT be displayed to users.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public required string Reasoning { get; init; }

    /// <summary>
    ///     Indicates whether the reasoning is plain-text or an encrypted / redacted blob that should not be surfaced.
    /// </summary>
    [JsonPropertyName("visibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReasoningVisibility Visibility { get; init; } = ReasoningVisibility.Plain;

    /// <summary>
    ///     Who can read this <see cref="ReasoningVisibility.Encrypted" /> blob, as <c>responses:{modelId}</c>. Only
    ///     the Responses agent sets it, and only with reasoning replay on. A row with this set goes back to that
    ///     model alone: other providers cannot read it, and it never goes to a subscriber or the browser.
    /// </summary>
    /// <remarks>
    ///     A row with this set is saved under its own <c>$type</c>, <see cref="ReplayTypeDiscriminator" />, the
    ///     way a newer checkpoint schema gets its own. A build that predates it does not know that type, so its
    ///     loaders skip the row: it is never read as ordinary encrypted reasoning and never sent to another
    ///     provider. After such a rollback the replay history is lost, not resent. There is one more limit: the
    ///     old build cannot compact a thread with replay rows once it outgrows the context window. Its compaction
    ///     loads fewer rows than the store holds and returns <c>unsafe_state</c>, so each request is then refused
    ///     before it is sent. The thread keeps working until then, and rolling forward clears it.
    /// </remarks>
    [JsonPropertyName("encryptedOrigin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedOrigin { get; init; }

    /// <summary>The <c>$type</c> a row with <see cref="EncryptedOrigin" /> set is saved under.</summary>
    public const string ReplayTypeDiscriminator = "reasoning_replay";

    public string? GetText()
    {
        return Visibility == ReasoningVisibility.Encrypted ? null : Reasoning;
    }

    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    public static BinaryData? GetBinary()
    {
        return null;
    }

    public static ToolCall? GetToolCalls()
    {
        return null;
    }

    public static IEnumerable<IMessage>? GetMessages()
    {
        return null;
    }
}

/// <summary>
///     When streaming providers emit partial reasoning tokens we accumulate them using this message type.
/// </summary>
[JsonConverter(typeof(ReasoningUpdateMessageJsonConverter))]
public record ReasoningUpdateMessage : IMessage, ICanGetText
{
    [JsonPropertyName("reasoning")]
    public required string Reasoning { get; init; }

    [JsonPropertyName("isUpdate")]
    public bool IsUpdate { get; init; } = true;

    [JsonPropertyName("visibility")]
    public ReasoningVisibility? Visibility { get; init; }

    public string? GetText()
    {
        return Reasoning;
    }

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    /// Chunk index within the same messageOrderIdx for streaming updates.
    /// Multiple chunks can belong to the same message during streaming.
    /// Note: Encrypted reasoning is typically sent as a single chunk.
    /// </summary>
    [JsonPropertyName("chunkIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunkIdx { get; init; }

    public static BinaryData? GetBinary()
    {
        return null;
    }

    public static ToolCall? GetToolCalls()
    {
        return null;
    }

    public static IEnumerable<IMessage>? GetMessages()
    {
        return null;
    }
}

/// <summary>
///     Indicates how the reasoning text may be surfaced.
/// </summary>
public enum ReasoningVisibility
{
    /// <summary>Reasoning is plain text (full chain-of-thought) and may be displayed to end-users or logs.</summary>
    Plain,

    /// <summary>
    ///     Reasoning is the provider-generated short summary of the chain-of-thought. It is safe to surface and
    ///     may be echoed back when encrypted reasoning is not available.
    /// </summary>
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
    private readonly StringBuilder _builder = new();
    public string? GenerationId { get; set; }
    public ReasoningVisibility Visibility { get; set; } = ReasoningVisibility.Plain;
    public string? ThreadId { get; set; }
    public string? RunId { get; set; }
    public string? ParentRunId { get; set; }
    public int? MessageOrderIdx { get; set; }

    public string? FromAgent { get; set; }
    public Role Role { get; set; } = Role.Assistant;

    public void Add(ReasoningUpdateMessage streamingMessageUpdate)
    {
        ArgumentNullException.ThrowIfNull(streamingMessageUpdate);
        _ = _builder.Append(streamingMessageUpdate.Reasoning);
    }

    IMessage IMessageBuilder.Build()
    {
        return Build();
    }

    public ReasoningMessage Build()
    {
        return new ReasoningMessage
        {
            Reasoning = _builder.ToString(),
            FromAgent = FromAgent,
            Role = Role,
            GenerationId = GenerationId,
            Visibility = Visibility,
            ThreadId = ThreadId,
            RunId = RunId,
            ParentRunId = ParentRunId,
            MessageOrderIdx = MessageOrderIdx,
        };
    }
}
