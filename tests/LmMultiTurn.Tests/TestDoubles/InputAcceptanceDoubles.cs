using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests;

/// <summary>Records every acceptance report, in order, with the agent that made it.</summary>
internal sealed class RecordingObserver : IInputAcceptanceObserver
{
    private readonly object _gate = new();

    public List<(string ThreadId, string InputId)> Accepted { get; } = [];

    public List<(string ThreadId, string InputId)> Rescinded { get; } = [];

    public List<IMultiTurnAgent> AcceptedBy { get; } = [];

    public void OnInputAccepted(string threadId, string inputId, IMultiTurnAgent acceptedBy)
    {
        lock (_gate)
        {
            Accepted.Add((threadId, inputId));
            AcceptedBy.Add(acceptedBy);
        }
    }

    public void OnInputAcceptanceRescinded(string threadId, string inputId, IMultiTurnAgent acceptedBy)
    {
        _ = acceptedBy;
        lock (_gate)
        {
            Rescinded.Add((threadId, inputId));
        }
    }

    /// <summary>
    /// A thread-safe snapshot. The reports arrive on whichever thread accepted the input — a
    /// sub-agent monitor task, in the relay tests — so a caller must not enumerate the live lists.
    /// </summary>
    public IReadOnlyList<(string ThreadId, string InputId)> AcceptedSnapshot()
    {
        lock (_gate)
        {
            return [.. Accepted];
        }
    }
}

/// <summary>An observer whose every callback throws, for the fail-closed send contract.</summary>
internal sealed class ThrowingObserver : IInputAcceptanceObserver
{
    public void OnInputAccepted(string threadId, string inputId, IMultiTurnAgent acceptedBy) =>
        throw new InvalidOperationException("simulated observer failure");

    public void OnInputAcceptanceRescinded(string threadId, string inputId, IMultiTurnAgent acceptedBy) =>
        throw new InvalidOperationException("simulated observer failure");
}

/// <summary>
/// A real <see cref="MultiTurnAgentBase"/> whose run loop is never started, so every accepted
/// input stays in the channel — the accepted-but-unstarted state the ledger exists for.
/// <see cref="QueuedInputCount"/> reads the channel directly so a test can prove an enqueue did
/// or did not happen without draining it; <see cref="DrainQueuedInputs"/> takes them out when the
/// test needs to see WHICH input landed.
/// </summary>
internal sealed class ObservedTestAgent : MultiTurnAgentBase
{
    public ObservedTestAgent(
        string threadId,
        IConversationStore? store = null,
        bool persistRunLedger = false,
        int inputChannelCapacity = 100)
        : base(
            threadId,
            store: store,
            inputChannelCapacity: inputChannelCapacity,
            persistRunLedger: persistRunLedger)
    {
    }

    public int QueuedInputCount => InputReader.Count;

    /// <summary>
    /// Removes and returns everything currently queued. Callers accumulate across polls, because a
    /// relay under test may not have enqueued yet when the first poll runs.
    /// </summary>
    public List<QueuedInput> DrainQueuedInputs()
    {
        var drained = new List<QueuedInput>();
        while (InputReader.TryRead(out var queued))
        {
            drained.Add(queued);
        }

        return drained;
    }

    protected override Task RunLoopAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
}
