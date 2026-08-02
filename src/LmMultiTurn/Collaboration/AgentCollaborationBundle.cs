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
        var resolution = Directory.Resolve(target);
        if (resolution.Entry is not { } entry)
        {
            return new AgentSendResult(null, null, resolution.FailureCode);
        }

        if (!entry.IsLive)
        {
            return new AgentSendResult(null, entry, AgentMessageFailureCodes.UnknownTarget);
        }

        var admission = Ledger.TryAdmit(
            new AgentMessageAdmissionRequest(fromAgentId, entry.AgentId, messageType, inResponseTo),
            Directory.GetInbox(entry.AgentId)
        );

        return new AgentSendResult(admission.MessageId, entry, admission.FailureCode);
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
    /// Records that an agent has left: it stays visible but unaddressable, and everything anybody was
    /// waiting on from it is closed.
    /// </summary>
    /// <remarks>
    /// One call rather than two, because the two halves are only correct together. Retiring without
    /// abandoning strands senders on questions that can never be answered; abandoning without retiring
    /// lets new messages queue for an agent that has already gone.
    /// </remarks>
    /// <param name="agentId">The agent that left.</param>
    /// <param name="status">Terminal status to record.</param>
    /// <returns>The messages that were closed, so their senders can be told.</returns>
    public IReadOnlyList<string> RetireAgent(string agentId, string status)
    {
        _ = Directory.TryUpdateStatus(agentId, status);
        _ = Directory.TryMarkRetained(agentId);
        return Ledger.AbandonMessagesFor(agentId, TargetLeftReasonCode);
    }
}
