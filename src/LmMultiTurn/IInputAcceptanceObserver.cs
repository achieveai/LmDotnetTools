namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Told about every input an agent accepts, at the moment it accepts it, so a host that pools agents
/// can know an accepted-but-unstarted turn is outstanding (#418, #434).
/// </summary>
/// <remarks>
/// <para>
/// The fact this carries is not derivable from <see cref="IMultiTurnAgent"/>. An input that has been
/// accepted and not yet picked up leaves <see cref="IMultiTurnAgent.CurrentRunId"/> null and
/// <see cref="IMultiTurnAgent.IsRunning"/> false — indistinguishable from an agent with nothing to
/// do. A host that tears an "idle" agent down in that state discards a turn whose sender already
/// holds a receipt.
/// </para>
/// <para>
/// It exists as an interface HERE, rather than as a call into the host's pool, because the
/// dependency runs one way: the pooling assembly references this one. Declaring the contract on this
/// side and letting the pool implement it is the same shape
/// <see cref="Persistence.IRunLedgerStore"/> already uses, and it is what lets an accept be reported
/// from inside this assembly without inverting that reference.
/// </para>
/// <para>
/// <b>Why this is reported from the agent and not from each caller.</b> Every accept, on every path,
/// mints its receipt id in exactly two places on the public send path — the two send methods on
/// <see cref="MultiTurnAgentBase"/>. Reporting from there covers each of them by construction. The
/// alternative — having every caller that sends to a pooled agent record the accept itself — is a
/// list that has to stay complete, and the paths that most need covering (a sub-agent relaying a
/// question to its parent, a completion notification, a peer's collaboration message) are precisely
/// the ones that live in this assembly and cannot reach the pool at all. A derived loop's internal
/// raw enqueues bypass both mint sites and this observer: the loop wake sentinel (inert — empty
/// payload, no run content, nothing to record) and the trigger notify (a real turn, and so genuinely
/// unobserved, but unreachable in this repository's host — it is gated behind trigger options that
/// only test mode supplies here, and #161 tracks enabling it. A host outside this repository that
/// enables triggers DOES reach it, and its notify turns are not covered by this observer).
/// </para>
/// <para>
/// Implementations must be safe to call from any thread and must not block: both methods run inline
/// on the sending caller's path, ahead of the enqueue.
/// </para>
/// </remarks>
public interface IInputAcceptanceObserver
{
    /// <summary>
    /// An input has been accepted by <paramref name="acceptedBy"/> and not yet started.
    /// </summary>
    /// <param name="threadId">The conversation whose agent took the input.</param>
    /// <param name="inputId">
    /// The accepted input's id — the same value the caller gets back as
    /// <see cref="Messages.SendReceipt.ReceiptId"/>. It is an id and not a flag because two accepts must be
    /// representable: with a flag the first run to start clears it while a second input is still
    /// queued, and the agent reads idle with a turn still owed.
    /// </param>
    /// <param name="acceptedBy">
    /// The agent instance that accepted it. An observer that tracks agents by conversation MUST
    /// compare this against the one it currently holds: the accept and the observer's own bookkeeping
    /// are two steps, and an agent can be replaced between them.
    /// </param>
    void OnInputAccepted(string threadId, string inputId, IMultiTurnAgent acceptedBy);

    /// <summary>
    /// Withdraws an acceptance reported by <see cref="OnInputAccepted"/> whose enqueue did not
    /// happen after all — a full input channel.
    /// </summary>
    /// <param name="threadId">The conversation reported to <see cref="OnInputAccepted"/>.</param>
    /// <param name="inputId">The id reported to <see cref="OnInputAccepted"/>.</param>
    /// <param name="acceptedBy">The agent that reported the acceptance.</param>
    /// <remarks>
    /// The partner of reporting BEFORE the enqueue, and the reason reporting early costs nothing.
    /// Without it a refused send leaves an id nothing can retire — no run will ever name an input the
    /// agent never received — so the conversation reads busy until the observer's own backstop
    /// expires: real time bought for a turn that was never queued.
    /// </remarks>
    void OnInputAcceptanceRescinded(string threadId, string inputId, IMultiTurnAgent acceptedBy);
}

/// <summary>
/// An agent that reports its own input acceptances to an <see cref="IInputAcceptanceObserver"/>.
/// </summary>
/// <remarks>
/// Separate from <see cref="IMultiTurnAgent"/> deliberately. Reporting is a capability a host may
/// find on an agent, not an obligation every implementation has to take on — and a host must be able
/// to tell the two apart, because for an agent that does NOT report, the host's own record of what it
/// handed over is the only ledger there is.
/// </remarks>
public interface IAcceptanceReportingAgent
{
    /// <summary>
    /// Where this agent reports acceptances, or <see langword="null"/> to report nowhere.
    /// </summary>
    /// <remarks>
    /// Settable rather than constructor-injected because the observer is normally the thing that
    /// OWNS the agent, and so does not exist until the agent does. A host must set it before the
    /// agent is reachable by any sender; an acceptance that happens before it is set is not reported
    /// and cannot be recovered.
    /// </remarks>
    IInputAcceptanceObserver? InputAcceptanceObserver { get; set; }
}
