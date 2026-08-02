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

    private readonly AgentCollaborationSetup _setup;

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
    /// <param name="cancellationToken">Cancels the delivery attempt, not the admission.</param>
    /// <returns>
    /// The admission result and the background delivery. When admission fails, the delivery task is
    /// already completed and nothing was sent.
    /// </returns>
    public AgentDispatch Send(
        string target,
        string body,
        AgentMessageType messageType,
        string? inResponseTo = null,
        CancellationToken cancellationToken = default
    )
    {
        var result = _setup.Bundle.TrySend(
            _setup.AgentId,
            target,
            messageType,
            inResponseTo
        );

        if (!result.Succeeded || result.Target is null || result.MessageId is null)
        {
            return new AgentDispatch(result, Task.CompletedTask);
        }

        var delivery = DeliverAsync(
            result.MessageId,
            result.Target.AgentId,
            messageType,
            body,
            inResponseTo,
            cancellationToken
        );

        return new AgentDispatch(result, delivery);
    }

    private async Task DeliverAsync(
        string messageId,
        string targetAgentId,
        AgentMessageType messageType,
        string body,
        string? inResponseTo,
        CancellationToken cancellationToken
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
                _ = ledger.MarkDeliveryFailed(messageId, NoEndpointReasonCode);
                return;
            }

            var message = AgentMessage.Create(
                messageId,
                messageType,
                _setup.AgentId,
                _setup.Name,
                body,
                inResponseTo
            );

            var outcome = await endpoint.DeliverAsync(message, cancellationToken);
            _ = outcome.IsDelivered
                ? ledger.MarkDelivered(messageId)
                : ledger.MarkDeliveryFailed(
                    messageId,
                    outcome.ReasonCode ?? RefusedReasonCode
                );
        }
        catch (Exception)
        {
            // A delivery is never retried under the same identifier, so the failure has to be recorded
            // rather than propagated: this task is not awaited by the sender, and an unobserved fault
            // would leave the ledger entry open forever with nobody able to see why.
            _ = ledger.MarkDeliveryFailed(messageId, DeliveryErrorReasonCode);
        }
        finally
        {
            // Return the slot the admission took. The inbox holds identifiers with no remove-by-id, so
            // this returns capacity rather than a specific entry — which is all the bound is for.
            _ = _setup.Directory.GetInbox(targetAgentId)?.TryDequeue(out _);
        }
    }
}

/// <summary>
/// Delivers into an agent loop's own non-blocking input path.
/// </summary>
/// <remarks>
/// Uses <see cref="IMultiTurnAgent.TrySendAsync"/> rather than the blocking send so a full input
/// channel becomes a visible, recoverable refusal instead of a stalled delivery task.
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

        var receipt = await _agent.TrySendAsync(
            [message],
            ct: cancellationToken
        );

        return receipt is null
            ? new AgentDeliveryOutcome(
                AgentDeliveryDisposition.Refused,
                InputQueueFullReasonCode
            )
            : new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered);
    }
}
