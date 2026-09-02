using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>The outcome of one send: the synchronous admission plus the background delivery it started.</summary>
/// <param name="Result">
/// Whether the message was admitted to the ledger and the target's inbox. This is what the sending
/// model is told, immediately.
/// </param>
/// <param name="Delivery">
/// The in-flight delivery. Completed (never faulted) once the ledger has been settled. Exposed so a
/// test can await delivery deterministically rather than polling; production callers ignore it.
/// </param>
public readonly record struct AgentDispatch(AgentSendResult Result, Task Delivery);

/// <summary>
/// Sends one agent's message to another: admit synchronously, deliver in the background.
/// </summary>
/// <remarks>
/// <para>
/// The split is deliberate. The ledger and the inbox hold identifiers only — never a body — so the
/// body has to stay with the sender until the target's own owner accepts it. Admission is therefore
/// synchronous (the sender learns "accepted" or "refused, here is why" in the same tool call) while
/// the delivery itself, which may block behind the target's lifecycle, runs on afterwards and settles
/// the ledger when it lands.
/// </para>
/// <para>
/// The inbox slot is released only after the ledger is settled, so a target whose owner is wedged
/// genuinely fills up and later senders get an explicit, recoverable backpressure result rather than
/// an unbounded queue.
/// </para>
/// </remarks>
public sealed class AgentCollaborationMessenger
{
    /// <summary>Reason recorded when the target has no endpoint that can receive a message.</summary>
    public const string NoEndpointReasonCode = "no_endpoint";

    /// <summary>Reason recorded when a delivery attempt threw.</summary>
    public const string DeliveryErrorReasonCode = "delivery_error";

    /// <summary>Reason recorded when the target's owner refused without saying why.</summary>
    public const string RefusedReasonCode = "refused";

    /// <summary>
    /// Reason recorded when the target could not take the message now but might later — it is queued,
    /// its input path is full, or its owner could not take a slot in time.
    /// </summary>
    /// <remarks>
    /// The distinction this code carries is the whole point of splitting the failure arms: the endpoint
    /// already knows whether a refusal is backpressure or an ending, and collapsing the two left a
    /// sender with no way to tell "wait and resend" from "this will never work". The recovery is the
    /// sender's, not the messenger's — a retry inside delivery would either double a tool call's latency
    /// or hide a target that is genuinely wedged.
    /// </remarks>
    public const string TargetBusyRetryReasonCode = "target_busy_retry";

    /// <summary>Reason recorded when the target can never take the message: it is gone, or its owner is.</summary>
    public const string TargetGoneReasonCode = "target_gone";

    private readonly AgentCollaborationSetup _setup;

    /// <summary>Whether a recorded delivery failure is one the sender could get past by trying again.</summary>
    /// <remarks>
    /// Deliberately a whitelist of the one retryable code rather than a blacklist of the permanent ones.
    /// A future failure whose recoverability nobody has thought about reads as permanent, which costs a
    /// sender one abandoned message; the other way round it would cost an unbounded retry loop.
    /// </remarks>
    public static bool IsRetryable(string? reasonCode) =>
        string.Equals(reasonCode, TargetBusyRetryReasonCode, StringComparison.Ordinal);

    /// <summary>Creates a messenger that sends on behalf of one agent.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="setup"/> is null.</exception>
    public AgentCollaborationMessenger(AgentCollaborationSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
    }

    /// <summary>
    /// Admits a message and starts delivering it.
    /// </summary>
    /// <param name="target">Canonical identifier or name of the recipient.</param>
    /// <param name="body">The message text. Never written to the ledger, the inbox, or a log.</param>
    /// <param name="messageType">What kind of message this is.</param>
    /// <param name="inResponseTo">The message being answered, when this is a reply.</param>
    /// <returns>
    /// The admission result and the background delivery. When admission fails, the delivery task is
    /// already completed and nothing was sent.
    /// </returns>
    /// <remarks>
    /// Takes no cancellation token by design. Once a message is admitted the collaboration has told the
    /// sender it accepted responsibility for it, and the sender's own turn ending — which is what its
    /// tool-call token signals — must not then silently drop a message the receiver has been promised.
    /// The delivery therefore runs uncancelled and always settles the ledger.
    /// </remarks>
    public AgentDispatch Send(string target, string body, AgentMessageType messageType, string? inResponseTo = null)
    {
        return _setup.Bundle.TrySendAndDeliver(
            _setup.AgentId,
            target,
            messageType,
            inResponseTo,
            (messageId, targetAgentId) => DeliverAsync(messageId, targetAgentId, messageType, body, inResponseTo)
        );
    }

    private async Task DeliverAsync(
        string messageId,
        string targetAgentId,
        AgentMessageType messageType,
        string body,
        string? inResponseTo
    )
    {
        // Yield first so the caller's tool result is produced from the admission alone: a delivery that
        // blocks behind the target's lifecycle must never stall the sender's turn.
        await Task.Yield();

        var ledger = _setup.Bundle.Ledger;
        try
        {
            var endpoint = _setup.Directory.GetWriteEndpoint(targetAgentId);
            if (endpoint is null)
            {
                await SettleFailureAsync(messageId, NoEndpointReasonCode);
                return;
            }

            var message = AgentMessage.Create(messageId, messageType, _setup.AgentId, _setup.Name, body, inResponseTo);

            var outcome = await endpoint.DeliverAsync(message, CancellationToken.None);
            if (outcome.IsDelivered)
            {
                _ = ledger.MarkDelivered(messageId);
                return;
            }

            await SettleFailureAsync(messageId, Classify(outcome));
        }
        catch (Exception)
        {
            // A delivery is never retried under the same identifier, so the failure has to be recorded
            // rather than propagated: this task is not awaited by the sender, and an unobserved fault
            // would leave the ledger entry open forever with nobody able to see why.
            await SettleFailureAsync(messageId, DeliveryErrorReasonCode);
        }
        finally
        {
            // Return the slot the admission took. The inbox holds identifiers with no remove-by-id, so
            // this returns capacity rather than a specific entry — which is all the bound is for.
            _ = _setup.Directory.GetInbox(targetAgentId)?.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Turns a hand-off the target's owner would not take into one of the two codes a sender can act on.
    /// </summary>
    /// <remarks>
    /// The disposition, not the endpoint's own reason string, decides. Every endpoint already
    /// distinguishes "not now" from "not ever" — that is what
    /// <see cref="AgentDeliveryDisposition.Refused"/> versus <see cref="AgentDeliveryDisposition.Failed"/>
    /// means — so mapping through the disposition gives one vocabulary for every endpoint that exists or
    /// will exist, rather than a lookup table of reason strings that each new endpoint would have to be
    /// added to.
    /// </remarks>
    private static string Classify(AgentDeliveryOutcome outcome) =>
        outcome.Disposition == AgentDeliveryDisposition.Refused ? TargetBusyRetryReasonCode : TargetGoneReasonCode;

    /// <summary>
    /// Records a failed delivery and pushes the news back to the sender.
    /// </summary>
    /// <remarks>
    /// The notification is inside the guard rather than beside it: <c>MarkDeliveryFailed</c> answers
    /// false for a message that some other path already closed, and notifying anyway would tell a sender
    /// twice about one message.
    /// </remarks>
    private async Task SettleFailureAsync(string messageId, string reasonCode)
    {
        if (_setup.Bundle.Ledger.MarkDeliveryFailed(messageId, reasonCode))
        {
            await _setup.Bundle.NotifySenderOfDeliveryFailureAsync(messageId, reasonCode);
        }
    }
}

/// <summary>
/// Delivers into an agent loop's own non-blocking input path.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="IMultiTurnAgent.TrySendAsync"/> rather than the blocking send so a full input
/// channel becomes a visible, recoverable refusal instead of a stalled delivery task.
/// </para>
/// <para>
/// This is the direct-at-the-loop shape that #690 removed from sub-agent delivery, and it is safe HERE
/// only because of who registers it: exclusively an unregistered ROOT loop (see the self-registration in
/// <c>MultiTurnAgentLoop</c>'s constructor); every sub-agent is registered by its parent's manager with a
/// <c>SubAgentWriteEndpoint</c>, which routes through the manager's restart-or-refuse path. A root loop
/// cannot reach the sub-agent failure mode — "alive loop, disposed provider" — because (1) nothing
/// disposes a root loop's provider while the loop lives: <c>MultiTurnAgentBase</c> never disposes the
/// provider it was handed, and the pool that owns both disposes the provider only as an owned resource
/// AFTER <c>Agent.DisposeAsync()</c>; (2) <c>DisposeAsync</c> sets the disposed flag under the admission
/// lock BEFORE any teardown step, so a send that arrives afterwards throws
/// <see cref="ObjectDisposedException"/> from <see cref="IMultiTurnAgent.TrySendAsync"/> rather than
/// queueing, and the messenger's delivery task records that as a failed delivery in the ledger; and (3) a
/// pool recreate cannot leave a stale entry for a dead loop, because the host builds one collaboration
/// bundle (directory + ledger) per root-loop creation, so the replacement loop registers in a fresh
/// directory and the old one dies with the old loop. If a host ever registers a NON-root loop with this
/// endpoint, or a root loop whose provider is scoped to a single run, the #690 analysis no longer holds
/// and delivery must go through the owning manager instead.
/// </para>
/// </remarks>
internal sealed class AgentLoopWriteEndpoint : IAgentWriteEndpoint
{
    /// <summary>Reason recorded when the target loop's input channel is full.</summary>
    internal const string InputQueueFullReasonCode = "input_queue_full";

    private readonly IMultiTurnAgent _agent;

    internal AgentLoopWriteEndpoint(IMultiTurnAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agent = agent;
    }

    public async ValueTask<AgentDeliveryOutcome> DeliverAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var receipt = await _agent.TrySendAsync([message], ct: cancellationToken);

        return receipt is null
            ? new AgentDeliveryOutcome(AgentDeliveryDisposition.Refused, InputQueueFullReasonCode)
            : new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered);
    }
}
