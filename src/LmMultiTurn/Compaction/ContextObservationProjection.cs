using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     Persists one <see cref="ContextObservation" /> per generation under the thread's metadata: the
///     latest under <c>context.latest</c>, and a bounded ring of recent ones under
///     <c>context.observations</c> (#680; spec 679 §4.1, §4.3).
/// </summary>
/// <remarks>
///     A pressure row (#681) reads <c>context.latest</c> and decides freshness by comparing its
///     generation ordinal with the thread's; the ring is for diagnostics and the eval corpus (#686). Both
///     keys are written in ONE metadata update so they can never disagree, and neither is touched when
///     either carries a schema version newer than this build.
/// </remarks>
public static class ContextObservationProjection
{
    /// <summary>The metadata property the latest observation lives under.</summary>
    public const string LatestPropertyKey = "context.latest";

    /// <summary>The metadata property the ring of recent observations lives under.</summary>
    public const string HistoryPropertyKey = "context.observations";

    /// <summary>How many observations the ring keeps by default.</summary>
    public const int DefaultHistoryLength = 50;

    /// <summary>The ring envelope. Versioned separately from the observation it carries.</summary>
    private sealed record ObservationRing
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        [JsonPropertyName("observations")]
        public IReadOnlyList<ContextObservation> Observations { get; init; } = [];
    }

    /// <summary>
    ///     Appends <paramref name="observation" /> to the thread's ring (keeping the newest
    ///     <paramref name="historyLength" />) and makes it the latest. A no-op when either key holds a
    ///     schema this build does not understand.
    /// </summary>
    public static Task RecordAsync(
        IConversationStore store,
        ContextObservation observation,
        int historyLength = DefaultHistoryLength,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLength, 1);

        return store.UpdateMetadataAsync(
            observation.ThreadId,
            existing =>
            {
                if (
                    MetadataProjectionJson.SchemaVersion(
                        existing,
                        HistoryPropertyKey,
                        ObservationRing.CurrentSchemaVersion
                    ) > ObservationRing.CurrentSchemaVersion
                    || MetadataProjectionJson.SchemaVersion(
                        existing,
                        LatestPropertyKey,
                        ContextObservation.CurrentSchemaVersion
                    ) > ContextObservation.CurrentSchemaVersion
                )
                {
                    return existing!;
                }

                var ring = RingFromMetadata(existing) ?? new ObservationRing();
                var observations = ring.Observations.Append(observation).ToList();
                if (observations.Count > historyLength)
                {
                    observations.RemoveRange(0, observations.Count - historyLength);
                }

                return MetadataProjectionJson.WithProperties(
                    existing,
                    observation.ThreadId,
                    (
                        HistoryPropertyKey,
                        JsonSerializer.Serialize(
                            ring with
                            {
                                Observations = observations,
                            },
                            MetadataProjectionJson.Options
                        )
                    ),
                    (LatestPropertyKey, JsonSerializer.Serialize(observation, MetadataProjectionJson.Options))
                );
            },
            ct
        );
    }

    /// <summary>The thread's latest observation, or null when absent, corrupt, or newer than this build.</summary>
    public static async Task<ContextObservation?> LoadLatestAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return LatestFromMetadata(await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false));
    }

    /// <summary>The thread's recent observations, oldest first; empty when absent, corrupt, or newer than this build.</summary>
    public static async Task<IReadOnlyList<ContextObservation>> LoadHistoryAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return HistoryFromMetadata(await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false));
    }

    /// <summary>Projects the latest observation out of already-loaded metadata.</summary>
    public static ContextObservation? LatestFromMetadata(ThreadMetadata? metadata)
    {
        if (
            MetadataProjectionJson.SchemaVersion(metadata, LatestPropertyKey, ContextObservation.CurrentSchemaVersion)
            > ContextObservation.CurrentSchemaVersion
        )
        {
            return null;
        }

        return MetadataProjectionJson.Deserialize<ContextObservation>(
            MetadataProjectionJson.RawJson(metadata, LatestPropertyKey)
        );
    }

    /// <summary>Projects the ring out of already-loaded metadata.</summary>
    public static IReadOnlyList<ContextObservation> HistoryFromMetadata(ThreadMetadata? metadata) =>
        RingFromMetadata(metadata)?.Observations ?? [];

    private static ObservationRing? RingFromMetadata(ThreadMetadata? metadata)
    {
        if (
            MetadataProjectionJson.SchemaVersion(metadata, HistoryPropertyKey, ObservationRing.CurrentSchemaVersion)
            > ObservationRing.CurrentSchemaVersion
        )
        {
            return null;
        }

        return MetadataProjectionJson.Deserialize<ObservationRing>(
            MetadataProjectionJson.RawJson(metadata, HistoryPropertyKey)
        );
    }
}
