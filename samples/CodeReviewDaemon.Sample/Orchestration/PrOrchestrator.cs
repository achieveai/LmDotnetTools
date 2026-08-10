using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Drives one review run through the <see cref="StageMachine"/> serially, persisting progress after
/// every stage so a crash resumes from the first incomplete step rather than re-doing work. Creation
/// is idempotent (the §6 identity tuple), and a PR observed as no longer open short-circuits to
/// completion. The per-stage work is delegated to <see cref="IReviewStageExecutor"/>.
/// </summary>
internal sealed class PrOrchestrator
{
    private readonly ReviewStore _store;
    private readonly IReviewStageExecutor _executor;
    private readonly ILogger<PrOrchestrator> _logger;
    private readonly ReviewProgressReporter? _progress;
    private readonly RetryGovernor? _retryGovernor;

    public PrOrchestrator(
        ReviewStore store,
        IReviewStageExecutor executor,
        ILogger<PrOrchestrator> logger,
        ReviewProgressReporter? progress = null,
        RetryGovernor? retryGovernor = null)
    {
        _store = store;
        _executor = executor;
        _logger = logger;
        _progress = progress;
        _retryGovernor = retryGovernor;
    }

    /// <summary>
    /// Ensures the run exists, then executes the stages still outstanding for it. Returns the run in
    /// its final state for this invocation.
    /// </summary>
    public async Task<ReviewRun> RunAsync(ReviewRun seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var run = _store.CreateOrGetReviewRun(seed);

        try
        {
            // The seed carries the freshest observed PR lifecycle; reconcile the persisted run with it.
            if (seed.PrLifecycleState != run.PrLifecycleState)
            {
                _store.UpdateReviewRunState(run.Id, run.Stage, run.WorkflowStatus, seed.PrLifecycleState);
                run = run with { PrLifecycleState = seed.PrLifecycleState };
            }

            // The seed also carries the freshest CONFIDENTIALITY TRUST SIGNAL, and nothing reconciled it.
            // Those flags are written by CreateOrGetReviewRun's INSERT and never touched again, so a run
            // created while the provider could not establish them replayed the stale answer on EVERY later
            // poll — resume had nothing to do with it. Live: run 144 got the cross-repo sibling gate CLOSED
            // with a 2-rule allow-list while runs 145 and 146, same binary and same repo, got it OPEN with 7.
            //
            // The refresh adopts the seed in BOTH directions, and the tightening direction is the one that
            // matters. Today's staleness is benign by luck — the stale value IS the fail-closed default, so
            // it withholds siblings — but the mechanism is not: a run seeded while a repo was same-trust
            // would otherwise keep that answer after the repo went public, and then staleness GRANTS access.
            // The seed's values already carry PrPollingService's `?? true` collapse, so an unknown arrives
            // here as the fail-closed value and this can never be more permissive than the evidence the
            // current poll actually carried.
            if (seed.IsForkPr != run.IsForkPr || seed.IsTargetRepoPublic != run.IsTargetRepoPublic)
            {
                _store.UpdateTrustSignal(run.Id, seed.IsForkPr, seed.IsTargetRepoPublic);
                run = run with { IsForkPr = seed.IsForkPr, IsTargetRepoPublic = seed.IsTargetRepoPublic };
            }

            // And the seed carries the freshest of WHAT THE PR SAYS IT DOES, which nothing reconciled either.
            // Author, title, description and target branch are written by the INSERT and never again, so the
            // brief's "Stated intent" block quotes the claim as it stood at DISCOVERY, however long ago that
            // was. Two facts, measured on .run/nova-review.db over 2026-08-06 → 2026-08-10:
            //   • PR titles do get rewritten mid-life. PR 5505154 was captured as "[WIP] Remove all references
            //     to the enableEmployeeDescriptiveAsPH flight…" by run 154 and, 2.5 h later, as the non-WIP
            //     "Remove EnableEmployeeDescriptiveAsPH flight references…" by run 169.
            //   • The freeze window is wide. Across the 251 runs that produced a review of record, creation to
            //     review ran median 9.6 min, mean 61.2, p90 155.8, max 2,095 (34.9 h).
            // What the store CANNOT show is a run that actually reviewed a superseded claim — because the
            // column was frozen, a row that went stale mid-run recorded nothing about it. The absence of that
            // evidence is a property of the defect, not of its rarity, and removing the freeze is what makes
            // the question answerable at all. "Does the diff do what it claims?" is the reviewer's first
            // question, and it should be asked about the claim the PR is currently making.
            //
            // The one place this must NOT copy the trust-signal refresh above: an absent value from the poll
            // never overwrites a captured one. The trust signals can be adopted unconditionally because
            // PrPollingService already collapsed "the provider could not tell" into a fail-closed bool, so the
            // seed always carries a decision. These four have no such collapse to lean on — a payload that
            // omitted the description arrives here as null, indistinguishable from an author who deleted it —
            // and of the two readings only one is recoverable. Keeping a stale description costs the reviewer
            // some freshness; erasing a captured one costs it the intent entirely, with nothing left to
            // re-read it from once the PR has closed.

            var freshAuthor = PreferFresh(seed.PrAuthor, run.PrAuthor);
            var freshTitle = PreferFresh(seed.PrTitle, run.PrTitle);
            var freshDescription = PreferFresh(seed.PrDescription, run.PrDescription);
            var freshTargetBranch = PreferFresh(seed.PrTargetBranch, run.PrTargetBranch);
            if (!string.Equals(freshAuthor, run.PrAuthor, StringComparison.Ordinal)
                || !string.Equals(freshTitle, run.PrTitle, StringComparison.Ordinal)
                || !string.Equals(freshDescription, run.PrDescription, StringComparison.Ordinal)
                || !string.Equals(freshTargetBranch, run.PrTargetBranch, StringComparison.Ordinal))
            {
                // Lengths only, and never the text: the title and description are the author's own words and
                // are EUII — the same rule the review-brief inventory line in DaemonReviewStageExecutor keeps.
                // Logged because a review read against a superseded intent is otherwise indistinguishable, in
                // every artifact the run leaves behind, from one read against the current one.
                if (!string.Equals(freshTitle, run.PrTitle, StringComparison.Ordinal)
                    || !string.Equals(freshDescription, run.PrDescription, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Review run {RunId}: PR {PrId}'s stated intent moved since it was captured — title "
                            + "{PriorTitleChars}→{TitleChars} chars, description "
                            + "{PriorDescriptionChars}→{DescriptionChars} chars. The review is judged against "
                            + "the fresh one.",
                        run.Id,
                        run.PrId,
                        run.PrTitle?.Length ?? 0,
                        freshTitle?.Length ?? 0,
                        run.PrDescription?.Length ?? 0,
                        freshDescription?.Length ?? 0);
                }

                _store.UpdatePrMetadata(run.Id, freshAuthor, freshTitle, freshDescription, freshTargetBranch);
                run = run with
                {
                    PrAuthor = freshAuthor,
                    PrTitle = freshTitle,
                    PrDescription = freshDescription,
                    PrTargetBranch = freshTargetBranch,
                };
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
                    "Review run {RunId} halted: PR {PrId} is {State}.", run.Id, run.PrId, run.PrLifecycleState);
                _store.UpdateReviewRunState(run.Id, run.Stage, WorkflowStatus.Completed, run.PrLifecycleState);
                _progress?.Finished(
                    run, $"halted (PR {run.PrLifecycleState})", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                return run with { WorkflowStatus = WorkflowStatus.Completed };
            }

            // Retry governance: a run that failed a recent poll is backing off, and one that exhausted its
            // attempts is parked — either way, skip this poll's attempt (leaving it RetryPending) instead of
            // the old ~30s hot-loop. Restart clears the in-memory state, so a restart retries everything.
            if (_retryGovernor is not null && !_retryGovernor.ShouldAttempt(run.Id))
            {
                return run;
            }

            // Claim the run for this process before any stage runs. WorkflowStatus.Running says "someone is
            // working on this"; the claim is what makes that statement checkable. Without it every Running
            // row looks unowned, and the startup reclaim (task 29) could not tell a run this daemon is
            // mid-review from one whose process died — taking the wrong one puts two processes on the same
            // PR, writing into the same notes branch.
            _store.ClaimReviewRun(run.Id, DaemonInstance.Id, DateTimeOffset.UtcNow);

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
                    }
                    _logger.LogError(ex, "Review run {RunId} failed at stage {Stage}.", run.Id, stage);
                    _progress?.Finished(
                        run, $"failed at {stage}", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
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
                }

                var workflowStatus = StageMachine.IsComplete(stage) ? WorkflowStatus.Completed : WorkflowStatus.Running;
                _store.UpdateReviewRunState(run.Id, stage, workflowStatus, run.PrLifecycleState);
                run = run with { Stage = stage, WorkflowStatus = workflowStatus };
            }

            _progress?.Finished(
                run,
                $"complete ({ClassifyDeliveryOutcome(run)})",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
            return run;
        }
        finally
        {
            // Drop this process's ownership claim on EVERY exit from the run — completion, the
            // PR-not-open short-circuit, and the failure→RetryPending rethrow alike. A claim left behind
            // by a process that has stopped working the run would make it look live to the next startup's
            // reclaim, stranding it for a whole stale window; one left behind permanently would strand it
            // for good, which is the leak this exists to close. Owner-scoped, so this can only ever drop
            // a claim this process holds.
            _store.ReleaseReviewRun(run.Id, DaemonInstance.Id);

            // Guarantee a pooled review slot is returned on EVERY terminal outcome of this run — normal
            // completion (where the Posted stage already returned it, so this is a no-op), the PR-not-open
            // short-circuit, and the failure→RetryPending rethrow — so a run that never reaches Posted can
            // never leak pool capacity. Uses CancellationToken.None so a cancelled run still returns its slot.
            await _executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Whether a stage's outcomes are accounted by the <see cref="RetryGovernor"/> at all. Deliberately tiny:
    /// the governor exists to park work that CANNOT self-heal by being retried on the poll interval, and
    /// widening it turns ordinary transients into abandoned reviews.
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
                var payload = JsonSerializer.Deserialize<ReviewArtifactPayload>(
                    artifact.Payload, DaemonReviewStageExecutor.PayloadOptions);
                // Through the executor's predicate, not a second copy of it. This was an inlined StartsWith,
                // which reported "nothing posted" for any review whose opening words happened to be the exit
                // phrase — a body full of BLOCKERs included. Two constructions of one rule drift, and the
                // drift shows up as a delivery outcome that contradicts what the run actually did.
                if (DaemonReviewStageExecutor.IsNoNewFindingsSentinel(payload?.ReviewText))
                {
                    return "no new findings — nothing posted";
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Run {RunId}: could not classify delivery from review artifact {ArtifactId}.", run.Id, artifact.Id);
            }
        }

        var delivery = _store.GetOutboxForRun(run.Id)
            .LastOrDefault(entry => string.Equals(
                entry.Operation,
                ReviewPoster.PostReviewCommentOperation,
                StringComparison.Ordinal));

        return delivery?.Status switch
        {
            OutboxStatus.Posted when !string.IsNullOrWhiteSpace(delivery.ProviderResponseId) => "posted",
            OutboxStatus.Collected => "collect-only",
            _ => "completed without provider-visible post evidence",
        };
    }

    private static bool IsGovernedStage(ReviewStage stage) =>
        stage is ReviewStage.ContextReady or ReviewStage.Reviewed;

    /// <summary>
    /// Which value of a stated-intent field the run should carry: the poll's, when the poll actually carried
    /// one, and otherwise whatever was already captured. Whitespace counts as nothing carried — an empty
    /// string on the run row renders as a present-but-blank intent, and "the author wrote no description" is
    /// precisely the thing these fields have to stay distinguishable from.
    /// <para>
    /// This is a one-way ratchet on presence, not on content: a poll that carries a value always wins, so a
    /// genuine edit is adopted immediately; only ABSENCE is refused. The cost is that an author who clears a
    /// description leaves the run holding the previous one. That is the deliberate side to be wrong on —
    /// nothing later can re-fetch a description off a PR that has closed.
    /// </para>
    /// </summary>
    private static string? PreferFresh(string? fromPoll, string? captured) =>
        string.IsNullOrWhiteSpace(fromPoll) ? captured : fromPoll;

    /// <summary>
    /// Whether <paramref name="ex"/> is a failure the governor should charge against the run's budget. Any
    /// ContextReady failure qualifies (the stuck-slot hot-loop). At Reviewed only three do:
    /// <see cref="ReviewBarrierDeadlineException"/> — the sub-agent completion barrier spent the review's whole
    /// absolute deadline waiting on a tree that never settled, so the next round would wait exactly as long on
    /// exactly the same tree; <see cref="ReviewCheckpointCorruptException"/>, where the stage cannot read
    /// the checkpoint that says whether a hosted tree is already running, and re-reading it will keep failing;
    /// and <see cref="ReviewHostContractException"/>, where the review host cannot keep a message contract the
    /// turn depends on — an incompatibility that reproduces identically on every attempt, and whose attempts
    /// are not free (each one can leave another turn running on the host). All three are stuck reviews, not
    /// transients: they have to park eventually. A provider blip, a host 5xx or a blank synthesis stays
    /// outside the budget and keeps retrying.
    /// </summary>
    private static bool IsGovernedFailure(ReviewStage stage, Exception ex) => stage switch
    {
        ReviewStage.ContextReady => true,
        ReviewStage.Reviewed => ex
            is ReviewBarrierDeadlineException
                or ReviewCheckpointCorruptException
                or ReviewHostContractException,
        _ => false,
    };

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
