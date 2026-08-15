namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests;

/// <summary>
/// Covers the fake clock itself. The suites that use it assert on timer bands constantly, but none
/// of them pins what a band actually means — and the answer decided whether a real run passed or
/// hung.
/// </summary>
public class ManualTimeProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_timer_armed_from_a_callback_is_matched_on_its_scheduled_delay()
    {
        // The exact shape that stalled CI. Advance overshoots the first timer's due time by 400 ms
        // and the callback arms the next one from inside that overshoot, so the new timer's
        // REMAINING time (1.2s - 0.4s = 800 ms) falls outside the [1s, 2s] band its test names,
        // while the delay it was actually scheduled with (1.2s) sits squarely inside it. Matching on
        // remaining time made this wait unsatisfiable — and, before the wait was bounded, wedged the
        // testhost until dotnet test aborted the whole run.
        //
        // Arming from inside the callback is what makes this deterministic rather than a race:
        // Advance fires callbacks synchronously, before it moves the clock the rest of the way to
        // the caller's target, which is exactly the interleaving the pipeline's async continuation
        // hits intermittently under load.
        var clock = new ManualTimeProvider(Start);
        ITimer? armed = null;

        using var first = clock.CreateTimer(
            _ =>
                armed = clock.CreateTimer(
                    _ => { },
                    null,
                    TimeSpan.FromMilliseconds(1200),
                    Timeout.InfiniteTimeSpan
                ),
            null,
            TimeSpan.FromMilliseconds(600),
            Timeout.InfiniteTimeSpan
        );

        clock.Advance(TimeSpan.FromSeconds(1));
        armed.Should().NotBeNull("the first timer must have fired and armed the second");

        await clock
            .WaitForTimerAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(5));

        armed!.Dispose();
    }

    [Fact]
    public async Task A_fired_one_shot_no_longer_matches_its_band()
    {
        // The other half of the contract: matching on the scheduled delay must not resurrect a timer
        // that already fired, or a test could "see" a backoff that is long since spent.
        var clock = new ManualTimeProvider(Start);
        using var timer = clock.CreateTimer(
            _ => { },
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan
        );

        await clock
            .WaitForTimerAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(1));

        var afterFiring = clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        afterFiring.IsCompleted.Should().BeFalse("the one-shot fired, so nothing is pending in band");
    }
}
