namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// Runs <see cref="EvalCorpusSweep.SweepOnceAsync"/> on its own cadence from the daemon's shared
/// maintenance tick.
/// <para>
/// The daemon has exactly one recurring seam — the PR poller's maintenance sweep — and it fires
/// every thirty seconds, which is the right cadence for the thing it was built for and far too hot
/// for a pass over the whole recorded review history. Giving the eval sweep its own
/// <see cref="BackgroundService"/> would mean a second timer, a second shutdown path and a second
/// place for a swallowed exception to hide; gating the shared tick keeps one loop and one failure
/// story. The interval is the operator's knob, and the seam it plugs into is unchanged.
/// </para>
/// <para>
/// <b>A truncated window overrides the interval.</b> Truncation is the exact condition under which
/// the corpus stops accumulating — the limit cut the window short and the rest of the history is
/// still waiting — so the cadence is deliberately not applied to the tick after one: waiting out an
/// interval per window would make a backlog drain at one window per interval, for as long as the
/// backlog lasts. This is the caller-side half of <c>CorpusPage.Truncated</c>'s contract, which is
/// why that flag is on the contract rather than only in a log line.
/// </para>
/// </summary>
internal sealed class EvalCorpusSweepSchedule
{
    private readonly Func<CancellationToken, Task<EvalSweepReport>> _sweepOnceAsync;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly ILogger<EvalCorpusSweepSchedule>? _logger;

    private DateTimeOffset? _lastSweepStartedAt;

    /// <summary>Builds the schedule.</summary>
    /// <param name="sweepOnceAsync">One sweep; in production <see cref="EvalCorpusSweep.SweepOnceAsync"/>.</param>
    /// <param name="interval">
    /// Least time between two sweeps. Positive — "never" is expressed by not registering this at
    /// all, not by a zero interval, because a zero interval reads as "as often as possible" and is
    /// exactly what the shared tick would then do.
    /// </param>
    /// <param name="clock">The clock; injected so the cadence is testable without waiting on it.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public EvalCorpusSweepSchedule(
        Func<CancellationToken, Task<EvalSweepReport>> sweepOnceAsync,
        TimeSpan interval,
        TimeProvider? clock = null,
        ILogger<EvalCorpusSweepSchedule>? logger = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _sweepOnceAsync =
            sweepOnceAsync ?? throw new ArgumentNullException(nameof(sweepOnceAsync));
        _interval = interval;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// The maintenance seam. Sweeps when one is due and returns immediately when it is not — this
    /// runs on every poll cycle, so the common outcome is doing nothing.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        if (_lastSweepStartedAt is { } last && now - last < _interval)
        {
            return;
        }

        // Stamped BEFORE the await, so a sweep that throws still costs an interval rather than
        // retrying on every tick: the poller already catches and logs, and a broken sweep hammering
        // the store every thirty seconds is a worse failure than a delayed one.
        _lastSweepStartedAt = now;

        var report = await _sweepOnceAsync(cancellationToken).ConfigureAwait(false);

        if (report.Truncated)
        {
            _logger?.LogInformation(
                "Eval sweep of '{CorpusId}' stopped at its window limit with history still "
                    + "unread; the next maintenance tick resumes from {ToCursor} rather than "
                    + "waiting out the {Interval} interval.",
                report.CorpusId,
                report.ToCursor,
                _interval
            );

            _lastSweepStartedAt = null;
        }
    }
}
