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
    Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default);

    /// <summary>
    /// Loads all messages for a thread, ordered by timestamp.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All messages for the thread, or empty list if thread not found.</returns>
    Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default);

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
    Task ReplaceMessageAsync(
        string threadId,
        PersistedMessage replacement,
        CancellationToken ct = default);

    // === Metadata (property bag for state, session mappings, etc.) ===

    /// <summary>
    /// Saves or updates thread metadata. Uses upsert semantics.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="metadata">Metadata to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveMetadataAsync(
        string threadId,
        ThreadMetadata metadata,
        CancellationToken ct = default);

    /// <summary>
    /// Loads thread metadata.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata for the thread, or null if not found.</returns>
    Task<ThreadMetadata?> LoadMetadataAsync(
        string threadId,
        CancellationToken ct = default);

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
        CancellationToken ct = default);

    // === Lifecycle ===

    /// <summary>
    /// Deletes all data for a thread (messages + metadata).
    /// No-op if the thread does not exist.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteThreadAsync(
        string threadId,
        CancellationToken ct = default);

    // === Listing ===

    /// <summary>
    /// Lists all threads with their metadata, ordered by last updated descending.
    /// </summary>
    /// <param name="limit">Maximum number of threads to return.</param>
    /// <param name="offset">Number of threads to skip (for pagination).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of thread metadata, or empty list if no threads exist.</returns>
    Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default);
}
