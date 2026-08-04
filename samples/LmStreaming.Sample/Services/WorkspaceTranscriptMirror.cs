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
    private readonly ConcurrentDictionary<string, ConversationTranscriptWriter> _writers =
        new(StringComparer.Ordinal);

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
        TimeSpan? readSettleDelay = null)
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
            (threadId, error) => _logger.LogWarning(
                error,
                "Transcript flush for thread {ThreadId} faulted on the drain loop",
                threadId));
    }

    /// <summary>
    ///     How many times a subscription was detected as silently dropped and re-established. Zero on a
    ///     healthy host; a climbing value means some conversation is publishing faster than this mirror
    ///     consumes. Exposed so the drop path is assertable rather than only observable in logs.
    /// </summary>
    public int ResubscribeCount => Volatile.Read(ref _resubscribeCount);

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
                _readSettleDelay));

        Subscription? replaced;
        Subscription subscription;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_subscriptions.TryGetValue(threadId, out var existing)
                && ReferenceEquals(existing.Agent, agent))
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
        _ = Task.Run(() => PumpAsync(subscription));
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
            _logger.LogDebug(ex, "Cancelling the transcript subscription for {ThreadId} threw", subscription.Agent.ThreadId);
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
    /// </remarks>
    private async Task PumpAsync(Subscription subscription)
    {
        var agent = subscription.Agent;
        var threadId = agent.ThreadId;
        var ct = subscription.Cancellation.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in agent.SubscribeAsync(ct).ConfigureAwait(false))
                {
                    Observe(threadId, message);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Best-effort, like every other path in this feature: a faulted stream ends the mirror for
                // this conversation rather than looping on a failure that is not going to clear.
                _logger.LogWarning(ex, "Transcript subscription for thread {ThreadId} faulted", threadId);
                return;
            }

            if (ct.IsCancellationRequested
                || _agentLookup(threadId) is not { } current
                || !ReferenceEquals(current, agent))
            {
                return;
            }

            _ = Interlocked.Increment(ref _resubscribeCount);
            _logger.LogWarning(
                "Transcript subscription for thread {ThreadId} was dropped (its output channel filled); "
                    + "re-subscribing. Turns published during the gap are still recovered — the writer's "
                    + "watermark is cumulative and the next flush re-reads the whole thread.",
                threadId);

            try
            {
                await Task.Delay(_resubscribeDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Applies one observed message to this conversation's mirror state.</summary>
    private void Observe(string threadId, IMessage message)
    {
        if (IsSubAgentActivity(message) && _writers.TryGetValue(threadId, out var writer))
        {
            writer.NoteSubAgentActivity();
        }

        if (message is RunCompletedMessage)
        {
            // The turn boundary, and the ONLY flush trigger. Non-blocking by contract, so no gateway
            // latency ever reaches this loop.
            _scheduler.Schedule(threadId);
        }
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
            NotifyMessage notify => notify.NotifyKind is NotifyKinds.SubAgentCompletion
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
    ///     writer has since been evicted is a no-op, and a <see cref="TranscriptFlushOutcome.Deferred"/>
    ///     result asks for exactly one more attempt (the writer's own capping is what makes that chain
    ///     terminate rather than spin).
    /// </summary>
    private async Task FlushAsync(string threadId, CancellationToken ct)
    {
        if (!_writers.TryGetValue(threadId, out var writer))
        {
            return;
        }

        if (await writer.FlushAsync(ct).ConfigureAwait(false) == TranscriptFlushOutcome.Deferred)
        {
            _scheduler.Schedule(threadId);
        }
    }
}
