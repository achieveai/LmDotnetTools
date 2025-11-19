using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;

/// <summary>
/// Repository interface for persisting and querying LmCore messages.
/// </summary>
/// <remarks>
/// All operations are async and thread-safe.
/// Messages are stored as complete JSON objects for accurate session recovery.
/// </remarks>
public interface IMessageRepository
{
    /// <summary>
    /// Retrieves a message by its unique identifier.
    /// </summary>
    /// <param name="id">The message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message entity if found; otherwise null.</returns>
    Task<MessageEntity?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all messages for a specific session with optional pagination.
    /// Messages are returned in chronological order (oldest first).
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="skip">Number of records to skip (for pagination).</param>
    /// <param name="take">Maximum number of records to return (for pagination).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of message entities.</returns>
    Task<IReadOnlyList<MessageEntity>> GetMessagesBySessionIdAsync(
        string sessionId,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves all messages for all sessions in a conversation with optional pagination.
    /// Messages are returned in chronological order (oldest first).
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="skip">Number of records to skip (for pagination).</param>
    /// <param name="take">Maximum number of records to return (for pagination).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of message entities.</returns>
    Task<IReadOnlyList<MessageEntity>> GetMessagesByConversationIdAsync(
        string conversationId,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new message in the database.
    /// </summary>
    /// <param name="message">The message entity to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CreateAsync(MessageEntity message, CancellationToken ct = default);
}
