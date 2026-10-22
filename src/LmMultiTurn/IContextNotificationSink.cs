using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Accepts sandbox notices (discovered context, an ended background task) without queueing input or starting
/// a model run.
/// </summary>
public interface IContextNotificationSink
{
    /// <summary>
    /// Appends context to an active run. Idle or unsupported targets refuse delivery;
    /// callers can read the context file explicitly on their next turn.
    /// </summary>
    ValueTask<bool> TryAppendContextAsync(NotifyMessage message, CancellationToken ct = default);
}
