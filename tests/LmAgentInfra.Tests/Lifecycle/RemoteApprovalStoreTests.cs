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

        using var ticket = store.TryRegister(Owner(AppA), Call(expiry))!;

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
        using var ticket = store.TryRegister(Owner(AppA), Call(Now.AddMinutes(5)))!;

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

        store.TryRegister(Owner(AppA), Call(Now.AddMinutes(5))).Should().BeNull();
        using var other = store.TryRegister(Owner(AppB), Call(Now.AddMinutes(5)));
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
                .TryRegister(Owner(AppB), Call(Now.AddMinutes(5)))
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

    private static RemoteApprovalTicket Register(RemoteApprovalStore store, string appId) =>
        store.TryRegister(Owner(appId), Call(Now.AddMinutes(5)))!;

    private static ToolApprovalDecision Decide(
        RemoteApprovalTicket ticket,
        string outcome,
        string? hash = null,
        string? requestId = null
    ) =>
        new()
        {
            RequestId = requestId ?? ticket.Request.RequestId,
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
