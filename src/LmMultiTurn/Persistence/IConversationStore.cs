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
