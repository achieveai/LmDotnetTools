using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Gives an unreachable review run a way back into the daemon.
/// <para>
/// A run only ever advances when a poll enumerates its PR, and <see cref="PrPollingService"/> enumerates the
/// OPEN pull requests inside the target's recency window. Nothing else in the daemon reads <c>review_run</c>
/// again — <see cref="PrLifecycleSweeper"/> resolves notes branches, not runs. So a run left non-terminal at
/// the moment its PR merges, closes, or simply goes quiet for longer than the recency window is orphaned
/// permanently: no retry is ever attempted, and the self-healing that lives on the retry path (a re-leased
/// slot clearing the stale <c>index.lock</c> that failed the last commit, say) never gets to happen. Neither
/// condition is under the run's control, and the run has no way to signal that it is stuck.
/// </para>
/// <para>
/// This reconciler is that missing route. Each pass takes the runs that have sat untouched past a grace
/// period and settles each one:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Superseded</b> (a later run exists for the same repo and PR) — retired without contacting anything.
///     The newer run reviewed a newer head; resuming this one would re-review a diff that no longer stands,
///     and on a posting daemon publish it.
///   </description></item>
///   <item><description>
///     <b>PR no longer open</b> — retired, which is exactly what <see cref="PrOrchestrator"/> does for a run
///     it observes as merged/closed. The run is finished-by-circumstance, not failed.
///   </description></item>
///   <item><description>
///     <b>PR still open</b> — resumed through the orchestrator, which runs only the stages the run has left.
///     A stranded open PR is by definition one the poll is not reaching, so its head has not moved and the
///     resumed review is against live code.
///   </description></item>
/// </list>
/// <para>
/// Resumes are capped per pass. The backlog this exists to drain accumulated over weeks, and releasing all of
/// it at once would put a burst of concurrent reviews — and comments — through a live daemon. Runs deferred by
/// the cap are logged and picked up by the next pass.
/// </para>
/// <para>
/// Each run is settled in its own try/catch, matching <see cref="PrLifecycleSweeper"/>: one unreachable
/// provider or one failing resume never aborts the pass, and the next pass retries it.
/// </para>
/// </summary>
internal sealed class StrandedRunReconciler
{
    private readonly Func<DateTimeOffset, int, IReadOnlyList<StrandedRunRow>> _listStrandedRuns;
    private readonly Func<StrandedRunRow, CancellationToken, Task<PrLifecycle>> _getPrLifecycleAsync;
    private readonly Func<ReviewRun, CancellationToken, Task<ReviewRun>> _resumeAsync;
    private readonly Action<long, ReviewStage, WorkflowStatus, PrLifecycleState> _retire;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _grace;
    private readonly int _scanLimit;
    private readonly int _maxResumesPerPass;
    private readonly ILogger<StrandedRunReconciler> _logger;

    public StrandedRunReconciler(
        Func<DateTimeOffset, int, IReadOnlyList<StrandedRunRow>> listStrandedRuns,
        Func<StrandedRunRow, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        Func<ReviewRun, CancellationToken, Task<ReviewRun>> resumeAsync,
        Action<long, ReviewStage, WorkflowStatus, PrLifecycleState> retire,
        TimeProvider timeProvider,
        TimeSpan grace,
        int scanLimit,
        int maxResumesPerPass,
        ILogger<StrandedRunReconciler> logger)
    {
        _listStrandedRuns = listStrandedRuns ?? throw new ArgumentNullException(nameof(listStrandedRuns));
        _getPrLifecycleAsync = getPrLifecycleAsync ?? throw new ArgumentNullException(nameof(getPrLifecycleAsync));
        _resumeAsync = resumeAsync ?? throw new ArgumentNullException(nameof(resumeAsync));
        _retire = retire ?? throw new ArgumentNullException(nameof(retire));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grace.Ticks);
        _grace = grace;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scanLimit);
        _scanLimit = scanLimit;
        ArgumentOutOfRangeException.ThrowIfNegative(maxResumesPerPass);
        _maxResumesPerPass = maxResumesPerPass;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Settles one pass worth of stranded runs. Never throws for a single run's failure — see the class
    /// summary.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var staleBefore = _timeProvider.GetUtcNow() - _grace;
        var stranded = _listStrandedRuns(staleBefore, _scanLimit);
        if (stranded.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Stranded-run reconciler found {Count} run(s) untouched since {StaleBefore:O}; resuming at most "
                + "{MaxResumes} this pass.",
            stranded.Count,
            staleBefore,
            _maxResumesPerPass);

        var resumed = 0;
        var deferred = 0;
        foreach (var row in stranded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await SettleAsync(row, resumed, cancellationToken).ConfigureAwait(false);
                if (outcome == SettleOutcome.Resumed)
                {
                    resumed++;
                }
                else if (outcome == SettleOutcome.Deferred)
                {
                    deferred++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Stranded-run reconciler failed to settle run {RunId} ({Provider} PR {PrId}); "
                        + "will retry on the next pass.",
                    row.Run.Id,
                    row.Repo.Provider,
                    row.Run.PrId);
            }
        }

        // The cap is a real limit on what this pass delivered, so it is stated rather than left to be inferred
        // from a shorter-than-expected run of log lines.
        if (deferred > 0)
        {
            _logger.LogInformation(
                "Stranded-run reconciler deferred {Deferred} open run(s) to a later pass after resuming "
                    + "{Resumed} (cap {MaxResumes}).",
                deferred,
                resumed,
                _maxResumesPerPass);
        }
    }

    /// <summary>What one run's pass through <see cref="SettleAsync"/> did with it.</summary>
    private enum SettleOutcome
    {
        /// <summary>Marked terminal — superseded, or its PR is no longer open. Costs nothing.</summary>
        Retired,

        /// <summary>Handed back to the orchestrator. Consumes one of the pass's resume slots.</summary>
        Resumed,

        /// <summary>Left for a later pass because the resume cap was already spent. The only outcome that
        /// leaves real work undone.</summary>
        Deferred,
    }

    /// <summary>Settles one run — see <see cref="SettleOutcome"/> for what the return value means.</summary>
    private async Task<SettleOutcome> SettleAsync(
        StrandedRunRow row, int resumedSoFar, CancellationToken cancellationToken)
    {
        var run = row.Run;

        // A later run for the same PR has already reviewed a newer head. Retire this one where it stands: its
        // diff is stale, so resuming it would spend a review — and on a posting daemon a comment — on a state
        // of the PR that no longer exists.
        if (row.Superseded)
        {
            Retire(row, "superseded by a later run for the same PR", run.PrLifecycleState);
            return SettleOutcome.Retired;
        }

        var lifecycle = await _getPrLifecycleAsync(row, cancellationToken).ConfigureAwait(false);
        var state = ToLifecycleState(lifecycle);
        if (state != PrLifecycleState.Open)
        {
            // Same rule PrOrchestrator applies to a run whose PR it observes as no longer open: stop working it
            // WITHOUT marking it failed. Applied here directly rather than by handing the run to the
            // orchestrator, because the orchestrator resolves a run by identity tuple and could settle a
            // same-head sibling instead — leaving this row stranded and re-picked every pass.
            Retire(row, $"PR is {state}", state);
            return SettleOutcome.Retired;
        }

        if (resumedSoFar >= _maxResumesPerPass)
        {
            _logger.LogInformation(
                "Stranded-run reconciler deferred run {RunId} ({Provider} PR {PrId}, stage {Stage}): "
                    + "this pass's resume cap of {MaxResumes} is spent.",
                run.Id,
                row.Repo.Provider,
                run.PrId,
                run.Stage,
                _maxResumesPerPass);
            return SettleOutcome.Deferred;
        }

        _logger.LogInformation(
            "Stranded-run reconciler resuming run {RunId} ({Provider} PR {PrId}) from stage {Stage}.",
            run.Id,
            row.Repo.Provider,
            run.PrId,
            run.Stage);

        var result = await _resumeAsync(run with { PrLifecycleState = state }, cancellationToken)
            .ConfigureAwait(false);

        // The orchestrator resolves the run to work on by identity tuple, so it can legitimately land on a
        // different row than the one handed to it (an earlier, further-progressed run at the same head). That
        // row got the work; this one would otherwise be re-picked, and re-charged against the cap, on every
        // pass forever.
        if (result.Id != run.Id)
        {
            Retire(row, $"already covered by run {result.Id} at the same head", state);
        }

        return SettleOutcome.Resumed;
    }

    /// <summary>
    /// Marks a run terminal at the stage it reached. <see cref="WorkflowStatus.Completed"/> here means "no
    /// longer being worked", which is the same sense <see cref="PrOrchestrator"/> uses when it halts a run
    /// whose PR has closed — not a claim that the review finished.
    /// </summary>
    private void Retire(StrandedRunRow row, string reason, PrLifecycleState state)
    {
        _retire(row.Run.Id, row.Run.Stage, WorkflowStatus.Completed, state);
        _logger.LogInformation(
            "Stranded-run reconciler retired run {RunId} ({Provider} PR {PrId}) at stage {Stage}: {Reason}.",
            row.Run.Id,
            row.Repo.Provider,
            row.Run.PrId,
            row.Run.Stage,
            reason);
    }

    private static PrLifecycleState ToLifecycleState(PrLifecycle lifecycle) => lifecycle switch
    {
        PrLifecycle.Open => PrLifecycleState.Open,
        PrLifecycle.Merged => PrLifecycleState.Merged,
        PrLifecycle.Abandoned => PrLifecycleState.Abandoned,
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unhandled PrLifecycle value."),
    };
}
