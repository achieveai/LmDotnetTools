using System.Net;
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
///     <b>Superseded</b> (a later run has already re-reviewed this one's work at a newer head) — retired
///     without contacting anything. The newer run reviewed a newer head; resuming this one would re-review a
///     diff that no longer stands, and on a posting daemon publish it. The store decides this narrowly, on
///     the review's identity rather than on the PR alone, because retirement is permanent and this pass is
///     the run's only route back.
///   </description></item>
///   <item><description>
///     <b>PR no longer open</b> — retired, which is exactly what <see cref="PrOrchestrator"/> does for a run
///     it observes as merged/closed. The run is finished-by-circumstance, not failed.
///   </description></item>
///   <item><description>
///     <b>PR still open</b> — resumed through the orchestrator, which runs only the stages the run has left.
///     A stranded open PR is by definition one the poll is not reaching, so its head has not moved and the
///     resumed review is against live code. The resume goes through <see cref="PrOrchestrator.ReconcileAsync"/>
///     rather than the poll entry, because a run the <see cref="RetryGovernor"/> has parked is exactly the kind
///     of unreachable run this pass exists for and the poll entry would refuse it before any stage ran.
///   </description></item>
/// </list>
/// <para>
/// Resumes are capped per pass. The backlog this exists to drain accumulated over weeks, and releasing all of
/// it at once would put a burst of concurrent reviews — and comments — through a live daemon. Runs deferred by
/// the cap are logged and picked up by the next pass.
/// </para>
/// <para>
/// Each run is settled in its own try/catch, matching <see cref="PrLifecycleSweeper"/>: one unreachable
/// provider or one failing resume never aborts the pass, and the rest of the pass carries on. A run that
/// failed does NOT come back on the very next pass — it comes back once its <c>updated_at</c> is older than
/// the grace period again, which is the only backoff this path has.
/// </para>
/// <para>
/// <b>Where that backoff comes from.</b> Nearly always from the orchestrator, not from here: a stage that
/// throws is written <see cref="WorkflowStatus.RetryPending"/> and rethrown
/// (<see cref="PrOrchestrator"/>'s stage catch), and that write refreshes <c>updated_at</c> before the
/// exception ever reaches this class. The claim stamp below covers only the narrower case where the resume
/// throws BEFORE any such write. Either way the effect is the same and it is load-bearing: resumes here go
/// through <see cref="PrOrchestrator.ReconcileAsync"/>, which resets the <see cref="RetryGovernor"/> for the
/// run on purpose, so the governor's backoff and park do not apply and <c>updated_at</c> + grace is the sole
/// remaining bound. Make a failed resume literally eligible on the next pass and a permanently-broken run
/// gets a full attempt — lease, clone, LLM — every cycle, through the one entry that has no governor: the
/// hot-loop the governor exists to kill. The accepted cost is the other direction: a genuinely recoverable
/// run whose resume failed waits a full grace period for its next attempt. A shorter dedicated retry window
/// would have to start by addressing the orchestrator's own <c>RetryPending</c> write, which produces this
/// delay on nearly every occurrence.
/// </para>
/// <para>
/// <b>Single owner.</b> A takeover here is claimed by stamping the row's <c>updated_at</c>, not by a
/// compare-and-swap lease, and that is a deliberate limit rather than an oversight. The sweep runs inside
/// <see cref="PrPollingService"/>'s single sequential maintenance seam, so two passes cannot overlap within a
/// process, and the daemon is configured single-instance against one SQLite store — the concurrency a lease
/// would defend against does not exist today. Adding a second instance, or moving this sweep off that seam,
/// is therefore the change that must bring fencing with it: the stamp narrows the window between listing and
/// resuming but does not close it, so two reconcilers would each see a stranded row and each resume it.
/// </para>
/// </summary>
internal sealed class StrandedRunReconciler
{
    private readonly Func<DateTimeOffset, int, IReadOnlyList<StrandedRunRow>> _listStrandedRuns;
    private readonly Func<StrandedRunRow, CancellationToken, Task<PrLifecycle>> _getPrLifecycleAsync;
    private readonly Func<ReviewRun, CancellationToken, Task<ReviewRun>> _resumeAsync;
    private readonly Action<long, ReviewStage, WorkflowStatus, PrLifecycleState> _updateRunState;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _grace;
    private readonly int _scanLimit;
    private readonly int _maxResumesPerPass;
    private readonly ILogger<StrandedRunReconciler> _logger;

    public StrandedRunReconciler(
        Func<DateTimeOffset, int, IReadOnlyList<StrandedRunRow>> listStrandedRuns,
        Func<StrandedRunRow, CancellationToken, Task<PrLifecycle>> getPrLifecycleAsync,
        Func<ReviewRun, CancellationToken, Task<ReviewRun>> resumeAsync,
        Action<long, ReviewStage, WorkflowStatus, PrLifecycleState> updateRunState,
        TimeProvider timeProvider,
        TimeSpan grace,
        int scanLimit,
        int maxResumesPerPass,
        ILogger<StrandedRunReconciler> logger)
    {
        _listStrandedRuns = listStrandedRuns ?? throw new ArgumentNullException(nameof(listStrandedRuns));
        _getPrLifecycleAsync = getPrLifecycleAsync ?? throw new ArgumentNullException(nameof(getPrLifecycleAsync));
        _resumeAsync = resumeAsync ?? throw new ArgumentNullException(nameof(resumeAsync));
        _updateRunState = updateRunState ?? throw new ArgumentNullException(nameof(updateRunState));
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

        var budget = new ResumeBudget(_maxResumesPerPass);
        var deferred = 0;
        foreach (var row in stranded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await SettleAsync(row, budget, cancellationToken).ConfigureAwait(false);
                if (outcome == SettleOutcome.Deferred)
                {
                    deferred++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Stranded-run reconciler failed to settle run {RunId} ({Provider} PR {PrId}); "
                        + "it becomes eligible again once it is untouched for the grace period.",
                    row.Run.Id,
                    row.Repo.Provider,
                    row.Run.PrId);
            }
        }

        // The cap is a real limit on what this pass delivered, so it is stated rather than left to be inferred
        // from a shorter-than-expected run of log lines. It reports slots SPENT rather than runs resumed, because
        // that is what the counter holds: saying "after resuming 2" on a pass where both resumes threw would tell
        // an operator the opposite of what happened, and the deferrals it explains would look unexplained.
        if (deferred > 0)
        {
            _logger.LogInformation(
                "Stranded-run reconciler deferred {Deferred} open run(s) to a later pass after spending "
                    + "{Spent} of this pass's {MaxResumes} resume slot(s).",
                deferred,
                budget.Spent,
                _maxResumesPerPass);
        }
    }

    /// <summary>
    /// What is left of one pass's resume allowance. A slot is spent when a run is CLAIMED, not when its resume
    /// returns: the slot pays for a lease, a clone, and the review's remaining stages, all of which a resume
    /// that throws has already spent. Counting only the ones that came back turned the cap into a bound on
    /// successes, which leaves a backlog of runs that all fail unbounded — and that is the backlog this
    /// reconciler sees, since a run reaches its listing by having gone wrong once already.
    /// <see cref="SettleAsync"/> is async and so cannot hand a count back through a <c>ref</c> parameter, which
    /// is the only reason this is an object rather than a local.
    /// </summary>
    private sealed class ResumeBudget(int max)
    {
        public int Spent { get; private set; }

        public bool IsSpent => Spent >= max;

        public void Charge() => Spent++;
    }

    /// <summary>What one run's pass through <see cref="SettleAsync"/> did with it.</summary>
    private enum SettleOutcome
    {
        /// <summary>Marked terminal — superseded, or its PR is no longer open. Costs nothing.</summary>
        Retired,

        /// <summary>Handed back to the orchestrator. Spent one of the pass's resume slots — whether or not the
        /// resume itself succeeded, since the slot pays for the attempt.</summary>
        Resumed,

        /// <summary>Left for a later pass because the resume cap was already spent. The only outcome that
        /// leaves real work undone.</summary>
        Deferred,
    }

    /// <summary>Settles one run — see <see cref="SettleOutcome"/> for what the return value means.</summary>
    private async Task<SettleOutcome> SettleAsync(
        StrandedRunRow row, ResumeBudget budget, CancellationToken cancellationToken)
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

        // Every stage is already done and only the terminal status write was lost — a crash between the last
        // stage's write and its status, say. There is no work here to resume: `RemainingStages` of a complete
        // stage is empty, so handing this row to the orchestrator makes it return without executing anything.
        // Answered locally, before the provider lookup, because it is a pure function of the row: asking the
        // provider about a run with nothing left to do spends a call to reach the same retirement. Without this
        // the row was "resumed" on every pass — charged against the cap, logged as if work happened — and the
        // pass that exists to drain stranded runs was itself the thing that never drained.
        if (StageMachine.IsComplete(run.Stage))
        {
            Retire(row, "every stage already done; only its terminal status write was lost", run.PrLifecycleState);
            return SettleOutcome.Retired;
        }

        PrLifecycle lifecycle;
        try
        {
            lifecycle = await _getPrLifecycleAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The provider has no such PR — the number was never one (a run seeded against an issue id, say),
            // or the PR was removed. Without this, the lookup throws on every pass and the run is stranded
            // exactly as before, one level further out. Narrowed to 404 deliberately: a 401, a 5xx or a
            // timeout says nothing about the PR's state, and retiring on those would write off live work.
            Retire(row, "the provider no longer has this PR", PrLifecycleState.Abandoned);
            return SettleOutcome.Retired;
        }

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

        if (budget.IsSpent)
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

        // Claim the row before handing it over, by re-writing the state it already has purely for the
        // `updated_at` the write carries. Two things need it. A resume that finds nothing to do writes nothing —
        // the orchestrator returns early for a run whose stages are all done — and a resume that throws before
        // reaching a stage leaves the row exactly as it found it; either way a row whose `updated_at` never
        // advances is listed again by the very next pass, logged as "resuming" again, and charged against the
        // cap again, forever, crowding out the runs the pass exists to drain. This is the narrow case only: a
        // stage that throws is written RetryPending by the orchestrator itself, which stamps `updated_at` and
        // holds the run for a grace period without any help from here. Stamping first also makes the takeover
        // visible the moment it is taken rather than whenever the review happens to finish, which can be many
        // minutes later.
        _updateRunState(run.Id, run.Stage, run.WorkflowStatus, state);
        budget.Charge();

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
        _updateRunState(row.Run.Id, row.Run.Stage, WorkflowStatus.Completed, state);
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
