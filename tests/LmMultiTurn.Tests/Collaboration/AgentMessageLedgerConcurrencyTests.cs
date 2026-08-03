using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers the ledger's exactly-once rule when the contenders arrive together: a question answered by
/// racing replies is claimed by one of them and no other, and a message cannot be closed twice by
/// competing terminal transitions.
/// </summary>
/// <remarks>
/// Claiming and closing are not compare-and-swaps; the check for "is this still open" and the write
/// that settles it share one critical section. That makes the rule easy to state and easy to break
/// later — a refactor that moves validation outside the lock would still pass every sequential test in
/// <c>AgentMessageLedgerTests</c>, because the second reply there arrives after the first has already
/// returned. These tests are what would notice.
///
/// The assertion is a conservation law rather than a claim about who wins: across all racers, the
/// number of closes is exactly one, and the resulting entry describes that one closer coherently. A
/// losing reply must also leave no trace at all — no ledger row and no consumed inbox slot — because a
/// refusal that had already spent the target's backpressure budget would let retries starve real work.
/// </remarks>
public class AgentMessageLedgerConcurrencyTests
{
    private const string Sender = "agent-a";
    private const string Target = "agent-b";
    private const int Racers = 16;
    private const int Attempts = 8;

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="participants"/> dedicated threads released
    /// together, so contention comes from a rendezvous rather than from a timing guess.
    /// </summary>
    private static void Race(int participants, Action<int> body)
    {
        using var barrier = new Barrier(participants);
        var threads = Enumerable
            .Range(0, participants)
            .Select(index =>
                Task.Factory.StartNew(
                    () =>
                    {
                        barrier.SignalAndWait();
                        body(index);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
            )
            .ToArray();

        Task.WaitAll(threads);
    }

    [Fact]
    public void TryAdmit_CompetingResponses_CloseTheQuestionExactlyOnce()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
            var senderInbox = new AgentInbox(Racers + 1);
            var targetInbox = new AgentInbox(Racers + 1);
            var question = ledger.TryAdmit(
                new AgentMessageAdmissionRequest(Sender, Target, AgentMessageType.Question),
                targetInbox
            );
            question.Succeeded.Should().BeTrue();
            var results = new AgentMessageAdmissionResult[Racers];

            // Every racer answers the same open question, which is what a retried tool call looks like
            // from the ledger's side: several identical, individually valid replies.
            Race(
                Racers,
                index =>
                    results[index] = ledger.TryAdmit(
                        new AgentMessageAdmissionRequest(
                            Target,
                            Sender,
                            AgentMessageType.Response,
                            question.MessageId
                        ),
                        senderInbox
                    )
            );

            var winners = results.Where(result => result.Succeeded).ToArray();
            winners.Should().ContainSingle();
            results
                .Where(result => !result.Succeeded)
                .Should()
                .OnlyContain(result =>
                    result.FailureCode == AgentMessageFailureCodes.CorrelationClosed
                    && result.MessageId == null
                );

            // Admission is a claim, not a closure: the winner reserves the exclusive right to answer,
            // and that reservation alone is what makes every retry behind it recoverable rather than a
            // second answer. The question is still open here, and still names nobody.
            var claimed = ledger.Find(question.MessageId!)!;
            claimed.PendingResponseMessageId.Should().Be(winners[0].MessageId);
            claimed.IsClosed.Should().BeFalse("an answer that has not arrived has answered nothing");

            // Delivery is where the answer actually reaches the asker, so it is where the question
            // closes and where it finally names the reply that closed it.
            ledger.MarkDelivered(winners[0].MessageId!).Should().BeTrue();

            var closed = ledger.Find(question.MessageId!)!;
            closed.State.Should().Be(AgentMessageDeliveryState.Answered);
            closed.ResponseMessageId.Should().Be(winners[0].MessageId);
            closed.IsClosed.Should().BeTrue();
            ledger.GetOpenOutbound(Sender).Should().BeEmpty();
        }
    }

    [Fact]
    public void TryAdmit_ARefusedCompetingResponse_SpendsNoInboxSlotAndWritesNoRow()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
            var senderInbox = new AgentInbox(Racers + 1);
            var targetInbox = new AgentInbox(Racers + 1);
            var question = ledger.TryAdmit(
                new AgentMessageAdmissionRequest(Sender, Target, AgentMessageType.Question),
                targetInbox
            );
            var results = new AgentMessageAdmissionResult[Racers];

            Race(
                Racers,
                index =>
                    results[index] = ledger.TryAdmit(
                        new AgentMessageAdmissionRequest(
                            Target,
                            Sender,
                            AgentMessageType.Response,
                            question.MessageId
                        ),
                        senderInbox
                    )
            );

            // A correlation refusal is decided before an identifier is minted or a slot reserved, so
            // fifteen losing retries must cost the sender's inbox nothing at all.
            var winnerId = results.Single(result => result.Succeeded).MessageId;
            senderInbox.Peek().Should().Equal(winnerId);
            ledger.Count.Should().Be(2);
        }
    }

    [Fact]
    public void Closing_WhenAReplyRacesTheTargetLeaving_HappensOnceAndCoherently()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
            var senderInbox = new AgentInbox(Racers + 1);
            var targetInbox = new AgentInbox(Racers + 1);
            var question = ledger.TryAdmit(
                new AgentMessageAdmissionRequest(Sender, Target, AgentMessageType.Question),
                targetInbox
            );

            // The reply is admitted before the race so it holds the claim: the contest under test is
            // between the two things that actually CLOSE a question — the answer landing and the target
            // leaving — rather than between an admission and a closure, which cannot tie.
            var reply = ledger.TryAdmit(
                new AgentMessageAdmissionRequest(
                    Target,
                    Sender,
                    AgentMessageType.Response,
                    question.MessageId
                ),
                senderInbox
            );
            reply.Succeeded.Should().BeTrue();
            var abandoned = 0;

            // Both genuinely race in production: a sub-agent's answer can be handed over at the same
            // moment its manager retires it.
            Race(
                Racers,
                index =>
                {
                    if (index % 2 == 0)
                    {
                        _ = ledger.MarkDelivered(reply.MessageId!);
                    }
                    else
                    {
                        _ = Interlocked.Add(
                            ref abandoned,
                            ledger
                                .AbandonMessagesFor(
                                    Target,
                                    AgentCollaborationBundle.TargetLeftReasonCode
                                )
                                .Count
                        );
                    }
                }
            );

            // Whichever outcome won, the entry must describe only that one: an answered question
            // carries a reply identifier and no failure reason, an abandoned one the reverse. A row
            // showing both would mean two closers had written over each other.
            var closed = ledger.Find(question.MessageId!)!;
            closed.IsClosed.Should().BeTrue();
            if (closed.State == AgentMessageDeliveryState.Answered)
            {
                closed.ResponseMessageId.Should().NotBeNull();
                closed.ReasonCode.Should().BeNull();
                abandoned
                    .Should()
                    .Be(0, "the question was answered, so nothing may also have abandoned it");
            }
            else
            {
                closed.State.Should().Be(AgentMessageDeliveryState.Abandoned);
                closed.ReasonCode.Should().Be(AgentCollaborationBundle.TargetLeftReasonCode);
                closed.ResponseMessageId.Should().BeNull();
                abandoned
                    .Should()
                    .Be(1, "a question can be abandoned once, however many retirements race");
            }
        }
    }

    [Fact]
    public void CompetingTerminalTransitions_ReportSuccessToExactlyOneCaller()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var ledger = new AgentMessageLedger(new AgentCollaborationOptions());
            var targetInbox = new AgentInbox(Racers + 1);
            // A steer expects no reply, so delivering it is itself terminal and can genuinely race the
            // failure path that a restart-time admission rejection would take.
            var steer = ledger.TryAdmit(
                new AgentMessageAdmissionRequest(Sender, Target, AgentMessageType.Steer),
                targetInbox
            );
            var closes = 0;

            Race(
                Racers,
                index =>
                {
                    var closed =
                        index % 2 == 0
                            ? ledger.MarkDelivered(steer.MessageId!)
                            : ledger.MarkDeliveryFailed(steer.MessageId!, "delivery_error");
                    if (closed)
                    {
                        _ = Interlocked.Increment(ref closes);
                    }
                }
            );

            // Reporting success twice would let a caller record a delivery that a second caller had
            // already recorded as failed, and the outcome a sender is shown would depend on which
            // report it happened to read.
            closes.Should().Be(1);
            ledger.Find(steer.MessageId!)!.IsClosed.Should().BeTrue();
        }
    }
}
