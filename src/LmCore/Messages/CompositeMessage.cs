

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(CompositeMessageJsonConverter))]
public class CompositeMessage : IMessage
{
    public Role Role { get; init; }

    public string? FromAgent { get; init; }

    public string? GenerationId { get; init; }

    public ImmutableDictionary<string, object>? Metadata { get; init; }

    public ImmutableList<IMessage> Messages { get; init; } = ImmutableList<IMessage>.Empty;
}

public class CompositeMessageJsonConverter : ShadowPropertiesJsonConverter<CompositeMessage>
{
    protected override CompositeMessage CreateInstance()
    {
        return new CompositeMessage { };
    }
}
