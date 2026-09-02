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
    ///     <paramref name="historyLength" />) - or replaces the ring's tail when it is the same generation -
    ///     and makes it the latest. A no-op when either key holds a schema this build does not understand.
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
                var observations = ring.Observations.ToList();

                // One entry per generation (#681): a generation is observed estimated before dispatch and
                // measured after the provider's usage arrives, and the later observation supersedes the
                // earlier one in place. Only the tail is compared - an older generation re-observed out of
                // order is a different record, not a correction.
                var recorded = observation;
                if (
                    observations.Count > 0
                    && string.Equals(observations[^1].GenerationId, observation.GenerationId, StringComparison.Ordinal)
                )
                {
                    recorded = Supersede(observations[^1], observation);
                    observations[^1] = recorded;
                }
                else
                {
                    observations.Add(recorded);
                }

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
                    (LatestPropertyKey, JsonSerializer.Serialize(recorded, MetadataProjectionJson.Options))
                );
            },
            ct
        );
    }

    /// <summary>
    ///     Stamps <paramref name="incoming" /> onto the generation's existing record. One generation is
    ///     written by two independent authors - the loop's measurement (#681) and the policy's decision
    ///     (#684; spec 679 §5.5 stamps the decision <em>on the observation</em>) - and neither carries the
    ///     other's fields, so superseding must not blank what it does not itself observe.
    /// </summary>
    private static ContextObservation Supersede(ContextObservation previous, ContextObservation incoming) =>
        incoming with
        {
            MeasuredInputTokens = incoming.MeasuredInputTokens ?? previous.MeasuredInputTokens,
            // Provenance describes the size number that survives, so it follows the carried-forward one.
            Provenance =
                incoming.MeasuredInputTokens is null && previous.MeasuredInputTokens is not null
                    ? previous.Provenance
                    : incoming.Provenance,
            WindowTokens = incoming.WindowTokens ?? previous.WindowTokens,
            PromptCachingEnabled = incoming.PromptCachingEnabled ?? previous.PromptCachingEnabled,
            ActiveCheckpointId = incoming.ActiveCheckpointId ?? previous.ActiveCheckpointId,
            Decision = incoming.Decision ?? previous.Decision,
        };

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
