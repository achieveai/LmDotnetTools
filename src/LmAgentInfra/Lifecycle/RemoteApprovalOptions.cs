namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Configuration for remote (out-of-process) tool approval (ADR 0003, ADR 0005). Bound from the
/// <c>Lifecycle:Approval</c> section.
/// <para>
/// Remote approval moves the decision about whether a tool call runs outside the agent process. The
/// limits here bound what an approver — or a caller impersonating traffic aimed at one — can make
/// the host hold onto while a decision is outstanding.
/// </para>
/// </summary>
public sealed class RemoteApprovalOptions
{
    /// <summary>Configuration section name these options are bound from.</summary>
    public const string SectionName = "Lifecycle:Approval";

    /// <summary>
    /// Master switch. <b>Default off.</b> With it off the agent keeps whatever local approval gate
    /// it already had; turning it on is what delegates the decision to a remote approver, and that
    /// is never something to inherit by accident.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum approval requests one owner may have outstanding. Per-owner so a single busy or
    /// wedged owner cannot crowd every other owner out of the shared budget below.
    /// </summary>
    public int MaxPendingPerOwner { get; set; } = 64;

    /// <summary>
    /// Maximum approval requests outstanding across all owners. Reaching it means new requests are
    /// denied rather than queued — fail-closed, per ADR 0003: a tool call that cannot be approved
    /// does not run.
    /// </summary>
    public int MaxPendingTotal { get; set; } = 512;

    /// <summary>
    /// How long a settled request's outcome is remembered after the decision. The tombstone is what
    /// makes a retried decision idempotent: a duplicate POST — the normal consequence of a network
    /// retry — gets the same answer as the original instead of a confusing "unknown request", and
    /// a second, *different* decision is rejected rather than silently overturning the first.
    /// Retention is bounded because remembering forever is a memory leak with a schedule.
    /// </summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum tombstones retained at once, bounding memory when decisions arrive faster than
    /// <see cref="TombstoneRetention"/> expires them. The oldest are evicted first; an evicted
    /// request id is treated as unknown, which denies — the same fail-closed answer as a forged one.
    /// </summary>
    public int MaxTombstones { get; set; } = 4096;

    /// <summary>
    /// Validates the configured values and throws on anything the runtime cannot honor, so a
    /// misconfiguration fails at startup instead of at the first approval.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is out of range or internally
    /// inconsistent.</exception>
    public void Validate()
    {
        if (MaxPendingPerOwner <= 0)
        {
            throw Invalid(nameof(MaxPendingPerOwner), "must be greater than zero");
        }

        if (MaxPendingTotal <= 0)
        {
            throw Invalid(nameof(MaxPendingTotal), "must be greater than zero");
        }

        if (MaxPendingPerOwner > MaxPendingTotal)
        {
            throw Invalid(nameof(MaxPendingPerOwner), $"must not exceed {nameof(MaxPendingTotal)} ({MaxPendingTotal})");
        }

        if (MaxTombstones <= 0)
        {
            throw Invalid(nameof(MaxTombstones), "must be greater than zero");
        }

        if (TombstoneRetention <= TimeSpan.Zero)
        {
            throw Invalid(nameof(TombstoneRetention), "must be greater than zero");
        }
    }

    private static InvalidOperationException Invalid(string name, string requirement) =>
        new($"{SectionName}:{name} {requirement}.");
}
