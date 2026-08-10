namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Runs one periodic maintenance sweep on a cadence of its own, independent of the PR-watching loop.
/// <para>
/// It exists because sharing a loop with the poller was wrong in BOTH directions, and the daemon was shipped
/// each way in turn. Sequenced AFTER the poll body, the PR-lifecycle sweep waited on a cycle that reviews
/// every PR of every target inline — hours on a real repo — so it had never executed once in the daemon's
/// life: its unguarded entry log appeared zero times in 3,603 lines, all 123 reviewed PRs were still 'Open',
/// and with no merge ever detected the Knowledge Base had never received a single entry. Moved BEFORE the
/// poll body it ran, immediately and correctly, and then its 125-PR first backlog held off every review for
/// roughly two hours. The ordering was never the bug: serializing unbounded work and periodic maintenance
/// into one loop is, because whichever runs first starves the other. So they no longer share a loop.
/// </para>
/// <para>
/// One instance per sweep rather than one chained delegate for all of them — otherwise the long sweep starves
/// its neighbours exactly as it starved the poller, which is the same defect one level down.
/// </para>
/// </summary>
internal sealed class MaintenanceSweepService : BackgroundService
{
    /// <summary>What this sweep is called in the log — the only thing that tells two otherwise identical
    /// loops apart when one of them starts failing.</summary>
    private readonly string _name;

    private readonly Func<CancellationToken, Task> _sweepAsync;
    private readonly TimeSpan _interval;
    private readonly ILogger<MaintenanceSweepService> _logger;
    private readonly TimeProvider _timeProvider;

    public MaintenanceSweepService(
        string name,
        Func<CancellationToken, Task> sweepAsync,
        TimeSpan interval,
        ILogger<MaintenanceSweepService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sweepAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _name = name;
        _sweepAsync = sweepAsync;
        _interval = interval;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Name} maintenance sweep is on its own cadence: first pass now, then every {IntervalSeconds}s. "
                + "It shares no loop with PR polling, so neither can starve the other.",
            _name,
            _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Sweep FIRST, delay after. A freshly restarted daemon does its maintenance on entry rather than
            // one interval into the process — which is the window an operator actually watches to see whether
            // the restart helped. Live, the first Knowledge Base entry this daemon ever wrote landed 27
            // seconds after a restart because of this ordering.
            try
            {
                await _sweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // UNCONDITIONAL, and load-bearing in a way it was not before the split — do not simplify this
                // away as defensive noise. Since .NET 6 a BackgroundService whose ExecuteAsync throws stops
                // the HOST by default (BackgroundServiceExceptionBehavior.StopHost). Inside the old poll loop
                // an unhandled sweep failure cost one cycle; here, without this catch, it terminates the whole
                // daemon — and it would present as "the process mysteriously exited", not as a sweep problem,
                // because the stack that killed it is three layers below anything a sweep log would show.
                // The sweepers are already best-effort per PR, so anything arriving here is unhandled by
                // definition; ending all maintenance (or the process) over one bad cycle is how a transient
                // provider error becomes a permanently cold Knowledge Base.
                _logger.LogError(ex, "{Name} maintenance sweep failed; retrying after the interval.", _name);
            }

            // Delaying AFTER the sweep rather than ticking on a wall clock is what makes overlap structurally
            // impossible: there is no tick to arrive while a sweep is in flight, so no flag or semaphore has
            // to be trusted to drop it. A sweep that outlasts several intervals — the 125-PR backlog does —
            // simply starts its next pass an interval after it finishes, and the passes it "missed" are
            // dropped rather than queued. A queue would discharge every one of them back-to-back the moment
            // the long sweep returned, which is worse than the overlap it was meant to prevent. The cost is
            // that the period is interval + duration rather than a fixed wall-clock beat; nothing here
            // depends on landing at particular times.
            try
            {
                await Task.Delay(_interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
