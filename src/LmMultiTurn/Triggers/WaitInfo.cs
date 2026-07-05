namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Lifecycle states of a single wait. A wait starts <see cref="Pending"/> and makes exactly one
/// terminal transition — enforced by the runtime's single-resolution latch — to <see cref="Fired"/>,
/// <see cref="TimedOut"/>, <see cref="Cancelled"/>, or <see cref="Failed"/>.
/// </summary>
public enum WaitState
{
    /// <summary>Armed and waiting.</summary>
    Pending = 0,

    /// <summary>The source's event fired first; the wait resolved with the fire payload.</summary>
    Fired = 1,

    /// <summary>The ceiling deadline elapsed before any fire; the wait resolved with a timed-out outcome.</summary>
    TimedOut = 2,

    /// <summary>Cancelled via <c>CancelWait</c> before any fire or timeout.</summary>
    Cancelled = 3,

    /// <summary>Arming/observation failed, or the wait could not be restored after restart.</summary>
    Failed = 4,
}

/// <summary>
/// Read model of a currently-armed wait, returned by <c>ListWaits</c>. Deliberately omits raw
/// payloads and source internals — it exposes only identity, kind, model-authored label, and timing.
/// </summary>
public sealed record WaitInfo
{
    /// <summary>Canonical wait id (the deferred tool_call_id for a block wait).</summary>
    public required string WaitId { get; init; }

    /// <summary>The registered kind (e.g. <c>"timer"</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Model-authored label, if any.</summary>
    public string? Label { get; init; }

    /// <summary>Lifecycle state. Armed waits report <see cref="WaitState.Pending"/>; terminal waits are removed.</summary>
    public required WaitState State { get; init; }

    /// <summary>When the wait was armed (ISO-8601).</summary>
    public required string ArmedAt { get; init; }

    /// <summary>Absolute ceiling after which the wait times out (ISO-8601).</summary>
    public required string Deadline { get; init; }
}
