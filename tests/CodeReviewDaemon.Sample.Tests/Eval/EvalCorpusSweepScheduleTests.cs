using CodeReviewDaemon.Sample.Eval;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Eval;

/// <summary>
/// The cadence gate between the daemon's thirty-second maintenance tick and a pass over the whole
/// recorded review history.
/// <para>
/// Driven off a <see cref="FakeTimeProvider"/> rather than real elapsed time: the claims here are
/// about a clock comparison, and a test that waited on wall time would be both slow and a flake —
/// the exact-boundary case cannot be expressed at all against a clock nobody controls.
/// </para>
/// </summary>
public class EvalCorpusSweepScheduleTests
{
    private static EvalSweepReport Report(bool truncated = false) =>
        new()
        {
            CorpusId = "daemon-reviews",
            FromCursor = 0,
            ToCursor = 10,
            Truncated = truncated,
            CandidateCount = 1,
            FindingCount = 0,
            AnchoredFindingCount = 0,
            CandidatesCitingNothing = 1,
            ScoredCandidates = 0,
            MeanRecordedScore = null,
            UnscoredCandidates = 0,
            AmbiguousLegacyGradeCandidates = 0,
            UngradedCandidates = 1,
        };

    private sealed class Sweeps
    {
        private readonly Func<int, EvalSweepReport> _report;

        public Sweeps(Func<int, EvalSweepReport>? report = null) => _report = report ?? (_ => Report());

        public int Count { get; private set; }

        public Task<EvalSweepReport> RunAsync(CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(_report(Count));
        }
    }

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>
    /// A daemon that has just started has never swept, so the first tick sweeps. Deferring it by an
    /// interval would mean a daemon restarted more often than the interval never sweeps at all.
    /// </summary>
    [Fact]
    public async Task The_first_tick_sweeps()
    {
        var sweeps = new Sweeps();
        var schedule = new EvalCorpusSweepSchedule(sweeps.RunAsync, Hour, new FakeTimeProvider());

        await schedule.SweepAsync(CancellationToken.None);

        sweeps.Count.Should().Be(1);
    }

    /// <summary>
    /// The gate itself. The shared tick fires every thirty seconds, so without this the eval sweep
    /// would re-read the corpus a hundred and twenty times an hour.
    /// </summary>
    [Fact]
    public async Task A_tick_inside_the_interval_does_not_sweep()
    {
        var clock = new FakeTimeProvider();
        var sweeps = new Sweeps();
        var schedule = new EvalCorpusSweepSchedule(sweeps.RunAsync, Hour, clock);

        await schedule.SweepAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(59));
        await schedule.SweepAsync(CancellationToken.None);

        sweeps.Count.Should().Be(1, "the interval has not elapsed");
    }

    /// <summary>
    /// The boundary, stated explicitly: a tick at <b>exactly</b> the interval sweeps. The comparison
    /// is <c>elapsed &lt; interval</c>, so equality is due rather than early — and with a tick that
    /// divides the interval evenly, which is the ordinary case, the other reading would silently
    /// stretch every cadence by one whole tick.
    /// </summary>
    [Fact]
    public async Task A_tick_at_exactly_the_interval_sweeps()
    {
        var clock = new FakeTimeProvider();
        var sweeps = new Sweeps();
        var schedule = new EvalCorpusSweepSchedule(sweeps.RunAsync, Hour, clock);

        await schedule.SweepAsync(CancellationToken.None);

        clock.Advance(Hour);
        await schedule.SweepAsync(CancellationToken.None);

        sweeps.Count.Should().Be(2);
    }

    /// <summary>
    /// A truncated window overrides the cadence: the limit cut the sweep short and the rest of the
    /// history is waiting, so the next tick resumes instead of waiting out an interval. Otherwise a
    /// backlog drains at one window per interval — on an hourly cadence, one window an hour, for as
    /// long as the backlog lasts.
    /// <para>
    /// The distinguishing input is a first sweep that truncates and a second that does not: the
    /// second tick must sweep (proving truncation lifted the gate) and the third must not (proving
    /// the gate came back, rather than the schedule having simply stopped gating).
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_truncated_window_resumes_on_the_next_tick_and_the_cadence_then_returns()
    {
        var clock = new FakeTimeProvider();
        var sweeps = new Sweeps(n => Report(truncated: n == 1));
        var schedule = new EvalCorpusSweepSchedule(sweeps.RunAsync, Hour, clock);

        await schedule.SweepAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(30));
        await schedule.SweepAsync(CancellationToken.None);
        sweeps.Count.Should().Be(2, "the first window stopped short of the end of the history");

        clock.Advance(TimeSpan.FromSeconds(30));
        await schedule.SweepAsync(CancellationToken.None);
        sweeps.Count.Should().Be(2, "the second window reached the end, so the cadence applies");
    }

    /// <summary>
    /// A sweep that throws still costs an interval. The poller catches and logs, so the alternative
    /// is a broken sweep hitting the store on every tick — a worse failure than a delayed one, and
    /// one that would go on until somebody reads the log.
    /// </summary>
    [Fact]
    public async Task A_sweep_that_threw_does_not_retry_before_its_next_interval()
    {
        var clock = new FakeTimeProvider();
        var attempts = 0;

        var schedule = new EvalCorpusSweepSchedule(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("the store is unhappy");
            },
            Hour,
            clock
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => schedule.SweepAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(30));
        await schedule.SweepAsync(CancellationToken.None);

        attempts.Should().Be(1, "the failed attempt still stamped the cadence");
    }

    /// <summary>
    /// "Never" is expressed by not registering the schedule, not by a zero interval — which reads as
    /// "as often as possible" and is exactly what the shared tick would then do.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_interval_is_refused(int minutes)
    {
        var build = () => new EvalCorpusSweepSchedule(_ => Task.FromResult(Report()), TimeSpan.FromMinutes(minutes));

        build.Should().Throw<ArgumentOutOfRangeException>();
    }
}
