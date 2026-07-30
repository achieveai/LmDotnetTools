using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Everything the loop knows about tool calls that deferred: which are still outstanding, whether
/// the run that asked for them ended waiting, and — when a result finally arrives — whether that
/// result has to start a child run and whether it is the one allowed to continue the conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a collaborator.</b> The rule being kept here — <i>a provider request is only ever made
/// with a complete set of tool results</i> — is a rule about the deferred set, not about the loop.
/// Held as loose fields on the loop it was four variables (<c>_lastDeferringRunId</c>,
/// <c>_lastDeferringGenerationId</c>, <c>_resumeScheduled</c>, and the deferred map) that only made
/// sense read together, and it modelled a <i>wave</i>: one resume for however many results arrived,
/// with the last writer deciding which run got resumed. That shape cannot express what
/// <see href="../../docs/adrs/0004-delayed-tool-results-as-child-runs.md">ADR 0004</see> requires —
/// one child run per result, in resolution order, with exactly one of them continuing.
/// </para>
/// <para>
/// <b>Capacity is reserved when the call defers, not when it resolves.</b> A resolution arrives on
/// an arbitrary thread — a webhook, a UI callback, a trigger firing — and must never be turned away
/// for want of room, because at that point the result already exists and dropping it would strand
/// the run forever. So the seat is taken at deferral acceptance, while the loop is still on its own
/// thread and can propagate a failure as an ordinary run error. Since exactly one committed cause
/// can ever come out of one reservation, the queue is bounded by the number of deferrals the loop
/// accepted, and enqueueing a cause cannot block or fail.
/// </para>
/// <para>
/// <b>Resolution order is commit order.</b> The ordinal is assigned at the same instant, under the
/// same lock, as the reservation's removal and the ownership decision, so concurrent resolutions
/// land in a total order and the queue hands them to the loop in it.
/// </para>
/// <para>
/// Not thread-safe by convention but by construction: every member takes <c>_lock</c>, because the
/// loop thread (parking, draining) and arbitrary caller threads (resolving) both reach it.
/// </para>
/// </remarks>
internal sealed class DelayedResultCoordinator
{
    private readonly object _lock = new();

    // Outstanding deferrals, keyed by ToolCallId. A key is present from deferral acceptance until
    // the resolution that removes it commits.
    private readonly Dictionary<string, DeferredEntry> _entries = new(StringComparer.Ordinal);

    // Committed causes the loop has not run yet, in commit order. Bounded by the number of
    // accepted deferrals — see the capacity note above.
    private readonly Queue<DelayedCause> _causes = new();

    private long _ordinal;

    /// <summary>True when no tool call is currently deferred.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count == 0;
            }
        }
    }

    /// <summary>
    /// True while a committed result is still waiting for its child run. Ordinary queued input
    /// waits behind this, so a delayed continuation is never overtaken by a fresh turn.
    /// </summary>
    public bool HasPendingCauses
    {
        get
        {
            lock (_lock)
            {
                return _causes.Count > 0;
            }
        }
    }

    /// <summary>How many committed causes are still waiting for their child run.</summary>
    public int PendingCauseCount
    {
        get
        {
            lock (_lock)
            {
                return _causes.Count;
            }
        }
    }

    /// <summary>
    /// The most recent run that ended waiting on a deferral. Inputs that arrive while the
    /// conversation is parked are persisted under it, so they survive a restart attached to the run
    /// that was actually in context when they arrived.
    /// </summary>
    public string? LastParkedRunId
    {
        get
        {
            lock (_lock)
            {
                return _lastParkedRunId;
            }
        }
    }

    private string? _lastParkedRunId;

    /// <summary>
    /// Takes the seat for a tool call that has just deferred.
    /// </summary>
    /// <param name="entry">The deferral.</param>
    /// <param name="parked">
    /// Whether the run that requested it has already ended. True only for entries rebuilt from
    /// persisted history: the process that owned that run is gone, so its resolution can never be
    /// folded back into it and must cause a child run.
    /// </param>
    /// <returns>False when this call was already reserved, so the caller does not double-register.</returns>
    public bool TryReserve(DeferredEntry entry, bool parked = false)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            if (_entries.ContainsKey(entry.ToolCallId))
            {
                return false;
            }

            entry.Parked = parked;
            _entries[entry.ToolCallId] = entry;
            return true;
        }
    }

    /// <summary>
    /// Gives the seat back. Used only to unwind a reservation whose placeholder failed to persist —
    /// a half-applied deferral would leave a resolution with nothing to resolve.
    /// </summary>
    public bool Release(string toolCallId)
    {
        lock (_lock)
        {
            return _entries.Remove(toolCallId);
        }
    }

    /// <summary>The outstanding deferrals, in no particular order.</summary>
    public IReadOnlyList<DeferredEntry> Snapshot()
    {
        lock (_lock)
        {
            return [.. _entries.Values];
        }
    }

    /// <summary>How many of a turn's tool calls are still unresolved.</summary>
    public int CountFor(string generationId)
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var entry in _entries.Values)
            {
                if (string.Equals(entry.GenerationId, generationId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Records that <paramref name="runId"/> is ending because <paramref name="generationId"/> left
    /// tool calls unresolved, and reports whether it did.
    /// </summary>
    /// <returns>
    /// True when at least one of that turn's calls is still outstanding — the caller ends the run.
    /// False when every one of them resolved while the turn was still running, in which case the
    /// run carries on and those results were folded into it.
    /// </returns>
    /// <remarks>
    /// The decision and the marking happen together under the lock, which is what closes the race
    /// against a resolution landing at exactly this moment: either it commits first and this turn
    /// never sees it outstanding, or this marks it parked and the commit that follows knows to
    /// start a child run. Neither order can leave a resolved call with nobody to continue it.
    /// </remarks>
    public bool TryPark(string runId, string generationId, out int unresolvedCount)
    {
        lock (_lock)
        {
            unresolvedCount = 0;
            foreach (var entry in _entries.Values)
            {
                if (string.Equals(entry.GenerationId, generationId, StringComparison.Ordinal))
                {
                    entry.Parked = true;
                    unresolvedCount++;
                }
            }

            if (unresolvedCount > 0)
            {
                _lastParkedRunId = runId;
            }

            return unresolvedCount > 0;
        }
    }

    /// <summary>
    /// Claims a deferred call for resolution and decides, once and for all, whether its result will
    /// cause a child run.
    /// </summary>
    /// <param name="toolCallId">The call being resolved.</param>
    /// <param name="fingerprint">
    /// A digest of the result being applied, remembered for the duration of the claim so a second
    /// delivery arriving mid-commit can be told apart from a conflicting one.
    /// </param>
    /// <param name="pending">The claim, to be handed back to <see cref="CompleteResolve"/> or
    /// <see cref="AbortResolve"/>.</param>
    /// <param name="inFlightFingerprint">
    /// When the claim is refused because another resolution holds it, that resolution's fingerprint —
    /// equal means this delivery is a duplicate, different means it conflicts. Null when the call is
    /// simply not deferred here.
    /// </param>
    /// <returns>
    /// False when the call is not deferred here — already resolved, never deferred, or claimed by a
    /// resolution already in flight. The caller distinguishes those against history.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The claim deliberately leaves the entry in place. Until the result is actually applied to
    /// history the placeholder is still there, so a turn that ended in this window must still park —
    /// and the child run this claim already committed to is what will pick the conversation back up.
    /// </para>
    /// <para>
    /// What is settled <em>here</em> is therefore only whether a child run id has to exist already,
    /// because a parked entry's durable resolution record names that run before it starts. Whether
    /// there is a child run at all is settled in <see cref="CompleteResolve"/>, from the entry as it
    /// stands then: <see cref="TryPark"/> can reach this same entry while the claim is in flight,
    /// and a claim-time snapshot would miss it.
    /// </para>
    /// </remarks>
    public bool TryBeginResolve(
        string toolCallId,
        string fingerprint,
        out ResolvingDeferral? pending,
        out string? inFlightFingerprint)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(toolCallId, out var entry))
            {
                pending = null;
                inFlightFingerprint = null;
                return false;
            }

            if (entry.Resolving)
            {
                pending = null;
                inFlightFingerprint = entry.ResolvingFingerprint;
                return false;
            }

            entry.Resolving = true;
            entry.ResolvingFingerprint = fingerprint;
            pending = new ResolvingDeferral(
                entry,
                ChildRunId: entry.Parked ? Guid.NewGuid().ToString("N") : null);
            inFlightFingerprint = null;
            return true;
        }
    }

    /// <summary>
    /// Releases a claim without resolving. The call stays deferred and the attempt stays retryable,
    /// which is the whole point: a resolution that could not be durably recorded has changed
    /// nothing, and its caller is entitled to send it again.
    /// </summary>
    public void AbortResolve(ResolvingDeferral pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        lock (_lock)
        {
            pending.Entry.Resolving = false;
            pending.Entry.ResolvingFingerprint = null;
        }
    }

    /// <summary>
    /// Commits a claimed resolution: retires the reservation, stamps the ordinal, decides whether
    /// this result is the one that continues the conversation, and queues its child run when it
    /// needs one.
    /// </summary>
    /// <param name="pending">The claim from <see cref="TryBeginResolve"/>.</param>
    /// <param name="resolved">
    /// The resolved result exactly as it now stands in history. It is the child run's cause — never
    /// a fabricated user message — and is carried by reference, not appended again.
    /// </param>
    /// <returns>
    /// The queued cause, or null when the requesting run was still going and the result was simply
    /// folded into it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Ownership is "nothing is outstanding any more", evaluated here rather than at claim time,
    /// so when several results race the last one to commit is the one that continues. It is a
    /// global test, not a per-run one, because the provider request carries the whole history: one
    /// unresolved placeholder anywhere in it makes the request invalid regardless of which run left
    /// it there.
    /// </para>
    /// <para>
    /// <b>Whether there is a child run is read from the entry, not from the claim.</b> The claim
    /// leaves the entry in <c>_entries</c> across the durable write and the history update, and
    /// <see cref="TryPark"/> can mark that same entry parked in exactly that window — the requesting
    /// run ended while this resolution was mid-commit. Trusting the claim-time snapshot there would
    /// retire the reservation, return no cause, and leave a result sitting resolved in history with
    /// no run to carry it to the provider. <see cref="DeferredEntry.Parked"/> only ever goes from
    /// false to true, so reading it here is a superset of what the claim saw, never a contradiction.
    /// </para>
    /// <para>
    /// A child run id minted here rather than at claim time is one the durable resolution record
    /// does not name — that write had already gone out saying the result was folded into a live
    /// run. Callers close that gap ahead of time with <see cref="MintChildRunIfParked"/>, which
    /// catches every park that happened before the result reached history; what is left for here is
    /// only a run that parked during the history write itself. The caller attaches that id to the
    /// committed record too (see <c>IRunLifecycleStore.AttachDeferredChildRunAsync</c>), so a
    /// process that dies before the cause runs leaves behind a record naming a child run that never
    /// started — which is exactly what <see cref="RecoverCauses"/> looks for on restart.
    /// </para>
    /// </remarks>
    public DelayedCause? CompleteResolve(ResolvingDeferral pending, ToolCallResultMessage resolved)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(resolved);

        lock (_lock)
        {
            pending.Entry.Resolving = false;
            pending.Entry.ResolvingFingerprint = null;
            _ = _entries.Remove(pending.Entry.ToolCallId);

            var ordinal = ++_ordinal;

            // The claim's id when it already had one — that is the id the durable resolution record
            // names, and it has to stand — otherwise a fresh one if the run parked while this
            // resolution was in flight. Null only when the requesting run is still going.
            var childRunId = pending.ChildRunId
                ?? (pending.Entry.Parked ? Guid.NewGuid().ToString("N") : null);

            if (childRunId == null)
            {
                return null;
            }

            var cause = new DelayedCause(
                ToolCallId: pending.Entry.ToolCallId,
                ToolName: pending.Entry.FunctionName,
                RequestingRunId: pending.Entry.RunId,
                RequestingGenerationId: pending.Entry.GenerationId,
                ChildRunId: childRunId,
                Ordinal: ordinal,
                IsContinuationOwner: _entries.Count == 0,
                Result: resolved);

            _causes.Enqueue(cause);
            return cause;
        }
    }

    /// <summary>
    /// Mints the child run id a claim turns out to need, when the requesting run parked after the
    /// claim was taken.
    /// </summary>
    /// <param name="pending">The claim from <see cref="TryBeginResolve"/>.</param>
    /// <returns>
    /// The claim to carry forward: the same one when nothing has changed, or a new one naming the
    /// child run this resolution now has to start.
    /// </returns>
    /// <remarks>
    /// Exists so the caller can name that run <em>durably</em> at a moment when refusing the whole
    /// resolution is still free — before the result reaches history. <see cref="CompleteResolve"/>
    /// can mint the same id later, but by then the resolution has happened and a store that will not
    /// record the id can only be complained about. Reading <see cref="DeferredEntry.Parked"/> here
    /// costs nothing when it is still false: the claim comes back unchanged and the caller writes
    /// nothing.
    /// </remarks>
    public ResolvingDeferral MintChildRunIfParked(ResolvingDeferral pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        lock (_lock)
        {
            return pending.ChildRunId == null && pending.Entry.Parked
                ? pending with { ChildRunId = Guid.NewGuid().ToString("N") }
                : pending;
        }
    }

    /// <summary>Takes the next committed cause, oldest first.</summary>
    public bool TryDequeueCause(out DelayedCause? cause)
    {
        lock (_lock)
        {
            if (_causes.Count == 0)
            {
                cause = null;
                return false;
            }

            cause = _causes.Dequeue();
            return true;
        }
    }

    /// <summary>
    /// Re-queues continuations a previous process committed but never ran, in the order recovery
    /// found them.
    /// </summary>
    /// <param name="owed">
    /// The continuations to recover, oldest first — each one a resolution whose durable record
    /// names a child run that was never started.
    /// </param>
    /// <returns>The causes actually queued, in queue order. Empty when there was nothing to do.</returns>
    /// <remarks>
    /// <para>
    /// Ownership is decided here, under the same lock, exactly as it is for a live resolution:
    /// <em>at most one</em> of the recovered causes performs a provider continuation, and only when
    /// no deferral is still outstanding. Every recovered result is already in history, so had two of
    /// them been given ownership the conversation would have been sent to the provider twice; and
    /// had one been given ownership while another call is still deferred, the request would have
    /// carried an unresolved placeholder.
    /// </para>
    /// <para>
    /// A tool call that is still deferred here, or one already queued, is skipped: the ordinary
    /// paths own those, and recovering them too would carry the same result twice.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DelayedCause> RecoverCauses(IReadOnlyList<RecoveredContinuation> owed)
    {
        ArgumentNullException.ThrowIfNull(owed);

        lock (_lock)
        {
            List<DelayedCause> recovered = [];
            foreach (var item in owed)
            {
                if (_entries.ContainsKey(item.ToolCallId)
                    || _causes.Any(c => string.Equals(c.ToolCallId, item.ToolCallId, StringComparison.Ordinal))
                    || recovered.Any(c => string.Equals(c.ToolCallId, item.ToolCallId, StringComparison.Ordinal)))
                {
                    continue;
                }

                recovered.Add(new DelayedCause(
                    ToolCallId: item.ToolCallId,
                    ToolName: item.ToolName,
                    RequestingRunId: item.RequestingRunId,
                    RequestingGenerationId: item.RequestingGenerationId,
                    ChildRunId: item.ChildRunId,
                    Ordinal: ++_ordinal,
                    IsContinuationOwner: false,
                    Result: item.Result));
            }

            if (recovered.Count > 0 && _entries.Count == 0)
            {
                recovered[^1] = recovered[^1] with { IsContinuationOwner = true };
            }

            foreach (var cause in recovered)
            {
                _causes.Enqueue(cause);
            }

            return recovered;
        }
    }
}

/// <summary>
/// A continuation a previous process committed durably but never ran.
/// </summary>
/// <param name="ToolCallId">The call that resolved.</param>
/// <param name="ToolName">The tool that had deferred.</param>
/// <param name="RequestingRunId">The run that asked for it.</param>
/// <param name="RequestingGenerationId">The turn that asked for it.</param>
/// <param name="ChildRunId">
/// The child run the durable record names. Reused rather than re-minted, so a second restart
/// recognises the run as started and cannot begin it twice.
/// </param>
/// <param name="Result">The resolved result as it stands in restored history.</param>
internal sealed record RecoveredContinuation(
    string ToolCallId,
    string ToolName,
    string? RequestingRunId,
    string? RequestingGenerationId,
    string ChildRunId,
    ToolCallResultMessage Result);

/// <summary>
/// One tool call awaiting external resolution. Public surface is <see cref="DeferredToolCallInfo"/>.
/// </summary>
/// <remarks>
/// A class rather than a record because <see cref="Parked"/> and <see cref="Resolving"/> are state
/// the coordinator transitions in place under its lock; a value copy would silently lose them.
/// </remarks>
internal sealed class DeferredEntry(
    string toolCallId,
    string functionName,
    string functionArgs,
    long deferredAtUnixMs,
    string? runId,
    string? generationId)
{
    /// <summary>The deferred call.</summary>
    public string ToolCallId { get; } = toolCallId;

    /// <summary>The tool that deferred.</summary>
    public string FunctionName { get; } = functionName;

    /// <summary>The arguments it was called with, as JSON.</summary>
    public string FunctionArgs { get; } = functionArgs;

    /// <summary>When it deferred, in Unix milliseconds.</summary>
    public long DeferredAtUnixMs { get; } = deferredAtUnixMs;

    /// <summary>The run that requested it, when known.</summary>
    public string? RunId { get; } = runId;

    /// <summary>The turn that requested it, when known.</summary>
    public string? GenerationId { get; } = generationId;

    /// <summary>
    /// Whether the requesting run has ended waiting on this call. Once true the resolution can no
    /// longer be folded into that run and must cause a child run instead.
    /// </summary>
    /// <remarks>
    /// Set once and never cleared, so a reader that sees false may simply be early — which is why
    /// <see cref="DelayedResultCoordinator.CompleteResolve"/> re-reads it under the lock instead of
    /// trusting the snapshot the claim took.
    /// </remarks>
    public bool Parked { get; set; }

    /// <summary>Whether a resolution has claimed this call and is mid-commit.</summary>
    public bool Resolving { get; set; }

    /// <summary>
    /// The digest of the result the in-flight claim is applying, so a delivery that arrives while it
    /// is still committing can be classified without waiting for the outcome. Null when not claimed.
    /// </summary>
    public string? ResolvingFingerprint { get; set; }
}

/// <summary>A claimed-but-not-yet-committed resolution.</summary>
/// <param name="Entry">
/// The call being resolved. It stays in the coordinator's map for the life of the claim, so it —
/// not this record — is the authority on whether the requesting run has since parked.
/// </param>
/// <param name="ChildRunId">
/// The id a child run will have if one is needed, minted at claim time when the requesting run had
/// already ended, so the durable resolution record can name it before it starts. Null when that run
/// was still going; <see cref="DelayedResultCoordinator.CompleteResolve"/> mints one late in the
/// narrow case where the run parked while this claim was in flight.
/// </param>
internal sealed record ResolvingDeferral(
    DeferredEntry Entry,
    string? ChildRunId);

/// <summary>
/// A committed delayed result and the child run it causes.
/// </summary>
/// <param name="ToolCallId">The call that resolved.</param>
/// <param name="ToolName">The tool that had deferred.</param>
/// <param name="RequestingRunId">The run that asked for it — the child's parent.</param>
/// <param name="RequestingGenerationId">The turn that asked for it.</param>
/// <param name="ChildRunId">The child run's id, minted at claim time.</param>
/// <param name="Ordinal">Commit order across the thread, starting at 1.</param>
/// <param name="IsContinuationOwner">
/// Whether this child performs the provider continuation. Exactly one result per unresolved batch
/// gets it — the one that clears the last outstanding call. The others complete with zero model
/// turns, because a provider cannot be handed a partially-resolved set of tool results.
/// </param>
/// <param name="Result">
/// The resolved result as it stands in history. This is the child's cause; the child does not
/// append it again.
/// </param>
internal sealed record DelayedCause(
    string ToolCallId,
    string ToolName,
    string? RequestingRunId,
    string? RequestingGenerationId,
    string ChildRunId,
    long Ordinal,
    bool IsContinuationOwner,
    ToolCallResultMessage Result);
