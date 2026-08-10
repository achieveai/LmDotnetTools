using CodeReviewDaemon.Sample.Persistence;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Reclaims review runs abandoned by a process that died, and keeps this process's own claims alive.
/// <para>
/// <see cref="Persistence.Models.WorkflowStatus.Running"/> asserts that some process is working a run
/// right now, and nothing ever withdrew the claim: a daemon killed mid-run left the row Running forever,
/// and because no query anywhere selected a run by status, nothing would look at it again. Measured on
/// the live store: four rows stranded at <c>ContextReady</c>, two from before the day's first restart,
/// one holding a real 158 KB context artifact computed and then abandoned. Reclaimed rows go to
/// <c>RetryPending</c> — a live, working state the resume machinery already handles — with their stage
/// intact, so the work already done is picked up rather than repeated.
/// </para>
/// <para>
/// The danger is the opposite mistake. Reclaiming a run that a CONCURRENT daemon is still working puts
/// two processes on the same PR, writing into the same notes branch — worse than the leak. So a run is
/// only taken when no live process can be holding it: either it carries no owner at all (nothing that
/// claims a run leaves the owner null, so such a row predates ownership and its process is long gone), or
/// its owner stopped heartbeating longer ago than <see cref="StaleAfter"/>. Every ambiguous case is left
/// alone: a missed reclaim costs one delayed retry, and those are not comparable.
/// </para>
/// </summary>
internal sealed class OrphanedRunReclaimer : BackgroundService
{
    /// <summary>How often this process re-asserts its claims. A run whose heartbeat is this fresh is
    /// unambiguously live.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an owner may go quiet before its runs are considered abandoned. Five heartbeats, so an
    /// ordinary stall — a GC pause, a slow disk, a stage that blocks its thread — cannot get a live run
    /// taken. The window is generous on purpose: it is the safety margin on the only decision here that
    /// can corrupt a review.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(150);

    private readonly ReviewStore _store;
    private readonly ILogger<OrphanedRunReclaimer> _logger;
    private readonly TimeProvider _timeProvider;

    public OrphanedRunReclaimer(
        ReviewStore store,
        ILogger<OrphanedRunReclaimer> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reclaim once on entry: the rows stranded by the PREVIOUS process are the whole reason this
        // exists, and making an operator wait a heartbeat interval to see a restart recover them would
        // repeat the mistake of the maintenance sweep that only ran after a cycle nobody ever reached.
        Reclaim();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // Re-assert this process's claims first, so a run of ours can never be inside the stale
                // window when the reclaim below evaluates it.
                _ = _store.HeartbeatOwnedRuns(DaemonInstance.Id, _timeProvider.GetUtcNow());
                Reclaim();
            }
            catch (Exception ex)
            {
                // Degrade, never throw: an exception escaping ExecuteAsync stops the HOST by default
                // (BackgroundServiceExceptionBehavior.StopHost), so one bad pass would take the daemon
                // down — far worse than the leak this closes.
                _logger.LogError(ex, "Orphaned-run reclaim pass failed; retrying after the interval.");
            }
        }
    }

    private void Reclaim()
    {
        try
        {
            var reclaimed = _store.ReclaimOrphanedRuns(StaleAfter);
            if (reclaimed > 0)
            {
                _logger.LogInformation(
                    "Reclaimed {Count} review run(s) left Running by a process that is no longer alive; "
                        + "they are now RetryPending with their stage intact and will resume where they stopped.",
                    reclaimed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphaned-run reclaim failed; the affected runs stay Running for now.");
        }
    }
}
