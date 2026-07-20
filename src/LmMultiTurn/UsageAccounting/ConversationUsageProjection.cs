using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Persists and reads conversation usage inside <see cref="ThreadMetadata.Properties" /> — zero schema
///     migration, uniform across every <see cref="IConversationStore" /> backend. Two artifacts are stored:
///     the canonical per-attempt <b>records</b> (the durable source of truth, deduped by provider attempt)
///     and the derived <b>aggregate</b> projection folded from them (#196).
/// </summary>
/// <remarks>
///     Values are stored as JSON <b>strings</b> so they round-trip identically whether the backing store
///     keeps native CLR objects (in-memory) or re-hydrates property-bag values as <see cref="JsonElement" />
///     (file / SQLite) — the single property-bag adapter for usage data.
/// </remarks>
public static class ConversationUsageProjection
{
    /// <summary>The metadata property-bag key under which the usage aggregate JSON is stored.</summary>
    public const string PropertyKey = "usage.aggregate";

    /// <summary>The metadata property-bag key under which the canonical per-attempt records JSON is stored.</summary>
    public const string RecordsPropertyKey = "usage.records";

    /// <summary>Highest usage-aggregate schema version this build understands; newer is treated as absent.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     Atomically persists the aggregate and its canonical records into the conversation's metadata
    ///     property bag. Durable across restarts, concurrent writers, and rollbacks:
    ///     <list type="bullet">
    ///         <item>
    ///             Records are <b>merged</b> with what is already persisted — unioned by
    ///             <see cref="UsageRecord.ProviderAttemptId" />, highest <see cref="UsageRecord.Revision" />
    ///             winning per attempt — then the aggregate is re-folded from the union. This is the durable
    ///             merge identity: no writer (post-restart, second instance, or out-of-order) can drop another
    ///             writer's attempts, even when both reuse the same process-local revision.
    ///         </item>
    ///         <item>
    ///             A projection written by a <b>newer schema version</b> is never overwritten, so an older
    ///             build during a rollback / mixed-version deployment preserves forward-compatible data.
    ///         </item>
    ///         <item>
    ///             When no records are supplied (aggregate-only save), it falls back to a
    ///             <see cref="ConversationUsageAggregate.FoldedRevision" /> guard that rejects a strictly-lower
    ///             write.
    ///         </item>
    ///     </list>
    /// </summary>
    public static Task SaveAsync(
        IConversationStore store,
        ConversationUsageAggregate aggregate,
        IReadOnlyList<UsageRecord>? records = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(aggregate);

        return store.UpdateMetadataAsync(
            aggregate.RootConversationId,
            existing =>
            {
                // Runs under the store's write serialization, so the read-merge-write below is atomic per
                // conversation: concurrent writers each union with the latest persisted state.

                // Forward-compatibility: refuse to overwrite a projection a newer build wrote.
                if (PersistedSchemaVersion(existing) > CurrentSchemaVersion)
                {
                    return existing!;
                }

                if (records is { Count: > 0 })
                {
                    var persisted = RecordsFromMetadata(existing);
                    var merged = MergeRecords(persisted, records);
                    var revision = Math.Max(aggregate.FoldedRevision, PersistedFoldedRevision(existing));
                    var foldedAggregate = ConversationUsageAggregate.Fold(
                        aggregate.RootConversationId, merged, revision, aggregate.Completeness);

                    return WithProjection(
                        existing,
                        aggregate.RootConversationId,
                        JsonSerializer.Serialize(foldedAggregate),
                        JsonSerializer.Serialize(merged));
                }

                // Aggregate-only save: keep the strictly-lower-watermark guard.
                var current = FromMetadata(existing);
                if (existing is not null && current is not null && current.FoldedRevision > aggregate.FoldedRevision)
                {
                    return existing;
                }

                return WithProjection(
                    existing,
                    aggregate.RootConversationId,
                    JsonSerializer.Serialize(aggregate),
                    recordsJson: null);
            },
            ct);
    }

    /// <summary>
    ///     Unions persisted and incoming records by <see cref="UsageRecord.ProviderAttemptId" />, keeping the
    ///     highest <see cref="UsageRecord.Revision" /> per attempt (cumulative streaming / retry). Order-
    ///     independent and idempotent, so it can never lose an attempt one writer knew about.
    /// </summary>
    private static IReadOnlyList<UsageRecord> MergeRecords(
        IReadOnlyList<UsageRecord> persisted,
        IReadOnlyList<UsageRecord> incoming)
    {
        if (persisted.Count == 0)
        {
            return incoming;
        }

        if (incoming.Count == 0)
        {
            return persisted;
        }

        var byAttempt = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var record in persisted)
        {
            byAttempt[record.ProviderAttemptId] = record;
        }

        foreach (var record in incoming)
        {
            if (!byAttempt.TryGetValue(record.ProviderAttemptId, out var existing) || record.Revision >= existing.Revision)
            {
                byAttempt[record.ProviderAttemptId] = record;
            }
        }

        return [.. byAttempt.Values];
    }

    /// <summary>Reads the persisted schema version even when it is newer than this build understands.</summary>
    private static int PersistedSchemaVersion(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata, PropertyKey);
        if (json is null)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("SchemaVersion", out var value)
                && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : CurrentSchemaVersion;
        }
        catch (JsonException)
        {
            return 0; // corrupt — treat as absent, allow overwrite
        }
    }

    private static long PersistedFoldedRevision(ThreadMetadata? metadata)
    {
        return FromMetadata(metadata)?.FoldedRevision ?? 0;
    }

    /// <summary>Loads the persisted aggregate for a conversation, or null when none has been stored.</summary>
    public static async Task<ConversationUsageAggregate?> LoadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var metadata = await store.LoadMetadataAsync(threadId, ct);
        return FromMetadata(metadata);
    }

    /// <summary>Loads the persisted canonical records for a conversation (empty when none), for rebuild.</summary>
    public static async Task<IReadOnlyList<UsageRecord>> LoadRecordsAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var metadata = await store.LoadMetadataAsync(threadId, ct);
        return RecordsFromMetadata(metadata);
    }

    /// <summary>
    ///     Extracts the aggregate from already-loaded metadata. Store-agnostic: accepts the value whether it
    ///     is a native JSON string (in-memory) or a re-hydrated <see cref="JsonElement" /> (file / SQLite).
    /// </summary>
    public static ConversationUsageAggregate? FromMetadata(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata, PropertyKey);
        if (json is null)
        {
            return null;
        }

        // Tolerate corrupt or incompatible persisted usage rather than surfacing a 500 on the usage
        // endpoint: treat unparseable/newer-schema metadata as "no usage recorded" (#196).
        try
        {
            var aggregate = JsonSerializer.Deserialize<ConversationUsageAggregate>(json);
            return aggregate is { SchemaVersion: <= CurrentSchemaVersion } ? aggregate : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Extracts the canonical records from already-loaded metadata (empty when absent/corrupt).</summary>
    public static IReadOnlyList<UsageRecord> RecordsFromMetadata(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata, RecordsPropertyKey);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<UsageRecord>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? RawJson(ThreadMetadata? metadata, string key)
    {
        if (metadata?.Properties is null
            || !metadata.Properties.TryGetValue(key, out var raw)
            || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => null,
        };
    }

    private static ThreadMetadata WithProjection(
        ThreadMetadata? existing,
        string threadId,
        string aggregateJson,
        string? recordsJson)
    {
        var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
            .SetItem(PropertyKey, aggregateJson);

        if (recordsJson is not null)
        {
            properties = properties.SetItem(RecordsPropertyKey, recordsJson);
        }

        if (existing is not null)
        {
            return existing with { Properties = properties };
        }

        return new ThreadMetadata
        {
            ThreadId = threadId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = properties,
        };
    }
}
