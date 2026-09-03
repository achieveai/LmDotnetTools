using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Persists a <see cref="SubAgentInstrumentation"/> snapshot into the conversation's
/// <see cref="ThreadMetadata.Properties"/> bag, so the coordination work a run did is readable
/// OFFLINE from the archived store alone — no live host, no log scraping.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <c>ConversationUsageProjection</c>, deliberately: values are stored as JSON
/// <b>strings</b> so they round-trip identically whether the backing store keeps native CLR objects
/// (in-memory) or re-hydrates property-bag values as <see cref="JsonElement"/> (file / SQLite).
/// </para>
/// <para>
/// Writes are guarded by an OBSERVATION WATERMARK rather than blindly replacing. The sink is
/// process-local and cumulative, so a restart hands this a fresh, emptier snapshot; without the guard
/// that snapshot would erase a completed run's measurements and the archive would report a busy
/// conversation as having done no coordination work at all. A whole snapshot is written or none is —
/// never a field-by-field maximum, which would report a combination that never actually existed.
/// </para>
/// </remarks>
public static class SubAgentInstrumentationProjection
{
    /// <summary>Property-bag key holding the per-spawn timing array as a JSON string.</summary>
    public const string SpawnTimingsPropertyKey = "subagents.spawnTimings";

    /// <summary>Property-bag key holding the run-level <see cref="SubAgentStartupWork"/> roll-up as a JSON string.</summary>
    public const string StartupWorkPropertyKey = "subagents.startupWork";

    /// <summary>
    /// Atomically stamps the sink's current snapshot onto <paramref name="threadId"/>'s metadata,
    /// unless what is already persisted records strictly more observations (see the watermark note on
    /// the type).
    /// </summary>
    public static Task SaveAsync(
        IConversationStore store,
        string threadId,
        SubAgentInstrumentation instrumentation,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(instrumentation);

        // Snapshot BEFORE entering the store's write lock: the update callback runs under that lock and
        // must stay fast and side-effect free, and a snapshot taken inside it would also make the two
        // artifacts below (timings and roll-up) capable of disagreeing.
        var work = instrumentation.Snapshot();
        var spawns = instrumentation.Spawns;
        var workJson = JsonSerializer.Serialize(work);
        var spawnsJson = JsonSerializer.Serialize(spawns);

        return store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                if (Observations(Persisted(existing)) > Observations(work))
                {
                    return existing!;
                }

                var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
                    .SetItem(StartupWorkPropertyKey, workJson)
                    .SetItem(SpawnTimingsPropertyKey, spawnsJson);

                return existing is not null
                    ? existing with
                    {
                        Properties = properties,
                    }
                    : new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Properties = properties,
                    };
            },
            ct
        );
    }

    /// <summary>
    /// Reads back the persisted roll-up, or null when the conversation carries none (or carries a value
    /// this build cannot parse — an unreadable stamp is "not measured", never a thrown read that would
    /// take a whole run's projection down with it).
    /// </summary>
    public static SubAgentStartupWork? Persisted(ThreadMetadata? metadata)
    {
        if (metadata?.Properties?.TryGetValue(StartupWorkPropertyKey, out var value) != true)
        {
            return null;
        }

        try
        {
            // JsonElement when the value came back through a file/SQLite round-trip, string when the
            // store keeps native CLR objects. ToString() yields the JSON text for both.
            return JsonSerializer.Deserialize<SubAgentStartupWork>(value?.ToString() ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How much this snapshot actually saw. Sums the three independent event families rather than using
    /// spawns alone, so a run that only ever listed the directory still advances the watermark.
    /// </summary>
    private static long Observations(SubAgentStartupWork? work) =>
        work is null ? -1 : work.Spawns + (long)work.TemplateCatalogBuilds + work.DirectoryListings;
}
