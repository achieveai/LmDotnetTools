using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Persists and reads the conversation usage aggregate as a projection inside
///     <see cref="ThreadMetadata.Properties" /> — zero schema migration, uniform across every
///     <see cref="IConversationStore" /> backend.
/// </summary>
/// <remarks>
///     The value is stored as a JSON <b>string</b> so it round-trips identically whether the backing store
///     keeps native CLR objects (in-memory) or re-hydrates property-bag values as <see cref="JsonElement" />
///     (file / SQLite). This is the single property-bag adapter for usage data (issue #196).
/// </remarks>
public static class ConversationUsageProjection
{
    /// <summary>The metadata property-bag key under which the usage aggregate JSON is stored.</summary>
    public const string PropertyKey = "usage.aggregate";

    /// <summary>Atomically writes the aggregate into the conversation's metadata property bag.</summary>
    public static Task SaveAsync(
        IConversationStore store,
        ConversationUsageAggregate aggregate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(aggregate);

        var json = JsonSerializer.Serialize(aggregate);
        return store.UpdateMetadataAsync(
            aggregate.RootConversationId,
            existing => WithProjection(existing, aggregate.RootConversationId, json),
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

    /// <summary>
    ///     Extracts the aggregate from already-loaded metadata. Store-agnostic: accepts the value whether it
    ///     is a native JSON string (in-memory) or a re-hydrated <see cref="JsonElement" /> (file / SQLite).
    /// </summary>
    public static ConversationUsageAggregate? FromMetadata(ThreadMetadata? metadata)
    {
        if (metadata?.Properties is null
            || !metadata.Properties.TryGetValue(PropertyKey, out var raw)
            || raw is null)
        {
            return null;
        }

        var json = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => null,
        };

        return json is null ? null : JsonSerializer.Deserialize<ConversationUsageAggregate>(json);
    }

    private static ThreadMetadata WithProjection(ThreadMetadata? existing, string threadId, string json)
    {
        var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
            .SetItem(PropertyKey, json);

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
