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
    ///     Atomically persists the aggregate and (optionally) its canonical records into the conversation's
    ///     metadata property bag, under a watermark guard so a stale/out-of-order/post-restart write cannot
    ///     replace a newer aggregate with a lower-<see cref="ConversationUsageAggregate.FoldedRevision" /> one.
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
                // Runs under the store's write serialization, so concurrent fire-and-forget writers cannot
                // clobber a newer aggregate with an older one; and a recreated writer after a restart cannot
                // regress the persisted totals.
                var current = FromMetadata(existing);
                if (existing is not null && current is not null && current.FoldedRevision > aggregate.FoldedRevision)
                {
                    return existing;
                }

                var aggregateJson = JsonSerializer.Serialize(aggregate);
                var recordsJson = records is null ? null : JsonSerializer.Serialize(records);
                return WithProjection(existing, aggregate.RootConversationId, aggregateJson, recordsJson);
            },
            ct);
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
