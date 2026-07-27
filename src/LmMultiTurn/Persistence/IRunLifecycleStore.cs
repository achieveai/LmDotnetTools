namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Durable lifecycle state for runs and their deferred tool calls, kept beside — never inside —
/// <see cref="IRunLedgerStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two interfaces answer different questions and are versioned independently: the ledger
/// backs the status API, this backs lifecycle observation and delayed-result resolution. A store
/// may implement both, and the built-in stores do, but nothing requires it.
/// </para>
/// <para>
/// <b>The two mutations that must not race are expressed as attempts.</b> Marking a run terminal
/// and resolving a deferred call are both first-writer-wins: an implementation performs them as a
/// single conditional write and reports whether this caller was the one that committed. A
/// read-modify-write around <see cref="LoadRunLifecycleAsync"/> is not an acceptable
/// implementation, because two callers reaching a terminal boundary at once would then both
/// believe they were first and a run would be reported as completed twice.
/// </para>
/// <para>
/// Failures of the store itself — a disk error, a closed connection — are thrown, not returned. A
/// caller that sees an exception knows the state is unchanged and the operation is safe to retry.
/// </para>
/// </remarks>
public interface IRunLifecycleStore
{
    /// <summary>
    /// Durably records that a run has started.
    /// </summary>
    /// <param name="state">
    /// The starting state. <see cref="RunLifecycleState.Phase"/> must be
    /// <see cref="RunLifecyclePhase.Running"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Upsert semantics on <see cref="RunLifecycleState.RunId"/>, so a retried start after a failed
    /// write is harmless. Recording a start for a run that already reached a terminal boundary
    /// throws — that is a caller bug, not a race, and silently resurrecting the run would erase its
    /// outcome.
    /// </remarks>
    Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default);

    /// <summary>
    /// Loads one run's lifecycle state.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state, or <see langword="null"/> when no such run was recorded.</returns>
    Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Lists a thread's runs, most recently started first.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The thread's runs, or an empty list when it has none.</returns>
    Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
        string threadId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the runs of a thread that started but never reached a terminal boundary.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The still-running runs, oldest first.</returns>
    /// <remarks>
    /// Called once on restart. Every run this returns belonged to a process that is gone, so each
    /// is terminalized as interrupted — a run that started must complete, or a subscriber is left
    /// holding an unpaired start forever.
    /// </remarks>
    Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a run to <see cref="RunLifecyclePhase.Terminal"/>, if it is not there
    /// already.
    /// </summary>
    /// <param name="runId">The run to terminalize.</param>
    /// <param name="outcome">How the run ended. See <c>LifecycleRunOutcomes</c>.</param>
    /// <param name="turnCount">How many turns the run performed.</param>
    /// <param name="terminalAt">When the run reached its terminal boundary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call committed the terminal state;
    /// <see langword="false"/> when the run was already terminal or was never recorded as started.
    /// </returns>
    /// <remarks>
    /// The caller that gets <see langword="true"/> — and only that caller — publishes the run's
    /// completion. This is what keeps a run that ends by cancellation racing its own error path
    /// from producing two terminal events.
    /// </remarks>
    Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default);

    /// <summary>
    /// Records that a tool call in <paramref name="runId"/> has deferred, assigning it its ordinal.
    /// </summary>
    /// <param name="runId">The run that requested the call.</param>
    /// <param name="record">
    /// The deferral. <see cref="DeferredToolCallRecord.Ordinal"/> is ignored — the store assigns it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The record as committed, including its assigned ordinal.</returns>
    /// <remarks>
    /// Recording the same <see cref="DeferredToolCallRecord.ToolCallId"/> twice returns the
    /// original record unchanged rather than assigning a second ordinal, so a retried write cannot
    /// duplicate a deferral. Throws when the run was never recorded as started.
    /// </remarks>
    Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically resolves a deferred tool call anywhere on a thread.
    /// </summary>
    /// <param name="threadId">The thread the call belongs to.</param>
    /// <param name="toolCallId">The deferred call to resolve.</param>
    /// <param name="resolutionFingerprint">
    /// A stable fingerprint of the resolution content, compared ordinally against a committed
    /// resolution to tell a retry from a conflict.
    /// </param>
    /// <param name="childRunId">
    /// The child run this resolution causes, when it causes one; <see langword="null"/> when the
    /// resolution is folded into a run that is still going.
    /// </param>
    /// <param name="resolvedAt">When the resolution arrived.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the attempt did. See <see cref="DeferredResolutionOutcome"/>.</returns>
    /// <remarks>
    /// Keyed by thread rather than by run because a caller resolving a call — a webhook receiver, a
    /// UI — knows the tool call it was given and generally not the run that requested it, which by
    /// then may have ended.
    /// </remarks>
    Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default);
}
