using System.Collections.Frozen;
using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Drives one review run through the <see cref="StageMachine"/> serially, persisting progress after
/// every stage so a crash resumes from the first incomplete step rather than re-doing work. Creation
/// is idempotent (the §6 identity tuple), and a PR observed as no longer open short-circuits to
/// completion. The per-stage work is delegated to <see cref="IReviewStageExecutor"/>.
/// </summary>
internal sealed class PrOrchestrator
{
    /// <summary>
    /// The park phrase for a governed failure with no more specific entry in <see cref="DescribeGovernedFailure"/>
    /// — and the one a replayed notice falls back to when the row's <c>park_reason</c> is absent or is not a
    /// value this build's vocabulary could have written (see <see cref="TrustedParkReasonForReplay"/>).
    /// </summary>
    private const string UnclassifiedParkPhrase = "the review could not be completed";

    private readonly ReviewStore _store;
    private readonly IReviewStageExecutor _executor;
    private readonly ILogger<PrOrchestrator> _logger;
    private readonly ReviewProgressReporter? _progress;
    private readonly RetryGovernor? _retryGovernor;
    private readonly int _maxDurableRetryAttempts;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IReviewParkNotifier? _parkNotifier;

    /// <summary>
    /// <c>maxDurableRetryAttempts</c> is the governed failures a run may accumulate DURABLY before it is
    /// parked permanently; see <see cref="Configuration.CodeReviewDaemonOptions.MaxDurableRetryAttempts"/>
    /// for why it must sit above <see cref="Configuration.CodeReviewDaemonOptions.MaxContextRetries"/>. It is
    /// refused below 1 for the reason <see cref="RetryGovernor"/> refuses its own bound there: a non-positive
    /// budget has no defined meaning, and zero would park every run on its first governed failure.
    /// <para>
    /// <c>parkNotifier</c> announces a permanent park on the pull request and is optional — null leaves the
    /// park silent outside the log, which is what every test and any daemon with no publisher wired does.
    /// </para>
    /// </summary>
    public PrOrchestrator(
        ReviewStore store,
        IReviewStageExecutor executor,
        ILogger<PrOrchestrator> logger,
        ReviewProgressReporter? progress = null,
        RetryGovernor? retryGovernor = null,
        int maxDurableRetryAttempts = 10,
        Func<DateTimeOffset>? clock = null,
        IReviewParkNotifier? parkNotifier = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDurableRetryAttempts, 1);

        _store = store;
        _executor = executor;
        _logger = logger;
        _progress = progress;
        _retryGovernor = retryGovernor;
        _maxDurableRetryAttempts = maxDurableRetryAttempts;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _parkNotifier = parkNotifier;
    }

    /// <summary>
    /// Ensures the run exists, then executes the stages still outstanding for it. Returns the run in
    /// its final state for this invocation.
    /// </summary>
    public Task<ReviewRun> RunAsync(ReviewRun seed, CancellationToken cancellationToken) =>
        RunAsync(seed, admitParked: false, cancellationToken);

    /// <summary>
    /// The same drive as <see cref="RunAsync(ReviewRun, CancellationToken)"/>, except that a run the
    /// <see cref="RetryGovernor"/> is backing off or has parked is admitted anyway.
    /// <para>
    /// This is the entry <see cref="StrandedRunReconciler"/> uses, and it exists because the ordinary entry
    /// cannot serve it: the reconciler's whole job is to give a run that nothing else will reach another
    /// attempt, and a parked run is precisely such a run. Through <see cref="RunAsync(ReviewRun,
    /// CancellationToken)"/> the governor refused it before any stage ran, so the resume did nothing, the row
    /// was never written, and the next pass found it stranded exactly as before — a permanent loop that also
    /// spent one of the pass's resume slots each time. Deciding to spend another attempt is the caller's, and
    /// the split keeps that decision explicit instead of quietly weakening park for the poll path too.
    /// </para>
    /// </summary>
    public Task<ReviewRun> ReconcileAsync(ReviewRun seed, CancellationToken cancellationToken) =>
        RunAsync(seed, admitParked: true, cancellationToken);

    private async Task<ReviewRun> RunAsync(ReviewRun seed, bool admitParked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var run = _store.CreateOrGetReviewRun(seed);

        // A PERMANENT park ends every path, including this one. It is checked before the reset below rather
        // than after it precisely because the reset is what made the in-memory park erasable: the reconciler
        // resumes a stuck run roughly every 45 minutes, cleared the accumulated failures each time, and so the
        // bound was never reached — three pull requests re-reviewed for 33 hours at 30 minutes of model work
        // apiece. Deciding to spend another attempt (ReconcileAsync) is still the caller's for a BACKING-OFF
        // run; it is not on offer for one whose durable budget is gone. The way back is a new commit, which is
        // a new identity tuple and therefore a new row with a full budget.
        if (run.ParkedAt is not null)
        {
            _logger.LogDebug(
                "Review run {RunId} (pr {PrId}) is permanently parked since {ParkedAt}; skipping. Reason: {Reason}",
                run.Id,
                run.PrId,
                run.ParkedAt,
                run.ParkReason
            );

            // The one retry cadence a lost park notice has. It cannot resurrect the run — the guard returns
            // immediately below and no stage is reached — and it is a no-op once the notice is delivered.
            await RetryOutstandingParkNoticeAsync(run);
            return run;
        }

        // Against the RESOLVED id, not the seed's: creation is idempotent on the §6 identity tuple, so the row
        // that actually gets worked — and therefore the id the governor is holding a park against — can be an
        // existing one rather than the seed.
        if (admitParked)
        {
            _retryGovernor?.Reset(run.Id);
        }

        try
        {
            // The seed carries the freshest observed PR lifecycle; reconcile the persisted run with it.
            if (seed.PrLifecycleState != run.PrLifecycleState)
            {
                _store.UpdateReviewRunState(run.Id, run.Stage, run.WorkflowStatus, seed.PrLifecycleState);
                run = run with { PrLifecycleState = seed.PrLifecycleState };
            }

            if (StageMachine.IsComplete(run.Stage))
            {
                return run;
            }

            // Everything below is real work for this run — announce it once. The steady-state no-op poll
            // (a completed run) returns above, so finished PRs don't re-announce every cycle.
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _progress?.Picked(run, DescribePickReason(run));

            if (run.PrLifecycleState != PrLifecycleState.Open)
            {
                // PR merged/closed/abandoned — stop working it without marking the run as failed.
                _logger.LogInformation(
                    "Review run {RunId} halted: PR {PrId} is {State}.",
                    run.Id,
                    run.PrId,
                    run.PrLifecycleState
                );
                _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.Completed, run.PrLifecycleState);
                _progress?.Finished(
                    run,
                    $"halted (PR {run.PrLifecycleState})",
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
                );
                return run with { WorkflowStatus = WorkflowStatus.Completed };
            }

            // Retry governance: a run that failed a recent poll is backing off, and one that exhausted its
            // attempts is parked — either way, skip this poll's attempt (leaving it RetryPending) instead of
            // the old ~30s hot-loop. Restart clears the in-memory state, so a restart retries everything, and
            // ReconcileAsync above has already cleared this run's state when a caller decided to spend an
            // attempt on it — so by here the answer is only ever about the poll path.
            if (_retryGovernor is not null && !_retryGovernor.ShouldAttempt(run.Id))
            {
                return run;
            }

            foreach (var stage in StageMachine.RemainingStages(run.Stage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                _progress?.StageStarting(run, stage);
                try
                {
                    await _executor.ExecuteStageAsync(stage, run, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.RetryPending, run.PrLifecycleState);
                    // The RetryGovernor bounds the ContextReady hot-loop (the stuck-slot case it exists for) and
                    // exactly one Reviewed failure: a review whose sub-agent completion barrier ran out the
                    // stage's shared deadline. Every OTHER failure at a later stage (Reviewed/Judged/Posted) is a
                    // different, usually self-healing problem — e.g. a Posted-stage lock the next lease's
                    // clean-on-entry clears — so it must NOT consume the budget or park recoverable work.
                    if (IsGovernedFailure(stage, ex))
                    {
                        _retryGovernor?.RecordFailure(run.Id, ex.Message);
                        await ChargeDurableBudgetAsync(run, stage, ex);
                    }
                    _logger.LogError(ex, "Review run {RunId} failed at stage {Stage}.", run.Id, stage);
                    _progress?.Finished(
                        run,
                        $"failed at {stage}",
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
                    );
                    throw;
                }

                // A governed stage that cleared its cause → forget any accumulated retry state so a later
                // re-review (or a resume past that stage) starts fresh. Reviewed is included for the same reason
                // it is governed at all: without it, a run that survived the barrier this round would still be
                // refused by a governor holding its earlier barrier failures, and could never finish the stages
                // AFTER Reviewed. Stages outside the governor's scope neither record nor clear.
                if (IsGovernedStage(stage))
                {
                    _retryGovernor?.RecordSuccess(run.Id);
                    // The durable half of the same contract. Without it a run that failed persistently and then
                    // RECOVERED still carries those failures toward a permanent park it no longer deserves.
                    _store.ClearGovernedFailureCount(run.Id);
                }

                var workflowStatus = StageMachine.IsComplete(stage) ? WorkflowStatus.Completed : WorkflowStatus.Running;
                _store.UpdateReviewRunState(run.Id, stage, workflowStatus, run.PrLifecycleState);
                run = run with { Stage = stage, WorkflowStatus = workflowStatus };
            }

            _progress?.Finished(
                run,
                $"complete ({ClassifyDeliveryOutcome(run)})",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
            );
            return run;
        }
        finally
        {
            // Guarantee a pooled review slot is returned on EVERY terminal outcome of this run — normal
            // completion (where the Posted stage already returned it, so this is a no-op), the PR-not-open
            // short-circuit, and the failure→RetryPending rethrow — so a run that never reaches Posted can
            // never leak pool capacity. Uses CancellationToken.None so a cancelled run still returns its slot.
            await _executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Charges one governed failure against the run's DURABLE budget and, once the budget is gone, parks the
    /// run permanently and announces it.
    /// </summary>
    /// <remarks>
    /// The in-memory <see cref="RetryGovernor"/> already counts, backs off and parks — but its state is a
    /// dictionary, and <see cref="ReconcileAsync"/> resets it for the run it is handed, which the stranded-run
    /// reconciler does roughly every 45 minutes. The count therefore never reached the bound and the park
    /// never fired. This is the same policy written where a resume cannot reach it.
    /// <para>
    /// Order matters against the caller: the stage catch has already written <c>RetryPending</c> for this run,
    /// so the park's <c>Failed</c> must be written AFTER it or the retry status would win.
    /// </para>
    /// </remarks>
    private async Task ChargeDurableBudgetAsync(ReviewRun run, ReviewStage stage, Exception ex)
    {
        var attempts = _store.IncrementGovernedFailureCount(run.Id);
        if (attempts < _maxDurableRetryAttempts)
        {
            return;
        }

        // A FIXED phrase, never ex.Message. This value lands in a persisted column and, through the notifier,
        // in a public pull-request comment — and the governed types carry raw external output in their
        // messages: ReviewHostContractException embeds the review host's HTTP response body, SlotCorruptException
        // embeds git's stderr. Interpolating either publishes host names, paths and command output to whoever
        // can read the PR. The stage stays: it is the daemon's own vocabulary, not external input.
        var reason = $"{stage}: {DescribeGovernedFailure(ex)}";

        // False means somebody already parked this row. The notice hangs off this boolean, so a second park
        // attempt cannot produce a second comment.
        if (!_store.TryMarkReviewRunParked(run.Id, _clock(), reason))
        {
            return;
        }

        // The RAW exception, deliberately, and it is the only place it survives. This is a protected operator
        // log; the sanitized phrase above is what the pull request gets, and diagnosing why a review parked
        // needs the host body / git stderr the phrase deliberately drops.
        _logger.LogError(
            ex,
            "review_run PARKED-PERMANENT run {RunId} pr {PrId} head {HeadSha} after {Attempts} durable "
                + "attempts at stage {Stage}: {Error}",
            run.Id,
            run.PrId,
            run.HeadSha,
            attempts,
            stage,
            ex.Message
        );

        if (_parkNotifier is null)
        {
            return;
        }

        try
        {
            // CancellationToken.None: the park is already committed, and a shutdown racing the notice would
            // otherwise leave a permanently parked run with nothing on the PR to explain the silence.
            await _parkNotifier.NotifyParkedAsync(run, reason, CancellationToken.None);
        }
        catch (Exception notifyFailure)
        {
            // Swallowed HERE rather than at the notifier, because the justification is the caller's: this runs
            // inside a catch block that is about to rethrow the stage's own exception, and letting a failed
            // courtesy comment replace it would hide the actual review failure from every log and every
            // caller. The park itself is already durable in the store, so nothing is lost but the notice.
            _logger.LogWarning(
                notifyFailure,
                "Review run {RunId} was parked, but the park notice could not be delivered.",
                run.Id
            );
        }
    }

    /// <summary>
    /// The public vocabulary for a park: one short, stable, operator-meaningful phrase per governed exception
    /// type, chosen by TYPE and never derived from the exception's text.
    /// </summary>
    /// <remarks>
    /// The phrases carry no paths, hosts, credentials or command output, because everything here is persisted
    /// in <c>review_run.park_reason</c> and posted verbatim to a pull request anyone with read access can see.
    /// The types it maps are the ones <see cref="IsGovernedFailure"/> admits.
    /// <para>
    /// This table is the SINGLE source of truth for that vocabulary: <see cref="DescribeGovernedFailure"/>
    /// reads it to choose a phrase and <see cref="KnownParkPhrases"/> is derived from it, so a phrase added
    /// here for a new governed type cannot silently fall out of the replay allow-list below.
    /// </para>
    /// <para>
    /// Keyed on the EXACT runtime type, which is equivalent to the type patterns this replaces because every
    /// mapped exception is <c>sealed</c>. Should one be unsealed later, a derived type simply misses the table
    /// and degrades to <see cref="UnclassifiedParkPhrase"/> — vaguer, never leakier.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<Type, string> GovernedFailurePhrases = new Dictionary<Type, string>
    {
        [typeof(ReviewBarrierDeadlineException)] = "the review did not finish within its time budget",
        [typeof(ReviewCheckpointCorruptException)] = "the review checkpoint could not be read",
        [typeof(ReviewHostContractException)] = "the review host rejected the request",
        [typeof(SentinelUnauthorizedException)] = "the review host refused the daemon's credentials",
        // The four workspace conditions are distinguished because the operator response differs: a
        // re-clone, a cleanup, a path that must be un-redirected, and a probe that has to answer.
        [typeof(SlotNeedsRecloneException)] = "the review workspace could not be prepared and has to be re-created",
        [typeof(SlotCorruptException)] = "the review workspace could not be cleaned for use",
        [typeof(SlotAddressUnusableException)] = "the review workspace path could not be used safely",
        [typeof(SlotProbeUnansweredException)] = "the state of the review workspace could not be established",
    }.ToFrozenDictionary();

    /// <summary>
    /// Every phrase this build's <see cref="DescribeGovernedFailure"/> can produce — the mapped vocabulary plus
    /// the unclassified default — DERIVED from the one table rather than restated, so the allow-list cannot
    /// drift away from what parking actually writes.
    /// </summary>
    private static readonly FrozenSet<string> KnownParkPhrases = GovernedFailurePhrases
        .Values.Append(UnclassifiedParkPhrase)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The other half of a park reason's shape: the stage, which is the daemon's own enum vocabulary
    /// and never external text.</summary>
    private static readonly FrozenSet<string> KnownStageNames = Enum.GetNames<ReviewStage>()
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Chooses the park phrase for a governed failure. See <see cref="GovernedFailurePhrases"/>; the fallback
    /// exists because that set is expected to grow and an unmapped type must degrade to a vague phrase rather
    /// than fall back to a raw message.
    /// </summary>
    private static string DescribeGovernedFailure(Exception ex) =>
        GovernedFailurePhrases.TryGetValue(ex.GetType(), out var phrase) ? phrase : UnclassifiedParkPhrase;

    /// <summary>
    /// The park reason a REPLAYED notice is allowed to publish: the persisted value if this build's own
    /// vocabulary could have produced it, and <see cref="UnclassifiedParkPhrase"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an ALLOW-LIST, not a scrub. <see cref="DescribeGovernedFailure"/> sanitizes at the moment of
    /// PARKING, but the replay boundary re-publishes a column it did not write — written by some other build,
    /// possibly one whose parking path predates that sanitizer — into a public pull-request comment. The
    /// governed exception types carry raw external output in their messages (<see
    /// cref="ReviewHostContractException"/> embeds the review host's HTTP response body, <see
    /// cref="SlotCorruptException"/> embeds git's stderr), so what such a row holds is unknown historical text.
    /// A denylist over unknown text cannot be shown correct — there is no enumeration of what a raw message may
    /// contain — whereas an allow-list can: a value is republished only if it is one this build could have
    /// emitted. Anything else (legacy raw text, a truncated message, a phrase from a future build, blank)
    /// degrades to the neutral fallback.
    /// </para>
    /// <para>
    /// The exposure window is genuinely narrow: <c>park_reason</c> is introduced by migration v8 in this same
    /// change, so no shipped build ever wrote a raw value into it. That argument is about deployment history,
    /// though, and it stops holding the moment anyone writes the column from somewhere else — a repair script,
    /// an import, a future park path. A public-disclosure boundary should not rest on it.
    /// </para>
    /// <para>
    /// Cheap on purpose: this runs on every poll of every parked pull request, and it is two frozen-set hits
    /// over one index-of split.
    /// </para>
    /// </remarks>
    private static string TrustedParkReasonForReplay(string? persisted)
    {
        // The shape parking writes: "{stage}: {phrase}". Anything not of that shape was not written by it.
        const string StageSeparator = ": ";
        if (persisted is null)
        {
            return UnclassifiedParkPhrase;
        }

        var separator = persisted.IndexOf(StageSeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return UnclassifiedParkPhrase;
        }

        return
            KnownStageNames.Contains(persisted[..separator])
            && KnownParkPhrases.Contains(persisted[(separator + StageSeparator.Length)..])
            ? persisted
            : UnclassifiedParkPhrase;
    }

    /// <summary>
    /// Re-attempts a park notice that never reached the pull request, from the one place a parked run is still
    /// reached: the poll's park guard.
    /// </summary>
    /// <remarks>
    /// The park is committed BEFORE the notice is sent and <see cref="ReviewStore.TryMarkReviewRunParked"/>
    /// refuses a second park, so a crash or a publisher blip between the two used to lose the notice forever
    /// while the park itself persisted — a silently abandoned pull request. There is no outbox drain to lean
    /// on, but there is already a cadence: the poller still calls <see cref="RunAsync(ReviewRun,
    /// CancellationToken)"/> for an open PR every cycle and it lands here. The run is NOT resurrected — this
    /// runs inside the guard, before the stage loop, and returns the row untouched.
    /// <para>
    /// Exactly-once DELIVERY is already <see cref="ReviewPoster"/>'s: it treats a
    /// <see cref="OutboxStatus.Posted"/> row as a terminal replay no-op and never reaches the publisher, so
    /// nothing here has to count. <see cref="IsParkNoticeOutstanding"/> reads the same row one step earlier
    /// for a different reason — a parked pull request can stay open for weeks, and re-entering the notifier on
    /// every poll would spend an enqueue and an Information-level replay line each time to reach a decision the
    /// row already answered. It is the same row a crashed publish leaves in
    /// <see cref="OutboxStatus.Sending"/>, which is precisely the state that must still be retried.
    /// </para>
    /// <para>
    /// Everything here logs at Debug and swallows: at poll cadence a Warning would be an outage's worth of
    /// noise, and a failed courtesy comment must never fail the poll or unpark the run.
    /// </para>
    /// </remarks>
    private async Task RetryOutstandingParkNoticeAsync(ReviewRun run)
    {
        if (_parkNotifier is null || !IsParkNoticeOutstanding(run.Id))
        {
            return;
        }

        _logger.LogDebug(
            "Review run {RunId} is parked with no delivered park notice; re-attempting it on this poll.",
            run.Id
        );

        try
        {
            // NOT run.ParkReason: this boundary publishes a column it did not write, so it re-validates the
            // value against the vocabulary this build could have produced. See TrustedParkReasonForReplay for
            // why that is an allow-list. A row parked by a build that predates the reason column has none, and
            // a public comment that says something vague is still better than one that says something raw.
            await _parkNotifier.NotifyParkedAsync(
                run,
                TrustedParkReasonForReplay(run.ParkReason),
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review run {RunId}: the park notice retry did not deliver either.", run.Id);
        }
    }

    /// <summary>
    /// Whether this run's park notice still owes a delivery attempt — no outbox row for it, or one that never
    /// reached a terminal disposition. <see cref="OutboxStatus.Posted"/> means the comment is on the PR and
    /// <see cref="OutboxStatus.Collected"/> means the daemon deliberately recorded it without posting; every
    /// other state (including the <see cref="OutboxStatus.Sending"/> a crashed publish strands) is unfinished.
    /// </summary>
    private bool IsParkNoticeOutstanding(long runId) =>
        !_store
            .GetOutboxForRun(runId)
            .Any(entry =>
                string.Equals(entry.Operation, ReviewParkNotifier.PostParkNoticeOperation, StringComparison.Ordinal)
                && entry.Status is OutboxStatus.Posted or OutboxStatus.Collected
            );

    /// <summary>
    /// Whether a stage CLEARS the run's accumulated retry state when it succeeds. Every executed stage does,
    /// and it has to: a stage whose failures can charge the budget (see <see cref="IsGovernedFailure"/>, which
    /// now charges a slot-preparation failure wherever prep re-ran) must also be able to un-charge it, or one
    /// persistent-then-recovered prep would follow the run to a park it no longer deserves.
    /// <para>
    /// The narrow judgement lives in <see cref="IsGovernedFailure"/>, not here. That is the one that decides
    /// what a stuck run IS, and widening THAT is what turns ordinary transients into abandoned reviews.
    /// </para>
    /// </summary>
    internal string ClassifyDeliveryOutcome(ReviewRun run)
    {
        if (!string.Equals(run.Mode, "post", StringComparison.Ordinal))
        {
            return "collect-only";
        }

        if (_store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind) is { } artifact)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<ReviewArtifactPayload>(artifact.Payload);
                if (
                    payload?.ReviewText.TrimStart().StartsWith("No new findings", StringComparison.OrdinalIgnoreCase)
                    == true
                )
                {
                    return "no new findings — nothing posted";
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Run {RunId}: could not classify delivery from review artifact {ArtifactId}.",
                    run.Id,
                    artifact.Id
                );
            }
        }

        var delivery = _store
            .GetOutboxForRun(run.Id)
            .LastOrDefault(entry =>
                string.Equals(entry.Operation, ReviewPoster.PostReviewCommentOperation, StringComparison.Ordinal)
            );

        return delivery?.Status switch
        {
            OutboxStatus.Posted when !string.IsNullOrWhiteSpace(delivery.ProviderResponseId) => "posted",
            OutboxStatus.Collected => "collect-only",
            _ => "completed without provider-visible post evidence",
        };
    }

    private static bool IsGovernedStage(ReviewStage stage) =>
        stage is ReviewStage.ContextReady or ReviewStage.Reviewed or ReviewStage.Judged or ReviewStage.Posted;

    /// <summary>
    /// Whether <paramref name="ex"/> is a failure the governor should charge against the run's budget. Any
    /// ContextReady failure qualifies (the stuck-slot hot-loop). At Reviewed only four do:
    /// <see cref="ReviewBarrierDeadlineException"/> — the sub-agent completion barrier spent the review's whole
    /// absolute deadline waiting on a tree that never settled, so the next round would wait exactly as long on
    /// exactly the same tree; <see cref="ReviewCheckpointCorruptException"/>, where the stage cannot read
    /// the checkpoint that says whether a hosted tree is already running, and re-reading it will keep failing;
    /// <see cref="ReviewHostContractException"/>, where the review host cannot keep a message contract the
    /// turn depends on — an incompatibility that reproduces identically on every attempt, and whose attempts
    /// are not free (each one can leave another turn running on the host); and
    /// <see cref="SentinelUnauthorizedException"/>, where the review answered that nothing had changed on a PR
    /// holding no earlier review — a question answered from the STORE, so the next poll asks the same question
    /// of the same rows and refuses identically, having paid for a full fanned-out review to get there. All
    /// four are stuck reviews, not transients: they have to park eventually. A provider blip, a host 5xx or a
    /// blank synthesis stays outside the budget and keeps retrying.
    /// </summary>
    private static bool IsGovernedFailure(ReviewStage stage, Exception ex)
    {
        // Slot PREPARATION is governed wherever it runs, not only under the stage it usually runs under. The
        // slot lease lives in memory only, so a run that persisted Stage=ContextReady in an earlier process (a
        // restart, or a resume after RetryPending) arrives at Reviewed/Judged/Posted with no lease and
        // re-prepares a slot there. These are the same stuck-store conditions ContextReady already
        // parks — a store that will not clone, a tree that will not clean, a path that cannot be established
        // as contained — and none of them is made better by waiting one more poll interval. Tagged with a
        // later stage they used to escape the budget entirely and busy-loop forever (issue #218 item 7).
        // A cleanliness probe that will not answer joins them, and is the mildest of the four: nothing
        // re-clones or retires the slot for it, so it RETRIES by construction — which is exactly why it needs
        // the budget. A probe that loses its output on every attempt would otherwise busy-loop a stage that
        // can never make progress, and the transient case it exists for is retried and gone long before the
        // budget is reached.
        if (
            ex
            is SlotNeedsRecloneException
                or SlotCorruptException
                or SlotAddressUnusableException
                or SlotProbeUnansweredException
        )
        {
            return true;
        }

        return stage switch
        {
            ReviewStage.ContextReady => true,
            ReviewStage.Reviewed => ex
                is ReviewBarrierDeadlineException
                    or ReviewCheckpointCorruptException
                    or ReviewHostContractException
                    or SentinelUnauthorizedException,
            _ => false,
        };
    }

    /// <summary>Human-readable reason a PR was picked this cycle: a brand-new run is "new PR" (no prior
    /// review of this PR) or "new commit {sha}" (its head advanced past the last reviewed commit); an
    /// incomplete run being resumed after a restart/retry reports the stage it left off at.</summary>
    private string DescribePickReason(ReviewRun run)
    {
        if (run.Stage != ReviewStage.Discovered)
        {
            return $"resuming at {run.Stage}";
        }

        var prior = _store.GetPriorReviewSummary(run.RepoId, run.PrId, run.Id);
        if (prior.PrevHeadSha is null)
        {
            return "new PR";
        }

        if (!string.Equals(prior.PrevHeadSha, run.HeadSha, StringComparison.Ordinal))
        {
            var shortSha = run.HeadSha.Length >= 7 ? run.HeadSha[..7] : run.HeadSha;
            return $"new commit {shortSha}";
        }

        return "re-review";
    }
}
