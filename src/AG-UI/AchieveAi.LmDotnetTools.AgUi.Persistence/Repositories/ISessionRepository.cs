using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;

/// <summary>
///     Repository interface for persisting and querying AG-UI sessions.
/// </summary>
/// <remarks>
///     All operations are async and thread-safe.
///     Implementations must use parameterized queries to prevent SQL injection.
/// </remarks>
public interface ISessionRepository
{
    /// <summary>
    ///     Retrieves a session by its unique identifier.
    /// </summary>
    /// <param name="id">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session entity if found; otherwise null.</returns>
    Task<SessionEntity?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves all sessions associated with a conversation identifier.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of session entities.</returns>
    Task<IReadOnlyList<SessionEntity>> GetByConversationIdAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    ///     Creates a new session in the database.
    /// </summary>
    /// <param name="session">The session entity to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CreateAsync(SessionEntity session, CancellationToken ct = default);

    /// <summary>
    ///     Updates an existing session in the database.
    /// </summary>
    /// <param name="session">The session entity with updated values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task UpdateAsync(SessionEntity session, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves all sessions that are not in "Completed" or "Failed" status.
    ///     Used for session recovery on application startup.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of incomplete session entities.</returns>
    Task<IReadOnlyList<SessionEntity>> GetIncompleteSessionsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Marks a session as failed.
    ///     Used during session recovery to mark crashed sessions.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task MarkSessionAsFailedAsync(string sessionId, CancellationToken ct = default);
}
