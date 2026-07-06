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

    Task DeleteAsync(string waitId, CancellationToken ct = default);

    Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default);
}
