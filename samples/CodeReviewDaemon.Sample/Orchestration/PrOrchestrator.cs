using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

internal sealed class WorkflowAttemptFailedException()
    : InvalidOperationException("The authored review workflow reported a determinate failure.");

/// <summary>
/// Serializes admission of authored workflow instances for a pull request and preserves durable retry,
/// parking, and progress reporting around the workflow runtime.
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
    private readonly IReviewWorkflowRunner _workflowRunner;
    private readonly ILogger<PrOrchestrator> _logger;
    private readonly ReviewProgressReporter? _progress;
    private readonly RetryGovernor? _retryGovernor;
    private readonly int _maxDurableRetryAttempts;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IReviewParkNotifier? _parkNotifier;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _workflowAdmissions = new();

    public PrOrchestrator(
        ReviewStore store,
        IReviewWorkflowRunner workflowRunner,
        ILogger<PrOrchestrator> logger,
        ReviewProgressReporter? progress = null,
        RetryGovernor? retryGovernor = null,
        int maxDurableRetryAttempts = 10,
        Func<DateTimeOffset>? clock = null,
        IReviewParkNotifier? parkNotifier = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDurableRetryAttempts, 1);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workflowRunner = workflowRunner ?? throw new ArgumentNullException(nameof(workflowRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progress = progress;
        _retryGovernor = retryGovernor;
        _maxDurableRetryAttempts = maxDurableRetryAttempts;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _parkNotifier = parkNotifier;
    }

    /// <summary>Resumes a durable authored workflow selected by the stranded-run reconciler.</summary>
    public Task<ReviewRun> ReconcileAsync(ReviewRun seed, CancellationToken cancellationToken) =>
        ResumeWorkflowAsync(seed, cancellationToken);

    public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) =>
        _workflowRunner.RecoverActiveWorkspacesAsync(cancellationToken);

    public Task<JsonObject> ReadFrozenContextAsync(
        ReviewRun run,
        WorkflowRound? round,
        CancellationToken cancellationToken
    ) => _workflowRunner.ReadFrozenContextAsync(run, round, cancellationToken);

    public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => _workflowRunner.HasFrozenContext(run, round);

    /// <summary>Runs or resumes the authored new-head workflow against its immutable context.</summary>
    public async Task<ReviewRun> RunAsync(ReviewRun seed, JsonObject frozenContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(frozenContext);
        var run = _store.CreateOrGetReviewRun(seed);
        var admission = _workflowAdmissions.GetOrAdd(run.Id, static _ => new SemaphoreSlim(1, 1));
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? startedAt = null;
        try
        {
            if (run.ParkedAt is not null)
            {
                await RetryOutstandingParkNoticeAsync(run).ConfigureAwait(false);
                return run;
            }
            if (run.WorkflowStatus == WorkflowStatus.Completed)
            {
                return run;
            }
            if (seed.PrLifecycleState != run.PrLifecycleState)
            {
                _store.UpdateReviewRunState(run.Id, run.Stage, run.WorkflowStatus, seed.PrLifecycleState);
                run = run with { PrLifecycleState = seed.PrLifecycleState };
            }
            if (
                run.PrLifecycleState != PrLifecycleState.Open
                || (_retryGovernor is not null && !_retryGovernor.ShouldAttempt(run.Id))
            )
            {
                return run;
            }

            startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _progress?.Picked(run, DescribePickReason(run));
            var status = await _workflowRunner
                .RunOrResumeAsync(run, null, frozenContext, cancellationToken)
                .ConfigureAwait(false);
            var finished = await FinishWorkflowAttemptAsync(run, status).ConfigureAwait(false);
            _progress?.Finished(
                finished,
                status == WorkflowInvocationStatus.Completed
                    ? $"complete ({ClassifyDeliveryOutcome(finished)})"
                    : status.ToString(),
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt.Value)
            );
            return finished;
        }
        catch (WorkspaceCapacityUnavailableException)
        {
            _logger.LogDebug("Review workflow for run {RunId} deferred because no workspace is available.", run.Id);
            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.RetryPending, run.PrLifecycleState);
            _retryGovernor?.RecordFailure(run.Id, ex.Message);
            _logger.LogError(ex, "Review workflow for run {RunId} failed.", run.Id);
            if (startedAt.HasValue)
            {
                _progress?.Finished(run, "failed", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt.Value));
            }
            throw;
        }
        finally
        {
            admission.Release();
        }
    }

    private async Task<ReviewRun> ResumeWorkflowAsync(ReviewRun seed, CancellationToken cancellationToken)
    {
        var run = _store.CreateOrGetReviewRun(seed);
        var admission = _workflowAdmissions.GetOrAdd(run.Id, static _ => new SemaphoreSlim(1, 1));
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (run.ParkedAt is not null)
            {
                await RetryOutstandingParkNoticeAsync(run).ConfigureAwait(false);
                return run;
            }
            var status = await _workflowRunner.ResumeAsync(run, null, cancellationToken).ConfigureAwait(false);
            return await FinishWorkflowAttemptAsync(run, status).ConfigureAwait(false);
        }
        catch (WorkspaceCapacityUnavailableException)
        {
            _logger.LogDebug("Review workflow for run {RunId} deferred because no workspace is available.", run.Id);
            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.RetryPending, run.PrLifecycleState);
            _retryGovernor?.RecordFailure(run.Id, ex.Message);
            _logger.LogError(ex, "Review workflow reconciliation for run {RunId} failed.", run.Id);
            throw;
        }
        finally
        {
            admission.Release();
        }
    }

    /// <summary>Serializes a supplementary discussion or merged round with its new-head run.</summary>
    public async Task<WorkflowInvocationStatus> RunRoundAsync(
        ReviewRun run,
        WorkflowRound round,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(round);
        var admission = _workflowAdmissions.GetOrAdd(run.Id, static _ => new SemaphoreSlim(1, 1));
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestedInstanceId = $"review-round-{round.Id}";
            var ownerStatus = await _workflowRunner
                .ReconcileActiveOwnerAsync(run, requestedInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (ownerStatus == WorkflowInvocationStatus.Unknown)
            {
                return WorkflowInvocationStatus.Unknown;
            }
            var frozen =
                JsonNode.Parse(round.FrozenInputJson) as JsonObject
                ?? throw new InvalidDataException("Workflow round frozen context is invalid.");
            return await _workflowRunner.RunAsync(run, round, frozen, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            admission.Release();
        }
    }

    private async Task<ReviewRun> FinishWorkflowAttemptAsync(ReviewRun run, WorkflowInvocationStatus status)
    {
        if (status == WorkflowInvocationStatus.Completed)
        {
            _retryGovernor?.RecordSuccess(run.Id);
            _store.ClearGovernedFailureCount(run.Id);
            return _store.GetReviewRun(run.Id)
                ?? throw new InvalidOperationException("Completed review run disappeared.");
        }
        _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.RetryPending, run.PrLifecycleState);
        if (status == WorkflowInvocationStatus.Failed)
        {
            var failure = new WorkflowAttemptFailedException();
            _retryGovernor?.RecordFailure(run.Id, failure.Message);
            await ChargeDurableBudgetAsync(run, run.Stage, failure).ConfigureAwait(false);
        }
        return _store.GetReviewRun(run.Id)
            ?? throw new InvalidOperationException("Review run disappeared after its workflow attempt.");
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
    /// The types it maps are determinate failures that can consume the durable retry budget.
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
        [typeof(ReviewHostContractException)] = "the review host rejected the request",
        [typeof(WorkflowAttemptFailedException)] = "the authored review workflow reported a determinate failure",
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
    /// on, but there is already a cadence: the poller still admits an open PR every cycle and lands here. The
    /// run remains parked; this method only retries its outstanding idempotent notice.
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

    /// <summary>Describes the durable provider-visible outcome used by completion progress reporting.</summary>
    internal string ClassifyDeliveryOutcome(ReviewRun run)
    {
        if (!string.Equals(run.Mode, "post", StringComparison.Ordinal))
        {
            return "collect-only";
        }

        if (_store.TryGetLatestArtifact(run.Id, ReviewArtifactKinds.ReviewArtifactKind) is { } artifact)
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
