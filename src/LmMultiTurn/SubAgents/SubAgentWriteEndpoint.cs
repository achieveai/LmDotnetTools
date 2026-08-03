using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Delivers a collaboration message into a sub-agent through the manager that owns it.
/// </summary>
/// <remarks>
/// Routed through <see cref="SubAgentManager.SendMessageAsync(string, IMessage, bool, CancellationToken)"/>
/// rather than straight at the child's
/// loop so delivery obeys the lifecycle rules that already exist: a running child is injected into, a
/// finished one is restarted, and a child that is neither refuses. Always background — the sender
/// already returned its admission result and must not be blocked on the target's turn.
/// </remarks>
internal sealed class SubAgentWriteEndpoint : IAgentWriteEndpoint
{
    /// <summary>Reason recorded when the target is queued, wedged, or otherwise not deliverable now.</summary>
    internal const string NotDeliverableReasonCode = "target_not_deliverable";

    /// <summary>Reason recorded when the manager no longer tracks the target.</summary>
    internal const string UnknownTargetReasonCode = "unknown_target";

    /// <summary>Reason recorded when the owning manager has been disposed.</summary>
    internal const string ManagerDisposedReasonCode = "manager_disposed";

    private readonly SubAgentManager _manager;
    private readonly string _agentId;

    internal SubAgentWriteEndpoint(SubAgentManager manager, string agentId)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _manager = manager;
        _agentId = agentId;
    }

    public async ValueTask<AgentDeliveryOutcome> DeliverAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            // The typed message, not its rendered text. The child's loop still reads the same
            // self-describing <agent-message> envelope — that is what AgentMessage projects — but the
            // structured sender, type, and correlation survive into the child's history, the UI, and
            // persistence, where a flattened TextMessage would have left an anonymous user turn.
            _ = await _manager.SendMessageAsync(
                _agentId,
                message,
                runInBackground: true,
                cancellationToken
            );

            return new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered);
        }
        catch (ObjectDisposedException)
        {
            return new AgentDeliveryOutcome(
                AgentDeliveryDisposition.Failed,
                ManagerDisposedReasonCode
            );
        }
        catch (ArgumentException)
        {
            // The manager no longer knows this id at all — retrying under the same message identifier
            // cannot succeed, so this is terminal rather than recoverable backpressure.
            return new AgentDeliveryOutcome(
                AgentDeliveryDisposition.Failed,
                UnknownTargetReasonCode
            );
        }
        catch (InvalidOperationException)
        {
            // Still queued, or the restart path could not take a concurrency slot in time. Recoverable:
            // the sender may try again once the target is actually running.
            return new AgentDeliveryOutcome(
                AgentDeliveryDisposition.Refused,
                NotDeliverableReasonCode
            );
        }
    }
}
