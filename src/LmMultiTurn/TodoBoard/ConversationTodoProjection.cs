using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;

/// <summary>
///     Persists and reads a conversation's todo board inside <see cref="ThreadMetadata.Properties" /> —
///     zero schema migration, uniform across every <see cref="IConversationStore" /> backend, exactly as
///     <c>ConversationUsageProjection</c> does for the usage banner.
/// </summary>
/// <remarks>
///     <para>
///         The snapshot is stored as a JSON <b>string</b> so it round-trips identically whether the backing
///         store keeps native CLR objects (in-memory) or re-hydrates property-bag values as
///         <see cref="JsonElement" /> (file / SQLite).
///     </para>
///     <para>
///         Reads are tolerant by construction: a corrupt or newer-schema blob reads as <b>absent</b> and
///         never throws. The board is a convenience view of work in flight — a bad blob must not turn
///         every subsequent read of that conversation into a 500.
///     </para>
/// </remarks>
public static class ConversationTodoProjection
{
    /// <summary>The metadata property-bag key under which the board snapshot JSON is stored.</summary>
    public const string PropertyKey = "todo.board";

    /// <summary>Highest board schema version this build understands; newer is treated as absent.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     Atomically persists <paramref name="snapshot" /> into the conversation's metadata property bag.
    ///     Two guards, both applied under the store's write serialization so the read-compare-write cannot
    ///     interleave:
    ///     <list type="bullet">
    ///         <item>
    ///             A projection written by a <b>newer schema version</b> is never overwritten, so an older
    ///             build during a rollback / mixed-version deployment preserves forward-compatible data.
    ///         </item>
    ///         <item>
    ///             A snapshot captured <b>strictly earlier</b> than the persisted one is dropped, so a slow
    ///             write racing a fresh one cannot regress the board to an older state.
    ///         </item>
    ///     </list>
    ///     Note what is deliberately <i>not</i> guarded: an empty board is persisted like any other, because
    ///     "the agent cleared every row" is a real state. Whether a given empty snapshot means "cleared" or
    ///     merely "this process has not seen the board yet" is knowable only at the call site, so that
    ///     policy lives there rather than being guessed at here.
    /// </summary>
    public static Task SaveAsync(IConversationStore store, TodoBoardSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);

        var json = JsonSerializer.Serialize(snapshot);

        return store.UpdateMetadataAsync(
            snapshot.ThreadId,
            existing =>
            {
                // Forward-compatibility: refuse to overwrite a projection a newer build wrote.
                if (PersistedSchemaVersion(existing) > CurrentSchemaVersion)
                {
                    return existing!;
                }

                // Monotonic in capture time. Equal instants are ACCEPTED, not rejected: at coarse clock
                // resolution successive captures routinely share a tick, and treating those as stale would
                // silently drop every write that lands inside one tick of the previous one.
                var persisted = FromMetadata(existing);
                if (persisted is not null && persisted.CapturedAtUtc > snapshot.CapturedAtUtc)
                {
                    return existing!;
                }

                return WithProjection(existing, snapshot.ThreadId, json);
            },
            ct
        );
    }

    /// <summary>Loads the persisted board for a conversation, or null when none has been stored.</summary>
    public static async Task<TodoBoardSnapshot?> LoadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        var metadata = await store.LoadMetadataAsync(threadId, ct);
        return FromMetadata(metadata);
    }

    /// <summary>
    ///     Extracts the board from already-loaded metadata. Store-agnostic: accepts the value whether it is
    ///     a native JSON string (in-memory) or a re-hydrated <see cref="JsonElement" /> (file / SQLite).
    /// </summary>
    public static TodoBoardSnapshot? FromMetadata(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata);
        if (json is null)
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<TodoBoardSnapshot>(json);
            return snapshot is { SchemaVersion: <= CurrentSchemaVersion } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the persisted schema version even when it is newer than this build understands.</summary>
    private static int PersistedSchemaVersion(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata);
        if (json is null)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("SchemaVersion", out var value)
                && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : CurrentSchemaVersion;
        }
        catch (JsonException)
        {
            return 0; // corrupt — treat as absent, allow overwrite
        }
    }

    private static string? RawJson(ThreadMetadata? metadata)
    {
        if (metadata?.Properties is null || !metadata.Properties.TryGetValue(PropertyKey, out var raw) || raw is null)
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

    private static ThreadMetadata WithProjection(ThreadMetadata? existing, string threadId, string boardJson)
    {
        // SetItem, never a wholesale replace: this bag is shared with the usage projection, the mode
        // binding, and the workspace binding.
        var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
            PropertyKey,
            boardJson
        );

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
