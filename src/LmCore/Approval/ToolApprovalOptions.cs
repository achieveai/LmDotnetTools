namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// How a host wants tool calls gated. The default instance configures nothing, and a preparer
/// built from it is inert — see <see cref="ToolInvocationPreparer.IsEnabled"/>.
/// </summary>
public sealed record ToolApprovalOptions
{
    /// <summary>The default <see cref="MaxApprovalWait"/>: five minutes.</summary>
    public static readonly TimeSpan DefaultMaxApprovalWait = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _maxApprovalWait = DefaultMaxApprovalWait;
    private readonly int _maxPendingApprovals = int.MaxValue;

    /// <summary>
    /// Evaluated first, before the host policy and before any gate. Refusals are reported as
    /// <see cref="ToolApprovalOutcomes.ProviderPolicyDenied"/>.
    /// </summary>
    public IToolExecutionPolicy? ProviderPolicy { get; init; }

    /// <summary>
    /// Evaluated after <see cref="ProviderPolicy"/> and before any gate. Refusals are reported as
    /// <see cref="ToolApprovalOutcomes.HostPolicyDenied"/>.
    /// </summary>
    public IToolExecutionPolicy? HostPolicy { get; init; }

    /// <summary>
    /// The approvers that must <b>all</b> allow a call before it runs. Consulted concurrently;
    /// the first blocking verdict short-circuits the rest.
    /// </summary>
    public IReadOnlyList<IToolApprovalGate> Gates { get; init; } = [];

    /// <summary>
    /// Requires an approval decision even when <see cref="Gates"/> is empty — in which case every
    /// call is blocked with <see cref="ToolApprovalOutcomes.MissingApprover"/>.
    /// </summary>
    /// <remarks>
    /// Set this when approval is a deployment requirement rather than a consequence of what
    /// happens to be wired up. Without it, losing the gate registration during a refactor would
    /// silently turn gating off; with it, the calls fail closed and the misconfiguration is loud.
    /// </remarks>
    public bool RequireApproval { get; init; }

    /// <summary>
    /// The longest a decision may be awaited. Must be finite and positive; defaults to
    /// <see cref="DefaultMaxApprovalWait"/>.
    /// </summary>
    /// <remarks>
    /// This is a ceiling, not the wait itself. The effective expiry is the earliest of this, any
    /// operation deadline supplied with the request, and run or turn cancellation — so a pending
    /// approval can never outlive the run that asked for it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is zero, negative, or infinite.
    /// </exception>
    public TimeSpan MaxApprovalWait
    {
        get => _maxApprovalWait;
        init
        {
            if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "MaxApprovalWait must be finite and positive — an unbounded approval wait would "
                        + "let a pending decision outlive the run that requested it."
                );
            }

            _maxApprovalWait = value;
        }
    }

    /// <summary>
    /// The most approval decisions that may be outstanding at once across this preparer. Calls
    /// beyond the limit are blocked with <see cref="ToolApprovalOutcomes.Overload"/> rather than
    /// queued. Defaults to <see cref="int.MaxValue"/> (no limit).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxPendingApprovals
    {
        get => _maxPendingApprovals;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "MaxPendingApprovals must be positive — zero would block every call as overloaded."
                );
            }

            _maxPendingApprovals = value;
        }
    }

    /// <summary>
    /// The clock used for the approval deadline. Defaults to <see cref="TimeProvider.System"/>;
    /// tests substitute a controllable one so expiry can be exercised without waiting.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
