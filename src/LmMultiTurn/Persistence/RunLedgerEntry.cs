namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Status of a run tracked in the run ledger.
/// </summary>
public enum RunStatus
{
    /// <summary>Accepted and queued, not yet picked up by the agent loop.</summary>
    Queued,

    /// <summary>The agent loop has started processing this run.</summary>
    InProgress,

    /// <summary>The run finished successfully.</summary>
    Completed,

    /// <summary>The run finished with an error.</summary>
    Errored,

    /// <summary>
    /// The run was interrupted (e.g. by a process restart) before reaching a terminal
    /// status. Terminal forever once observed — polling never transitions back out of it.
    /// </summary>
    Interrupted,
}

/// <summary>
/// Durable per-run status record used to resolve conversation status by <c>runId</c> or
/// <c>inputId</c> across process restarts. Companion to <see cref="IConversationStore"/> —
/// tracked separately since a run does not exist until an input has been assigned to it.
/// </summary>
/// <param name="ThreadId">The thread this run belongs to.</param>
/// <param name="RunId">The unique identifier for this run.</param>
/// <param name="Status">The current status of the run.</param>
/// <param name="InputIds">
/// The input IDs folded into this run. A single run can have more than one input ID when a
/// later send is injected into an already-in-flight run rather than starting a new one.
/// </param>
/// <param name="CreatedAt">When the run was created (queued).</param>
/// <param name="UpdatedAt">When the run's status was last updated.</param>
public sealed record RunLedgerEntry(
    string ThreadId,
    string RunId,
    RunStatus Status,
    IReadOnlyList<string> InputIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
