using AchieveAi.LmDotnetTools.AgUi.DataObjects;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;

/// <summary>
///     Publishes AG-UI events to subscribers
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    ///     Publishes an event to all subscribers of the session
    /// </summary>
    /// <param name="evt">The event to publish</param>
    /// <param name="ct">Cancellation token</param>
    Task PublishAsync(AgUiEventBase evt, CancellationToken ct = default);

    /// <summary>
    ///     Subscribes to events for a specific session
    /// </summary>
    /// <param name="sessionId">Session ID to subscribe to</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of events</returns>
    IAsyncEnumerable<AgUiEventBase> SubscribeAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Unsubscribes from a session and closes the channel
    /// </summary>
    /// <param name="sessionId">Session ID to unsubscribe from</param>
    void Unsubscribe(string sessionId);
}
