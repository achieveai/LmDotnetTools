using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Where a forked conversation's history starts: a message, and its position, in the conversation
/// it was forked from.
/// </summary>
public sealed record ForkPoint
{
    /// <summary>The conversation that was forked. Its rows up to <see cref="Seq"/> are the fork's past.</summary>
    [JsonPropertyName("thread_id")]
    public required string ThreadId { get; init; }

    /// <summary>
    /// The last shared message, which the fork's first own message names as its parent. <c>null</c>
    /// only when <see cref="Seq"/> is 0 - a fork taken before the first message shares nothing.
    /// </summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    /// <summary>
    /// <see cref="MessageId"/>'s position in the forked conversation. The fork's own rows are numbered
    /// from <c>Seq + 1</c>, so its history reads as one dense sequence.
    /// </summary>
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }
}

/// <summary>
/// A conversation's place in a fork family, persisted under
/// <c>ThreadMetadata.Properties["fork.lineage"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A fork stores only its own messages. Its history is the forked conversation's rows up to
/// <see cref="ForkPoint.Seq"/> followed by its own - the stores stitch the two on read, so every
/// reader of <see cref="IConversationStore.LoadMessagesAsync"/> sees one conversation. Nothing is
/// copied and no id is re-minted.
/// </para>
/// <para>
/// Kept in the property bag rather than as a first-class <see cref="ThreadMetadata"/> member on
/// purpose: many writers rebuild the metadata record by hand and carry the bag over, so a new
/// member would be dropped by the first run that completed, silently cutting the fork off from its
/// past.
/// </para>
/// </remarks>
public sealed record ConversationLineage
{
    /// <summary>The property-bag key this record occupies.</summary>
    public const string PropertyKey = "fork.lineage";

    /// <summary>The persisted schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Schema version of this record.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Where this conversation's history starts, or <c>null</c> for one that is not a fork.</summary>
    [JsonPropertyName("forked_from")]
    public ForkPoint? ForkedFrom { get; init; }

    /// <summary>
    /// The conversation at the top of the fork chain - the original every fork in the family groups
    /// under. <c>null</c> for one that is not a fork.
    /// </summary>
    [JsonPropertyName("root_thread_id")]
    public string? RootThreadId { get; init; }

    /// <summary>
    /// Set when the user deleted this conversation while forks still depended on its messages. The
    /// rows stay until the last dependent fork is gone; the conversation itself is no longer usable.
    /// Unix milliseconds.
    /// </summary>
    [JsonPropertyName("deleted_at")]
    public long? DeletedAt { get; init; }

    /// <summary>
    /// Whether <paramref name="metadata"/> is a deleted conversation kept only because forks still read
    /// its messages. To everything but those forks it is gone.
    /// </summary>
    public static bool IsDeleted(ThreadMetadata? metadata) => Read(metadata)?.DeletedAt is not null;

    /// <summary>The lineage stored on <paramref name="metadata"/>, or <c>null</c> when none (or unreadable).</summary>
    public static ConversationLineage? Read(ThreadMetadata? metadata)
    {
        var json = ThreadMetadataProjection.RawJson(metadata, PropertyKey);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConversationLineage>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns <paramref name="metadata"/> with <paramref name="lineage"/> written into its property bag.</summary>
    public static ThreadMetadata Write(ThreadMetadata metadata, ConversationLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(lineage);
        return ThreadMetadataProjection.WithProjection(
            metadata,
            PropertyKey,
            JsonSerializer.Serialize(lineage, JsonOptions)
        );
    }
}
