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
    /// Serializes a genuine terminal completion's status flip + owned-provider disposal against a
    /// concurrent <c>SubAgentManager.SendMessageAsync</c>. See <see cref="MarkTerminal"/> /
    /// <see cref="TryBeginRunningContinuation"/>. Only synchronous work runs under this lock (no
    /// awaits), so a blocking/backpressured send or a slow provider disposal can never deadlock it.
    /// </summary>
    private readonly object _lifecycleLock = new();

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
    /// Atomically flips the sub-agent out of <see cref="SubAgentStatus.Running"/> at a genuine
    /// terminal completion, under <see cref="_lifecycleLock"/> so it is serialized against
    /// <see cref="TryBeginRunningContinuation"/>. The manager disposes the owned provider only AFTER
    /// this returns, so a concurrent SendMessage can never observe <see cref="SubAgentStatus.Running"/>
    /// — and route a message into the loop — in the window where the provider is being disposed.
    /// </summary>
    public void MarkTerminal(bool isError)
    {
        lock (_lifecycleLock)
        {
            _status = isError ? SubAgentStatus.Error : SubAgentStatus.Completed;
        }
    }

    /// <summary>
    /// Atomically reads whether the loop is currently running and records the continuation's relay
    /// preference, under the same <see cref="_lifecycleLock"/> that guards <see cref="MarkTerminal"/>.
    /// Returns true when the loop is running (the caller injects the message into it) and false when
    /// it has finished (the caller restarts it, recreating a fresh provider), so the running-vs-finished
    /// decision can never straddle a concurrent terminal completion that is about to dispose the provider.
    /// </summary>
    public bool TryBeginRunningContinuation(bool notifyParentOnCompletion)
    {
        lock (_lifecycleLock)
        {
            _notifyParentOnCompletion = notifyParentOnCompletion;
            return _status == SubAgentStatus.Running;
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
