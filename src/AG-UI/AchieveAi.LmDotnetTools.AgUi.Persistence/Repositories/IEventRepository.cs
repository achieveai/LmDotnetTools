using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;

/// <summary>
///     Repository interface for persisting and querying AG-UI event metadata.
/// </summary>
/// <remarks>
///     All operations are async and thread-safe.
///     Only minimal event metadata is stored - full events can be regenerated from messages.
/// </remarks>
public interface IEventRepository
{
    /// <summary>
    ///     Retrieves an event by its unique identifier.
    /// </summary>
    /// <param name="id">The event identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The event entity if found; otherwise null.</returns>
    Task<EventEntity?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves all events for a specific session in chronological order.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of event entities.</returns>
    Task<IReadOnlyList<EventEntity>> GetBySessionIdAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Creates a new event in the database.
    /// </summary>
    /// <param name="evt">The event entity to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CreateAsync(EventEntity evt, CancellationToken ct = default);
}
