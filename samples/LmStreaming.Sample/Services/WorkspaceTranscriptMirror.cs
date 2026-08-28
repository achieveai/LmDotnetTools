using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Composition root of the workspace transcript mirror (#251): owns one
///     <see cref="ConversationTranscriptWriter"/> per live conversation, the single
///     <see cref="TranscriptFlushScheduler"/> that drains them, and the per-conversation message
///     subscription that decides WHEN to schedule a drain.
/// </summary>
/// <remarks>
///     <para>
///     <b>Always on.</b> There is no feature flag. Every pooled agent is attached, and a conversation
///     with no workspace bound simply produces <see cref="TranscriptFlushOutcome.Unavailable"/> on each
///     flush — no gateway call, no file, no cost. The only opt-out is the <c>.gitignore</c> the writer
///     drops into <c>.conversations/</c>.
///     </para>
///     <para>
///     <b>S2S is UI-only for v1 — deliberately, not by omission.</b> A background flush has no inbound
///     request to borrow a credential from, so a session an S2S caller owns resolves as
///     <c>CredentialConflict</c> forever and that conversation gets no transcript. Recorded in ADR 0011.
///     </para>
///     <para>
///     <b>Disposal is synchronous <see cref="IDisposable"/>, never <c>IAsyncDisposable</c>-only.</b> This
///     is the single hardest constraint on the type. <c>BrowserWebAppFactory</c> tears the sample host
///     down with the synchronous <c>IHost.Dispose()</c>, and a container-tracked
///     <c>IAsyncDisposable</c>-only singleton makes <c>ServiceProvider.Dispose()</c> throw
///     <i>"only implements IAsyncDisposable"</i> — which breaks every E2E test at teardown, for a reason
///     that looks nothing like this feature. The same trap is documented verbatim at the
///     <c>SqliteConnectionFactory</c> construction in <c>Program.cs</c>.
///     </para>
///     <para>
///     <b>The transcript is RETAINED when a conversation is deleted.</b> <see cref="Evict"/> drops the
///     in-memory writer and its subscription and touches nothing in the workspace: the point of mirroring
///     a conversation into its workspace is that the record outlives the conversation.
///     </para>
/// </remarks>
public sealed class WorkspaceTranscriptMirror : IDisposable
{
    /// <summary>
    ///     How long a dropped subscription waits before re-subscribing. Non-zero so a conversation whose
    ///     channel is genuinely saturated re-attaches at a bounded rate rather than spinning a hot loop
    ///     against an agent that will only drop it again. Tests pass <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public static readonly TimeSpan DefaultResubscribeDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     How many consecutive <see cref="TranscriptFlushOutcome.Deferred"/> results one conversation may
    ///     re-schedule on before the mirror stops chasing it until something new happens.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>The bound is what makes the retry chain terminate at all.</b> <c>Deferred</c> means something
    ///     FAILED — a read that never settled, or a gateway call that did not land — and neither
    ///     self-terminates: a workspace whose gateway is refusing writes, or a conversation being written to
    ///     faster than its rows settle, defers every attempt forever. An unbounded re-schedule against that
    ///     is a hot loop on the single drain thread that also starves every OTHER conversation's flush.
    ///     </para>
    ///     <para>
    ///     <b>This budget covers failures and NOTHING else.</b> An orderly continuation — the sub-agent
    ///     fan-out being capped per flush — reports <see cref="TranscriptFlushOutcome.Progressing"/>
    ///     instead, and is re-scheduled without touching the budget. Charging progress to a failure budget
    ///     puts a hard ceiling of (this + 1) x the per-flush cap on how many descendants a single trigger
    ///     can ever reach, and silently strands the tail of any roster larger than that; the writer's own
    ///     coverage sweep is what terminates the progressing chain instead. The budget is per TRIGGER —
    ///     reset by every independent external trigger, not only by a completed run — so a transient outage
    ///     cannot silence a conversation permanently.
    ///     </para>
    /// </remarks>
    public const int MaxDeferredRetries = 3;

    /// <summary>One conversation's live subscription: the agent instance it is bound to, and the token
    /// that ends it. The instance is load-bearing — see <see cref="PumpAsync"/>'s drop detection.</summary>
    private sealed record Subscription(IMultiTurnAgent Agent, CancellationTokenSource Cancellation);

    private readonly Func<string, IMultiTurnAgent?> _agentLookup;
    private readonly IConversationStore _store;
    private readonly IWorkspaceFileBrowser _fileBrowser;
    private readonly ConversationDescendantScanner _descendants;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WorkspaceTranscriptMirror> _logger;
    private readonly TimeSpan _resubscribeDelay;
    private readonly TimeSpan? _readSettleDelay;
    private readonly TranscriptFlushScheduler _scheduler;

    /// <summary>
    ///     One writer per conversation, keyed by threadId. Concurrent because the drain loop reads it
    ///     while <see cref="Attach"/> may be adding to it. Survives a mode switch (same threadId, new
    ///     agent instance) on purpose: the writer owns that conversation's watermarks, and rebuilding it
    ///     would re-append the whole history.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConversationTranscriptWriter> _writers = new(StringComparer.Ordinal);

    /// <summary>
    ///     Consecutive deferred flushes per conversation, against <see cref="MaxDeferredRetries"/>, stamped
    ///     with the generation that accrued them. Reset whenever a flush is not deferred and at every turn
    ///     boundary, so the budget bounds one stuck chain rather than the conversation's lifetime.
    /// </summary>
    /// <remarks>
    ///     The stamp exists because a flush is asynchronous and a trigger is not. <see cref="FlushAsync"/>
    ///     can still be awaiting gateway I/O when a fresh trigger clears this counter on the subscriber
    ///     thread; without the stamp, that older flush's <c>Deferred</c> outcome re-creates the counter at 1
    ///     and the new generation silently starts one attempt down.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DeferredBudget> _deferredAttempts = new(StringComparer.Ordinal);

    /// <summary>
    ///     Monotonic generation per conversation, bumped by <see cref="ScheduleFreshAttempt"/>. Identifies
    ///     which trigger's work a completing flush belongs to.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _generations = new(StringComparer.Ordinal);

    /// <summary>Guards <see cref="_subscriptions"/> and <see cref="_disposed"/>. Never held across
    /// cancellation or I/O — see <see cref="Attach"/>.</summary>
    private readonly object _gate = new();
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    private bool _disposed;
    private int _resubscribeCount;

    /// <summary>Creates the mirror.</summary>
    /// <param name="agentLookup">
    ///     Resolves the pool's CURRENT agent for a threadId, or null when the pool no longer holds one.
    ///     A delegate rather than the pool itself because the pool's own registration resolves this type
    ///     — taking <c>MultiTurnAgentPool</c> as a constructor dependency would close a DI cycle. The
    ///     delegate is only invoked when a subscription ends, long after both singletons exist.
    /// </param>
    /// <param name="store">Source of persisted rows and metadata, handed to each writer.</param>
    /// <param name="fileBrowser">The sandbox seam, handed to each writer.</param>
    /// <param name="descendants">Shared descendant cache, handed to each writer.</param>
    /// <param name="loggerFactory">Builds this type's logger and each writer's.</param>
    /// <param name="resubscribeDelay">Overrides <see cref="DefaultResubscribeDelay"/>.</param>
    /// <param name="readSettleDelay">
    ///     Overrides <see cref="ConversationTranscriptWriter.DefaultReadSettleDelay"/> on every writer
    ///     this mirror creates.
    /// </param>
    public WorkspaceTranscriptMirror(
        Func<string, IMultiTurnAgent?> agentLookup,
        IConversationStore store,
        IWorkspaceFileBrowser fileBrowser,
        ConversationDescendantScanner descendants,
        ILoggerFactory loggerFactory,
        TimeSpan? resubscribeDelay = null,
        TimeSpan? readSettleDelay = null
    )
    {
        ArgumentNullException.ThrowIfNull(agentLookup);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileBrowser);
        ArgumentNullException.ThrowIfNull(descendants);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _agentLookup = agentLookup;
        _store = store;
        _fileBrowser = fileBrowser;
        _descendants = descendants;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WorkspaceTranscriptMirror>();
        _resubscribeDelay = resubscribeDelay ?? DefaultResubscribeDelay;
        _readSettleDelay = readSettleDelay;
        _scheduler = new TranscriptFlushScheduler(
            FlushAsync,
            (threadId, error) =>
                _logger.LogWarning(error, "Transcript flush for thread {ThreadId} faulted on the drain loop", threadId)
        );
    }

    /// <summary>
    ///     How many times a subscription was detected as silently dropped and re-established. Zero on a
    ///     healthy host; a climbing value means some conversation is publishing faster than this mirror
    ///     consumes. Exposed so the drop path is assertable rather than only observable in logs.
    /// </summary>
    public int ResubscribeCount => Volatile.Read(ref _resubscribeCount);

    /// <summary>
    ///     Whether <paramref name="threadId"/> currently has a live subscription.
    /// </summary>
    /// <remarks>
    ///     A test seam, and specifically the one that makes "this provider was never attached" assertable.
    ///     Every other symptom of a missed <see cref="Attach"/> is an <i>absence</i> — no root transcript,
    ///     no descendant files — which only shows up long after the agent was built and looks identical to
    ///     a conversation that simply had nothing to write yet. Six of the sample's provider branches
    ///     shipped unattached precisely because nothing could ask this question.
    /// </remarks>
    internal bool IsMirroring(string threadId)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        lock (_gate)
        {
            return _subscriptions.ContainsKey(threadId);
        }
    }

    /// <summary>
    ///     Starts mirroring <paramref name="agent"/>'s conversation. Called from the sample's agent
    ///     factory for every agent the pool creates, including the replacement built by a mode switch —
    ///     that case cancels the previous subscription and keeps the existing writer.
    /// </summary>
    /// <remarks>Idempotent for the same instance: re-attaching an already-attached agent is a no-op.</remarks>
    public void Attach(IMultiTurnAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var threadId = agent.ThreadId;
        if (string.IsNullOrEmpty(threadId))
        {
            return;
        }

        _ = _writers.GetOrAdd(
            threadId,
            id => new ConversationTranscriptWriter(
                id,
                _store,
                _fileBrowser,
                _descendants,
                _loggerFactory.CreateLogger<ConversationTranscriptWriter>(),
                _readSettleDelay
            )
        );

        Subscription? replaced;
        Subscription subscription;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_subscriptions.TryGetValue(threadId, out var existing) && ReferenceEquals(existing.Agent, agent))
            {
                return;
            }

            replaced = existing;
            subscription = new Subscription(agent, new CancellationTokenSource());
            _subscriptions[threadId] = subscription;
        }

        // Outside the lock: Cancel() runs its registrations inline, and one of those resumes a pump that
        // may call straight back into this type.
        Cancel(replaced);
        StartPump(subscription);
    }

    /// <summary>
    ///     Establishes the subscription <b>on the calling thread</b>, then hands the already-running
    ///     enumerator to a worker that consumes it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>The registration must happen-before <see cref="Attach"/> returns.</b>
    ///     <c>MultiTurnAgentBase.SubscribeAsync</c> is an async iterator that adds the subscriber to the
    ///     fan-out under the agent's replay lock in its synchronous prologue — that is, inside the FIRST
    ///     <c>MoveNextAsync()</c>, on whichever thread calls it. Starting the whole pump with
    ///     <c>Task.Run</c> therefore let <c>Attach</c> return before the subscriber existed, and everything
    ///     published in that window went to nobody. That window is not theoretical: the sample attaches
    ///     from the pool's agent factory, on the same request that then starts the run, so a short
    ///     conversation could complete its ONLY run before the worker was scheduled — no turn boundary was
    ///     ever observed, no flush was ever scheduled, and the conversation ended with no transcript at
    ///     all rather than with a stale one.
    ///     </para>
    ///     <para>
    ///     Only the registration is synchronous. <c>MoveNextAsync</c> is started, not awaited: its
    ///     continuation and every message after it run on the worker, so no message handling and no
    ///     gateway I/O ever reaches the caller. And it cannot throw into the pool's factory — a failure to
    ///     subscribe is logged and leaves this conversation unmirrored, like every other failure here.
    ///     </para>
    ///     <para>
    ///     <b>The <c>catch</c> below is the narrow door, not the usual one.</b> It only fires for a
    ///     subscription that throws SYNCHRONOUSLY, which the production agent does not: its
    ///     <c>SubscribeAsync</c> is an async iterator, so its prologue's failures come back as a faulted
    ///     <c>ValueTask</c> and are handled where they surface, in <see cref="PumpAsync"/>. Both doors have
    ///     to undo the registration, which is why they share <see cref="Retire"/>.
    ///     </para>
    /// </remarks>
    private void StartPump(Subscription subscription)
    {
        var agent = subscription.Agent;
        var ct = subscription.Cancellation.Token;

        IAsyncEnumerator<IMessage>? enumerator = null;
        ValueTask<bool> pending;
        try
        {
            enumerator = agent.SubscribeAsync(ct).GetAsyncEnumerator(ct);
            pending = enumerator.MoveNextAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subscribing the transcript mirror to thread {ThreadId} failed", agent.ThreadId);
            AbandonFailedSubscription(subscription, enumerator);
            return;
        }

        _ = Task.Run(() => PumpAsync(subscription, enumerator, pending));
    }

    /// <summary>
    ///     Undoes a subscription whose setup failed, so the conversation is merely unmirrored rather than
    ///     permanently unmirrorable.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>The registration has to go.</b> <see cref="Attach"/> records the subscription before starting
    ///     the pump — deliberately, so no message can be published into the window between the two — which
    ///     means a failure here leaves behind a mapping no pump is consuming. <see cref="Attach"/> treats an
    ///     existing mapping for the SAME agent instance as an idempotent no-op, so the pool re-attaching
    ///     that instance (a request that reuses the pooled agent, say) would return without ever
    ///     re-subscribing, and a transient setup failure would cost the conversation its transcript for the
    ///     rest of its life rather than for one attempt.
    ///     </para>
    ///     <para>
    ///     <b>Nothing here may throw or block.</b> <see cref="Attach"/> runs inside the pool's agent
    ///     factory. The enumerator — which exists when <c>MoveNextAsync</c> is what threw, and holds that
    ///     subscriber's channel registration until it is disposed — is therefore drained off-thread through
    ///     a path that swallows its own faults. It is disposed HERE and nowhere else: no pump ever ran for
    ///     this subscription, so <see cref="ConsumeAsync"/>'s <c>finally</c> never claimed it.
    ///     </para>
    /// </remarks>
    private void AbandonFailedSubscription(Subscription subscription, IAsyncEnumerator<IMessage>? enumerator)
    {
        Retire(subscription);

        if (enumerator is null)
        {
            return;
        }

        var threadId = subscription.Agent.ThreadId;

        _ = Task.Run(async () =>
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Disposing the failed transcript subscription for thread {ThreadId} threw",
                    threadId
                );
            }
        });
    }

    /// <summary>
    ///     Ends one subscription's life in this mirror: drops its registration and cancels its token.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Removed by reference, never by key.</b> A concurrent <see cref="Attach"/> — a mode switch is
    ///     the ordinary case — may already have replaced this mapping with a live successor's; deleting the
    ///     key would then cancel nothing but silently unregister the subscription that is actually working.
    ///     The pool makes that the NORMAL ordering rather than a narrow race:
    ///     <c>MultiTurnAgentPool.SwapAgentUnderLockAsync</c> builds the replacement through the agent
    ///     factory — which is where <see cref="Attach"/> is called from — and publishes it into its own map
    ///     only afterwards, so by the time anything can observe the new agent, the new mapping is already
    ///     in place.
    ///     </para>
    ///     <para>
    ///     <b>Disposes nothing.</b> Every enumerator is owned by whoever created it:
    ///     <see cref="ConsumeAsync"/>'s <c>finally</c> for a subscription that got as far as a pump, and
    ///     <see cref="AbandonFailedSubscription"/> for one that never did.
    ///     </para>
    /// </remarks>
    private void Retire(Subscription subscription)
    {
        var threadId = subscription.Agent.ThreadId;

        lock (_gate)
        {
            if (_subscriptions.TryGetValue(threadId, out var current) && ReferenceEquals(current, subscription))
            {
                _ = _subscriptions.Remove(threadId);
            }
        }

        Cancel(subscription);
    }

    /// <summary>
    ///     Stops mirroring <paramref name="threadId"/> and forgets its writer. Wired to
    ///     <c>MultiTurnAgentPool.ThreadRemoved</c>. <b>Never deletes the transcript</b> — the file is the
    ///     durable record and outliving the conversation is the point.
    /// </summary>
    public void Evict(string threadId)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            return;
        }

        _ = _writers.TryRemove(threadId, out _);
        _ = _deferredAttempts.TryRemove(threadId, out _);
        _ = _generations.TryRemove(threadId, out _);

        Subscription? removed;
        lock (_gate)
        {
            _ = _subscriptions.Remove(threadId, out removed);
        }

        Cancel(removed);
    }

    /// <summary>
    ///     Ends every subscription and stops the drain loop. Synchronous, non-throwing, and does not wait
    ///     on the pumps — see the type remarks for why this must never become <c>IAsyncDisposable</c>-only.
    /// </summary>
    public void Dispose()
    {
        List<Subscription> live;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            live = [.. _subscriptions.Values];
            _subscriptions.Clear();
        }

        foreach (var subscription in live)
        {
            Cancel(subscription);
        }

        _writers.Clear();
        _deferredAttempts.Clear();
        _generations.Clear();
        _scheduler.Dispose();
    }

    /// <summary>
    ///     Cancels one subscription. The source is deliberately NOT disposed: its token is still held by a
    ///     pump that may be parked inside <c>SubscribeAsync</c>, and a token whose source was disposed
    ///     throws on the next registration. A cancelled source holds no OS handle (no WaitHandle is ever
    ///     taken from it) and is reclaimed by GC once the pump lets go.
    /// </summary>
    private void Cancel(Subscription? subscription)
    {
        if (subscription is null)
        {
            return;
        }

        try
        {
            subscription.Cancellation.Cancel();
        }
        catch (Exception ex)
        {
            // Cancel() surfaces whatever a downstream registration threw. Teardown continues regardless:
            // neither Evict nor Dispose may throw into the pool's ThreadRemoved fan-out or host shutdown.
            _logger.LogDebug(
                ex,
                "Cancelling the transcript subscription for {ThreadId} threw",
                subscription.Agent.ThreadId
            );
        }
    }

    /// <summary>
    ///     Consumes one conversation's message stream, scheduling a flush at every turn boundary — and
    ///     recovering from a subscription the agent silently dropped.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Why the drop check exists.</b> <c>MultiTurnAgentBase.PublishToSubscriber</c> publishes with
    ///     <c>TryWrite</c>; when a subscriber's bounded channel is full the subscriber is REMOVED from the
    ///     fan-out and its channel completed. The enumerator then ends <i>normally, with no exception</i>,
    ///     so a naive <c>await foreach</c> would exit as if the conversation had finished and every later
    ///     turn of a still-live conversation would go unmirrored — silently, with nothing in the logs.
    ///     </para>
    ///     <para>
    ///     <b>Why the identity comparison is the right discriminator.</b> Three things end an enumeration:
    ///     the drop, <see cref="Evict"/>/<see cref="Dispose"/> (our own token), and the agent being
    ///     disposed. The last two are distinguishable because <c>MultiTurnAgentPool.RemoveAgentAsync</c>
    ///     removes the entry from its map BEFORE disposing it, and <c>RecreateAgentWithModeAsync</c>
    ///     likewise swaps the entry before disposing the old agent — so by the time a disposal completes
    ///     our channel, the pool either no longer holds this threadId or holds a DIFFERENT instance.
    ///     Reference equality on the agent, not on the threadId, is therefore what separates "we were
    ///     dropped from a conversation that is still live" from an ordinary teardown.
    ///     </para>
    ///     <para>
    ///     <b>Every exit retires the subscription.</b> A pump that has returned is draining nothing, so a
    ///     registration left behind turns <see cref="Attach"/>'s idempotent early return — which compares
    ///     the agent INSTANCE — from "mirroring ended for this conversation" into "this conversation can
    ///     never be mirrored again, not even by an explicit re-attach". The exit that reaches this in
    ///     production is a FAULT, and it does not arrive through <see cref="StartPump"/>'s <c>try</c>:
    ///     <c>MultiTurnAgentBase.SubscribeAsync</c> is an async iterator, so a throw in its prologue —
    ///     where it creates the channel and joins the fan-out under the replay lock — is captured by the
    ///     compiler-generated state machine into a FAULTED <c>ValueTask</c> returned by the first
    ///     <c>MoveNextAsync</c> rather than thrown out of it, and surfaces here instead.
    ///     </para>
    ///     <para>
    ///     Retiring in a <c>finally</c> rather than on the faulting return is deliberate.
    ///     <see cref="ConsumeAsync"/> reports fault and cancellation as one <c>bool</c>, so singling the
    ///     fault out would mean widening that contract for no behavioural gain — on the three exits that
    ///     were already safe, <see cref="Retire"/> is a provable no-op. Cancellation is only ever ours to
    ///     request and every requester (<see cref="Evict"/>, <see cref="Dispose"/>, <see cref="Attach"/>'s
    ///     replacement) fixes the mapping first; an agent gone from the pool takes <see cref="Evict"/> with
    ///     it, since the pool removes the entry before raising <c>ThreadRemoved</c>; and a REPLACED agent's
    ///     successor has already overwritten the mapping, which the reference comparison leaves alone. The
    ///     <c>finally</c> additionally covers an exit by EXCEPTION — <see cref="_agentLookup"/> is injected
    ///     code and this task's fault is discarded by its caller — which no return-site fix can see.
    ///     </para>
    /// </remarks>
    private async Task PumpAsync(
        Subscription subscription,
        IAsyncEnumerator<IMessage> enumerator,
        ValueTask<bool> pending
    )
    {
        var agent = subscription.Agent;
        var threadId = agent.ThreadId;
        var ct = subscription.Cancellation.Token;

        try
        {
            while (true)
            {
                if (
                    await ConsumeAsync(threadId, enumerator, pending).ConfigureAwait(false)
                    || ct.IsCancellationRequested
                    || _agentLookup(threadId) is not { } current
                    || !ReferenceEquals(current, agent)
                )
                {
                    return;
                }

                _ = Interlocked.Increment(ref _resubscribeCount);
                _logger.LogWarning(
                    "Transcript subscription for thread {ThreadId} was dropped (its output channel filled); "
                        + "re-subscribing. Turns published during the gap are still recovered — the writer's "
                        + "watermark is cumulative and the next flush re-reads the whole thread.",
                    threadId
                );

                try
                {
                    await Task.Delay(_resubscribeDelay, ct).ConfigureAwait(false);
                    enumerator = agent.SubscribeAsync(ct).GetAsyncEnumerator(ct);
                    pending = enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Near-unreachable for the shape the production agent has: SubscribeAsync is an async
                    // iterator, so a failure to re-subscribe does not throw out of these two lines — it comes
                    // back as a faulted ValueTask and re-enters through ConsumeAsync above. This return is
                    // covered for free by the finally, not handled separately; it is written out only so the
                    // next reader does not have to re-derive that it is safe.
                    _logger.LogWarning(
                        ex,
                        "Re-subscribing the transcript mirror to thread {ThreadId} failed",
                        threadId
                    );
                    return;
                }

                // The gap between the drop and this re-subscription is EXACTLY where a turn boundary goes
                // unobserved: the channel filled, so the messages that overflowed it — the run completion
                // among them — reached no subscriber, and nothing will republish them. The recovery the log
                // line above promises ("the next flush re-reads the whole thread") only holds if a flush
                // actually happens, and without this line the only thing that can trigger one is a LATER turn
                // that may never come. Scheduling here is free when nothing changed: a flush with no new rows
                // is UpToDate and issues no gateway write. It is a fresh generation of work like any other
                // trigger — the drop is new evidence, and inheriting a spent budget from before it would give
                // the recovery a single pass.
                //
                // It also forces a DESCENDANT RESCAN, which the other two triggers deliberately do not. What
                // was lost here is unknown by definition, and a spawn call or completion notification is
                // exactly the kind of message that can have been among it. If an earlier flush cached a roster
                // taken before a child was persisted, and that child's only announcement went down with the
                // channel, the cache is stale with nothing left to invalidate it and the child is never
                // mirrored at all. The other two call sites are the opposite case: they run because a message
                // ARRIVED, so the message itself is the signal — sub-agent activity already arms a rescan
                // through NoteSubAgentActivity, and a run completion is not evidence about descendants.
                ScheduleFreshAttempt(threadId, forceDescendantRescan: true);
            }
        }
        finally
        {
            Retire(subscription);
        }
    }

    /// <summary>
    ///     Drains one subscription's enumerator to its end, applying every message. Reports whether it
    ///     ended in a FAULT — a normal end is what both a silent drop and an ordinary teardown look like,
    ///     and telling those two apart is the caller's job.
    /// </summary>
    private async Task<bool> ConsumeAsync(
        string threadId,
        IAsyncEnumerator<IMessage> enumerator,
        ValueTask<bool> pending
    )
    {
        try
        {
            while (await pending.ConfigureAwait(false))
            {
                Observe(threadId, enumerator.Current);
                pending = enumerator.MoveNextAsync();
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort, like every other path in this feature: a faulted stream ends the mirror for
            // this conversation rather than looping on a failure that is not going to clear.
            _logger.LogWarning(ex, "Transcript subscription for thread {ThreadId} faulted", threadId);
            return true;
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disposing the transcript subscription for thread {ThreadId} threw", threadId);
            }
        }
    }

    /// <summary>Applies one observed message to this conversation's mirror state.</summary>
    private void Observe(string threadId, IMessage message)
    {
        var subAgentActivity = IsSubAgentActivity(message);
        if (subAgentActivity && _writers.TryGetValue(threadId, out var writer))
        {
            writer.NoteSubAgentActivity();
        }

        if (message is RunCompletedMessage)
        {
            // The turn boundary.
            ScheduleFreshAttempt(threadId);
            return;
        }

        if (subAgentActivity)
        {
            // The SECOND trigger, and the only one a background child produces. Noting the activity is not
            // enough on its own: NoteSubAgentActivity only arms the next flush's descendant rescan, and if
            // that flush never happens the arming does nothing. A sub-agent that finishes after its
            // parent's last turn publishes no RunCompletedMessage and nothing follows it, so a mirror that
            // scheduled only on run completion left that child's transcript unwritten forever — for the
            // whole life of the conversation, not until the next turn.
            ScheduleFreshAttempt(threadId);
        }
    }

    /// <summary>
    ///     Schedules a flush of <paramref name="threadId"/> as a NEW generation of work: the bounded
    ///     deferred-retry budget is released and the writer's descendant coverage sweep starts over.
    /// </summary>
    /// <param name="threadId">The conversation to flush.</param>
    /// <param name="forceDescendantRescan">
    ///     Also invalidates the cached descendant roster, for a trigger that means messages were LOST
    ///     rather than delivered. Only the drop-recovery path passes true; see its call site for why the
    ///     other two must not.
    /// </param>
    /// <remarks>
    ///     <b>Every independent external trigger goes through here, not just the turn boundary.</b> The
    ///     budget and the sweep are both scoped to "one trigger's worth of attempts", so a trigger that
    ///     inherits an exhausted budget gets a single pass and is then abandoned — and the trigger most
    ///     likely to inherit one is the least recoverable, a background sub-agent completing after its
    ///     parent's last turn, because nothing follows it to try again.
    /// </remarks>
    private void ScheduleFreshAttempt(string threadId, bool forceDescendantRescan = false)
    {
        // Bump BEFORE clearing, so a flush still in flight for the old generation can already tell that
        // its outcome is stale by the time it returns.
        _ = _generations.AddOrUpdate(threadId, 1, (_, previous) => previous + 1);
        _ = _deferredAttempts.TryRemove(threadId, out _);

        if (_writers.TryGetValue(threadId, out var writer))
        {
            writer.NoteExternalTrigger();
            if (forceDescendantRescan)
            {
                writer.NoteSubAgentActivity();
            }
        }

        _scheduler.Schedule(threadId);
    }

    /// <summary>
    ///     Whether <paramref name="message"/> is evidence that a sub-agent exists or progressed, which is
    ///     what makes the next flush refresh the descendant graph before fanning out.
    /// </summary>
    /// <remarks>
    ///     Two shapes, because a sub-agent is visible to its parent at two distinct moments: the
    ///     <c>Agent</c> tool call that spawns it, and the notification it (or a descendant of it) pushes
    ///     back. Matching only the notification would miss a foreground spawn whose result never travels
    ///     as a <see cref="NotifyMessage"/>; matching only the tool call would miss a background spawn
    ///     that was already running when this process attached.
    /// </remarks>
    private static bool IsSubAgentActivity(IMessage message) =>
        message switch
        {
            NotifyMessage notify => notify.NotifyKind
                is NotifyKinds.SubAgentCompletion
                    or NotifyKinds.DescendantQuestion,
            ToolsCallAggregateMessage aggregate => HasSpawnCall(aggregate.ToolsCallMessage),
            ICanGetToolCalls calls => HasSpawnCall(calls),
            _ => false,
        };

    private static bool HasSpawnCall(ICanGetToolCalls message)
    {
        var calls = message.GetToolCalls();
        if (calls is null)
        {
            return false;
        }

        foreach (var call in calls)
        {
            if (string.Equals(call.FunctionName, SubAgentToolProvider.SpawnToolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The scheduler's drain callback: resolves the conversation's writer and flushes it. A key whose
    ///     writer has since been evicted is a no-op.
    /// </summary>
    /// <remarks>
    ///     Two outcomes ask for another attempt and they are budgeted differently.
    ///     <see cref="TranscriptFlushOutcome.Deferred"/> means something FAILED and is retried at most
    ///     <see cref="MaxDeferredRetries"/> consecutive times, which is what stops a permanently-deferring
    ///     conversation from spinning the drain loop. <see cref="TranscriptFlushOutcome.Progressing"/>
    ///     means the pass stopped short of work it knows about — the capped sub-agent fan-out — and is
    ///     re-scheduled WITHOUT spending the failure budget; it terminates on the writer's own coverage
    ///     sweep instead. It does not reset the budget either: a conversation that alternates between
    ///     failing and progressing must still be bounded.
    ///     <para>
    ///     All of that accounting is scoped to the GENERATION this flush started in. A fresh trigger can
    ///     land while the flush below is awaiting gateway I/O, and that trigger has already released the
    ///     budget and scheduled its own attempt; letting this one's outcome land afterwards would charge
    ///     the new generation for the old one's failure. So a superseded outcome is dropped outright —
    ///     including its re-schedule, which the new generation has already issued.
    ///     </para>
    /// </remarks>
    private async Task FlushAsync(string threadId, CancellationToken ct)
    {
        if (!_writers.TryGetValue(threadId, out var writer))
        {
            return;
        }

        var generation = _generations.GetOrAdd(threadId, 0);

        var outcome = await writer.FlushAsync(ct).ConfigureAwait(false);
        if (generation != _generations.GetOrAdd(threadId, 0))
        {
            return;
        }

        if (outcome == TranscriptFlushOutcome.Progressing)
        {
            _scheduler.Schedule(threadId);
            return;
        }

        if (outcome != TranscriptFlushOutcome.Deferred)
        {
            if (_deferredAttempts.TryGetValue(threadId, out var spent) && spent.Generation == generation)
            {
                _ = _deferredAttempts.TryRemove(new KeyValuePair<string, DeferredBudget>(threadId, spent));
            }

            return;
        }

        // A counter left by an older generation is REPLACED rather than added to. It can only exist if
        // that generation's flush lost the race against the check above, and it is not this generation's
        // budget to spend.
        var budget = _deferredAttempts.AddOrUpdate(
            threadId,
            _ => new DeferredBudget(generation, 1),
            (_, previous) =>
                previous.Generation == generation
                    ? previous with
                    {
                        Attempts = previous.Attempts + 1,
                    }
                    : new DeferredBudget(generation, 1)
        );

        if (budget.Attempts > MaxDeferredRetries)
        {
            _logger.LogWarning(
                "Transcript flush for thread {ThreadId} deferred {Attempts} times in a row; giving up until "
                    + "the next turn or sub-agent update. No transcript rows were lost — a deferral leaves "
                    + "the watermark of whatever it failed to write unadvanced, and the persisted "
                    + "conversation itself is untouched — but this conversation's workspace copy now stops "
                    + "short until something triggers it again.",
                threadId,
                budget.Attempts
            );
            return;
        }

        _scheduler.Schedule(threadId);
    }

    /// <summary>One conversation's consecutive-deferral count, and the generation that accrued it.</summary>
    private readonly record struct DeferredBudget(int Generation, int Attempts);
}
