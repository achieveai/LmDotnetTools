namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Provider-agnostic persistence interface for multi-turn conversations.
/// Implementations can use SQLite, MongoDB, file-based storage, in-memory, etc.
/// </summary>
public interface IConversationStore
{
    // === Messages (append-only storage) ===

    /// <summary>
    /// Appends messages to the thread. Does not replace existing messages.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="messages">Messages to append.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendMessagesAsync(string threadId, IReadOnlyList<PersistedMessage> messages, CancellationToken ct = default);

    /// <summary>
    /// Loads all messages for a thread in append order: by <see cref="PersistedMessage.Seq"/> where
    /// the rows carry one, with any legacy rows that do not (written before the column existed)
    /// ordered by <c>(timestamp, message_order_idx)</c> after them.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All messages for the thread, or empty list if thread not found.</returns>
    Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// The highest <see cref="PersistedMessage.Seq"/> in the thread, or 0 when the thread has no rows
    /// or none of its rows has been sequenced yet (a legacy thread before its first append).
    /// </summary>
    /// <remarks>
    /// Read from the store on every call, never cached: its whole purpose is to tell a caller whether
    /// ANOTHER writer appended since the caller last looked (spec 679 §2.2). The default reads the
    /// whole thread; stores with an index answer it directly.
    /// </remarks>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default)
    {
        var messages = await LoadMessagesAsync(threadId, ct).ConfigureAwait(false);
        return messages.Count == 0 ? 0 : messages.Max(m => m.Seq ?? 0);
    }

    /// <summary>
    /// Loads the rows whose <see cref="PersistedMessage.Seq"/> lies in
    /// <c>[<paramref name="fromSeq"/>, <paramref name="toSeq"/>]</c>, ascending, at most
    /// <paramref name="limit"/> of them. Rows without a Seq are never part of a range. An inverted
    /// range is empty.
    /// </summary>
    /// <remarks>
    /// This is the read a recall tool and a checkpoint reconciler make: a bounded slice of history by
    /// position, without loading the thread. The default filters a full load; stores with an index
    /// answer it directly.
    /// </remarks>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="fromSeq">First sequence number to include.</param>
    /// <param name="toSeq">Last sequence number to include.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    )
    {
        if (limit <= 0 || toSeq < fromSeq)
        {
            return [];
        }

        var messages = await LoadMessagesAsync(threadId, ct).ConfigureAwait(false);
        return
        [
            .. messages.Where(m => m.Seq is { } seq && seq >= fromSeq && seq <= toSeq).OrderBy(m => m.Seq).Take(limit),
        ];
    }

    /// <summary>
    /// Replaces a single previously-appended message identified by its persisted Id.
    /// Used to mutate <see cref="LmCore.Messages.ToolCallResultMessage"/> placeholders to their
    /// final form when a deferred tool call is resolved via
    /// <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>.
    /// </summary>
    /// <remarks>
    /// Implementations MUST preserve the message's original timestamp so that load ordering
    /// remains stable across replacement. Throws <see cref="InvalidOperationException"/> if
    /// no message with the given Id exists for the thread.
    /// </remarks>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="replacement">The replacement message. Its <see cref="PersistedMessage.Id"/>
    /// is the lookup key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default);

    // === Metadata (property bag for state, session mappings, etc.) ===

    /// <summary>
    /// Saves or updates thread metadata. Uses upsert semantics.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="metadata">Metadata to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Loads thread metadata.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata for the thread, or null if not found.</returns>
    Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// Atomically reads the current metadata, applies <paramref name="update"/> to it, and saves the
    /// result — the whole read-modify-write runs under the store's write serialization so concurrent
    /// callers cannot clobber each other's properties (a lost update). Use this whenever you mutate a
    /// SUBSET of the property bag (e.g. the provider/workspace/mode bindings, or a title/preview edit)
    /// rather than replacing the whole record; a plain <see cref="LoadMetadataAsync"/> +
    /// <see cref="SaveMetadataAsync"/> pair leaves a gap in which another writer's save is lost.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="update">
    /// Receives the current metadata (<c>null</c> if none exists yet) and returns the metadata to save.
    /// Invoked while the write lock is held, so keep it fast and side-effect free.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    );

    // === Lifecycle ===

    /// <summary>
    /// Deletes all data for a thread (messages + metadata).
    /// No-op if the thread does not exist.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteThreadAsync(string threadId, CancellationToken ct = default);

    // === Listing ===

    /// <summary>
    /// Lists all threads with their metadata, ordered by last updated descending.
    /// </summary>
    /// <remarks>
    /// <paramref name="options"/> is applied BEFORE <paramref name="limit"/> and
    /// <paramref name="offset"/>, for the same reason the scope of the other overload is: a listing
    /// is a filter, not a loop, and a page trimmed first and filtered second is short by however
    /// many rows the filter removed. See <see cref="ConversationListOptions"/> for the production
    /// failure that made this parameter necessary.
    /// </remarks>
    /// <param name="limit">Maximum number of threads to return.</param>
    /// <param name="offset">Number of threads to skip (for pagination).</param>
    /// <param name="options">
    /// Presentation shape of the listing - excluded id prefixes and sort order. <c>null</c> means
    /// no exclusion and last-used order: exactly what this overload returned before the parameter
    /// existed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of thread metadata, or empty list if no threads exist.</returns>
    Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Lists the threads one principal may read, ordered by last updated descending.
    /// </summary>
    /// <remarks>
    /// The predicate is pushed into the store rather than applied to the result of
    /// <see cref="ListThreadsAsync(int, int, ConversationListOptions, CancellationToken)"/>:
    /// filtering after a <paramref name="limit"/> silently returns short pages, because the page
    /// was trimmed before the filter ran. See P1 spec 7.5.
    /// <para>
    /// The scope covers READ only. It is not a substitute for calling
    /// <c>IResourceAccessPolicy</c> on write, delete or share - those vary by relationship and by
    /// publication state in ways no list query models. An endpoint that infers "it was in your
    /// list, so you may edit it" reintroduces exactly the collapse the rights table prevents.
    /// </para>
    /// <para>
    /// <paramref name="options"/> is a SECOND, independent narrowing applied alongside the scope and
    /// likewise before the page is taken. The two are kept apart deliberately: the scope decides
    /// what the caller may see and comes from their identity, the options decide what this surface
    /// is asking for and come from their query. <see cref="ConversationListOptions"/> explains at
    /// length why folding them together would be wrong.
    /// </para>
    /// <para>
    /// The default implementation THROWS. A default that ignored the scope and delegated to the
    /// unscoped overload would be a silent fail-open - every conversation in the deployment, handed
    /// to whoever asked - and one that filtered the returned page would be the short-page bug above.
    /// A store that cannot answer a scoped listing must say so where it is called, loudly, rather
    /// than answer something plausible. Narrow test doubles for unrelated concerns are what this
    /// spares; every store that reaches a listing route implements it.
    /// </para>
    /// </remarks>
    /// <param name="scope">The principal's tenant, identity, role and resolved grants.</param>
    /// <param name="limit">Maximum number of threads to return.</param>
    /// <param name="offset">Number of threads to skip.</param>
    /// <param name="options">
    /// Presentation shape of the listing - excluded id prefixes and sort order. <c>null</c> means
    /// no exclusion and last-used order: exactly what this overload returned before the parameter
    /// existed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement scoped conversation listing (P1 spec 7.5)."
        );
}
