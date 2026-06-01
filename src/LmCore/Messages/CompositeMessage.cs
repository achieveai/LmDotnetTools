using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(CompositeMessageJsonConverter))]
public class CompositeMessage : IMessage
{
    [JsonPropertyName("messages")]
    public ImmutableList<IMessage> Messages { get; init; } = [];

    [JsonPropertyName("role")]
    public Role Role { get; init; }

    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }
}

public class CompositeMessageJsonConverter : ShadowPropertiesJsonConverter<CompositeMessage>
{
    protected override CompositeMessage CreateInstance()
    {
        return new CompositeMessage();
    }
}
