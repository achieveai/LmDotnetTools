using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// The coordinator's decision points, driven directly rather than through a loop.
/// </summary>
/// <remarks>
/// <para>
/// <c>DelayedResultChildRunTests</c> covers the same rules end to end, and that is where they belong
/// — but it cannot reach the one interleaving that matters most here. A claim stays open across a
/// durable write, a history update and a publication, and <see cref="DelayedResultCoordinator.TryPark"/>
/// can land in the middle of that window. Reproducing that through the loop would mean racing a
/// background run against a webhook and hoping the two threads land in the right order; driving the
/// coordinator by hand makes the interleaving a sequence of calls, so the regression is pinned
/// deterministically and cannot go quiet on a fast machine.
/// </para>
/// <para>
/// The rules themselves come from ADR 0004: one child run per resolved result, in commit order, with
/// exactly one of them — the one that clears the last outstanding call — continuing the conversation.
/// </para>
/// </remarks>
public class DelayedResultCoordinatorTests
{
    private const string RunId = "run-1";
    private const string GenerationId = "gen-1";

    #region Resolve versus park

    [Fact]
    public void AResolutionThatWasInFlightWhenTheRunParked_StillCausesAChildRun()
    {
        // The regression. The claim is taken while the run is still going, so nothing about it needs a
        // child run; the run then ends — the durable write and the history update are still in
        // progress — and the commit lands after. Reading the claim's snapshot here would retire the
        // reservation with no cause, and the result would sit resolved in history with no run to carry
        // it to the provider: no loop wake, no continuation, no answer.
        var coordinator = new DelayedResultCoordinator();
        coordinator.TryReserve(Entry("tc_1")).Should().BeTrue();

        coordinator
            .TryBeginResolve("tc_1", "fp-1", out var pending, out _)
            .Should()
            .BeTrue("the call is outstanding and unclaimed");

        coordinator
            .TryPark(RunId, GenerationId, out var unresolved)
            .Should()
            .BeTrue("the claim leaves the entry outstanding, so a turn ending now must still park");
        unresolved.Should().Be(1);

        var cause = coordinator.CompleteResolve(pending!, Resolved("tc_1"));

        cause.Should().NotBeNull("the run that asked for this is over; only a child run can carry it");
        cause!.ChildRunId.Should().NotBeNullOrEmpty("a child run needs an id even when parking was late");
        cause.ToolCallId.Should().Be("tc_1");
        cause.IsContinuationOwner.Should().BeTrue("nothing is outstanding any more");
        coordinator.HasPendingCauses.Should().BeTrue("the loop has a child run to pick up");
    }

    [Fact]
    public void AResolutionThatCommitsBeforeTheRunEnds_IsFoldedIntoIt()
    {
        // The other side of the same window, and the reason the check cannot simply be "always start a
        // child run": a result that lands while its run is still going belongs to that run.
        var coordinator = new DelayedResultCoordinator();
        coordinator.TryReserve(Entry("tc_1")).Should().BeTrue();

        coordinator.TryBeginResolve("tc_1", "fp-1", out var pending, out _).Should().BeTrue();
        var cause = coordinator.CompleteResolve(pending!, Resolved("tc_1"));

        cause.Should().BeNull("the requesting run is still going, so there is nothing for a child to do");
        coordinator
            .TryPark(RunId, GenerationId, out var unresolved)
            .Should()
            .BeFalse("the call resolved while the turn was still running, so the run carries on");
        unresolved.Should().Be(0);
        coordinator.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ParkingDuringAnAbortedResolution_IsCarriedIntoTheRetry()
    {
        // A resolution the store would not take has changed nothing, so its caller may send it again.
        // What must not happen is the retry forgetting that the run ended in the meantime.
        var coordinator = new DelayedResultCoordinator();
        coordinator.TryReserve(Entry("tc_1")).Should().BeTrue();

        coordinator.TryBeginResolve("tc_1", "fp-1", out var first, out _).Should().BeTrue();
        coordinator.TryPark(RunId, GenerationId, out _).Should().BeTrue();
        coordinator.AbortResolve(first!);

        coordinator
            .Snapshot()
            .Should()
            .ContainSingle(e => e.ToolCallId == "tc_1", "an aborted claim leaves the call outstanding");

        coordinator
            .TryBeginResolve("tc_1", "fp-1", out var retry, out _)
            .Should()
            .BeTrue("the aborted claim was released, so the same result may be delivered again");
        retry!.ChildRunId.Should().NotBeNullOrEmpty("by now the claim itself can see the run has ended");

        var cause = coordinator.CompleteResolve(retry, Resolved("tc_1"));

        cause.Should().NotBeNull();
        cause!.ChildRunId.Should().Be(retry.ChildRunId, "the id the durable record already names must stand");
    }

    [Fact]
    public void LateParkingDoesNotDisturbCommitOrder_NorWhoContinues()
    {
        // Two calls from one turn. The first resolution is claimed while the run is still going and
        // commits only after the run parked; the second is claimed and committed entirely afterwards.
        // Ordinals must still be commit order, and the owner must still be whoever clears the last
        // outstanding call rather than whoever was claimed first.
        var coordinator = new DelayedResultCoordinator();
        coordinator.TryReserve(Entry("tc_a")).Should().BeTrue();
        coordinator.TryReserve(Entry("tc_b")).Should().BeTrue();

        coordinator.TryBeginResolve("tc_a", "fp-a", out var pendingA, out _).Should().BeTrue();
        coordinator.TryPark(RunId, GenerationId, out var unresolved).Should().BeTrue();
        unresolved.Should().Be(2, "both of the turn's calls are still outstanding");

        var causeA = coordinator.CompleteResolve(pendingA!, Resolved("tc_a"));

        coordinator.TryBeginResolve("tc_b", "fp-b", out var pendingB, out _).Should().BeTrue();
        var causeB = coordinator.CompleteResolve(pendingB!, Resolved("tc_b"));

        causeA.Should().NotBeNull();
        causeB.Should().NotBeNull();
        causeA!.Ordinal.Should().Be(1);
        causeB!.Ordinal.Should().Be(2, "the ordinal is commit order, and it is stamped under the same lock");
        causeA.ChildRunId.Should().NotBe(causeB.ChildRunId);
        causeA
            .IsContinuationOwner.Should()
            .BeFalse("tc_b was still outstanding, so a provider request would have been invalid");
        causeB
            .IsContinuationOwner.Should()
            .BeTrue("the result that clears the last outstanding call is the one that continues");

        coordinator.PendingCauseCount.Should().Be(2);
        coordinator.TryDequeueCause(out var firstOut).Should().BeTrue();
        firstOut!.ToolCallId.Should().Be("tc_a", "causes are handed to the loop oldest first");
        coordinator.TryDequeueCause(out var secondOut).Should().BeTrue();
        secondOut!.ToolCallId.Should().Be("tc_b");
    }

    [Fact]
    public void ParkingWhileEveryCallIsMidCommit_StrandsNothing()
    {
        // The worst shape of the same race: the whole turn's calls are claimed, and only then does the
        // run end. Every one of them has to come back as a child run, and exactly one of those may
        // talk to the provider.
        var coordinator = new DelayedResultCoordinator();
        string[] toolCallIds = ["tc_a", "tc_b", "tc_c"];
        var claims = new List<ResolvingDeferral>();

        foreach (var toolCallId in toolCallIds)
        {
            coordinator.TryReserve(Entry(toolCallId)).Should().BeTrue();
        }

        foreach (var toolCallId in toolCallIds)
        {
            coordinator.TryBeginResolve(toolCallId, $"fp-{toolCallId}", out var pending, out _)
                .Should()
                .BeTrue();
            claims.Add(pending!);
        }

        coordinator.TryPark(RunId, GenerationId, out var unresolved).Should().BeTrue();
        unresolved.Should().Be(3);

        var causes = claims
            .Select(claim => coordinator.CompleteResolve(claim, Resolved(claim.Entry.ToolCallId)))
            .ToList();

        causes.Should().OnlyContain(c => c != null, "not one of these results may be left without a run");
        causes.Select(c => c!.ChildRunId).Should().OnlyHaveUniqueItems();
        causes
            .Count(c => c!.IsContinuationOwner)
            .Should()
            .Be(1, "a provider request carries the whole history, so only the last one out may build one");
        causes[^1]!.IsContinuationOwner.Should().BeTrue();
        coordinator.IsEmpty.Should().BeTrue();
    }

    #endregion

    #region Claim bookkeeping

    [Fact]
    public void ASecondDeliveryArrivingMidCommit_IsRefusedAndToldWhatIsInFlight()
    {
        var coordinator = new DelayedResultCoordinator();
        coordinator.TryReserve(Entry("tc_1")).Should().BeTrue();
        coordinator.TryBeginResolve("tc_1", "fp-1", out _, out _).Should().BeTrue();

        coordinator
            .TryBeginResolve("tc_1", "fp-2", out var second, out var inFlight)
            .Should()
            .BeFalse("one claim at a time; the holder decides this call's fate");
        second.Should().BeNull();
        inFlight
            .Should()
            .Be("fp-1", "the caller needs the in-flight fingerprint to tell a redelivery from a conflict");
    }

    [Fact]
    public void AParkedEntryRebuiltFromHistory_NeedsAChildRunFromTheClaimOnwards()
    {
        // Entries restored from persisted history are pre-parked: the process that owned that run is
        // gone, so the id has to exist by claim time for the durable record to name it.
        var coordinator = new DelayedResultCoordinator();
        coordinator.TryReserve(Entry("tc_1"), parked: true).Should().BeTrue();

        coordinator.TryBeginResolve("tc_1", "fp-1", out var pending, out _).Should().BeTrue();

        pending!.ChildRunId.Should().NotBeNullOrEmpty();
        coordinator.CompleteResolve(pending, Resolved("tc_1"))!.ChildRunId.Should().Be(pending.ChildRunId);
    }

    #endregion

    #region Helpers

    private static DeferredEntry Entry(string toolCallId) =>
        new(
            toolCallId,
            $"defer_{toolCallId}",
            "{}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RunId,
            GenerationId);

    private static ToolCallResultMessage Resolved(string toolCallId) =>
        new()
        {
            ToolCallId = toolCallId,
            ToolName = $"defer_{toolCallId}",
            Result = $"result-{toolCallId}",
            Role = Role.User,
        };

    #endregion
}
