using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using Microsoft.Extensions.Logging.Abstractions;
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0003 — the pending state behind remote tool approval. The properties worth pinning here are
/// the ones a reviewer cannot confirm by reading the code: that "first decision wins" survives a
/// real race rather than only a sequential retry, that every way of failing to find a request is
/// reported identically so the endpoint is not a probing oracle, and that both tombstone bounds and
/// both admission bounds actually bite. The clock is driven by hand, so nothing here waits.
/// </summary>
public sealed class RemoteApprovalStoreTests
{
    private const string ArgumentsJson = """{"path":"/etc/hosts"}""";
    private const string AppA = "app-a";
    private const string AppB = "app-b";

    /// <summary>The approver every request is registered with unless a test says otherwise.</summary>
    private const string SubA = "sub-a";

    /// <summary>A second approver, used where unanimity or substitution is the point.</summary>
    private const string SubB = "sub-b";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    // ---- The happy path --------------------------------------------------------------------

    [Fact]
    public async Task A_valid_decision_settles_the_request_and_wakes_the_waiter()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);

        var settlement = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed));

        settlement.Status.Should().Be(RemoteApprovalSettleStatus.Accepted);
        settlement.Outcome.Should().Be(WireOutcomes.Allowed);
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed);
        store.PendingCount.Should().Be(0, "a decided request no longer occupies an admission slot");
    }

    [Fact]
    public void A_registered_request_carries_the_frozen_hash_and_the_effective_expiry_but_not_the_arguments()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        var expiry = Now.AddMinutes(5);

        using var ticket = store.TryRegister(Owner(AppA), Call(expiry), [SubA])!;

        ticket.Request.ArgumentsHash.Should().Be(CanonicalToolArguments.Freeze(ArgumentsJson).Sha256Hex);
        ticket.Request.ExpiresAt.Should().Be(expiry);
        ticket
            .Request.Arguments.Should()
            .BeNull("what an approver may see is decided per subscriber, not stored here");
    }

    // ---- First decision wins ---------------------------------------------------------------

    [Fact]
    public async Task Exactly_one_of_many_racing_decisions_settles_the_request()
    {
        // The whole point of the atomic claim: a check-then-set would let two of these both believe
        // they won, and a tool call would then have two contradicting authorizations on record.
        const int Racers = 64;
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable
            .Range(0, Racers)
            .Select(i =>
                Task.Run(async () =>
                {
                    await start.Task;
                    return store.Settle(
                        Owner(AppA),
                        Decide(ticket, i % 2 == 0 ? WireOutcomes.Allowed : WireOutcomes.Denied)
                    );
                })
            )
            .ToArray();

        start.SetResult();
        var settlements = await Task.WhenAll(racers);

        settlements
            .Count(s => s.Status == RemoteApprovalSettleStatus.Accepted)
            .Should()
            .Be(1, "the pending -> decided transition happens exactly once");
        var standing = (await ticket.Decision).Decision;
        settlements
            .Should()
            .OnlyContain(
                s => s.Outcome == standing,
                "every racer, winner or loser, is told the outcome that actually stands"
            );
        settlements
            .Should()
            .NotContain(
                s => s.Status == RemoteApprovalSettleStatus.Unknown,
                "the request existed and was owned by the caller throughout"
            );
    }

    [Fact]
    public void An_identical_retry_returns_the_identical_answer()
    {
        // A duplicated delivery, or an approver that never saw the first response, must be safe.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);
        var decision = Decide(ticket, WireOutcomes.Denied);

        var first = store.Settle(Owner(AppA), decision);
        var retry = store.Settle(Owner(AppA), decision);

        first.Status.Should().Be(RemoteApprovalSettleStatus.Accepted);
        retry.Status.Should().Be(RemoteApprovalSettleStatus.AlreadyDecided);
        retry.Outcome.Should().Be(first.Outcome);
    }

    [Fact]
    public async Task A_contradicting_second_decision_is_refused_and_the_first_one_stands()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);

        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Denied));
        var reversal = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed));

        reversal.Status.Should().Be(RemoteApprovalSettleStatus.Contradicted);
        reversal
            .Outcome.Should()
            .Be(WireOutcomes.Denied, "the answer reported back is the one that is in force");
        (await ticket.Decision)
            .Decision.Should()
            .Be(WireOutcomes.Denied, "an allow arriving second must not overturn a deny");
    }

    // ---- Shape ------------------------------------------------------------------------------

    [Fact]
    public void A_decision_quoting_a_different_arguments_hash_is_refused()
    {
        // The approver answered about bytes other than the ones that would run.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);

        var settlement = store.Settle(
            Owner(AppA),
            Decide(ticket, WireOutcomes.Allowed, hash: new string('a', 64))
        );

        settlement.Status.Should().Be(RemoteApprovalSettleStatus.Mismatched);
        settlement.Outcome.Should().BeNull();
        ticket.Decision.IsCompleted.Should().BeFalse("the request is still waiting for a real answer");
    }

    [Theory]
    [InlineData(WireOutcomes.Timeout)]
    [InlineData(WireOutcomes.HostPolicyDenied)]
    [InlineData("")]
    public void A_decision_value_no_approver_may_submit_is_refused(string outcome)
    {
        // Host-only codes describe why the host blocked a call; an approver claiming one would be
        // asserting a decision it is not the one making.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);

        store
            .Settle(Owner(AppA), Decide(ticket, outcome))
            .Status.Should()
            .Be(RemoteApprovalSettleStatus.Mismatched);
    }

    // ---- Every frozen approver must allow -----------------------------------------------------

    [Fact]
    public async Task An_allow_from_one_of_two_approvers_records_without_authorizing_the_call()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);

        var first = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));

        first.Status.Should().Be(RemoteApprovalSettleStatus.Recorded);
        first.Outcome.Should().BeNull("nothing has been decided, so there is no outcome to report");
        ticket.Decision.IsCompleted.Should().BeFalse("the call stays blocked until everyone allows");
        store.PendingCount.Should().Be(1, "a recorded allow is not a settlement");

        var second = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubB));

        second.Status.Should().Be(RemoteApprovalSettleStatus.Accepted);
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed);
        store.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Repeating_the_same_approvers_allow_does_not_stand_in_for_the_other_ones()
    {
        // The ballot is a set, not a counter. Counting would let one approver allow twice and satisfy
        // a two-approver request on its own — the precise substitution unanimity exists to prevent.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);
        var allow = Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA);

        _ = store.Settle(Owner(AppA), allow);
        var repeat = store.Settle(Owner(AppA), allow);

        repeat.Status.Should().Be(RemoteApprovalSettleStatus.Recorded);
        ticket.Decision.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task One_approvers_deny_settles_the_request_without_waiting_for_the_others()
    {
        // Waiting for the rest would be pointless — the call can no longer be allowed — and would keep
        // an admission slot occupied for however long the remaining approvers take.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);

        var settlement = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Denied, subscriptionId: SubB));

        settlement.Status.Should().Be(RemoteApprovalSettleStatus.Accepted);
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Denied);
        store.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task An_allow_completing_the_set_cannot_overturn_a_deny_that_already_landed()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);

        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));
        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Denied, subscriptionId: SubA));
        var late = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubB));

        late.Status.Should().Be(RemoteApprovalSettleStatus.Contradicted);
        late.Outcome.Should().Be(WireOutcomes.Denied);
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Denied);
    }

    [Fact]
    public async Task Racing_approvers_settle_the_request_exactly_once()
    {
        // Two approvers answering at the same instant must not both see the set complete, or both
        // would try to settle and one would read its own allow back as a contradiction.
        const int Rounds = 200;
        for (var round = 0; round < Rounds; round++)
        {
            var store = CreateStore(new ManualTimeProvider(Now));
            using var ticket = Register(store, AppA, SubA, SubB);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var votes = new[] { SubA, SubB }
                .Select(subscription =>
                    Task.Run(async () =>
                    {
                        await start.Task;
                        return store.Settle(
                            Owner(AppA),
                            Decide(ticket, WireOutcomes.Allowed, subscriptionId: subscription)
                        );
                    })
                )
                .ToArray();

            start.SetResult();
            var settlements = await Task.WhenAll(votes);

            settlements
                .Count(s => s.Status == RemoteApprovalSettleStatus.Accepted)
                .Should()
                .Be(1, "exactly one allow completes the set");
            settlements
                .Should()
                .OnlyContain(
                    s =>
                        s.Status == RemoteApprovalSettleStatus.Accepted
                        || s.Status == RemoteApprovalSettleStatus.Recorded
                        || s.Status == RemoteApprovalSettleStatus.AlreadyDecided,
                    "the loser is counted — and is told the request is already decided if the winner "
                        + "committed before it looked, which is the truth rather than a conflict"
                );
            (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed);
        }
    }

    // ---- A retry that lands while the winning allow is still settling -------------------------

    // Completing the ballot and settling the request are two steps, and a retry can land between
    // them. Answering "recorded" there is a lie with consequences: the endpoint returns 202, so the
    // approver's client is told to keep retrying a request that has in fact already been answered.
    //
    // The gap is driven directly through RecordAllow rather than by racing threads. That is the seam
    // the winner crosses on its way to Commit, so calling it leaves the ticket in exactly the state a
    // real winner leaves it in mid-settle — and it does so on every run, not on the unlucky ones.

    [Fact]
    public async Task A_retry_arriving_while_the_only_approvers_allow_is_settling_reads_the_answer()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA);

        ticket
            .RecordAllow(SubA)
            .Should()
            .Be(RemoteApprovalBallot.Unanimous, "this is the allow that completed the set");

        var retry = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));

        retry
            .Status.Should()
            .NotBe(
                RemoteApprovalSettleStatus.Recorded,
                "there is nobody left to wait for, so 202-and-retry is not a truthful answer"
            );
        retry.Outcome.Should().Be(WireOutcomes.Allowed);
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed);
    }

    [Fact]
    public async Task A_retry_arriving_while_the_last_of_several_allows_is_settling_reads_the_answer()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);

        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA))
            .Status.Should()
            .Be(RemoteApprovalSettleStatus.Recorded, "SubB has genuinely not answered yet");

        ticket.RecordAllow(SubB).Should().Be(RemoteApprovalBallot.Unanimous);

        var retry = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));

        retry.Status.Should().NotBe(RemoteApprovalSettleStatus.Recorded);
        retry
            .Outcome.Should()
            .Be(
                WireOutcomes.Allowed,
                "the set completed, so SubA's earlier allow now has a standing answer to report"
            );
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed);
        store.PendingCount.Should().Be(0, "an answered request holds no admission slot");
    }

    [Fact]
    public void A_retry_arriving_after_the_winner_committed_reads_the_same_answer_again()
    {
        // The settled side of the same window: whichever side of the winner's commit a retry lands
        // on, the approver is told the same thing.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);
        var allow = Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA);

        _ = store.Settle(Owner(AppA), allow);
        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubB));

        var retry = store.Settle(Owner(AppA), allow);

        retry.Status.Should().Be(RemoteApprovalSettleStatus.AlreadyDecided);
        retry.Outcome.Should().Be(WireOutcomes.Allowed);
    }

    [Fact]
    public async Task A_retry_that_completes_the_set_against_a_deny_is_refused_rather_than_left_waiting()
    {
        // Denial settles immediately, so a repeat allow reaching the settle path finds the request
        // taken. It has to come back with the refusal — promptly, and without overturning it.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);

        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));
        ticket.RecordAllow(SubB).Should().Be(RemoteApprovalBallot.Unanimous);
        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Denied, subscriptionId: SubB))
            .Status.Should()
            .Be(RemoteApprovalSettleStatus.Accepted);

        var retry = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));

        retry.Status.Should().Be(RemoteApprovalSettleStatus.Contradicted);
        retry.Outcome.Should().Be(WireOutcomes.Denied, "the recorded answer is the one that stands");
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Denied);
    }

    [Fact]
    public void A_retry_that_completes_the_set_after_withdrawal_finds_nothing_to_decide()
    {
        // Nothing was ever decided here, so there is no answer to read back — and the retry must not
        // sit waiting for one.
        var store = CreateStore(new ManualTimeProvider(Now));
        var ticket = Register(store, AppA, SubA, SubB);

        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));
        ticket.RecordAllow(SubB).Should().Be(RemoteApprovalBallot.Unanimous);
        ticket.Dispose();

        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA))
            .Should()
            .Be(NothingToDecide);
    }

    [Fact]
    public void A_retry_that_completes_the_set_after_expiry_finds_nothing_to_decide()
    {
        // The call it would have authorized was blocked when the deadline passed, so a complete
        // ballot arriving now changes nothing and must not settle anything.
        var clock = new ManualTimeProvider(Now);
        var store = CreateStore(clock);
        using var ticket = Register(store, AppA, SubA, SubB);

        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));
        ticket.RecordAllow(SubB).Should().Be(RemoteApprovalBallot.Unanimous);
        clock.Advance(TimeSpan.FromMinutes(5));

        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA))
            .Should()
            .Be(NothingToDecide);
        ticket.Decision.IsCompleted.Should().BeFalse("an expired request is answered by the waiter");
    }

    [Fact]
    public async Task A_retry_racing_the_allow_that_completes_the_set_never_ends_up_the_only_caller_waiting()
    {
        // The same window under real threads, as a check that the seam-driven tests above describe
        // something the scheduler can actually produce. Both orderings are legitimate — a retry that
        // arrives genuinely early is genuinely still waiting — so what is asserted is the state they
        // both have to leave behind: one settlement, an allowed answer, and a retry afterwards that
        // reads it.
        const int Rounds = 200;
        for (var round = 0; round < Rounds; round++)
        {
            var store = CreateStore(new ManualTimeProvider(Now));
            using var ticket = Register(store, AppA, SubA, SubB);
            var retry = Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA);
            _ = store.Settle(Owner(AppA), retry);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completing = Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubB);
            var submissions = new[] { completing, retry }
                .Select(decision =>
                    Task.Run(async () =>
                    {
                        await start.Task;
                        return store.Settle(Owner(AppA), decision);
                    })
                )
                .ToArray();

            start.SetResult();
            var settlements = await Task.WhenAll(submissions);

            settlements
                .Count(s => s.Status == RemoteApprovalSettleStatus.Accepted)
                .Should()
                .Be(1, "the set is completed exactly once however the two are interleaved");
            settlements
                .Should()
                .OnlyContain(
                    s => s.Outcome == null || s.Outcome == WireOutcomes.Allowed,
                    "two allows cannot contradict each other"
                );
            (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed);

            var afterwards = store.Settle(Owner(AppA), retry);
            afterwards.Status.Should().Be(RemoteApprovalSettleStatus.AlreadyDecided);
            afterwards.Outcome.Should().Be(WireOutcomes.Allowed);
        }
    }

    [Fact]
    public void A_decision_from_an_approver_that_was_not_asked_is_indistinguishable_from_an_unknown_id()
    {
        // Another approval-capable subscription under the same owner is exactly the actor that must
        // not be able to answer in the frozen approver's place.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA);

        var substitute = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubB));

        substitute.Should().Be(NothingToDecide);
        ticket.Decision.IsCompleted.Should().BeFalse("nobody entitled to answer has answered");
        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA))
            .Status.Should()
            .Be(RemoteApprovalSettleStatus.Accepted, "the real approver is unaffected by the attempt");
    }

    [Fact]
    public void A_decision_naming_no_subscription_is_refused()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA);

        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: ""))
            .Should()
            .Be(NothingToDecide, "an answer nobody is accountable for authorizes nothing");
    }

    [Fact]
    public void Registering_a_request_with_no_approvers_is_a_wiring_error_rather_than_a_pending_entry()
    {
        // Such a request could never be unanimously allowed, so it would occupy a slot until it timed
        // out and then block the call — a failure whose cause is invisible at the point it appears.
        var store = CreateStore(new ManualTimeProvider(Now));

        var act = () => store.TryRegister(Owner(AppA), Call(Now.AddMinutes(5)), []);

        act.Should().Throw<ArgumentException>();
        store.PendingCount.Should().Be(0);
    }

    // ---- Revoking an approver ------------------------------------------------------------------

    [Fact]
    public async Task Revoking_an_approver_denies_what_it_was_still_being_asked_about()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA, SubA, SubB);
        _ = store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed, subscriptionId: SubA));

        var denied = store.InvalidateForSubscription(Owner(AppA), SubB);

        denied.Should().Be(1);
        (await ticket.Decision)
            .Decision.Should()
            .Be(
                WireOutcomes.Denied,
                "unanimity can no longer be reached, so the outcome is already settled"
            );
        store.PendingCount.Should().Be(0, "the admission slot is freed rather than held until expiry");
    }

    [Fact]
    public void Revoking_a_subscription_leaves_requests_it_was_not_an_approver_for_alone()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var mine = Register(store, AppA, SubA);
        using var theirs = Register(store, AppB, SubB);

        store.InvalidateForSubscription(Owner(AppA), SubB).Should().Be(0);

        mine.Decision.IsCompleted.Should().BeFalse("this request has a different approver");
        theirs
            .Decision.IsCompleted.Should()
            .BeFalse("and this one belongs to a different owner entirely");
        store.PendingCount.Should().Be(2);
    }

    // ---- Nothing to decide ------------------------------------------------------------------

    [Fact]
    public void A_request_id_minted_by_another_process_is_unknown()
    {
        // After a restart there is no tool call left to authorize, so an approval carried over from
        // the previous process must not be able to settle anything — and does not, without a byte of
        // state being persisted or consulted.
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);

        var stale = Decide(ticket, WireOutcomes.Allowed);
        stale.RequestId = WithForeignEpoch(stale.RequestId);

        store.Settle(Owner(AppA), stale).Should().Be(NothingToDecide);
    }

    [Fact]
    public void A_decision_arriving_after_the_request_expired_is_unknown()
    {
        var clock = new ManualTimeProvider(Now);
        var store = CreateStore(clock);
        using var ticket = store.TryRegister(Owner(AppA), Call(Now.AddMinutes(5)), [SubA])!;

        clock.Advance(TimeSpan.FromMinutes(5));

        store.Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed)).Should().Be(NothingToDecide);
        ticket.Decision.IsCompleted.Should().BeFalse("a late decision does not release the waiter");
    }

    [Fact]
    public void Another_owners_decision_cannot_settle_a_request_and_is_indistinguishable_from_an_unknown_id()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        using var ticket = Register(store, AppA);
        var decision = Decide(ticket, WireOutcomes.Allowed);

        var foreign = store.Settle(Owner(AppB), decision);
        var invented = store.Settle(Owner(AppB), Decide(ticket, WireOutcomes.Allowed, requestId: WithForeignEpoch(ticket.Request.RequestId)));

        foreign
            .Should()
            .Be(
                invented,
                "probing another owner's ids must teach a caller exactly as much as probing invented ones"
            );
        foreign.Should().Be(NothingToDecide);
        store
            .Settle(Owner(AppA), decision)
            .Status.Should()
            .Be(RemoteApprovalSettleStatus.Accepted, "the real owner's decision is untouched by the attempt");
    }

    // ---- Tombstones are bounded --------------------------------------------------------------

    [Fact]
    public void A_tombstone_older_than_the_retention_window_is_unknown()
    {
        var clock = new ManualTimeProvider(Now);
        var store = CreateStore(clock);
        using var ticket = Register(store, AppA);
        var decision = Decide(ticket, WireOutcomes.Allowed);
        _ = store.Settle(Owner(AppA), decision);

        clock.Advance(Retention + TimeSpan.FromMinutes(1));
        using var later = Register(store, AppA); // any operation is enough to age the queue

        store
            .Settle(Owner(AppA), decision)
            .Should()
            .Be(NothingToDecide, "an aged-out id denies rather than being re-decidable");
    }

    [Fact]
    public void The_oldest_tombstone_is_evicted_once_the_count_bound_is_reached()
    {
        var store = CreateStore(new ManualTimeProvider(Now), o => o.MaxTombstones = 2);
        var decisions = new List<ToolApprovalDecision>();
        for (var i = 0; i < 3; i++)
        {
            using var ticket = Register(store, AppA);
            var decision = Decide(ticket, WireOutcomes.Allowed);
            decisions.Add(decision);
            _ = store.Settle(Owner(AppA), decision);
        }

        store
            .Settle(Owner(AppA), decisions[0])
            .Should()
            .Be(NothingToDecide, "the oldest answer is the one dropped when the bound bites");
        store
            .Settle(Owner(AppA), decisions[2])
            .Status.Should()
            .Be(RemoteApprovalSettleStatus.AlreadyDecided, "the newest answers are still on record");
    }

    // ---- Admission is bounded ----------------------------------------------------------------

    [Fact]
    public void One_owner_cannot_consume_another_owners_admission()
    {
        // The per-owner bound exists precisely so a wedged owner — an approver that stopped
        // answering — cannot crowd every other owner out of the shared budget.
        var store = CreateStore(
            new ManualTimeProvider(Now),
            o =>
            {
                o.MaxPendingPerOwner = 2;
                o.MaxPendingTotal = 8;
            }
        );

        using var first = Register(store, AppA);
        using var second = Register(store, AppA);

        store.TryRegister(Owner(AppA), Call(Now.AddMinutes(5)), [SubA]).Should().BeNull();
        using var other = store.TryRegister(Owner(AppB), Call(Now.AddMinutes(5)), [SubA]);
        other.Should().NotBeNull("another owner's budget is unaffected by a wedged one");
    }

    [Fact]
    public void Admission_stops_at_the_total_bound()
    {
        var store = CreateStore(
            new ManualTimeProvider(Now),
            o =>
            {
                o.MaxPendingPerOwner = 3;
                o.MaxPendingTotal = 4;
            }
        );

        var held = new List<RemoteApprovalTicket>();
        try
        {
            held.AddRange([Register(store, AppA), Register(store, AppA), Register(store, AppA), Register(store, AppB)]);

            store
                .TryRegister(Owner(AppB), Call(Now.AddMinutes(5)), [SubA])
                .Should()
                .BeNull("the total bound holds even though this owner is under its own");
            store.PendingCount.Should().Be(4);
        }
        finally
        {
            held.ForEach(t => t.Dispose());
        }
    }

    [Fact]
    public void Withdrawing_a_pending_request_frees_its_slot_and_releases_the_waiter()
    {
        var store = CreateStore(new ManualTimeProvider(Now));
        var ticket = Register(store, AppA);

        ticket.Dispose();

        store.PendingCount.Should().Be(0, "an abandoned request must not hold an admission slot open");
        ticket.Decision.IsCanceled.Should().BeTrue();
        store
            .Settle(Owner(AppA), Decide(ticket, WireOutcomes.Allowed))
            .Should()
            .Be(NothingToDecide, "there is no longer a call this decision could authorize");
    }

    [Fact]
    public void Withdrawing_after_a_decision_does_not_erase_the_recorded_answer()
    {
        // The gate disposes its ticket as soon as it has an answer; if that wiped the tombstone, a
        // retried delivery would find the request unknown and the approver would be told its
        // decision never landed.
        var store = CreateStore(new ManualTimeProvider(Now));
        var ticket = Register(store, AppA);
        var decision = Decide(ticket, WireOutcomes.Denied);
        _ = store.Settle(Owner(AppA), decision);

        ticket.Dispose();

        var retry = store.Settle(Owner(AppA), decision);
        retry.Status.Should().Be(RemoteApprovalSettleStatus.AlreadyDecided);
        retry.Outcome.Should().Be(WireOutcomes.Denied);
    }

    // ---- Configuration ------------------------------------------------------------------------

    [Fact]
    public void An_inconsistent_configuration_fails_at_construction()
    {
        // The alternative is discovering it at the first tool call, when the only safe response left
        // is to block the call.
        var act = () => CreateStore(new ManualTimeProvider(Now), o => o.MaxPendingPerOwner = 0);

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>The single answer every "there is nothing here to decide" path must produce.</summary>
    private static readonly RemoteApprovalSettlement NothingToDecide =
        new(RemoteApprovalSettleStatus.Unknown, null);

    private static RemoteApprovalStore CreateStore(
        ManualTimeProvider clock,
        Action<RemoteApprovalOptions>? configure = null
    )
    {
        var options = new RemoteApprovalOptions
        {
            Enabled = true,
            TombstoneRetention = Retention,
        };
        configure?.Invoke(options);
        return new RemoteApprovalStore(options, clock, NullLogger<RemoteApprovalStore>.Instance);
    }

    private static LifecycleOwnerKey Owner(string appId) => LifecycleOwnerKey.ForAppId(appId);

    private static ToolApprovalContext Call(DateTimeOffset expiresAt) =>
        new()
        {
            ToolName = "write_file",
            ToolCallId = "call-1",
            ThreadId = "thread-1",
            Arguments = CanonicalToolArguments.Freeze(ArgumentsJson),
            ExpiresAt = expiresAt,
        };

    private static RemoteApprovalTicket Register(
        RemoteApprovalStore store,
        string appId,
        params string[] approvers
    ) =>
        store.TryRegister(
            Owner(appId),
            Call(Now.AddMinutes(5)),
            approvers.Length == 0 ? [SubA] : approvers
        )!;

    private static ToolApprovalDecision Decide(
        RemoteApprovalTicket ticket,
        string outcome,
        string? hash = null,
        string? requestId = null,
        string? subscriptionId = null
    ) =>
        new()
        {
            RequestId = requestId ?? ticket.Request.RequestId,
            SubscriptionId = subscriptionId ?? SubA,
            Decision = outcome,
            ArgumentsHash = hash ?? ticket.Request.ArgumentsHash,
        };

    /// <summary>
    /// Rewrites the epoch segment of a real id so it could only have come from another process.
    /// Flipping the leading character keeps the id well formed and the right length, so what the
    /// store rejects is the epoch itself rather than the shape.
    /// </summary>
    private static string WithForeignEpoch(string requestId)
    {
        var characters = requestId.ToCharArray();
        characters[0] = characters[0] == '0' ? '1' : '0';
        return new string(characters);
    }
}
