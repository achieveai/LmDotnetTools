namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Everything a <see cref="ITriggerSource"/> needs to arm a single wait. The runtime owns the
/// wait lifecycle (latch, ceiling timeout, cancellation, delivery); a source only observes its
/// event and reports it through the supplied <see cref="ITriggerEventSink"/>.
/// </summary>
public sealed record TriggerArmRequest
{
    /// <summary>Canonical wait identity. For a block wait this is the deferred tool_call_id.</summary>
    public required string WaitId { get; init; }

    /// <summary>The registered kind this arm targets (e.g. <c>"timer"</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Raw per-kind arguments JSON (the <c>args</c> object from the <c>Wait</c> call).</summary>
    public required string ArgsJson { get; init; }

    /// <summary>Optional short, human-readable label echoed in <c>ListWaits</c>.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Reference instant the wait was (re-)armed at. Fresh arms pass <c>now</c>; restart
    /// reconciliation passes the original arm time so a source can compute the remaining delay
    /// relative to when the wait was first created.
    /// </summary>
    public required DateTimeOffset ArmedAt { get; init; }

    /// <summary>
    /// Absolute ceiling for this wait. The runtime enforces this independently (a source need not
    /// honor it); it is provided so time-based sources with no explicit fire time can default to it.
    /// </summary>
    public required DateTimeOffset Deadline { get; init; }
}
