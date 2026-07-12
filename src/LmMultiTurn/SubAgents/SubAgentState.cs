using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Lifecycle status of a sub-agent.
/// </summary>
public enum SubAgentStatus
{
    Running,
    Completed,
    Error,
    Stopped,
}

/// <summary>
/// Summary of a single turn within a sub-agent's execution.
/// Used to provide lightweight progress visibility to the parent.
/// </summary>
public record SubAgentTurnSummary
{
    public required string MessageType { get; init; }
    public string? ToolName { get; init; }
    public string? ToolArgsPreview { get; init; }
    public string? TextPreview { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// How a <c>SubAgentManager.SendMessageAsync</c> continuation must proceed, decided atomically by
/// <see cref="SubAgentState.BeginContinuation"/> against a concurrent terminal completion and other
/// concurrent continuations.
/// </summary>
internal enum ContinuationMode
{
    /// <summary>Inject into the still-running loop under a send lease that terminal disposal awaits.</summary>
    Inject,

    /// <summary>Restart the finished run with a fresh provider; this caller owns the restart.</summary>
    Restart,

    /// <summary>Another caller owns an in-flight restart; await it, then re-evaluate.</summary>
    AwaitRestart,
}

/// <summary>
/// Result of <see cref="SubAgentState.BeginContinuation"/>. For <see cref="ContinuationMode.AwaitRestart"/>,
/// <see cref="RestartCompleted"/> completes when the owning restart finishes.
/// </summary>
internal readonly record struct ContinuationDecision(ContinuationMode Mode, Task? RestartCompleted);

/// <summary>
/// Mutable state tracker for a running sub-agent instance.
/// Internal to the SubAgents module; not exposed to consumers.
/// </summary>
internal class SubAgentState
{
    public required string AgentId { get; init; }
    public required string TemplateName { get; init; }
    public required string Task { get; init; }
    public required IMultiTurnAgent Agent { get; set; }

    /// <summary>
    /// The template and spawn inputs needed to recreate a completed owned-provider run before a
    /// continuation. Borrowed providers retain the existing loop.
    /// </summary>
    public required SubAgentTemplate Template { get; init; }
    public string? ModelOverride { get; init; }
    public string[]? AddTools { get; init; }
    public string[]? RemoveTools { get; init; }

    /// <summary>
    /// Provider pipeline created specifically for this sub-agent. Null means the provider is
    /// borrowed/shared and must not be disposed by this state.
    /// </summary>
    public IStreamingAgent? OwnedProviderAgent { get; private set; }

    // Owned-provider disposal progress. A single caller transitions Idle -> InProgress and performs
    // the disposal; concurrent callers back off. A successful disposal latches Disposed; a FAILED
    // attempt resets the guard to Idle so a later cleanup path (completion, restart, manager dispose)
    // can retry instead of leaking the provider — the guard is never permanently latched on failure.
    private const int OwnedProviderDisposeIdle = 0;
    private const int OwnedProviderDisposeInProgress = 1;
    private const int OwnedProviderDisposeDisposed = 2;
    private int _ownedProviderDisposeState;

    /// <summary>
    /// Guards the sub-agent lifecycle bookkeeping — the status transition, the outstanding inject-send
    /// lease count, and the single-flight restart claim — so a continuation's admission decision, a
    /// terminal owned-provider disposal, and a restart never interleave incorrectly. Only synchronous
    /// work runs under this lock (no awaits), so a blocking/backpressured send or a slow provider
    /// disposal can never deadlock it; the awaitable coordination is done via the drain/restart signals.
    /// </summary>
    private readonly object _lifecycleLock = new();

    // Terminal owned-provider disposal is in progress. Once set (for an owned provider) no new
    // inject-continuation is admitted — a concurrent SendMessage is routed to a fresh-provider restart
    // instead of injecting into the provider being disposed.
    private bool _terminating;

    // Count of admitted inject-continuations whose SendAsync is still in flight. A terminal disposal
    // waits for this to reach zero (via _sendLeasesDrained) so disposal can never overlap a send
    // through the owned provider.
    private int _activeSendLeases;
    private TaskCompletionSource<bool>? _sendLeasesDrained;

    // Single-flight restart claim. Exactly one caller restarts a finished run; concurrent continuations
    // await _restartCompleted and then re-evaluate (the restart flips the loop back to Running, so they
    // inject into it) — so two callers can never both enter RestartRunAsync.
    private bool _restarting;
    private TaskCompletionSource<bool>? _restartCompleted;

    private static TaskCompletionSource<bool> NewLifecycleSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Optional caller-supplied handle so SendMessage can address this agent by name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// When true (background spawn/continuation), run completion is relayed to the
    /// parent as an injected user message. When false (synchronous call), the tool
    /// handler awaiting <see cref="Completion"/> returns the result directly instead.
    /// </summary>
    private volatile bool _notifyParentOnCompletion;
    public bool NotifyParentOnCompletion { get => _notifyParentOnCompletion; set => _notifyParentOnCompletion = value; }

    public Task? RunTask { get; set; }
    public Task? MonitorTask { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public ConcurrentQueue<SubAgentTurnSummary> TurnBuffer { get; } = new();

    private volatile SubAgentStatus _status = SubAgentStatus.Running;
    public SubAgentStatus Status { get => _status; set => _status = value; }

    /// <summary>
    /// Decides — atomically against a concurrent terminal completion and other concurrent
    /// continuations — how <c>SubAgentManager.SendMessageAsync</c> must continue this sub-agent, and
    /// records the continuation's relay preference.
    /// <list type="bullet">
    /// <item><description><see cref="ContinuationMode.Inject"/>: the loop is running and no owned-provider
    /// disposal is under way. A send lease is taken; the caller injects, then calls
    /// <see cref="EndInjectLease"/>. A terminal disposal awaits this lease, so a send can never race the
    /// provider being disposed.</description></item>
    /// <item><description><see cref="ContinuationMode.Restart"/>: the run finished (or its owned provider
    /// is being disposed). This caller owns the restart; it runs <c>RestartRunAsync</c> then calls
    /// <see cref="EndRestart"/>.</description></item>
    /// <item><description><see cref="ContinuationMode.AwaitRestart"/>: another caller already owns the
    /// restart. The caller awaits <see cref="ContinuationDecision.RestartCompleted"/> and re-evaluates —
    /// the restart flips the loop back to Running, so the retry injects into it.</description></item>
    /// </list>
    /// </summary>
    public ContinuationDecision BeginContinuation(bool notifyParentOnCompletion)
    {
        lock (_lifecycleLock)
        {
            _notifyParentOnCompletion = notifyParentOnCompletion;

            // Inject into the live loop only when it is genuinely running AND an owned-provider terminal
            // disposal is not under way. Once disposal has begun for an owned provider, route to restart
            // so we never inject through a provider that is being torn down.
            if (_status == SubAgentStatus.Running && !(_terminating && OwnedProviderAgent is not null))
            {
                _activeSendLeases++;
                return new ContinuationDecision(ContinuationMode.Inject, null);
            }

            if (_restarting)
            {
                _restartCompleted ??= NewLifecycleSignal();
                return new ContinuationDecision(ContinuationMode.AwaitRestart, _restartCompleted.Task);
            }

            _restarting = true;
            _restartCompleted = NewLifecycleSignal();
            return new ContinuationDecision(ContinuationMode.Restart, null);
        }
    }

    /// <summary>
    /// Releases an inject send lease taken by <see cref="BeginContinuation"/> (call in a finally after
    /// the inject SendAsync). When the last lease drains during a terminal disposal, the waiting
    /// disposal is signalled so it can proceed.
    /// </summary>
    public void EndInjectLease()
    {
        lock (_lifecycleLock)
        {
            _activeSendLeases--;
            if (_activeSendLeases == 0 && _sendLeasesDrained is not null)
            {
                _ = _sendLeasesDrained.TrySetResult(true);
            }
        }
    }

    /// <summary>
    /// Releases the restart claim taken by <see cref="BeginContinuation"/> (call in a finally after
    /// <c>RestartRunAsync</c>, success or failure) and wakes any continuations awaiting the restart so
    /// they re-evaluate against the re-armed (or still-finished, on failure) status.
    /// </summary>
    public void EndRestart()
    {
        TaskCompletionSource<bool>? toSignal;
        lock (_lifecycleLock)
        {
            _restarting = false;
            toSignal = _restartCompleted;
            _restartCompleted = null;
        }

        _ = toSignal?.TrySetResult(true);
    }

    /// <summary>
    /// Begins a genuine terminal completion. Blocks new inject admissions (for an owned provider), waits
    /// for any in-flight admitted send to finish so the imminent owned-provider disposal cannot overlap a
    /// send through it, then flips the status terminal. The manager disposes the owned provider only
    /// AFTER this returns; a continuation that arrives now observes the finished status (or the
    /// disposing owned provider) and takes the restart path — recreating a fresh provider — instead of
    /// injecting into the one being disposed.
    /// </summary>
    public async Task BeginTerminalDisposalAsync(bool isError)
    {
        Task? drain = null;
        lock (_lifecycleLock)
        {
            _terminating = true;

            // Only an owned provider is disposed at completion, so only then is there anything a send
            // could race; wait for outstanding inject leases to drain before flipping terminal.
            if (OwnedProviderAgent is not null && _activeSendLeases > 0)
            {
                _sendLeasesDrained = NewLifecycleSignal();
                drain = _sendLeasesDrained.Task;
            }
        }

        if (drain is not null)
        {
            await drain;
            lock (_lifecycleLock)
            {
                _sendLeasesDrained = null;
            }
        }

        lock (_lifecycleLock)
        {
            _status = isError ? SubAgentStatus.Error : SubAgentStatus.Completed;
        }
    }

    /// <summary>
    /// Clears the terminal-disposal flag after the owned provider has been disposed (call in a finally),
    /// so a subsequent restart's re-arm to Running admits injects again.
    /// </summary>
    public void EndTerminalDisposal()
    {
        lock (_lifecycleLock)
        {
            _terminating = false;
        }
    }

    public IConversationStore? Store { get; set; }

    /// <summary>
    /// Set to true when SendToParentAsync fails, so CheckAgent/Peek can surface the error.
    /// </summary>
    public bool SendToParentFailed { get; set; }

    /// <summary>
    /// Error message from the most recent failed SendToParentAsync call.
    /// </summary>
    public string? SendToParentError { get; set; }

    /// <summary>
    /// Stores the final text result after the sub-agent completes.
    /// Populated from the last assistant TextMessage before RunCompletedMessage.
    /// </summary>
    public string? LastResult { get; set; }

    /// <summary>
    /// Signal resolved when the current run completes: the final assistant text on
    /// success, faulted on error, cancelled when the run ends without completing.
    /// Synchronous Agent/SendMessage calls await this to return the result as the
    /// tool result.
    /// </summary>
    public TaskCompletionSource<string> Completion { get; private set; } = CreateCompletionSource();

    public async ValueTask DisposeOwnedProviderAgentAsync()
    {
        if (OwnedProviderAgent is null)
        {
            return;
        }

        // Claim the disposal: only the caller that transitions Idle -> InProgress performs it; a
        // caller that finds it already Disposed (or another disposal in progress) backs off. Crucially
        // the guard is set to its terminal Disposed value only AFTER a successful DisposeAsync/Dispose;
        // a failed attempt resets it to Idle in the catch below so a later cleanup can retry rather
        // than leak the provider behind a guard that latched before the work actually succeeded.
        if (
            Interlocked.CompareExchange(
                ref _ownedProviderDisposeState,
                OwnedProviderDisposeInProgress,
                OwnedProviderDisposeIdle
            ) != OwnedProviderDisposeIdle
        )
        {
            return;
        }

        try
        {
            if (OwnedProviderAgent is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (OwnedProviderAgent is IDisposable disposable)
            {
                disposable.Dispose();
            }

            Volatile.Write(ref _ownedProviderDisposeState, OwnedProviderDisposeDisposed);
        }
        catch
        {
            // Disposal failed: reset the guard so a subsequent cleanup path can retry the disposal.
            Volatile.Write(ref _ownedProviderDisposeState, OwnedProviderDisposeIdle);
            throw;
        }
    }

    /// <summary>
    /// Indicates that the current run's owned provider has been disposed at completion and a
    /// continuation must create a fresh provider pipeline.
    /// </summary>
    public bool HasDisposedOwnedProviderAgent =>
        OwnedProviderAgent is not null
        && Volatile.Read(ref _ownedProviderDisposeState) == OwnedProviderDisposeDisposed;

    /// <summary>
    /// Assigns the provider created for the current run. This resets the per-run disposal guard
    /// when a completed owned-provider sub-agent is recreated for a continuation.
    /// </summary>
    public void SetOwnedProviderAgent(IStreamingAgent? ownedProviderAgent)
    {
        OwnedProviderAgent = ownedProviderAgent;
        Volatile.Write(ref _ownedProviderDisposeState, OwnedProviderDisposeIdle);
    }

    /// <summary>
    /// Guards the read-check-replace of <see cref="Completion"/> against the monitor
    /// thread resolving the same source, so reset and resolution never race.
    /// </summary>
    private readonly object _completionLock = new();

    /// <summary>
    /// Replaces an already-resolved completion with a fresh one so a follow-up
    /// run (SendMessage continuation) can be awaited. A pending (unresolved)
    /// completion is kept — existing waiters observe the next resolution.
    /// </summary>
    public void ResetCompletionIfFinished()
    {
        lock (_completionLock)
        {
            if (Completion.Task.IsCompleted)
            {
                Completion = CreateCompletionSource();
            }
        }
    }

    /// <summary>
    /// Resolves the current completion with a successful result under the completion
    /// lock, so it cannot race a concurrent <see cref="ResetCompletionIfFinished"/>.
    /// </summary>
    public bool TryCompleteWithResult(string result)
    {
        lock (_completionLock)
        {
            return Completion.TrySetResult(result);
        }
    }

    /// <summary>
    /// Faults the current completion under the completion lock, so it cannot race a
    /// concurrent <see cref="ResetCompletionIfFinished"/>.
    /// </summary>
    public bool TryCompleteWithException(Exception exception)
    {
        lock (_completionLock)
        {
            return Completion.TrySetException(exception);
        }
    }

    private static TaskCompletionSource<string> CreateCompletionSource()
    {
        // RunContinuationsAsynchronously: the monitor task resolves this signal;
        // running waiter continuations inline would execute tool-handler code on
        // the monitor's subscription thread.
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Idempotent, per-gate-acquisition-"epoch" release guard for the manager's shared
/// concurrency <see cref="SemaphoreSlim"/>. Every successful <c>_concurrencyGate.WaitAsync</c>
/// call - the original spawn, or a later restart via <c>RestartRunAsync</c> against the SAME,
/// reused <see cref="SubAgentState"/> - gets its own fresh <see cref="GateReleaseGuard"/>
/// instance, created immediately after the acquisition succeeds and threaded explicitly through
/// to both the monitor task and the acquiring method's own failure-cleanup path. Whichever of
/// the two notices the run's end first calls <see cref="ReleaseOnce"/>; the other's call is then
/// a safe no-op.
/// <para>
/// A single flag/field stored directly on the long-lived <see cref="SubAgentState"/> (reset in
/// place for each new epoch) cannot serve this role: <c>RestartRunAsync</c> cancels the previous
/// epoch's monitor but does not wait for its <c>finally</c> block to run before resetting the
/// shared flag and starting the next epoch's monitor, so the previous epoch's still-in-flight
/// release can fire AFTER the flag was reset for the new epoch - silently consuming the new
/// epoch's only legitimate release as a spurious extra one. A fresh, independent instance per
/// epoch makes that impossible: each epoch's monitor and cleanup path close over their own
/// object, so a late release from an old epoch can never be mistaken for a new epoch's release.
/// </para>
/// </summary>
internal sealed class GateReleaseGuard
{
    private int _released;

    /// <summary>
    /// Releases <paramref name="gate"/> exactly once for this guard's epoch. Over-releasing a
    /// <see cref="SemaphoreSlim"/> corrupts its count - it can throw
    /// <see cref="SemaphoreFullException"/> and, short of that, silently lets more than
    /// MaxConcurrentSubAgents run concurrently.
    /// </summary>
    public void ReleaseOnce(SemaphoreSlim gate)
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _ = gate.Release();
        }
    }
}
