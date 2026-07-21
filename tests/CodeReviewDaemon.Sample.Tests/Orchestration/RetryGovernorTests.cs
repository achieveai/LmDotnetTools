using CodeReviewDaemon.Sample.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// <see cref="RetryGovernor"/> replaces the daemon's 30 s hot-loop with attempt-counting, exponential
/// backoff, and park-after-K. State is in-memory (a restart clears it → retry all), so these tests drive a
/// mutable fake clock to pin the backoff gate and the parked terminal.
/// </summary>
public class RetryGovernorTests
{
    private DateTimeOffset _now = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private RetryGovernor Create(int maxAttempts = 5) => new(
        maxAttempts,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(900),
        () => _now,
        NullLogger<RetryGovernor>.Instance);

    [Fact]
    public void ShouldAttempt_is_true_for_an_unseen_run() => Create().ShouldAttempt(1).Should().BeTrue();

    [Fact]
    public void After_a_failure_the_run_backs_off_until_the_next_eligible_time()
    {
        var g = Create();
        g.RecordFailure(1, "boom").Should().Be(RetryDecision.Retry);
        g.ShouldAttempt(1).Should().BeFalse("within the 30s backoff window");

        _now = _now.AddSeconds(31);
        g.ShouldAttempt(1).Should().BeTrue("the first backoff elapsed");
    }

    [Fact]
    public void Backoff_grows_exponentially_between_attempts()
    {
        var g = Create();
        g.RecordFailure(1, "1"); // next eligible at +30s
        _now = _now.AddSeconds(30);
        g.RecordFailure(1, "2"); // second backoff is 60s

        _now = _now.AddSeconds(31);
        g.ShouldAttempt(1).Should().BeFalse("the second backoff is 60s, only 31s elapsed");
        _now = _now.AddSeconds(30);
        g.ShouldAttempt(1).Should().BeTrue("60s elapsed");
    }

    [Fact]
    public void After_maxAttempts_the_run_is_parked_and_never_eligible()
    {
        var g = Create(maxAttempts: 3);
        g.RecordFailure(1, "a").Should().Be(RetryDecision.Retry);
        g.RecordFailure(1, "b").Should().Be(RetryDecision.Retry);
        g.RecordFailure(1, "c").Should().Be(RetryDecision.Parked);

        _now = _now.AddDays(1);
        g.ShouldAttempt(1).Should().BeFalse("a parked run is not retried until a new commit or a restart");
    }

    [Fact]
    public void RecordSuccess_clears_the_backoff_so_the_run_is_eligible_again()
    {
        var g = Create();
        g.RecordFailure(1, "boom");
        g.ShouldAttempt(1).Should().BeFalse();

        g.RecordSuccess(1);
        g.ShouldAttempt(1).Should().BeTrue("a successful attempt clears the retry state");
    }

    [Fact]
    public void Governance_is_per_run_id_so_a_new_run_starts_fresh()
    {
        var g = Create(maxAttempts: 1);
        g.RecordFailure(1, "boom").Should().Be(RetryDecision.Parked);

        g.ShouldAttempt(2).Should().BeTrue("a different run id (e.g. a new commit) is unaffected by run 1's park");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_a_nonpositive_maxAttempts(int maxAttempts)
    {
        var act = () => new RetryGovernor(
            maxAttempts, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(900), () => _now, NullLogger<RetryGovernor>.Instance);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_rejects_a_negative_base_delay()
    {
        var act = () => new RetryGovernor(
            5, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(900), () => _now, NullLogger<RetryGovernor>.Instance);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_rejects_a_cap_below_the_base()
    {
        var act = () => new RetryGovernor(
            5, TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(30), () => _now, NullLogger<RetryGovernor>.Instance);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Backoff_stays_cap_bounded_and_does_not_overflow_with_a_large_base()
    {
        // A large base at a high attempt count would overflow base * 2^shift; the clamp must fall back to the
        // cap instead of computing (and storing) an overflowed/negative delay.
        var g = new RetryGovernor(
            maxAttempts: 40, TimeSpan.FromDays(1000), TimeSpan.FromDays(2000), () => _now, NullLogger<RetryGovernor>.Instance);

        for (var i = 0; i < 35; i++)
        {
            g.RecordFailure(1, "x").Should().Be(RetryDecision.Retry); // never throws OverflowException
        }

        _now = _now.AddDays(2001);
        g.ShouldAttempt(1).Should().BeTrue("the backoff is bounded by the 2000-day cap, not an overflowed delay");
    }

    [Fact]
    public void Tracked_state_is_bounded_and_evicts_the_oldest_runs()
    {
        // Retry state only clears on success; without eviction a long-lived daemon's parked/superseded runs
        // would accumulate for its whole lifetime. Past the cap, the oldest run ids are evicted (start fresh)
        // while recent failing runs stay governed.
        var g = Create(maxAttempts: 100); // never parks within the loop, so each failing run keeps state
        for (var i = 0; i < 10_050; i++)
        {
            g.RecordFailure(i, "x");
        }

        g.ShouldAttempt(0).Should().BeTrue("the oldest tracked run was evicted to bound memory");
        g.ShouldAttempt(10_049).Should().BeFalse("the most recent failing run is still governed (backing off)");
    }
}
