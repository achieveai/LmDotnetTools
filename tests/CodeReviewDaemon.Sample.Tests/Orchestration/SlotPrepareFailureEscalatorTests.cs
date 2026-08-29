using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// <see cref="SlotPrepareFailureEscalator"/> is issue #582's backstop for the classifier's inevitable next gap:
/// after <see cref="SlotPrepareFailureEscalator.MaxConsecutiveFailures"/> IDENTICAL consecutive prepare
/// failures for the SAME store root, the caller must reclone regardless of how the failure was classified.
/// </summary>
public class SlotPrepareFailureEscalatorTests
{
    private const string StoreRoot = "/pool/slot-0/store";

    [Fact]
    public void Does_not_escalate_before_the_threshold()
    {
        var escalator = new SlotPrepareFailureEscalator();

        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures - 1; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
        }
    }

    [Fact]
    public void Escalates_on_the_Nth_identical_consecutive_failure()
    {
        var escalator = new SlotPrepareFailureEscalator();

        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures - 1; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
        }

        escalator
            .RecordFailureAndShouldEscalate(StoreRoot, "same failure")
            .Should()
            .BeTrue($"the {SlotPrepareFailureEscalator.MaxConsecutiveFailures}th identical failure must escalate");
    }

    [Fact]
    public void A_differing_message_resets_the_streak_instead_of_accumulating()
    {
        // A slot failing for two DIFFERENT reasons in a row has not demonstrated a stuck condition — a
        // transient blip followed by something unrelated is ordinary noise, not evidence a reclone should fix.
        var escalator = new SlotPrepareFailureEscalator();

        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures - 1; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
        }

        escalator.RecordFailureAndShouldEscalate(StoreRoot, "a different failure").Should().BeFalse();
    }

    [Fact]
    public void Escalating_clears_the_streak_so_a_future_failure_starts_fresh()
    {
        var escalator = new SlotPrepareFailureEscalator();

        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure");
        }

        // The reclone the caller ran after escalating is itself a repair attempt. Without the reset, every
        // single subsequent prepare on this slot would re-escalate forever instead of counting a fresh streak.
        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures - 1; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
        }
    }

    [Fact]
    public void A_success_clears_the_streak()
    {
        var escalator = new SlotPrepareFailureEscalator();

        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures - 1; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
        }

        escalator.RecordSuccess(StoreRoot);

        escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
    }

    [Fact]
    public void Two_identical_failures_under_different_run_ids_still_count_as_one_streak_of_two()
    {
        // Review finding F-001 (issue #582 PR #589): every real prepare-failure message is stamped
        // "Run <id>: ...", and the mcqdb incident was SIX DIFFERENT runs hitting the identical wedged store
        // over two days. Without stripping the run id first, each call's message differs from the last purely
        // because the run id differs, the streak resets to one every time, and this backstop can never
        // accumulate the cross-run repeat its own type doc says it exists to catch.
        var escalator = new SlotPrepareFailureEscalator();
        const string sameUnderlyingFailure =
            "checking out branch 'x' from 'main' failed (exit 128): fatal: not a git repository: sub/../.git/modules/sub";

        escalator.RecordFailureAndShouldEscalate(StoreRoot, $"Run 101: {sameUnderlyingFailure}").Should().BeFalse();
        escalator
            .RecordFailureAndShouldEscalate(StoreRoot, $"Run 202: {sameUnderlyingFailure}")
            .Should()
            .BeFalse(
                "two identical underlying failures across different runs must still count as ONE streak of "
                    + "two, not two independent streaks of one each stuck below the threshold forever"
            );

        // The THIRD occurrence — under yet another run id — is what proves accumulation rather than reset:
        // if the run id were not stripped first, this would look like a fresh, different message and the
        // streak would restart at one instead of reaching MaxConsecutiveFailures.
        escalator
            .RecordFailureAndShouldEscalate(StoreRoot, $"Run 303: {sameUnderlyingFailure}")
            .Should()
            .BeTrue(
                "a third run hitting the identical failure on the identical store root is the cross-run streak this backstop exists to close"
            );
    }

    [Fact]
    public void Streaks_are_tracked_independently_per_store_root()
    {
        var escalator = new SlotPrepareFailureEscalator();
        const string otherStoreRoot = "/pool/slot-1/store";

        for (var i = 0; i < SlotPrepareFailureEscalator.MaxConsecutiveFailures - 1; i++)
        {
            escalator.RecordFailureAndShouldEscalate(StoreRoot, "same failure").Should().BeFalse();
        }

        // A different slot's failure streak must not be affected by (or contribute to) this one's.
        escalator.RecordFailureAndShouldEscalate(otherStoreRoot, "same failure").Should().BeFalse();
        escalator
            .RecordFailureAndShouldEscalate(StoreRoot, "same failure")
            .Should()
            .BeTrue("the other slot's failures must not have advanced this slot's streak");
    }
}
