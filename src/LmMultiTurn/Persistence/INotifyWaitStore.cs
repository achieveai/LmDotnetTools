namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// One persisted live notify-watcher arming record. Notify waits have no deferred tool_call_id to
/// reuse as their arming record (unlike block waits), so they persist here to survive a restart.
/// </summary>
public sealed record NotifyWaitRecord(
    string WaitId,
    string ThreadId,
    string Kind,
    string Args,
    string? Label,
    int? MaxFires,
    int FiresSoFar,
    long TimeoutAtUnixMs,
    long ArmedAtUnixMs,
    string Status);

/// <summary>
/// Durable store for live notify-mode waits. Separate from <see cref="IConversationStore"/> on
/// purpose: notify waits are not message history, and forcing every conversation store to
/// implement wait persistence would be a leaky abstraction. Only a host that configures durable
/// notify restore wires an implementation.
/// </summary>
public interface INotifyWaitStore
{
    Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default);

    /// <summary>
    /// Deletes the row identified by the composite (<paramref name="threadId"/>,
    /// <paramref name="waitId"/>) key. <paramref name="waitId"/> alone is not globally unique (it is
    /// the model-assigned tool_call_id, which two threads can share), so the thread must be
    /// supplied to scope the delete correctly.
    /// </summary>
    Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default);

    Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default);
}
