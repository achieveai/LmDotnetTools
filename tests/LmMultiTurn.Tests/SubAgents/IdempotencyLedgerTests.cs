using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The ledger's guarantee is exclusivity, not caching: while one caller holds a key, no other caller
/// may do that key's work. These cover the edges where the bound on remembered keys meets that promise.
/// </summary>
public class IdempotencyLedgerTests
{
    private const string Tool = "Agent";

    [Fact]
    public async Task ReserveAsync_WhenTheOldestKeyIsStillRunning_KeepsItRatherThanEvictingIt()
    {
        // Eviction may forget a FINISHED key: losing a replay costs a stale answer nobody can act on.
        // Forgetting a RUNNING one costs the guarantee itself — the next caller of that key finds
        // nothing, claims it, and spawns the second agent the key was supplied to prevent.
        var ledger = new IdempotencyLedger();
        var (inFlight, replay) = await ledger.ReserveAsync(Tool, "the-long-one");
        _ = inFlight.Should().NotBeNull();
        _ = replay.Should().BeNull();

        // Push the running key off the end of the bound. These all finish, so the running one is the
        // only entry eviction has any reason to keep.
        for (var i = 0; i < IdempotencyLedger.MaxRemembered; i++)
        {
            var (filler, _) = await ledger.ReserveAsync(Tool, $"filler-{i}");
            ledger.Complete(filler!, $"filler receipt {i}", errorCode: null);
        }

        var second = ledger.ReserveAsync(Tool, "the-long-one").AsTask();

        // Not "returned a replay" — returning ANYTHING here means it did not wait. A caller handed a
        // claim would go on to run the work a second time.
        second
            .IsCompleted.Should()
            .BeFalse("the first call has not finished, so its key must still be held against a second caller");

        ledger.Complete(inFlight!, "the first result", errorCode: null);

        var (claimAfter, replayAfter) = await second.WaitAsync(TimeSpan.FromSeconds(10));
        _ = claimAfter.Should().BeNull();
        replayAfter!.Value.Receipt.Should().Be("the first result");
    }

    [Fact]
    public async Task ReserveAsync_WhenTheOldestKeysAreFinished_StillForgetsThemToStayBounded()
    {
        // Non-vacuity for the test above: keeping running claims must not turn into keeping everything.
        // A finished key past the bound is forgotten, so its next caller does the work afresh.
        var ledger = new IdempotencyLedger();
        var (first, _) = await ledger.ReserveAsync(Tool, "evict-me");
        ledger.Complete(first!, "the first result", errorCode: null);

        for (var i = 0; i < IdempotencyLedger.MaxRemembered; i++)
        {
            var (filler, _) = await ledger.ReserveAsync(Tool, $"filler-{i}");
            ledger.Complete(filler!, $"filler receipt {i}", errorCode: null);
        }

        var (claimAgain, replayAgain) = await ledger.ReserveAsync(Tool, "evict-me");
        _ = claimAgain.Should().NotBeNull("a forgotten key is claimable again");
        _ = replayAgain.Should().BeNull();
    }
}
