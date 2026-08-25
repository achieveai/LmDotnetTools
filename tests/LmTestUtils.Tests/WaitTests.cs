using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace LmTestUtils.Tests;

/// <summary>
/// Covers the contract that makes <see cref="Wait"/> worth consolidating: a wait that never
/// completes has to fail the test rather than return quietly. Every private poll helper this class
/// replaces got that backwards.
/// </summary>
public class WaitTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_condition_that_never_holds_throws_naming_what_it_waited_for()
    {
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => Wait.UntilAsync(() => false, "the widget was flushed", Brief, Tick)
        );

        // The description and the call site are the whole point: the failure has to say which wait
        // stalled and what it wanted, because a bare timeout is what made these bugs expensive.
        Assert.Contains("the widget was flushed", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(A_condition_that_never_holds_throws_naming_what_it_waited_for),
            thrown.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("WaitTests.cs", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_condition_already_true_returns_without_polling()
    {
        var evaluations = 0;
        var elapsed = Stopwatch.StartNew();

        await Wait.UntilAsync(
            () =>
            {
                evaluations++;
                return true;
            },
            "the condition already holds",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10)
        );

        // One evaluation, and crucially no sleep: the poll interval must not become a floor on how
        // long an already-satisfied wait takes, or every such wait taxes the suite.
        Assert.Equal(1, evaluations);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"an already-true condition must not sleep, but the wait took {elapsed.Elapsed}"
        );
    }

    [Fact]
    public async Task A_condition_that_becomes_true_partway_through_completes()
    {
        var remaining = 3;

        await Wait.UntilAsync(() => --remaining <= 0, "the countdown reached zero", Brief, Tick);

        Assert.True(remaining <= 0);
    }

    [Fact]
    public async Task TryUntilAsync_reports_the_timeout_instead_of_throwing()
    {
        // The escape hatch returns bool precisely so a caller cannot ignore the outcome by accident.
        Assert.False(await Wait.TryUntilAsync(() => false, Brief, Tick));
        Assert.True(await Wait.TryUntilAsync(() => true, Brief, Tick));
    }

    [Fact]
    public async Task An_asynchronous_condition_is_polled_the_same_way()
    {
        var remaining = 3;

        await Wait.UntilAsync(
            () => Task.FromResult(--remaining <= 0),
            "the async countdown reached zero",
            Brief,
            Tick
        );

        Assert.True(remaining <= 0);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => Wait.UntilAsync(() => Task.FromResult(false), "the async widget flushed", Brief, Tick)
        );
        Assert.Contains("the async widget flushed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_timeout_still_evaluates_the_condition_once()
    {
        // Otherwise a caller passing an elapsed budget gets a failure that never looked, which reads
        // in the log exactly like a condition that was checked and found false.
        var evaluations = 0;

        await Wait.UntilAsync(
            () =>
            {
                evaluations++;
                return true;
            },
            "an already-satisfied condition under a zero budget",
            TimeSpan.Zero,
            Tick
        );

        Assert.Equal(1, evaluations);
    }

    [Fact]
    public async Task An_exception_from_the_condition_propagates_rather_than_being_swallowed()
    {
        // Swallowing belongs at the call site, where the decision to tolerate a transient read is
        // visible, not hidden inside the shared helper where nobody sees it.
        // Explicitly typed: a bare `() => throw ...` is convertible to both the sync and async
        // condition overloads, so it needs to say which one it means.
        Func<bool> throws = () => throw new InvalidOperationException("boom");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Wait.UntilAsync(throws, "a condition that throws", Brief, Tick)
        );
    }
}
