using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;

/// <summary>
///     The boards a <c>bulk-initialize</c> clear dropped, newest first and bounded.
/// </summary>
/// <remarks>
///     A record of its own rather than a bare list, so the blob carries a schema version the way every
///     other projection in this bag does and a newer build's archive is never silently truncated by an
///     older one.
/// </remarks>
public sealed record TodoBoardArchive
{
    /// <summary>Schema version of the serialized archive; a newer version reads as absent.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    ///     The archived boards, most recently cleared FIRST. Never null; capped at
    ///     <see cref="ConversationTodoArchiveProjection.MaxEntries" />.
    /// </summary>
    public IReadOnlyList<TodoBoardSnapshot> Entries { get; init; } = [];
}

/// <summary>
///     Keeps the last few todo boards a clear destroyed, inside <see cref="ThreadMetadata.Properties" />
///     under its own key, so bug 19 — a sub-agent wiping the shared board and taking hours of completed
///     work with it — is recoverable rather than final.
/// </summary>
/// <remarks>
///     <para>
///         A SEPARATE property key from the live board (<c>todo.board</c>) on purpose. The live board is
///         written on every mutation by a coalescing writer whose monotonic guard drops stale captures;
///         an archive entry is written once, at a clear, and must survive every subsequent board write.
///         Sharing one blob would put the archive behind that guard and behind the board's own schema
///         version.
///     </para>
///     <para>
///         Bounded at <see cref="MaxEntries" /> because the conversation's metadata row is not a log. A
///         model that clears in a loop would otherwise grow one row by a whole board per call, and the
///         entries worth keeping are the recent ones: a clear is noticed within a turn or two, or not at
///         all.
///     </para>
///     <para>
///         Reads are tolerant and writes never mint a row, for exactly the reasons
///         <see cref="ConversationTodoProjection" /> gives: a corrupt or newer blob reads as absent
///         rather than throwing on every read of the conversation, and a metadata row minted here would
///         carry no <c>TenantId</c> and be unreadable by everyone.
///     </para>
/// </remarks>
public static class ConversationTodoArchiveProjection
{
    /// <summary>The metadata property-bag key under which cleared boards are kept.</summary>
    public const string PropertyKey = "todo.board.archive";

    /// <summary>How many cleared boards are retained. Older entries fall off the end.</summary>
    public const int MaxEntries = 5;

    /// <summary>Highest archive schema version this build understands; newer is treated as absent.</summary>
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    ///     Prepends <paramref name="cleared" /> to the conversation's archive, dropping the oldest entry
    ///     past <see cref="MaxEntries" />. A no-op when the conversation has no metadata row.
    /// </summary>
    /// <remarks>
    ///     The read-modify-write runs inside the store's own <c>UpdateMetadataAsync</c> callback, so two
    ///     clears racing each other cannot lose an entry to a lost update.
    /// </remarks>
    public static async Task AppendAsync(
        IConversationStore store,
        TodoBoardSnapshot cleared,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cleared);

        // Never bring a conversation's metadata row into existence — see the same guard, with the full
        // reasoning, in ConversationTodoProjection.SaveAsync.
        if (await store.LoadMetadataAsync(cleared.ThreadId, ct) is null)
        {
            return;
        }

        await store.UpdateMetadataAsync(
            cleared.ThreadId,
            existing =>
            {
                // Re-checked inside the store's write serialization: the row can be deleted between the
                // probe above and this callback, and throwing is the only way to decline a write.
                if (existing is null)
                {
                    throw new TodoBoardDeclinedException(
                        $"Conversation '{cleared.ThreadId}' no longer exists; refusing to recreate its "
                            + "metadata row to archive a cleared todo board."
                    );
                }

                // Forward-compatibility: refuse to overwrite an archive a newer build wrote, rather than
                // rewriting it in this build's shape and dropping whatever that build recorded.
                if (PersistedSchemaVersion(existing) > CurrentSchemaVersion)
                {
                    return existing;
                }

                var kept = FromMetadata(existing)?.Entries ?? [];
                var archive = new TodoBoardArchive
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Entries = [cleared, .. kept.Take(MaxEntries - 1)],
                };

                return ThreadMetadataProjection.WithProjection(
                    existing,
                    PropertyKey,
                    JsonSerializer.Serialize(archive)
                );
            },
            ct
        );
    }

    /// <summary>Loads the conversation's archive, or null when nothing has been archived.</summary>
    public static async Task<TodoBoardArchive?> LoadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return FromMetadata(await store.LoadMetadataAsync(threadId, ct));
    }

    /// <summary>
    ///     Extracts the archive from already-loaded metadata. Store-agnostic and tolerant: a corrupt or
    ///     newer-schema blob reads as absent.
    /// </summary>
    public static TodoBoardArchive? FromMetadata(ThreadMetadata? metadata)
    {
        var json = ThreadMetadataProjection.RawJson(metadata, PropertyKey);
        if (json is null)
        {
            return null;
        }

        try
        {
            var archive = JsonSerializer.Deserialize<TodoBoardArchive>(json, ReadOptions);
            return archive is { SchemaVersion: <= CurrentSchemaVersion } ? archive : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int PersistedSchemaVersion(ThreadMetadata? metadata) =>
        ThreadMetadataProjection.PersistedSchemaVersion(
            metadata,
            PropertyKey,
            nameof(TodoBoardArchive.SchemaVersion),
            whenUnversioned: CurrentSchemaVersion
        );
}
