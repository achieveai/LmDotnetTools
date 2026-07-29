using System.Collections.Concurrent;
using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;

// Both namespaces define ToolApprovalOutcomes. The wire vocabulary is the one a submitted decision
// speaks, so it is named explicitly rather than left to which using won the lookup.
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// How a submitted <see cref="ToolApprovalDecision"/> was resolved against the pending request it
/// names.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is deliberately the zero value: a settlement that was never assigned reads
/// as "there is nothing here to allow", which is the fail-closed direction (ADR 0003).
/// </remarks>
public enum RemoteApprovalSettleStatus
{
    /// <summary>
    /// There is no answerable request under that id for that owner. Every reason collapses here —
    /// never registered, epoch from a previous process, someone else's request, already expired,
    /// tombstone pruned — so a caller cannot use the endpoint to learn which request ids are real.
    /// </summary>
    Unknown = 0,

    /// <summary>This call performed the pending → decided transition; its decision is the outcome.</summary>
    Accepted,

    /// <summary>
    /// An allow was recorded, and the request stays pending because a frozen approver has not answered
    /// yet. Not a settlement and not a failure: the tool has not been authorized, and will not be until
    /// the last approver agrees. Repeating the same allow reports this again rather than double-counting.
    /// </summary>
    Recorded,

    /// <summary>
    /// The request was already decided the same way. A retry of the winning decision — a duplicated
    /// delivery, an approver that did not see the first response — is idempotent rather than an
    /// error, and reports the identical outcome.
    /// </summary>
    AlreadyDecided,

    /// <summary>
    /// The decision does not describe the pending request: a different arguments hash, or a
    /// decision value no approver may submit. Refused rather than applied, because it decided about
    /// something other than what will run.
    /// </summary>
    Mismatched,

    /// <summary>
    /// A contradicting decision arrived after the request was settled. The first decision stands and
    /// is reported back; the second is not applied.
    /// </summary>
    Contradicted,
}

/// <summary>The result of submitting a decision, with the outcome that actually stands.</summary>
/// <param name="Status">How the submission was resolved.</param>
/// <param name="Outcome">
/// The decision that stands, for <see cref="RemoteApprovalSettleStatus.Accepted"/>,
/// <see cref="RemoteApprovalSettleStatus.AlreadyDecided"/> and
/// <see cref="RemoteApprovalSettleStatus.Contradicted"/>; otherwise <c>null</c>.
/// </param>
public readonly record struct RemoteApprovalSettlement(
    RemoteApprovalSettleStatus Status,
    string? Outcome
);

/// <summary>What one approver's allow did to a request's outstanding ballot.</summary>
internal enum RemoteApprovalBallot
{
    /// <summary>Counted, and at least one frozen approver still has not allowed.</summary>
    Recorded,

    /// <summary>This approver had already allowed; the ballot is unchanged.</summary>
    Duplicate,

    /// <summary>This allow was the last one outstanding, so the request may now be settled.</summary>
    Unanimous,
}

/// <summary>
/// One in-flight approval: the request its approvers were asked, and the task the gate is waiting on.
/// </summary>
/// <remarks>
/// <para>
/// Two pieces of state, deliberately separate. The ballot (<see cref="RecordAllow"/>) accumulates
/// allows and settles nothing; the claim settles the request exactly once. Keeping them apart is what
/// lets an allow be recorded without authorizing anything, which is the whole of the unanimity rule.
/// </para>
/// <para>
/// Disposing the ticket withdraws the request. Withdrawal races the decision endpoint, and the race
/// is resolved by the same atomic claim that resolves two competing decisions, so a decision that
/// landed first is never erased by the waiter giving up.
/// </para>
/// </remarks>
public sealed class RemoteApprovalTicket : IDisposable
{
    /// <summary>Claim marker for "the waiter abandoned this request", distinct from any decision.</summary>
    private static readonly object AbandonedMarker = new();

    private readonly RemoteApprovalStore _store;
    private readonly TaskCompletionSource<ToolApprovalDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Guards <see cref="_allowed"/>. Separate from the store's own lock because a ballot belongs to
    /// one request: two approvers answering different calls have no reason to contend.
    /// </summary>
    private readonly object _ballot = new();

    /// <summary>Which of <see cref="Approvers"/> have allowed so far. Guarded by <see cref="_ballot"/>.</summary>
    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);

    /// <summary>
    /// The single piece of mutable state that decides everything: <c>null</c> while pending, a
    /// <see cref="ToolApprovalDecision"/> once decided, <see cref="AbandonedMarker"/> once withdrawn.
    /// Transitioned only through <see cref="TryClaim"/>, so there is no read-then-write window in
    /// which two callers can both believe they won.
    /// </summary>
    private object? _claim;

    internal RemoteApprovalTicket(
        RemoteApprovalStore store,
        LifecycleOwnerKey owner,
        ToolApprovalRequest request,
        IReadOnlySet<string> approvers
    )
    {
        _store = store;
        Owner = owner;
        Request = request;
        Approvers = approvers;
    }

    /// <summary>The request handed to approvers. Its <c>arguments_hash</c> pins what may run.</summary>
    public ToolApprovalRequest Request { get; }

    /// <summary>
    /// Completes with the decision that settled this request, or is cancelled if the request is
    /// withdrawn before any decision arrives.
    /// </summary>
    public Task<ToolApprovalDecision> Decision => _completion.Task;

    /// <summary>The owner this request is scoped to; only that owner's decisions can settle it.</summary>
    internal LifecycleOwnerKey Owner { get; }

    /// <summary>
    /// The subscriptions frozen as this request's approvers at the moment the gate opened.
    /// </summary>
    /// <remarks>
    /// Membership is fixed for the life of the request, and that is the point. A subscription
    /// registered after the gate opened cannot answer, and an approval-capable subscriber that was
    /// simply not chosen cannot stand in for one that was — so "who may decide this call" is settled
    /// once, by the host, rather than re-derived from whatever the subscription registry happens to
    /// contain when a decision lands.
    /// </remarks>
    internal IReadOnlySet<string> Approvers { get; }

    /// <summary>When the request was decided, used to age its tombstone out. Written under the claim.</summary>
    internal DateTimeOffset SettledAtUtc { get; private set; }

    /// <summary>The decision that stands, or <c>null</c> while pending or once abandoned.</summary>
    internal ToolApprovalDecision? Decided => Volatile.Read(ref _claim) as ToolApprovalDecision;

    /// <summary>Withdraws the request. Safe to call repeatedly and after the request was decided.</summary>
    public void Dispose() => _store.Withdraw(this);

    /// <summary>
    /// Attempts the one-shot pending → settled transition.
    /// </summary>
    /// <param name="claim">The decision, or <see cref="AbandonedMarker"/>.</param>
    /// <returns><c>true</c> for the single caller that made the transition.</returns>
    private bool TryClaim(object claim) =>
        Interlocked.CompareExchange(ref _claim, claim, null) is null;

    /// <summary>Claims the request for <paramref name="decision"/> and stamps the settle time.</summary>
    internal bool TryDecide(ToolApprovalDecision decision, DateTimeOffset now)
    {
        if (!TryClaim(decision))
        {
            return false;
        }

        // Only the winner writes this, and it is published to the pruner by the store's lock.
        SettledAtUtc = now;
        return true;
    }

    /// <summary>Claims the request as abandoned, so no later decision can settle it.</summary>
    internal bool TryAbandon() => TryClaim(AbandonedMarker);

    /// <summary>
    /// Counts one frozen approver's allow towards unanimity.
    /// </summary>
    /// <param name="subscriptionId">The approver allowing. Already known to be in <see cref="Approvers"/>.</param>
    /// <returns>Whether the ballot is now complete, unchanged, or still short an approver.</returns>
    /// <remarks>
    /// The count is taken under the lock rather than compared afterwards, so of two approvers
    /// answering simultaneously exactly one can see the set complete — which is what stops both from
    /// racing to settle and one of them reading its own allow back as a contradiction.
    /// </remarks>
    internal RemoteApprovalBallot RecordAllow(string subscriptionId)
    {
        lock (_ballot)
        {
            if (!_allowed.Add(subscriptionId))
            {
                return RemoteApprovalBallot.Duplicate;
            }

            return _allowed.Count >= Approvers.Count
                ? RemoteApprovalBallot.Unanimous
                : RemoteApprovalBallot.Recorded;
        }
    }

    /// <summary>Hands the decision to the waiting gate.</summary>
    internal void PublishDecision(ToolApprovalDecision decision) =>
        _ = _completion.TrySetResult(decision);

    /// <summary>Fails the waiting gate, so a withdrawn request does not wait out its full expiry.</summary>
    internal void PublishWithdrawal() => _ = _completion.TrySetCanceled();
}

/// <summary>
/// The pending state behind remote tool approval: which calls are waiting for an answer, which have
/// already been answered, and who is entitled to answer them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purely in-memory, and that is the design.</b> Request ids embed a per-process epoch, so a
/// decision minted before a restart is not merely unknown but structurally unrecognizable. That
/// matters because after a restart there is no tool call left to run: honouring an old approval
/// could only ever authorize something the host is no longer holding. Fixing this by persisting
/// pending approvals would create the opposite hazard — an approval surviving the call it was about.
/// </para>
/// <para>
/// <b>Everything is scoped by <see cref="LifecycleOwnerKey"/></b> (ADR 0005), and every scope
/// failure is reported as <see cref="RemoteApprovalSettleStatus.Unknown"/>. A caller probing another
/// owner's request ids learns exactly what a caller probing invented ids learns.
/// </para>
/// </remarks>
public sealed class RemoteApprovalStore
{
    /// <summary>Separates the process epoch from the per-request randomness in a request id.</summary>
    private const char EpochSeparator = '.';

    /// <summary>
    /// Minted once per process from a CSPRNG rather than from a timestamp or a counter: a clock can
    /// repeat across a restart and a counter always does, either of which would let a decision from a
    /// previous process land on a fresh request that happens to reuse its id.
    /// </summary>
    private static readonly string ProcessEpoch = Convert.ToHexStringLower(
        RandomNumberGenerator.GetBytes(8)
    );

    private readonly RemoteApprovalOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoteApprovalStore> _logger;

    /// <summary>
    /// Pending requests <em>and</em> the tombstones of settled ones, in one map. Keeping a settled
    /// request in place — rather than removing it and recording the answer elsewhere — is what makes
    /// "first decision wins" observable: a retry finds the same entry and reads the same answer.
    /// </summary>
    private readonly ConcurrentDictionary<string, RemoteApprovalTicket> _tickets =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Guards the admission counters and the tombstone queue. A plain lock is appropriate here
    /// because admission is bounded to a few hundred outstanding requests, so this is never a hot
    /// path; the settle path's actual race is resolved lock-free on the ticket itself.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>Settled request ids in settle order, which is what makes pruning amortized.</summary>
    private readonly Queue<string> _tombstones = new();

    private readonly Dictionary<string, int> _pendingByOwner = new(StringComparer.Ordinal);
    private int _pendingTotal;

    /// <summary>Creates the store over its configuration and clock.</summary>
    /// <param name="options">Admission and retention limits; validated here so a bad configuration
    /// fails at startup rather than at the first tool call, when the only safe response left is to
    /// block the call.</param>
    /// <param name="timeProvider">Drives expiry and tombstone ageing, so tests need no wall clock.</param>
    /// <param name="logger">Diagnostics sink. Only request ids and owner keys are logged, never arguments.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The options are inconsistent.</exception>
    public RemoteApprovalStore(
        RemoteApprovalOptions options,
        TimeProvider timeProvider,
        ILogger<RemoteApprovalStore> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Requests currently awaiting a decision. Diagnostic; tombstones are not counted.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingTotal;
            }
        }
    }

    /// <summary>
    /// Registers a pending approval for <paramref name="owner"/>, or refuses it when an admission
    /// limit is already reached.
    /// </summary>
    /// <param name="owner">The server-resolved owner entitled to decide this request.</param>
    /// <param name="context">The tool call being gated; its frozen arguments hash and effective
    /// expiry are copied onto the request.</param>
    /// <param name="approverSubscriptionIds">
    /// The subscriptions selected as this request's approvers. Frozen here: every one of them must
    /// allow before the call runs, and nothing outside this set can answer at all.
    /// </param>
    /// <returns>
    /// The ticket to await and dispose, or <c>null</c> when the request was refused. A refusal is
    /// never a soft failure: per ADR 0003 a call that cannot be approved does not run.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="approverSubscriptionIds"/> is empty. A request with no approvers could never be
    /// unanimously allowed, so registering one would create a pending entry whose only possible
    /// outcome is a timeout — a wiring mistake worth failing loudly rather than a state worth holding.
    /// </exception>
    public RemoteApprovalTicket? TryRegister(
        LifecycleOwnerKey owner,
        ToolApprovalContext context,
        IEnumerable<string> approverSubscriptionIds
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(approverSubscriptionIds);

        var approvers = approverSubscriptionIds.ToHashSet(StringComparer.Ordinal);
        if (approvers.Count == 0)
        {
            throw new ArgumentException(
                "An approval request needs at least one approver; a request with none can only ever time out.",
                nameof(approverSubscriptionIds)
            );
        }

        var request = new ToolApprovalRequest
        {
            RequestId = MintRequestId(),
            ThreadId = context.ThreadId ?? string.Empty,
            RunId = context.RunId ?? string.Empty,
            GenerationId = context.GenerationId ?? string.Empty,
            ToolCallId = context.ToolCallId ?? string.Empty,
            ToolName = context.ToolName,
            // The hash, not the arguments: what an approver may see is decided per subscriber, and
            // the store has no business holding the argument text for every pending call.
            ArgumentsHash = context.Arguments.Sha256Hex,
            ExpiresAt = context.ExpiresAt,
        };

        lock (_gate)
        {
            // Registration is the natural moment to age tombstones out: it is the one operation whose
            // rate tracks the state we are bounding.
            PruneTombstones(_timeProvider.GetUtcNow());

            var forOwner = _pendingByOwner.GetValueOrDefault(owner.Value);
            if (_pendingTotal >= _options.MaxPendingTotal || forOwner >= _options.MaxPendingPerOwner)
            {
                _logger.LogWarning(
                    "Refusing tool approval for owner {Owner} on tool {Tool}: {OwnerPending} pending for this owner, {TotalPending} in total.",
                    owner.Value,
                    context.ToolName,
                    forOwner,
                    _pendingTotal
                );
                return null;
            }

            _pendingTotal++;
            _pendingByOwner[owner.Value] = forOwner + 1;
        }

        // Built only once the slot is genuinely held, so a ticket cannot exist without a slot to
        // release — which is what makes Dispose's decrement unconditionally correct.
        var ticket = new RemoteApprovalTicket(this, owner, request, approvers);

        // The id is freshly minted, so this cannot collide with an existing entry.
        _tickets[request.RequestId] = ticket;
        return ticket;
    }

    /// <summary>
    /// Applies a decision submitted by <paramref name="owner"/> to the request it names.
    /// </summary>
    /// <param name="owner">The server-resolved owner of the caller. Never taken from the payload.</param>
    /// <param name="decision">The submitted decision.</param>
    /// <returns>How the submission was resolved, and the outcome that stands.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public RemoteApprovalSettlement Settle(LifecycleOwnerKey owner, ToolApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(decision);

        // A stale-epoch id is rejected before any lookup, so a flood of decisions minted by a
        // previous process cannot touch — or even probe — this process's state.
        if (!HasCurrentEpoch(decision.RequestId) || !_tickets.TryGetValue(decision.RequestId, out var ticket))
        {
            return Unknown();
        }

        // Cross-owner submissions are indistinguishable from unknown ids by construction: both
        // return here, with the same status and the same absence of detail.
        if (!string.Equals(ticket.Owner.Value, owner.Value, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Owner {Owner} submitted a decision for a request it does not own; refused as unknown.",
                owner.Value
            );
            return Unknown();
        }

        // "You were not asked" is reported as unknown, exactly like another owner's request id. An
        // approval-capable subscriber that was not frozen into this request cannot use the endpoint to
        // learn that the id is real, let alone substitute its answer for the one that was solicited.
        if (
            decision.SubscriptionId is not { Length: > 0 } submitter
            || !ticket.Approvers.Contains(submitter)
        )
        {
            _logger.LogWarning(
                "Subscription {SubscriptionId} submitted a decision for a request it was not asked to approve; refused as unknown.",
                decision.SubscriptionId
            );
            return Unknown();
        }

        // Shape before state: a decision that describes different arguments, or carries a value no
        // approver may submit, is wrong regardless of whether the request is still pending.
        if (!decision.Matches(ticket.Request))
        {
            return new RemoteApprovalSettlement(RemoteApprovalSettleStatus.Mismatched, null);
        }

        if (ticket.Decided is { } already)
        {
            return Resolve(already, decision);
        }

        var now = _timeProvider.GetUtcNow();
        if (now >= ticket.Request.ExpiresAt)
        {
            // Expiry reads as unknown rather than as its own status: the tool call has already been
            // blocked by the time this arrives, so the honest answer is "there is nothing to decide".
            _logger.LogInformation(
                "Discarding decision for expired approval {RequestId}.",
                decision.RequestId
            );
            return Unknown();
        }

        // An allow settles nothing until it is the last one outstanding. A deny skips the ballot
        // entirely: one approver refusing is already the answer, and counting it would only postpone
        // a call that is not going to run either way.
        if (
            WireOutcomes.IsAllowed(decision.Decision)
            && ticket.RecordAllow(submitter) != RemoteApprovalBallot.Unanimous
        )
        {
            // Re-read the claim rather than reporting "recorded" blind: a deny may have settled the
            // request between the expiry check and the ballot, and telling an approver its allow is
            // outstanding when the call has already been refused is a worse answer than the truth.
            return ticket.Decided is { } settled
                ? Resolve(settled, decision)
                : new RemoteApprovalSettlement(RemoteApprovalSettleStatus.Recorded, null);
        }

        return Commit(ticket, decision, now);
    }

    /// <summary>
    /// Denies every pending request <paramref name="subscriptionId"/> was frozen into, because that
    /// subscription can no longer answer.
    /// </summary>
    /// <param name="owner">The owner the revoked subscription belonged to. Scoped so one tenant's
    /// revocation cannot reach into another's pending calls.</param>
    /// <param name="subscriptionId">The subscription that was revoked.</param>
    /// <returns>How many pending requests this denied. Diagnostic.</returns>
    /// <remarks>
    /// Without this, revoking an approver would leave every call it was asked about hanging until its
    /// expiry: unanimity means the remaining approvers can never complete the set, so the outcome is
    /// already decided and only the timing is in question. Denying immediately makes the wait honest
    /// and frees the admission slot, and it is the fail-closed direction either way.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public int InvalidateForSubscription(LifecycleOwnerKey owner, string subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var now = _timeProvider.GetUtcNow();
        var denied = 0;

        // A snapshot of a map bounded by MaxPendingTotal plus its tombstones, walked on an operation
        // that happens when a subscriber goes away. An index keyed by subscription would have to stay
        // consistent with the ballot through every settle; this cannot drift.
        foreach (var ticket in _tickets.Values)
        {
            if (
                !string.Equals(ticket.Owner.Value, owner.Value, StringComparison.Ordinal)
                || !ticket.Approvers.Contains(subscriptionId)
                || ticket.Decided is not null
            )
            {
                continue;
            }

            var settlement = Commit(
                ticket,
                new ToolApprovalDecision
                {
                    RequestId = ticket.Request.RequestId,
                    SubscriptionId = subscriptionId,
                    Decision = WireOutcomes.Denied,
                    ArgumentsHash = ticket.Request.ArgumentsHash,
                    Reason = "an approver's subscription was revoked while the request was pending",
                    DecidedAt = now,
                },
                now
            );

            if (settlement.Status == RemoteApprovalSettleStatus.Accepted)
            {
                denied++;
            }
        }

        if (denied > 0)
        {
            _logger.LogInformation(
                "Denied {Count} pending approval(s) because subscription {SubscriptionId} was revoked.",
                denied,
                subscriptionId
            );
        }

        return denied;
    }

    /// <summary>
    /// Performs the one-shot pending → decided transition and publishes the result to the waiting
    /// gate. The single place a request is ever settled, so a decision submitted by an approver and
    /// one synthesized by a revocation cannot diverge in what they do to the ticket.
    /// </summary>
    private RemoteApprovalSettlement Commit(
        RemoteApprovalTicket ticket,
        ToolApprovalDecision decision,
        DateTimeOffset now
    )
    {
        if (!ticket.TryDecide(decision, now))
        {
            // Lost a genuine race — to another decision, whose answer is authoritative for this call
            // too, or to the waiter withdrawing the request in the same instant. A withdrawn request
            // reads as unknown because there is no longer a call this decision could authorize.
            return ticket.Decided is { } winner ? Resolve(winner, decision) : Unknown();
        }

        RetireTicket(ticket, now);
        ticket.PublishDecision(decision);
        return new RemoteApprovalSettlement(RemoteApprovalSettleStatus.Accepted, decision.Decision);
    }

    /// <summary>
    /// Withdraws a pending request. Called by <see cref="RemoteApprovalTicket.Dispose"/>; a request
    /// that was already decided keeps its tombstone so retries still read the recorded answer.
    /// </summary>
    /// <param name="ticket">The ticket being withdrawn.</param>
    internal void Withdraw(RemoteApprovalTicket ticket)
    {
        if (!ticket.TryAbandon())
        {
            return;
        }

        // Abandoned requests leave no tombstone: nothing was decided, so there is no answer to
        // remember, and the entry must disappear rather than accumulate.
        _ = _tickets.TryRemove(ticket.Request.RequestId, out _);

        lock (_gate)
        {
            ReleasePendingSlot(ticket.Owner);
        }

        ticket.PublishWithdrawal();
    }

    /// <summary>Moves a decided request from the pending budget into the bounded tombstone set.</summary>
    private void RetireTicket(RemoteApprovalTicket ticket, DateTimeOffset now)
    {
        lock (_gate)
        {
            ReleasePendingSlot(ticket.Owner);

            // Enqueued while the entry is still in the map, so a retry arriving at any instant either
            // reads the recorded answer or reads unknown — never a window where the answer is lost.
            _tombstones.Enqueue(ticket.Request.RequestId);
            PruneTombstones(now);
        }
    }

    private void ReleasePendingSlot(LifecycleOwnerKey owner)
    {
        _pendingTotal--;

        var remaining = _pendingByOwner.GetValueOrDefault(owner.Value) - 1;
        if (remaining <= 0)
        {
            // Drop the key rather than leaving a zero behind, so a long-lived host does not
            // accumulate one dictionary entry per owner it has ever served.
            _ = _pendingByOwner.Remove(owner.Value);
        }
        else
        {
            _pendingByOwner[owner.Value] = remaining;
        }
    }

    /// <summary>
    /// Ages tombstones out by retention and then by count. Both bounds are enforced from the head of
    /// a settle-ordered queue, so each call does work proportional to what actually expired rather
    /// than sweeping every entry.
    /// </summary>
    private void PruneTombstones(DateTimeOffset now)
    {
        while (_tombstones.Count > 0)
        {
            var oldest = _tombstones.Peek();
            if (
                _tickets.TryGetValue(oldest, out var settled)
                && now - settled.SettledAtUtc < _options.TombstoneRetention
            )
            {
                // The queue is in settle order, so a head still inside retention implies every entry
                // behind it is too.
                break;
            }

            Evict();
        }

        while (_tombstones.Count > _options.MaxTombstones)
        {
            Evict();
        }

        void Evict()
        {
            _ = _tickets.TryRemove(_tombstones.Dequeue(), out _);
        }
    }

    private static RemoteApprovalSettlement Unknown() =>
        new(RemoteApprovalSettleStatus.Unknown, null);

    /// <summary>
    /// Compares a late decision against the one that stands. The arguments hash is already known to
    /// match (<see cref="ToolApprovalDecision.Matches"/> ran first), so allow-versus-deny is the only
    /// way the two can disagree.
    /// </summary>
    private static RemoteApprovalSettlement Resolve(
        ToolApprovalDecision standing,
        ToolApprovalDecision submitted
    )
    {
        var identical = string.Equals(
            standing.Decision,
            submitted.Decision,
            StringComparison.Ordinal
        );

        return new RemoteApprovalSettlement(
            identical
                ? RemoteApprovalSettleStatus.AlreadyDecided
                : RemoteApprovalSettleStatus.Contradicted,
            standing.Decision
        );
    }

    /// <summary>An unguessable id, prefixed with this process's epoch.</summary>
    private static string MintRequestId() =>
        ProcessEpoch + EpochSeparator + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Whether an id was minted by this process. Checked without touching any state so that a
    /// stale-epoch decision costs nothing and reveals nothing.
    /// </summary>
    private static bool HasCurrentEpoch(string? requestId) =>
        requestId is not null
        && requestId.Length > ProcessEpoch.Length
        && requestId[ProcessEpoch.Length] == EpochSeparator
        && string.CompareOrdinal(requestId, 0, ProcessEpoch, 0, ProcessEpoch.Length) == 0;
}
