using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Host-configurable limits and extra source registrations for the Wait/trigger primitive. Passed
/// to <c>MultiTurnAgentLoop</c> to enable the <c>Wait</c> / <c>CancelWait</c> / <c>ListWaits</c>
/// tools. When provided, the built-in one-shot <c>timer</c> source is registered automatically;
/// <see cref="AdditionalRegistrations"/> adds host-specific kinds.
/// </summary>
public sealed record TriggerOptions
{
    /// <summary>
    /// Maximum number of simultaneously-armed waits. Arming beyond this returns a structured
    /// <c>rejected</c> result (not an exception, not a park) so the model can react.
    /// </summary>
    public int MaxConcurrentWaits { get; init; } = 16;

    /// <summary>Maximum bytes of source-supplied payload merged into a delivered result; excess is truncated.</summary>
    public int MaxPayloadBytes { get; init; } = 8 * 1024;

    /// <summary>Hard ceiling on a block wait's duration. A requested timeout larger than this is clamped down.</summary>
    public TimeSpan MaxBlockWaitDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long an arm waits to acquire a concurrency slot before rejecting with
    /// <c>max_concurrent_waits</c>. Kept small so a full gate rejects promptly.
    /// </summary>
    public TimeSpan GateAcquireTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Host-specific trigger kinds to register in addition to the built-in <c>timer</c>.</summary>
    public IReadOnlyList<TriggerSourceRegistration> AdditionalRegistrations { get; init; } = [];

    /// <summary>When set (with <see cref="ThreadId"/>), notify-mode waits persist here so they survive
    /// a restart. Null disables durable notify restore (notify waits are then process-lifetime only).</summary>
    public INotifyWaitStore? NotifyWaitStore { get; init; }

    /// <summary>Thread scope for <see cref="NotifyWaitStore"/> rows. Required when the store is set.</summary>
    public string? ThreadId { get; init; }
}
