using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>Where one admitted message has got to.</summary>
/// <remarks>
/// Acceptance and delivery are separate states because they are separate events with separate failure
/// modes. A ledger that collapsed them would have no way to express "the collaboration took
/// responsibility for this message and then could not hand it over", which is exactly the case a
/// waiting sender must be told about.
/// </remarks>
public enum AgentMessageDeliveryState
{
    /// <summary>Admitted and queued for its target, not yet handed over.</summary>
    Accepted,

    /// <summary>The target's owner accepted it into the target's own input path.</summary>
    Delivered,

    /// <summary>A reply correlated back to it. Terminal.</summary>
    Answered,

    /// <summary>It could not be handed over and never will be. Terminal.</summary>
    DeliveryFailed,

    /// <summary>Its target left before replying. Terminal.</summary>
    Abandoned,
}

/// <summary>Why a message could not be admitted.</summary>
public static class AgentMessageFailureCodes
{
    /// <summary>The target has no directory entry.</summary>
    public const string UnknownTarget = "unknown_target";

    /// <summary>The target's inbox is at capacity. Recoverable: the sender may retry later.</summary>
    public const string InboxFull = "inbox_full";

    /// <summary>The message this one replies to is not in the ledger.</summary>
    public const string UnknownCorrelation = "unknown_correlation";

    /// <summary>
    /// A message type that only exists as a reply carried no correlation. Recoverable: the sender may
    /// resend it naming the message it answers, or progresses.
    /// </summary>
    public const string MissingCorrelation = "missing_correlation";

    /// <summary>The message this one replies to has already been answered, failed, or abandoned.</summary>
    public const string CorrelationClosed = "correlation_closed";

    /// <summary>The sender is not the agent the original message was addressed to.</summary>
    public const string CorrelationNotAddressedToSender = "correlation_not_addressed_to_sender";

    /// <summary>The original message did not expect a reply.</summary>
    public const string CorrelationDoesNotExpectReply = "correlation_does_not_expect_reply";

    /// <summary>
    /// A progress update correlated to something that is not an open delegation. Recoverable: the
    /// sender may resend it as an answer, or against the delegation it is actually progress on.
    /// </summary>
    public const string CorrelationNotADelegation = "correlation_not_a_delegation";

    /// <summary>A message addressed to its own sender.</summary>
    public const string SelfDelivery = "self_delivery";

    /// <summary>The sender identity is blank, unknown, or belongs to an agent that has left.</summary>
    public const string InvalidSender = "invalid_sender";

    /// <summary>
    /// The target has been admitted but has not started running yet, so nothing can be handed to it.
    /// Recoverable: the sender may retry once the target reports <c>running</c>.
    /// </summary>
    public const string TargetNotStarted = "target_not_started";

    /// <summary>
    /// A steer was addressed to an agent that is not currently running, so there is no work in flight
    /// to redirect. Recoverable only in the sense that a different message type may apply.
    /// </summary>
    public const string TargetNotActive = "target_not_active";
}

/// <summary>One agent's request to send a message to another.</summary>
/// <param name="FromAgentId">Canonical identifier of the sender.</param>
/// <param name="ToAgentId">Canonical identifier of the target.</param>
/// <param name="MessageType">What kind of message this is.</param>
/// <param name="InResponseTo">
/// Identifier of the message this replies to, when it is a reply. Every correlation rule keys off this
/// one field, which is why it is part of admission rather than something applied afterwards.
/// </param>
/// <remarks>
/// Carries no body. The ledger records what a message <em>means</em> — who, to whom, in reply to what —
/// and never what it says, so no ledger operation, diagnostic, or event can become a route by which
/// message content escapes.
/// </remarks>
public readonly record struct AgentMessageAdmissionRequest(
    string FromAgentId,
    string ToAgentId,
    AgentMessageType MessageType,
    string? InResponseTo = null
);

/// <summary>The outcome of an admission attempt.</summary>
/// <param name="MessageId">Identifier minted for the admitted message, or null when it was refused.</param>
/// <param name="FailureCode">
/// A code from <see cref="AgentMessageFailureCodes"/> when the message was refused, otherwise null.
/// </param>
public readonly record struct AgentMessageAdmissionResult(
    string? MessageId,
    string? FailureCode = null
)
{
    /// <summary>Whether the message was admitted.</summary>
    public bool Succeeded => MessageId is not null;
}

/// <summary>
/// A content-free announcement that a message was admitted for a target.
/// </summary>
/// <remarks>
/// Deliberately just the identifiers and the kind. It exists so a target's owner can be woken instead
/// of polling its inbox, and something that only needs to say "there is work for you" has no business
/// carrying what the work says.
/// </remarks>
/// <param name="MessageId">Identifier of the admitted message.</param>
/// <param name="FromAgentId">Who sent it.</param>
/// <param name="ToAgentId">Who it is for.</param>
/// <param name="MessageType">What kind of message it is.</param>
public readonly record struct AgentMessageAdmittedNotice(
    string MessageId,
    string FromAgentId,
    string ToAgentId,
    AgentMessageType MessageType
);

/// <summary>The ledger's record of one message.</summary>
public sealed record AgentMessageLedgerEntry
{
    /// <summary>Identifier minted at admission.</summary>
    public required string MessageId { get; init; }

    /// <summary>Canonical identifier of the sender.</summary>
    public required string FromAgentId { get; init; }

    /// <summary>Canonical identifier of the target.</summary>
    public required string ToAgentId { get; init; }

    /// <summary>What kind of message this is.</summary>
    public required AgentMessageType MessageType { get; init; }

    /// <summary>The message this one replies to, when it is a reply.</summary>
    public string? InResponseTo { get; init; }

    /// <summary>Whether the sender is waiting for a reply to this message.</summary>
    public bool ExpectsReply { get; init; }

    /// <summary>Where this message has got to.</summary>
    public required AgentMessageDeliveryState State { get; init; }

    /// <summary>Content-free explanation of a failure or abandonment.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Identifier of the reply that closed this message, when one did.</summary>
    public string? ResponseMessageId { get; init; }

    /// <summary>
    /// Identifier of an admitted-but-not-yet-delivered reply that has claimed the right to close this
    /// message. Null when nothing is answering it.
    /// </summary>
    /// <remarks>
    /// A claim, not a closure. Admission proves only that the collaboration took responsibility for the
    /// reply, and a reply that then fails to be delivered never reaches the asker — closing on admission
    /// would leave the asker holding an answer that does not exist and no way to admit a second attempt.
    /// The claim gives idempotency (a concurrent second reply is refused while one is in flight) without
    /// giving up recoverability (a failed delivery releases it).
    /// </remarks>
    public string? PendingResponseMessageId { get; init; }

    /// <summary>
    /// Whether an agent's wait has already been interrupted by this message.
    /// </summary>
    /// <remarks>
    /// A delivered question stays open until it is answered, so without this flag every subsequent wait
    /// would rediscover it in the sweep and return immediately — the waiting agent would spin instead of
    /// waiting. The flag bounds a message to interrupting at most one wait while leaving it open, and so
    /// still independently answerable.
    /// </remarks>
    public bool WaitInterruptClaimed { get; init; }

    /// <summary>When the message was admitted.</summary>
    public required DateTimeOffset AdmittedAt { get; init; }

    /// <summary>When the message reached a terminal state, if it has.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Whether the message has reached a terminal state.</summary>
    public bool IsClosed => ClosedAt is not null;
}

/// <summary>
/// The single source of truth for what every in-flight collaboration message means and what has
/// happened to it.
/// </summary>
/// <remarks>
/// <para>
/// Split from the directory on purpose. The directory answers "where is this agent"; the ledger
/// answers "what is outstanding". Keeping identity and correlation in one class would mean an agent's
/// lifecycle and a message's lifecycle share a lock and a retention rule, which they should not: an
/// agent's entry outlives it for minutes, while a message closes the moment it is answered.
/// </para>
/// <para>
/// Admission is a single critical section that both claims the correlation and reserves the inbox
/// slot. Anything less would allow a reply to be admitted against a message that another thread was
/// simultaneously closing, or a slot to be consumed by a message that was then refused — neither of
/// which can be undone afterwards, because the refusal has already been returned to a model.
/// </para>
/// <para>
/// In-flight state is memory-only. A message that was never delivered before a restart cannot be
/// resumed, because the target's turn is gone; persisting it would only guarantee that it is retried
/// into a context that no longer exists.
/// </para>
/// </remarks>
public sealed class AgentMessageLedger
{
    private readonly Dictionary<string, AgentMessageLedgerEntry> _entries = new(
        StringComparer.Ordinal
    );

    // Closed entries in the order they closed, so retention is a walk from the front rather than a
    // scan of everything on every admission.
    private readonly Queue<string> _closedOrder = new();

    private readonly object _gate = new();
    private readonly AgentCollaborationOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an empty ledger.</summary>
    /// <param name="options">Root configuration supplying the retention bounds.</param>
    /// <param name="timeProvider">Clock used for admission and retention. Defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public AgentMessageLedger(AgentCollaborationOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised after a message is admitted, so a target's owner can be woken rather than made to poll.
    /// </summary>
    /// <remarks>
    /// Raised outside the ledger's lock. A handler that ran under it could call back into the ledger,
    /// or block it for as long as it takes to schedule a turn, either of which would stall every other
    /// sender in the collaboration.
    /// </remarks>
    public event Action<AgentMessageAdmittedNotice>? MessageAdmitted;

    /// <summary>How many messages the ledger currently remembers, open and retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Admits a message, claiming its correlation and reserving its inbox slot together, or refuses it
    /// without leaving a trace.
    /// </summary>
    /// <param name="request">Who is sending what, to whom, in reply to what.</param>
    /// <param name="targetInbox">
    /// The target's inbox, obtained from the directory. Null means the target is not registered.
    /// </param>
    /// <returns>The minted identifier, or a content-free refusal code.</returns>
    public AgentMessageAdmissionResult TryAdmit(
        AgentMessageAdmissionRequest request,
        AgentInbox? targetInbox
    )
    {
        if (
            string.IsNullOrWhiteSpace(request.FromAgentId)
            || string.IsNullOrWhiteSpace(request.ToAgentId)
        )
        {
            return new AgentMessageAdmissionResult(null, AgentMessageFailureCodes.InvalidSender);
        }

        if (string.Equals(request.FromAgentId, request.ToAgentId, StringComparison.Ordinal))
        {
            return new AgentMessageAdmissionResult(null, AgentMessageFailureCodes.SelfDelivery);
        }

        if (targetInbox is null)
        {
            return new AgentMessageAdmissionResult(null, AgentMessageFailureCodes.UnknownTarget);
        }

        AgentMessageAdmittedNotice notice;

        lock (_gate)
        {
            PruneClosed();

            var correlationFailure = ValidateCorrelation(request);
            if (correlationFailure is not null)
            {
                return new AgentMessageAdmissionResult(null, correlationFailure);
            }

            var messageId = $"agentmsg-{Guid.NewGuid():N}";

            // Reserved before the entry is written, so a refusal for a full inbox leaves the ledger
            // exactly as it was and the minted identifier is simply discarded.
            if (!targetInbox.TryEnqueue(messageId))
            {
                return new AgentMessageAdmissionResult(null, AgentMessageFailureCodes.InboxFull);
            }

            var now = _timeProvider.GetUtcNow();
            var expectsReply =
                request.MessageType is AgentMessageType.Question or AgentMessageType.DelegateTask;

            _entries[messageId] = new AgentMessageLedgerEntry
            {
                MessageId = messageId,
                FromAgentId = request.FromAgentId,
                ToAgentId = request.ToAgentId,
                MessageType = request.MessageType,
                InResponseTo = request.InResponseTo,
                ExpectsReply = expectsReply,
                State = AgentMessageDeliveryState.Accepted,
                AdmittedAt = now,
            };

            // Only a Response settles what it answers, and only once it has actually been delivered. A
            // TaskUpdate is progress on a delegation that is still running, so settling on it would
            // strand the delegator waiting for a result it had already been told would come.
            if (
                request.InResponseTo is { } original
                && request.MessageType == AgentMessageType.Response
                && TryGetOpen(original, out var originalEntry)
            )
            {
                _entries[original] = originalEntry with { PendingResponseMessageId = messageId };
            }

            notice = new AgentMessageAdmittedNotice(
                messageId,
                request.FromAgentId,
                request.ToAgentId,
                request.MessageType
            );
        }

        MessageAdmitted?.Invoke(notice);
        return new AgentMessageAdmissionResult(notice.MessageId);
    }

    /// <summary>
    /// Records that the target's owner accepted the message.
    /// </summary>
    /// <remarks>
    /// A message that expected no reply is finished at this point, so it closes here. One that did stays
    /// open until it is answered or its target leaves. A delivered <see cref="AgentMessageType.Response"/>
    /// is also the moment the message it answers is finally closed: only now has the answer reached the
    /// asker.
    /// </remarks>
    /// <returns>False when there is no open entry with that identifier.</returns>
    public bool MarkDelivered(string messageId)
    {
        lock (_gate)
        {
            if (!TryGetOpen(messageId, out var entry))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            SettleCorrelation(entry, delivered: true, now);

            if (!entry.ExpectsReply)
            {
                _entries[messageId] = entry with
                {
                    State = AgentMessageDeliveryState.Delivered,
                    ClosedAt = now,
                };
                _closedOrder.Enqueue(messageId);
                return true;
            }

            _entries[messageId] = entry with { State = AgentMessageDeliveryState.Delivered };
            return true;
        }
    }

    /// <summary>Records that the message will never arrive.</summary>
    /// <param name="messageId">The message that failed.</param>
    /// <param name="reasonCode">Content-free explanation, safe to surface and to log.</param>
    /// <returns>False when there is no open entry with that identifier.</returns>
    public bool MarkDeliveryFailed(string messageId, string reasonCode)
    {
        lock (_gate)
        {
            if (!TryGetOpen(messageId, out var entry))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();

            // Release before closing: an answer that never arrived must leave the question it claimed
            // open, or the responder could never try again and the asker would wait forever.
            SettleCorrelation(entry, delivered: false, now);
            return Close(
                messageId,
                AgentMessageDeliveryState.DeliveryFailed,
                reasonCode,
                responseMessageId: null,
                now
            );
        }
    }

    /// <summary>
    /// Claims the right for one waiter to be interrupted by this message, at most once.
    /// </summary>
    /// <returns>
    /// True when this caller took the claim. False when the message is unknown, already closed, or a
    /// previous waiter took it.
    /// </returns>
    public bool TryClaimWaitInterrupt(string messageId)
    {
        lock (_gate)
        {
            if (!TryGetOpen(messageId, out var entry) || entry.WaitInterruptClaimed)
            {
                return false;
            }

            _entries[messageId] = entry with { WaitInterruptClaimed = true };
            return true;
        }
    }

    /// <summary>
    /// Gives a claim back when the waiter that took it did not end up reporting the message.
    /// </summary>
    /// <remarks>
    /// Without this, a wait that lost its race to a timeout would silently consume the one interrupt a
    /// message gets, and no later wait would ever be woken by it.
    /// </remarks>
    public void ReleaseWaitInterrupt(string messageId)
    {
        lock (_gate)
        {
            if (TryGetOpen(messageId, out var entry) && entry.WaitInterruptClaimed)
            {
                _entries[messageId] = entry with { WaitInterruptClaimed = false };
            }
        }
    }

    /// <summary>
    /// Resolves the claim a reply took on the message it answers: closes that message as answered when
    /// the reply landed, or releases the claim when it did not. A no-op for anything that claimed
    /// nothing.
    /// </summary>
    /// <remarks>Callers already hold <see cref="_gate"/>.</remarks>
    private void SettleCorrelation(
        AgentMessageLedgerEntry reply,
        bool delivered,
        DateTimeOffset now
    )
    {
        if (
            reply.InResponseTo is not { } original
            || reply.MessageType != AgentMessageType.Response
            || !TryGetOpen(original, out var originalEntry)
            || !string.Equals(
                originalEntry.PendingResponseMessageId,
                reply.MessageId,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        if (delivered)
        {
            _ = Close(
                original,
                AgentMessageDeliveryState.Answered,
                reasonCode: null,
                reply.MessageId,
                now
            );
            return;
        }

        _entries[original] = originalEntry with { PendingResponseMessageId = null };
    }

    /// <summary>
    /// Closes every open message addressed to an agent that has left.
    /// </summary>
    /// <remarks>
    /// This is what stops a sender waiting forever. Without it, an agent that stopped between admission
    /// and reply would leave its correspondents holding open Questions that nothing could ever close.
    /// </remarks>
    /// <param name="toAgentId">The agent that left.</param>
    /// <param name="reasonCode">Content-free explanation, safe to surface and to log.</param>
    /// <returns>The identifiers that were closed, so their senders can be told.</returns>
    public IReadOnlyList<string> AbandonMessagesFor(string toAgentId, string reasonCode) =>
        string.IsNullOrWhiteSpace(toAgentId)
            ? []
            : Abandon(
                entry => string.Equals(entry.ToAgentId, toAgentId, StringComparison.Ordinal),
                reasonCode
            );

    /// <summary>
    /// Closes every open message an agent that has left was still owed an answer to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror image of <see cref="AbandonMessagesFor"/>, and just as necessary: a Question outlives
    /// the agent that asked it. Left open, the recipient keeps being offered it as answerable work — it
    /// can still interrupt a wait, and a reply to it is still admitted — on behalf of somebody who is no
    /// longer there to read the answer.
    /// </para>
    /// <para>
    /// Only entries that <see cref="AgentMessageLedgerEntry.ExpectsReply"/> are closed. A message nobody
    /// owes a reply to is finished the moment it is delivered, and an open one is simply mid-hand-off;
    /// closing that would race the delivery already in flight for no benefit, since there is no
    /// obligation left behind to cancel.
    /// </para>
    /// </remarks>
    /// <param name="fromAgentId">The agent that left.</param>
    /// <param name="reasonCode">Content-free explanation, safe to surface and to log.</param>
    /// <returns>The identifiers that were closed, so their recipients can stop holding them.</returns>
    public IReadOnlyList<string> AbandonMessagesFrom(string fromAgentId, string reasonCode) =>
        string.IsNullOrWhiteSpace(fromAgentId)
            ? []
            : Abandon(
                entry =>
                    entry.ExpectsReply
                    && string.Equals(entry.FromAgentId, fromAgentId, StringComparison.Ordinal),
                reasonCode
            );

    /// <summary>Abandons every open entry matching <paramref name="selector"/> under one lock.</summary>
    private IReadOnlyList<string> Abandon(
        Func<AgentMessageLedgerEntry, bool> selector,
        string reasonCode
    )
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var affected = _entries
                .Values.Where(entry => !entry.IsClosed && selector(entry))
                .Select(entry => entry.MessageId)
                .ToList();

            foreach (var messageId in affected)
            {
                _ = Close(
                    messageId,
                    AgentMessageDeliveryState.Abandoned,
                    reasonCode,
                    responseMessageId: null,
                    now
                );
            }

            return affected;
        }
    }

    /// <summary>The ledger's record of one message, or null once it has been pruned.</summary>
    public AgentMessageLedgerEntry? Find(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(messageId, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// What a sender is still waiting on, oldest first.
    /// </summary>
    /// <remarks>
    /// Ordered by admission time so a caller that has to choose one — to report, to time out, to give
    /// up on — makes the same choice every time.
    /// </remarks>
    public IReadOnlyList<AgentMessageLedgerEntry> GetOpenOutbound(string fromAgentId)
    {
        return SnapshotOpen(entry =>
            string.Equals(entry.FromAgentId, fromAgentId, StringComparison.Ordinal)
        );
    }

    /// <summary>What an agent still owes a reply on, oldest first.</summary>
    public IReadOnlyList<AgentMessageLedgerEntry> GetOpenInbound(string toAgentId)
    {
        return SnapshotOpen(entry =>
            string.Equals(entry.ToAgentId, toAgentId, StringComparison.Ordinal)
        );
    }

    private IReadOnlyList<AgentMessageLedgerEntry> SnapshotOpen(
        Func<AgentMessageLedgerEntry, bool> predicate
    )
    {
        lock (_gate)
        {
            return
            [
                .. _entries
                    .Values.Where(entry => !entry.IsClosed && predicate(entry))
                    .OrderBy(entry => entry.AdmittedAt)
                    .ThenBy(entry => entry.MessageId, StringComparer.Ordinal),
            ];
        }
    }

    private string? ValidateCorrelation(AgentMessageAdmissionRequest request)
    {
        if (request.InResponseTo is not { } original)
        {
            // A Response and a TaskUpdate only exist relative to something. Admitting one with no
            // correlation would produce a message the receiver cannot place and the ledger cannot
            // settle, so it is refused here rather than delivered as an orphan.
            return request.MessageType
                is AgentMessageType.Response
                    or AgentMessageType.TaskUpdate
                ? AgentMessageFailureCodes.MissingCorrelation
                : null;
        }

        if (!_entries.TryGetValue(original, out var entry))
        {
            return AgentMessageFailureCodes.UnknownCorrelation;
        }

        // Idempotency lives here: a second reply to an already-answered message is refused rather than
        // silently accepted, so a retry can never produce two answers to one question. A reply that is
        // admitted but still in flight holds the same ground via its claim, and gives it back if it
        // fails to be delivered.
        if (entry.IsClosed || entry.PendingResponseMessageId is not null)
        {
            return AgentMessageFailureCodes.CorrelationClosed;
        }

        // Only the agent a message was addressed to may answer it. Otherwise a third party could close
        // somebody else's question and the original target's real answer would be refused.
        if (!string.Equals(entry.ToAgentId, request.FromAgentId, StringComparison.Ordinal))
        {
            return AgentMessageFailureCodes.CorrelationNotAddressedToSender;
        }

        if (!string.Equals(entry.FromAgentId, request.ToAgentId, StringComparison.Ordinal))
        {
            return AgentMessageFailureCodes.CorrelationNotAddressedToSender;
        }

        // Progress belongs to delegated work and to nothing else. A TaskUpdate correlated to a Question
        // would leave the asker holding an open question while being told about progress it never asked
        // for — and, because an update closes nothing, the question could then only ever be closed by
        // the target leaving. Refused at admission rather than recorded and ignored, so the sender is
        // told in time to send an answer instead.
        if (
            request.MessageType == AgentMessageType.TaskUpdate
            && entry.MessageType != AgentMessageType.DelegateTask
        )
        {
            return AgentMessageFailureCodes.CorrelationNotADelegation;
        }

        return entry.ExpectsReply ? null : AgentMessageFailureCodes.CorrelationDoesNotExpectReply;
    }

    private bool TryGetOpen(string messageId, out AgentMessageLedgerEntry entry)
    {
        if (
            !string.IsNullOrWhiteSpace(messageId)
            && _entries.TryGetValue(messageId, out var found)
            && !found.IsClosed
        )
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    private bool Close(
        string messageId,
        AgentMessageDeliveryState state,
        string? reasonCode,
        string? responseMessageId,
        DateTimeOffset now
    )
    {
        if (!TryGetOpen(messageId, out var entry))
        {
            return false;
        }

        _entries[messageId] = entry with
        {
            State = state,
            ReasonCode = reasonCode,
            ResponseMessageId = responseMessageId,
            ClosedAt = now,
        };
        _closedOrder.Enqueue(messageId);
        return true;
    }

    /// <summary>
    /// Forgets closed entries that are old enough or numerous enough to be beyond use.
    /// </summary>
    /// <remarks>
    /// Runs on the admission path rather than on a timer: the ledger only grows when a message is
    /// admitted, so that is exactly when it is worth checking, and it means the class owns no timer and
    /// therefore needs no disposal. Open entries are never pruned — a message that is still outstanding
    /// is still needed however old it is.
    /// </remarks>
    private void PruneClosed()
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.ClosedEntryRetention;

        while (_closedOrder.Count > 0)
        {
            var messageId = _closedOrder.Peek();

            if (!_entries.TryGetValue(messageId, out var entry) || entry.ClosedAt is null)
            {
                // Already gone, or reopened by nothing that exists — drop the stale pointer.
                _ = _closedOrder.Dequeue();
                continue;
            }

            var isOverCount = _closedOrder.Count > _options.MaxClosedEntries;
            if (!isOverCount && entry.ClosedAt > cutoff)
            {
                // The queue is in close order, so the first entry that is young enough proves every
                // entry behind it is too.
                break;
            }

            _ = _closedOrder.Dequeue();
            _ = _entries.Remove(messageId);
        }
    }
}
