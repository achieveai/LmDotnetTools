using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers the half of a send the sender never used to see: what happened AFTER it was told "accepted".
/// </summary>
/// <remarks>
/// Admission is synchronous and delivery is not, so "accepted" is a promise about responsibility rather
/// than about arrival. Every one of these asserts that a broken promise reaches the sender — with a code
/// that says whether trying again could work — instead of being written only to a ledger nobody reads.
/// </remarks>
public class AgentDeliveryFailureFeedbackTests
{
    private const string CollaborationId = "collab-1";
    private const string SenderId = "agent-sender";
    private const string TargetId = "agent-target";

    /// <summary>A stand-in for an agent's owner whose answer to every hand-off is fixed by the test.</summary>
    private sealed class FakeEndpoint(AgentDeliveryDisposition disposition, string? reasonCode = null)
        : IAgentWriteEndpoint
    {
        private readonly List<AgentMessage> _received = [];

        public IReadOnlyList<AgentMessage> Received
        {
            get
            {
                lock (_received)
                {
                    return [.. _received];
                }
            }
        }

        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        )
        {
            lock (_received)
            {
                _received.Add(message);
            }

            return ValueTask.FromResult(new AgentDeliveryOutcome(disposition, reasonCode));
        }
    }

    /// <summary>
    /// A collaboration of exactly two agents, each with an endpoint the test controls: the sender, whose
    /// endpoint is where a delivery-failure notice must land, and the target, whose endpoint decides how
    /// the hand-off fails.
    /// </summary>
    private static (AgentCollaborationSetup Sender, FakeEndpoint SenderEndpoint) BuildPair(
        IAgentWriteEndpoint targetEndpoint
    )
    {
        var bundle = new AgentCollaborationBundle(CollaborationId, new AgentCollaborationOptions());
        var senderContext = AgentCollaborationContext.ForRoot(CollaborationId, SenderId);
        var senderEndpoint = new FakeEndpoint(AgentDeliveryDisposition.Delivered);
        _ = bundle.Directory.TryRegister(senderContext, "sender", AgentCollaborationStatuses.Running, senderEndpoint);

        var targetContext = senderContext.CreateChild(TargetId, AgentKind.SubAgent, "target", "the target");
        _ = bundle.Directory.TryRegister(targetContext, "target", AgentCollaborationStatuses.Running, targetEndpoint);

        return (new AgentCollaborationSetup(bundle, senderContext, "sender"), senderEndpoint);
    }

    /// <summary>
    /// An endpoint whose hand-off does not finish until the test says so, which is what makes "the
    /// sender finished WHILE the delivery was in flight" a fact rather than a race.
    /// </summary>
    private sealed class GatedEndpoint : IAgentWriteEndpoint
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        )
        {
            await _gate.Task;
            return new AgentDeliveryOutcome(AgentDeliveryDisposition.Failed, "unknown_target");
        }
    }

    private static AgentMessage? NoticeIn(FakeEndpoint endpoint) =>
        endpoint.Received.SingleOrDefault(m => m.AgentMessageType == AgentMessageType.DeliveryFailure);

    [Fact]
    public async Task Refused_MarksTheMessageRetryable_AndTellsTheSender()
    {
        var (sender, senderEndpoint) = BuildPair(
            new FakeEndpoint(AgentDeliveryDisposition.Refused, "input_queue_full")
        );

        var dispatch = new AgentCollaborationMessenger(sender).Send(
            "target",
            "are you there?",
            AgentMessageType.Question
        );
        dispatch.Result.Succeeded.Should().BeTrue();
        await dispatch.Delivery;

        // A target that could not take the message NOW may take it later, so the code has to say
        // "retry" — a sender told only "failed" would give up on a recoverable condition.
        var entry = sender.Bundle.Ledger.Find(dispatch.Result.MessageId!)!;
        entry.State.Should().Be(AgentMessageDeliveryState.DeliveryFailed);
        entry.ReasonCode.Should().Be(AgentCollaborationMessenger.TargetBusyRetryReasonCode);
        AgentCollaborationMessenger.IsRetryable(entry.ReasonCode).Should().BeTrue();

        // The sender was told "accepted" and then moved on. The push is the only thing that stops that
        // acceptance from being the last it ever hears.
        var notice = NoticeIn(senderEndpoint)!;
        notice.Should().NotBeNull();
        notice.FromAgentId.Should().Be(AgentCollaborationBundle.SystemSenderAgentId);
        notice.ExpectsReply.Should().BeFalse("a notice that invited a reply would start a loop with nobody");
        notice.Body.Should().Contain(dispatch.Result.MessageId!).And.Contain("target_busy_retry");
    }

    [Fact]
    public async Task Failed_MarksTheMessagePermanent_AndTellsTheSender()
    {
        var (sender, senderEndpoint) = BuildPair(new FakeEndpoint(AgentDeliveryDisposition.Failed, "unknown_target"));

        var dispatch = new AgentCollaborationMessenger(sender).Send(
            "target",
            "take this",
            AgentMessageType.DelegateTask
        );
        await dispatch.Delivery;

        // Distinct from the retryable code by value, not by wording: a sender that retries this one
        // burns turns on something that can never succeed.
        var entry = sender.Bundle.Ledger.Find(dispatch.Result.MessageId!)!;
        entry.ReasonCode.Should().Be(AgentCollaborationMessenger.TargetGoneReasonCode);
        AgentCollaborationMessenger.IsRetryable(entry.ReasonCode).Should().BeFalse();
        NoticeIn(senderEndpoint)!.Body.Should().Contain("target_gone");
    }

    [Fact]
    public async Task DeliveryFailure_DoesNotWakeASenderThatHasAlreadyFinished()
    {
        var target = new GatedEndpoint();
        var (sender, senderEndpoint) = BuildPair(target);

        // The case under test is a sender that finished between being told "accepted" and the delivery
        // settling, so it has to be running at admission and finished by the time the failure lands.
        var dispatch = new AgentCollaborationMessenger(sender).Send("target", "take this", AgentMessageType.Question);
        _ = sender.Bundle.Directory.TryUpdateStatus(SenderId, AgentCollaborationStatuses.Completed);
        target.Release();
        await dispatch.Delivery;

        // Restarting a finished agent to tell it about a message it can no longer act on costs a whole
        // model run and produces nothing. The ledger still records the truth for anyone who looks.
        NoticeIn(senderEndpoint).Should().BeNull();
        sender
            .Bundle.Ledger.Find(dispatch.Result.MessageId!)!
            .State.Should()
            .Be(AgentMessageDeliveryState.DeliveryFailed);
    }

    [Fact]
    public async Task TheNotice_IsNotAdmitted_SoItCreatesNoObligationOfItsOwn()
    {
        var (sender, senderEndpoint) = BuildPair(new FakeEndpoint(AgentDeliveryDisposition.Failed, "unknown_target"));

        var dispatch = new AgentCollaborationMessenger(sender).Send("target", "hello", AgentMessageType.Question);
        await dispatch.Delivery;

        // A notice that went through the ledger would be a message addressed to the sender that the
        // sender then owes something on — and a failed notice would notify about the notice.
        NoticeIn(senderEndpoint).Should().NotBeNull();
        sender.Bundle.Ledger.Count.Should().Be(1);
        sender.Bundle.Ledger.GetOpenInbound(SenderId).Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAbandonedObligations_TellsOnlyTheSendersOfMessagesAimedAtTheAgentThatLeft()
    {
        var (sender, senderEndpoint) = BuildPair(new FakeEndpoint(AgentDeliveryDisposition.Delivered));
        var bundle = sender.Bundle;
        var toTarget = bundle.TrySend(SenderId, "target", AgentMessageType.Question).MessageId!;
        var fromTarget = bundle.TrySend(TargetId, "sender", AgentMessageType.Question).MessageId!;

        await bundle.NotifyAbandonedObligationsAsync(bundle.RetireAgent(TargetId, "stopped"), TargetId);

        // RetireAgent closes BOTH directions. Notifying the outbound half would deliver to the agent
        // being retired — restarting the very agent that just left.
        var notice = NoticeIn(senderEndpoint)!;
        notice.Should().NotBeNull();
        notice.Body.Should().Contain(toTarget).And.NotContain(fromTarget);
        senderEndpoint
            .Received.Count(m => m.AgentMessageType == AgentMessageType.DeliveryFailure)
            .Should()
            .Be(1, "only one of the two abandoned messages was addressed to the agent that left");
    }
}
