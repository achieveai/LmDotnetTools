using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>The outcome of one agent's attempt to send a message to another.</summary>
/// <param name="MessageId">Identifier minted for the admitted message, or null when it was refused.</param>
/// <param name="Target">The resolved target, when resolution succeeded.</param>
/// <param name="FailureCode">
/// A code from <see cref="AgentDirectoryFailureCodes"/> or <see cref="AgentMessageFailureCodes"/> when
/// the message was refused, otherwise null.
/// </param>
public readonly record struct AgentSendResult(
    string? MessageId,
    AgentDirectoryEntry? Target,
    string? FailureCode = null
)
{
    /// <summary>Whether the message was admitted.</summary>
    public bool Succeeded => MessageId is not null;
}

/// <summary>
/// The one object a collaboration is: a directory of who is here, a ledger of what is outstanding, and
/// the settings both obey.
/// </summary>
/// <remarks>
/// <para>
/// Created once by the root and shared by reference down the whole hierarchy, which is what makes
/// hierarchy-wide addressing possible at all: a nested agent's manager knows only its own children, so
/// only something owned above every manager can answer questions about agents in a different branch.
/// </para>
/// <para>
/// Its absence is the feature gate. Nothing constructs a bundle unless collaboration is configured, and
/// every collaboration-aware code path is behind a null check, so an unconfigured run takes exactly the
/// code path it took before this existed.
/// </para>
/// <para>
/// Composition, not another layer of policy. Sending is directory resolution followed by ledger
/// admission, and reading is a directory lookup followed by the transcript policy; the bundle exists so
/// those two orderings are written once rather than at each of the several call sites that will need
/// them.
/// </para>
/// </remarks>
public sealed class AgentCollaborationBundle
{
    /// <summary>Reason recorded against messages abandoned because their target left.</summary>
    public const string TargetLeftReasonCode = "target_left";

    /// <summary>Reason recorded against messages abandoned because the agent that sent them left.</summary>
    public const string SenderLeftReasonCode = "sender_left";

    // One tail per target, so admissions for the same target hand over in the order they were admitted.
    // Keyed by canonical agent id, and therefore no larger than the directory itself.
    private readonly Dictionary<string, Task> _deliveryTails = new(StringComparer.Ordinal);

    // Guards the admission-plus-hand-off pair. Separate from the ledger's own lock because it covers a
    // strictly wider critical section: the ledger only has to make admission atomic, while ordering also
    // needs the chain link taken before the next admission can observe it.
    private readonly object _deliveryGate = new();

    /// <summary>Creates a collaboration and validates the settings it will enforce.</summary>
    /// <param name="collaborationId">Identifier of the root thread this collaboration belongs to.</param>
    /// <param name="options">Settings the directory and ledger enforce.</param>
    /// <param name="timeProvider">Clock used for ledger retention. Defaults to the system clock.</param>
    /// <remarks>
    /// Options are validated here rather than at first use, because every bound they carry becomes a
    /// refusal returned to a model. A bad bound must fail the run that configured it, not surface much
    /// later as an inexplicable refusal in the middle of a conversation.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="collaborationId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound in <paramref name="options"/> is invalid.</exception>
    public AgentCollaborationBundle(
        string collaborationId,
        AgentCollaborationOptions options,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        CollaborationId = collaborationId;
        Options = options;
        Directory = new AgentCollaborationDirectory(collaborationId, options);
        Ledger = new AgentMessageLedger(options, timeProvider);
    }

    /// <summary>Identifier of the root thread this collaboration belongs to.</summary>
    public string CollaborationId { get; }

    /// <summary>Settings the directory and ledger enforce.</summary>
    public AgentCollaborationOptions Options { get; }

    /// <summary>Who is in this collaboration and where.</summary>
    public AgentCollaborationDirectory Directory { get; }

    /// <summary>What is outstanding between them.</summary>
    public AgentMessageLedger Ledger { get; }

    /// <summary>
    /// Resolves a target and admits a message to it, or refuses with one content-free code.
    /// </summary>
    /// <param name="fromAgentId">Canonical identifier of the sender.</param>
    /// <param name="target">Canonical identifier or name of the target.</param>
    /// <param name="messageType">What kind of message this is.</param>
    /// <param name="inResponseTo">Identifier of the message this replies to, when it is a reply.</param>
    /// <remarks>
    /// Liveness is checked between resolution and admission. A retained entry stays resolvable so a
    /// sender can be told what became of an agent, but nothing new may be queued for one, and the
    /// distinction is invisible unless it is enforced right here.
    /// </remarks>
    public AgentSendResult TrySend(
        string fromAgentId,
        string target,
        AgentMessageType messageType,
        string? inResponseTo = null
    )
    {
        lock (_deliveryGate)
        {
            return TrySendCore(fromAgentId, target, messageType, inResponseTo);
        }
    }

    /// <summary>Performs one admission while the caller holds <see cref="_deliveryGate"/>.</summary>
    private AgentSendResult TrySendCore(
        string fromAgentId,
        string target,
        AgentMessageType messageType,
        string? inResponseTo
    )
    {
        var sender = Directory.FindById(fromAgentId);
        if (sender is null || !sender.IsLive)
        {
            return new AgentSendResult(null, null, AgentMessageFailureCodes.InvalidSender);
        }

        var resolution = Directory.Resolve(target);
        if (resolution.Entry is not { } entry)
        {
            return new AgentSendResult(null, null, resolution.FailureCode);
        }

        if (!entry.IsLive)
        {
            return new AgentSendResult(null, entry, AgentMessageFailureCodes.UnknownTarget);
        }

        // A queued agent has a directory entry and an inbox but no turn to inject into yet, so its
        // owner would refuse the hand-off after the sender had already been told "accepted". Refusing
        // here instead keeps admission and delivery agreeing about what is deliverable, and gives the
        // sender a recoverable answer it can act on rather than a silent drop it cannot see.
        if (
            string.Equals(entry.Status, AgentCollaborationStatuses.Queued, StringComparison.Ordinal)
        )
        {
            return new AgentSendResult(null, entry, AgentMessageFailureCodes.TargetNotStarted);
        }

        // A steer redirects work that is under way. Sent to an agent that is not running it would
        // either restart it — the opposite of redirecting — or land with nothing to redirect, so it is
        // refused synchronously while the sender can still choose a message type that does apply.
        if (
            messageType == AgentMessageType.Steer
            && !string.Equals(
                entry.Status,
                AgentCollaborationStatuses.Running,
                StringComparison.Ordinal
            )
        )
        {
            return new AgentSendResult(null, entry, AgentMessageFailureCodes.TargetNotActive);
        }

        var admission = Ledger.TryAdmit(
            new AgentMessageAdmissionRequest(fromAgentId, entry.AgentId, messageType, inResponseTo),
            Directory.GetInbox(entry.AgentId)
        );

        return new AgentSendResult(admission.MessageId, entry, admission.FailureCode);
    }

    /// <summary>
    /// Admits a message and links its delivery onto the target's delivery chain, so deliveries to one
    /// target run in admission order.
    /// </summary>
    /// <param name="fromAgentId">Canonical identifier of the sender.</param>
    /// <param name="target">Canonical identifier or name of the target.</param>
    /// <param name="messageType">What kind of message this is.</param>
    /// <param name="inResponseTo">Identifier of the message this replies to, when it is a reply.</param>
    /// <param name="deliver">
    /// Hands the admitted message over. Called with the minted message identifier and the resolved
    /// target identifier, never under any lock, and expected to report its own outcome to the ledger
    /// rather than throw.
    /// </param>
    /// <remarks>
    /// Admission and the chain link are taken together under one lock, which is what makes the order
    /// real: admitting first and linking afterwards would let two senders admit in one order and link in
    /// the other, and the receiving agent would see a reply before the message it replies to.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="deliver"/> is null.</exception>
    internal AgentDispatch TrySendAndDeliver(
        string fromAgentId,
        string target,
        AgentMessageType messageType,
        string? inResponseTo,
        Func<string, string, Task> deliver
    )
    {
        ArgumentNullException.ThrowIfNull(deliver);

        lock (_deliveryGate)
        {
            var result = TrySendCore(fromAgentId, target, messageType, inResponseTo);
            if (result is not { MessageId: { } messageId, Target: { } entry })
            {
                return new AgentDispatch(result, Task.CompletedTask);
            }

            var targetAgentId = entry.AgentId;

            // A completed tail is dropped rather than chained, so an idle target starts a fresh chain
            // instead of accumulating a longer and longer completed prefix to await.
            var previous =
                _deliveryTails.TryGetValue(targetAgentId, out var tail) && !tail.IsCompleted
                    ? tail
                    : Task.CompletedTask;

            var chained = ChainAsync(previous, () => deliver(messageId, targetAgentId));
            _deliveryTails[targetAgentId] = chained;
            return new AgentDispatch(result, chained);
        }
    }

    /// <summary>Runs one delivery after the target's previous one, whatever became of that one.</summary>
    private static async Task ChainAsync(Task previous, Func<Task> deliver)
    {
        try
        {
            await previous;
        }
        catch (Exception)
        {
            // The previous delivery already recorded its own outcome in the ledger. Letting its fault
            // travel down the chain would cancel deliveries that have nothing to do with it.
        }

        await deliver();
    }

    /// <summary>Decides whether one agent may read another agent's transcript.</summary>
    /// <param name="readerAgentId">Canonical identifier of the agent asking.</param>
    /// <param name="targetAgentId">Canonical identifier or name of the agent being asked about.</param>
    public TranscriptAccessDecision EvaluateTranscriptAccess(
        string readerAgentId,
        string targetAgentId
    )
    {
        return TranscriptVisibilityPolicy.Evaluate(
            Directory.FindById(readerAgentId),
            Directory.Resolve(targetAgentId).Entry,
            Options.TranscriptVisibility
        );
    }

    /// <summary>
    /// Records that an agent has left: it stays visible but unaddressable, and every obligation it was
    /// party to in either direction is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One call rather than three, because the parts are only correct together. Retiring without
    /// abandoning strands senders on questions that can never be answered; abandoning without retiring
    /// lets new messages queue for an agent that has already gone.
    /// </para>
    /// <para>
    /// Both directions are swept, and they are genuinely different failures. Inbound leaves the SENDER
    /// waiting for an answer nobody can give; outbound leaves the RECIPIENT holding a question it is
    /// still being offered as answerable work on behalf of somebody who has gone. The two reason codes
    /// stay distinct so the record says which of the two happened.
    /// </para>
    /// </remarks>
    /// <param name="agentId">The agent that left.</param>
    /// <param name="status">Terminal status to record.</param>
    /// <returns>The messages that were closed, so their correspondents can be told.</returns>
    /// <remarks>
    /// Takes the same delivery gate <see cref="TrySendAndDeliver"/> admits under. Without it, a sender
    /// could read the target's directory entry as live an instant before this method's status update
    /// lands, then admit its message only after the sweep below has already run — a reply-expecting
    /// message accepted for an agent that has already left, which no later sweep will ever revisit.
    /// Serializing the two makes that ordering impossible: either the whole retirement completes
    /// before admission starts (so <see cref="TrySend"/>'s liveness checks refuse it), or admission
    /// completes before retirement starts (so the sweep catches it).
    /// </remarks>
    public IReadOnlyList<string> RetireAgent(string agentId, string status)
    {
        lock (_deliveryGate)
        {
            _ = Directory.TryUpdateStatus(agentId, status);
            _ = Directory.TryMarkRetained(agentId);

            // Disjoint by construction: an agent cannot address itself, so no message is in both sweeps.
            return
            [
                .. Ledger.AbandonMessagesFor(agentId, TargetLeftReasonCode),
                .. Ledger.AbandonMessagesFrom(agentId, SenderLeftReasonCode),
            ];
        }
    }
}
