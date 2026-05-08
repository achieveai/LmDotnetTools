using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Represents a message containing usage information for a language model call.
///     This message type separates usage data from content messages.
///     Provider implementations should create this message directly rather than embedding usage in metadata.
/// </summary>
[JsonConverter(typeof(UsageMessageJsonConverter))]
public record UsageMessage : IMessage, ICanGetUsage
{
    /// <summary>
    ///     The usage information for the language model call.
    /// </summary>
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }

    /// <summary>
    ///     Gets the usage information from this message.
    /// </summary>
    /// <returns>The usage information.</returns>
    public Usage? GetUsage()
    {
        return Usage;
    }

    /// <summary>
    ///     The role associated with this usage (typically matches the role of the last content message).
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    ///     The name or identifier of the agent that generated this usage.
    /// </summary>
    [JsonPropertyName("fromAgent")]
    public string? FromAgent { get; init; }

    /// <summary>
    ///     Additional metadata associated with the usage message.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     A unique identifier for the generation this usage is associated with.
    /// </summary>
    [JsonPropertyName("generationId")]
    public string? GenerationId { get; init; }

    /// <summary>
    ///     Thread identifier for conversation continuity (used with AG-UI protocol).
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    ///     Run identifier for this specific execution (used with AG-UI protocol).
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    ///     Order index of this message within its generation (same GenerationId).
    /// </summary>
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }
}

/// <summary>
///     JSON converter for UsageMessage that supports the shadow properties pattern.
/// </summary>
public class UsageMessageJsonConverter : ShadowPropertiesJsonConverter<UsageMessage>
{
    /// <summary>
    ///     Creates a new instance of UsageMessage during deserialization.
    /// </summary>
    /// <returns>A minimal UsageMessage instance.</returns>
    protected override UsageMessage CreateInstance()
    {
        return new UsageMessage { Usage = new Usage() };
    }
}
