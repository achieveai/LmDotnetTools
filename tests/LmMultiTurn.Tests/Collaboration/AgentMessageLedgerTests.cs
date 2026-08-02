using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers what the ledger promises: admission is atomic with the inbox reservation, a question is
/// answered exactly once, a refusal leaves no trace, and nobody waits forever.
/// </summary>
/// <remarks>
/// The ledger is the only place that knows a sender is blocked on an answer, so every terminal path —
/// answered, undeliverable, target gone — is asserted here. A message that could reach none of them is
/// a conversation that never resumes.
/// </remarks>
public class AgentMessageLedgerTests
{
    /// <summary>A clock the test drives, so retention is asserted rather than waited for.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow += delta;
        }
    }

    private const string Sender = "agent-a";
    private const string Target = "agent-b";

    private static AgentMessageAdmissionRequest Request(
        AgentMessageType messageType = AgentMessageType.Question,
        string from = Sender,
        string to = Target,
        string? inResponseTo = null
    )
    {
        return new AgentMessageAdmissionRequest(from, to, messageType, inResponseTo);
    }

    [Fact]
    public void TryAdmit_RecordsTheMessage_AndReservesAnInboxSlot()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var inbox = new AgentInbox(8);

        var result = ledger.TryAdmit(Request(), inbox);

        result.Succeeded.Should().BeTrue();
        result.FailureCode.Should().BeNull();

        // The inbox holds identifiers only: the queue is a claim on the target's attention, not a
        // second copy of the conversation.
        inbox.Peek().Should().Equal(result.MessageId!);

        var entry = ledger.Find(result.MessageId!)!;
        entry.State.Should().Be(AgentMessageDeliveryState.Accepted);
        entry.ExpectsReply.Should().BeTrue();
        entry.IsClosed.Should().BeFalse();
    }

    [Theory]
    [InlineData(AgentMessageType.Question, true)]
    [InlineData(AgentMessageType.DelegateTask, true)]
    [InlineData(AgentMessageType.Steer, false)]
    [InlineData(AgentMessageType.TaskUpdate, false)]
    [InlineData(AgentMessageType.Response, false)]
    public void TryAdmit_DerivesWhetherTheSenderIsWaiting_FromTheMessageKind(
        AgentMessageType messageType,
        bool expectsReply
    )
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        // A reply-only kind has nothing to be admitted against on its own. Give each the message it
        // is actually a reply to, so this stays a test about the kind rather than about correlation.
        var inResponseTo = messageType switch
        {
            AgentMessageType.Response => ledger
                .TryAdmit(Request(AgentMessageType.Question, Target, Sender), new AgentInbox(8))
                .MessageId,
            AgentMessageType.TaskUpdate => ledger
                .TryAdmit(Request(AgentMessageType.DelegateTask, Target, Sender), new AgentInbox(8))
                .MessageId,
            _ => null,
        };

        var result = ledger.TryAdmit(
            Request(messageType, inResponseTo: inResponseTo),
            new AgentInbox(8)
        );

        // Whether a sender is blocked is a property of the kind of message it sent, not something a
        // caller may assert about itself.
        ledger.Find(result.MessageId!)!.ExpectsReply.Should().Be(expectsReply);
    }

    [Fact]
    public void TryAdmit_RefusesAMessageAnAgentSendsToItself()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        var result = ledger.TryAdmit(Request(from: Sender, to: Sender), new AgentInbox(8));

        // Self-delivery is a loop: the agent would wake itself, answer itself, and never make progress.
        result.FailureCode.Should().Be(AgentMessageFailureCodes.SelfDelivery);
        ledger.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("", Target)]
    [InlineData("   ", Target)]
    [InlineData(Sender, "")]
    public void TryAdmit_RefusesBlankIdentities(string from, string to)
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        ledger
            .TryAdmit(Request(from: from, to: to), new AgentInbox(8))
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.InvalidSender);
    }

    [Fact]
    public void TryAdmit_RefusesATargetTheDirectoryDoesNotKnow()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        // A null inbox is how the directory says "no such agent"; the ledger declines rather than
        // inventing a queue for an identifier nobody is reading.
        ledger
            .TryAdmit(Request(), targetInbox: null)
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.UnknownTarget);
        ledger.Count.Should().Be(0);
    }

    [Fact]
    public void TryAdmit_AppliesBackpressure_LeavingTheLedgerUntouched()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var inbox = new AgentInbox(1);
        _ = ledger.TryAdmit(Request(AgentMessageType.Steer), inbox);

        var refused = ledger.TryAdmit(Request(AgentMessageType.Steer), inbox);

        // A full inbox is recoverable, so the refusal must not leave a phantom entry that would later
        // be reported as outstanding work nobody sent.
        refused.FailureCode.Should().Be(AgentMessageFailureCodes.InboxFull);
        ledger.Count.Should().Be(1);
        inbox.Count.Should().Be(1);
    }

    [Fact]
    public void MarkDelivered_ClosesTheQuestionAResponseAnswers_OnlyOnceTheAnswerHasLanded()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var targetInbox = new AgentInbox(8);
        var senderInbox = new AgentInbox(8);
        var question = ledger.TryAdmit(Request(), targetInbox).MessageId!;

        var answer = ledger.TryAdmit(
            Request(AgentMessageType.Response, Target, Sender, question),
            senderInbox
        );

        // Admission is a claim, not a closure: the asker has not been told anything yet.
        var claimed = ledger.Find(question)!;
        claimed.State.Should().Be(AgentMessageDeliveryState.Accepted);
        claimed.IsClosed.Should().BeFalse();
        ledger.GetOpenOutbound(Sender).Should().ContainSingle();

        _ = ledger.MarkDelivered(answer.MessageId!);

        var closed = ledger.Find(question)!;
        closed.State.Should().Be(AgentMessageDeliveryState.Answered);
        closed.ResponseMessageId.Should().Be(answer.MessageId);
        closed.IsClosed.Should().BeTrue();
        ledger.GetOpenOutbound(Sender).Should().BeEmpty();
    }

    [Fact]
    public void MarkDeliveryFailed_ReleasesTheQuestionAnUndeliveredAnswerClaimed()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        var reply = Request(AgentMessageType.Response, Target, Sender, question);
        var first = ledger.TryAdmit(reply, new AgentInbox(8)).MessageId!;

        _ = ledger.MarkDeliveryFailed(first, "delivery_error");

        // An answer that never arrived must leave the question answerable. Closing it on admission
        // would have stranded the asker on a reply it was never given, with no way to admit another.
        ledger.Find(question)!.IsClosed.Should().BeFalse();

        var second = ledger.TryAdmit(reply, new AgentInbox(8));
        second.Succeeded.Should().BeTrue();

        _ = ledger.MarkDelivered(second.MessageId!);
        ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Answered);
    }

    [Fact]
    public void TryAdmit_RefusesAReplyOnlyKindThatNamesNothing()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        // Both kinds only mean something relative to another message. Admitted bare they would reach
        // the receiver as an orphan it cannot place and the ledger could never settle.
        ledger
            .TryAdmit(Request(AgentMessageType.Response), new AgentInbox(8))
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.MissingCorrelation);
        ledger
            .TryAdmit(Request(AgentMessageType.TaskUpdate), new AgentInbox(8))
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.MissingCorrelation);
        ledger.Count.Should().Be(0);
    }

    [Fact]
    public void TryClaimWaitInterrupt_LetsOneWaiterAtATimeBeWokenByAMessageThatStaysOpen()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        _ = ledger.MarkDelivered(question);

        ledger.TryClaimWaitInterrupt(question).Should().BeTrue();

        // The second wait must not rediscover the same delivered-but-unanswered question, or an agent
        // that chose not to answer would spin instead of waiting.
        ledger.TryClaimWaitInterrupt(question).Should().BeFalse();

        // Giving the claim back is what stops a wait that lost its race to a timeout from consuming
        // the one interrupt the question gets.
        ledger.ReleaseWaitInterrupt(question);
        ledger.TryClaimWaitInterrupt(question).Should().BeTrue();

        // Interrupting never settles anything: the question is still owed an answer.
        ledger.Find(question)!.IsClosed.Should().BeFalse();
    }

    [Fact]
    public void TryAdmit_RefusesASecondAnswerToTheSameQuestion()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        var reply = Request(AgentMessageType.Response, Target, Sender, question);
        _ = ledger.TryAdmit(reply, new AgentInbox(8));

        // This is the idempotency guarantee. A retried reply must not deliver a second answer to a
        // sender that already resumed on the first one.
        ledger
            .TryAdmit(reply, new AgentInbox(8))
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.CorrelationClosed);
    }

    [Fact]
    public void TryAdmit_LeavesADelegationOpenWhileProgressIsReported()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var delegation = ledger
            .TryAdmit(Request(AgentMessageType.DelegateTask), new AgentInbox(8))
            .MessageId!;

        var update = ledger.TryAdmit(
            Request(AgentMessageType.TaskUpdate, Target, Sender, delegation),
            new AgentInbox(8)
        );

        // Progress is not a result. Closing here would tell the delegator the work is finished while
        // the delegate is still doing it.
        update.Succeeded.Should().BeTrue();
        ledger.Find(delegation)!.IsClosed.Should().BeFalse();
        ledger
            .GetOpenInbound(Target)
            .Should()
            .ContainSingle(entry => entry.MessageId == delegation);
    }

    [Fact]
    public void TryAdmit_RefusesProgressReportedAgainstAQuestion()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        var inbox = new AgentInbox(8);

        var update = ledger.TryAdmit(
            Request(AgentMessageType.TaskUpdate, Target, Sender, question),
            inbox
        );

        // A question is answered, not progressed. Admitting an update against one would leave the
        // asker holding a question that closes nothing could ever close, short of the target leaving.
        update.Succeeded.Should().BeFalse();
        update.FailureCode.Should().Be(AgentMessageFailureCodes.CorrelationNotADelegation);

        // Recoverable, and refused before anything was spent: the question is untouched and the
        // target's inbox slot is still free for the answer that should have been sent instead.
        ledger.Find(question)!.IsClosed.Should().BeFalse();
        inbox.Count.Should().Be(0);
    }

    [Fact]
    public void TryAdmit_RefusesAReplyToAMessageItDoesNotKnow()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        ledger
            .TryAdmit(
                Request(AgentMessageType.Response, Target, Sender, "agentmsg-missing"),
                new AgentInbox(8)
            )
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.UnknownCorrelation);
    }

    [Fact]
    public void TryAdmit_RefusesAReplyFromAnAgentTheQuestionWasNotAskedOf()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;

        // Otherwise a bystander could close somebody else's question, and the real target's answer
        // would then be refused as a duplicate.
        ledger
            .TryAdmit(
                Request(AgentMessageType.Response, "agent-c", Sender, question),
                new AgentInbox(8)
            )
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.CorrelationNotAddressedToSender);
    }

    [Fact]
    public void TryAdmit_RefusesAReplyAimedSomewhereOtherThanTheOriginalSender()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;

        ledger
            .TryAdmit(
                Request(AgentMessageType.Response, Target, "agent-c", question),
                new AgentInbox(8)
            )
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.CorrelationNotAddressedToSender);
    }

    [Fact]
    public void TryAdmit_RefusesAReplyToSomethingThatNeverAskedForOne()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var steer = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8)).MessageId!;

        ledger
            .TryAdmit(Request(AgentMessageType.Response, Target, Sender, steer), new AgentInbox(8))
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.CorrelationDoesNotExpectReply);
    }

    [Fact]
    public void MarkDelivered_ClosesAMessageNobodyIsWaitingOn_AndKeepsAQuestionOpen()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var steer = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8)).MessageId!;
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;

        ledger.MarkDelivered(steer).Should().BeTrue();
        ledger.MarkDelivered(question).Should().BeTrue();

        // Handover is the whole lifecycle of a steer; for a question it is only the halfway point.
        ledger.Find(steer)!.IsClosed.Should().BeTrue();
        ledger.Find(question)!.IsClosed.Should().BeFalse();
        ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Delivered);
    }

    [Fact]
    public void MarkDelivered_ReportsFailure_ForAnUnknownOrAlreadyClosedMessage()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var steer = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8)).MessageId!;
        _ = ledger.MarkDelivered(steer);

        ledger.MarkDelivered(steer).Should().BeFalse();
        ledger.MarkDelivered("agentmsg-missing").Should().BeFalse();
    }

    [Fact]
    public void MarkDeliveryFailed_ClosesTheMessage_WithASurfaceableReason()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var question = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;

        ledger.MarkDeliveryFailed(question, "target_faulted").Should().BeTrue();

        var entry = ledger.Find(question)!;
        entry.State.Should().Be(AgentMessageDeliveryState.DeliveryFailed);
        entry.ReasonCode.Should().Be("target_faulted");
        ledger.GetOpenOutbound(Sender).Should().BeEmpty();
    }

    [Fact]
    public void AbandonMessagesFor_ClosesEverythingAimedAtAnAgentThatLeft()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var toTarget = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        var elsewhere = ledger.TryAdmit(Request(to: "agent-c"), new AgentInbox(8)).MessageId!;

        var closed = ledger.AbandonMessagesFor(Target, "target_left");

        // Without this the sender waits on an answer that no longer has anyone to write it.
        closed.Should().Equal(toTarget);
        ledger.Find(toTarget)!.State.Should().Be(AgentMessageDeliveryState.Abandoned);
        ledger.Find(toTarget)!.ReasonCode.Should().Be("target_left");
        ledger.Find(elsewhere)!.IsClosed.Should().BeFalse();
    }

    [Fact]
    public void AbandonMessagesFor_IsHarmlessWhenThereIsNothingToClose()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());

        ledger.AbandonMessagesFor("agent-nobody", "target_left").Should().BeEmpty();
        ledger.AbandonMessagesFor("  ", "target_left").Should().BeEmpty();
    }

    [Fact]
    public void OpenViews_AreOrderedOldestFirst_SoCallersChooseTheSameMessageEveryTime()
    {
        var clock = new ManualClock();
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions(), clock);
        var first = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;

        ledger
            .GetOpenOutbound(Sender)
            .Select(entry => entry.MessageId)
            .Should()
            .Equal(first, second);
        ledger
            .GetOpenInbound(Target)
            .Select(entry => entry.MessageId)
            .Should()
            .Equal(first, second);
        ledger.GetOpenInbound(Sender).Should().BeEmpty();
    }

    [Fact]
    public void ClosedEntries_AreForgottenOnceTheyAreOlderThanRetention()
    {
        var clock = new ManualClock();
        var ledger = new AgentMessageLedger(
            new AgentCollaborationOptions { ClosedEntryRetention = TimeSpan.FromMinutes(1) },
            clock
        );
        var stale = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8)).MessageId!;
        var open = ledger.TryAdmit(Request(), new AgentInbox(8)).MessageId!;
        _ = ledger.MarkDelivered(stale);

        clock.Advance(TimeSpan.FromMinutes(5));
        _ = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8));

        // A closed entry is kept only long enough for a late caller to learn what happened; an open one
        // is still needed however old it is.
        ledger.Find(stale).Should().BeNull();
        ledger.Find(open).Should().NotBeNull();
    }

    [Fact]
    public void ClosedEntries_AreCappedInCount_SoABusyCollaborationCannotGrowWithoutBound()
    {
        var clock = new ManualClock();
        var ledger = new AgentMessageLedger(
            new AgentCollaborationOptions
            {
                MaxClosedEntries = 1,
                ClosedEntryRetention = TimeSpan.FromHours(1),
            },
            clock
        );

        var closed = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var messageId = ledger
                .TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8))
                .MessageId!;
            _ = ledger.MarkDelivered(messageId);
            closed.Add(messageId);
        }

        _ = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8));

        // Oldest go first, so what survives is the history most likely to still be asked about.
        ledger.Find(closed[0]).Should().BeNull();
        ledger.Find(closed[1]).Should().BeNull();
        ledger.Find(closed[2]).Should().NotBeNull();
    }

    [Fact]
    public void MessageAdmitted_AnnouncesWorkWithoutAnnouncingItsContent()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        AgentMessageAdmittedNotice? observed = null;
        ledger.MessageAdmitted += notice => observed = notice;

        var result = ledger.TryAdmit(Request(AgentMessageType.Steer), new AgentInbox(8));

        // Enough to wake the target's owner, and nothing more: a wake-up signal is not a channel for
        // message content.
        observed.Should().NotBeNull();
        observed!.Value.MessageId.Should().Be(result.MessageId);
        observed.Value.FromAgentId.Should().Be(Sender);
        observed.Value.ToAgentId.Should().Be(Target);
        observed.Value.MessageType.Should().Be(AgentMessageType.Steer);
    }

    [Fact]
    public void MessageAdmitted_IsRaisedOutsideTheLock_SoAHandlerMayReadTheLedger()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        AgentMessageLedgerEntry? seenFromHandler = null;
        ledger.MessageAdmitted += notice => seenFromHandler = ledger.Find(notice.MessageId);

        _ = ledger.TryAdmit(Request(), new AgentInbox(8));

        // A handler is a real caller's code; if it could only observe the ledger by deadlocking it, the
        // event would be unusable for the one job it exists to do.
        seenFromHandler.Should().NotBeNull();
    }

    [Fact]
    public void MessageAdmitted_IsNotRaisedForARefusal()
    {
        var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
        var raised = 0;
        ledger.MessageAdmitted += _ => raised++;

        _ = ledger.TryAdmit(Request(from: Sender, to: Sender), new AgentInbox(8));
        _ = ledger.TryAdmit(Request(), targetInbox: null);

        raised.Should().Be(0);
    }
}
