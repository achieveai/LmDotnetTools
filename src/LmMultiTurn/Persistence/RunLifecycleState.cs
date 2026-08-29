using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Where a run sits between having started and having reached a terminal boundary.
/// </summary>
public enum RunLifecyclePhase
{
    /// <summary>The run has started and has not reached a terminal boundary.</summary>
    Running,

    /// <summary>The run reached a terminal boundary. Terminal forever.</summary>
    Terminal,
}

/// <summary>
/// What a resolution attempt did.
/// </summary>
/// <remarks>
/// A store reports failures of its own — a disk error, a closed connection — by throwing, and
/// cancellation by throwing <see cref="OperationCanceledException"/>. Those are not outcomes here
/// because they say nothing about the call being resolved: the call is left unresolved and the
/// attempt is safe to retry. Only the states below describe what happened to the call itself.
/// </remarks>
public enum DeferredResolutionOutcome
{
    /// <summary>The call was outstanding and this attempt resolved it.</summary>
    Resolved,

    /// <summary>
    /// The call was already resolved with the same content. The attempt changed nothing and is a
    /// success — this is what a retried delivery of the same webhook looks like.
    /// </summary>
    Duplicate,

    /// <summary>No deferred call with that identifier exists on the thread.</summary>
    NotFound,

    /// <summary>
    /// The call was already resolved with <em>different</em> content. Nothing is overwritten: the
    /// first resolution stands, and the caller is told its answer disagrees with the committed one.
    /// </summary>
    Conflict,
}

/// <summary>
/// A tool call that deferred, and — once it resolves — how it resolved.
/// </summary>
/// <remarks>
/// This is the durable half of the deferral the loop tracks in memory. It exists so that a call
/// deferred by a process that then died is still known to the process that replaces it, and so that
/// a resolution arriving twice can be recognized as the same resolution rather than applied twice.
/// </remarks>
public sealed record DeferredToolCallRecord
{
    /// <summary>The tool call that deferred.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>The tool's registered name.</summary>
    public required string ToolName { get; init; }

    /// <summary>The turn that requested the call.</summary>
    public string? GenerationId { get; init; }

    /// <summary>
    /// This deferral's position among its run's deferrals, starting at <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Assigned by the store when the deferral is accepted, not by the caller, so that the order is
    /// the committed order rather than whatever order concurrent callers happened to arrive in.
    /// </remarks>
    public int Ordinal { get; init; }

    /// <summary>When the handler signaled deferral.</summary>
    public DateTimeOffset DeferredAt { get; init; }

    /// <summary>When the call resolved. Absent while it is still outstanding.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>
    /// A caller-supplied fingerprint of the resolution content, used to tell a retry of the same
    /// resolution from a conflicting second one. Absent while the call is outstanding.
    /// </summary>
    /// <remarks>
    /// The store never interprets this; it only compares it for ordinal equality. A caller that
    /// supplies an unstable fingerprint gets <see cref="DeferredResolutionOutcome.Conflict"/> for
    /// its own retries, so the fingerprint must be derived from the resolution content alone.
    /// </remarks>
    public string? ResolutionFingerprint { get; init; }

    /// <summary>
    /// The child run this resolution caused, when it caused one.
    /// </summary>
    /// <remarks>
    /// Absent for a resolution that arrived while its requesting run was still running: that
    /// resolution is folded into the existing run instead of starting a child. See ADR 0004.
    /// </remarks>
    public string? ChildRunId { get; init; }

    /// <summary>Whether the call has resolved.</summary>
    /// <remarks>Derived from <see cref="ResolvedAt"/>; never persisted separately.</remarks>
    [JsonIgnore]
    public bool IsResolved => ResolvedAt != null;
}

/// <summary>
/// The durable lifecycle state of one run: when it started, what caused it, where it came from,
/// what it deferred, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// This sits <em>beside</em> <see cref="RunLedgerEntry"/> rather than extending it. The ledger
/// answers a caller's question — "what is the status of the input I sent?" — and its shape is part
/// of the status API's contract. This answers a subscriber's question — "what happened in this
/// run, and how does it connect to the others?" — and carries lineage and deferral state the
/// ledger has no reason to know about. Keeping them separate means lifecycle observation can be
/// enabled, disabled, or stored elsewhere without perturbing status reporting.
/// </para>
/// <para>
/// Every member is a property with an initializer rather than a positional record parameter, so a
/// later slice can add a member without breaking every construction site.
/// </para>
/// </remarks>
public sealed record RunLifecycleState
{
    /// <summary>The thread this run belongs to.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The run.</summary>
    public required string RunId { get; init; }

    /// <summary>The run's originating turn.</summary>
    public string GenerationId { get; init; } = string.Empty;

    /// <summary>
    /// The run that caused this one — a resumed run's predecessor, a delayed result's requesting
    /// run, or the parent-thread run that spawned this sub-agent.
    /// </summary>
    public string? ParentRunId { get; init; }

    /// <summary>The thread of the agent that spawned this run's agent, for a sub-agent.</summary>
    public string? ParentThreadId { get; init; }

    /// <summary>The tool call that spawned this run's agent, when one did.</summary>
    public string? SpawningToolCallId { get; init; }

    /// <summary>The sub-agent that owns this run, when a sub-agent does.</summary>
    public string? SubAgentId { get; init; }

    /// <summary>What caused the run. See <c>LifecycleRunCauseKinds</c>.</summary>
    public string CauseKind { get; init; } = string.Empty;

    /// <summary>
    /// The tool call whose result caused the run, for a delayed-result child or a sub-agent spawn.
    /// </summary>
    public string? CauseToolCallId { get; init; }

    /// <summary>Whether the run reached a terminal boundary.</summary>
    public RunLifecyclePhase Phase { get; init; } = RunLifecyclePhase.Running;

    /// <summary>
    /// How the run ended. See <c>LifecycleRunOutcomes</c>. Absent while
    /// <see cref="Phase"/> is <see cref="RunLifecyclePhase.Running"/>.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>How many turns the run performed.</summary>
    public int TurnCount { get; init; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the run reached its terminal boundary. Absent while it is running.</summary>
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>
    /// The tool calls this run deferred, in committed order.
    /// </summary>
    public IReadOnlyList<DeferredToolCallRecord> DeferredToolCalls { get; init; } = [];

    /// <summary>
    /// The deferred calls that have not yet resolved.
    /// </summary>
    /// <remarks>Derived from <see cref="DeferredToolCalls"/>; never persisted separately.</remarks>
    [JsonIgnore]
    public IEnumerable<DeferredToolCallRecord> UnresolvedToolCalls => DeferredToolCalls.Where(d => !d.IsResolved);
}
