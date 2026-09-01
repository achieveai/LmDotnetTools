using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The <see cref="PrOrchestrator"/> ↔ <see cref="RetryGovernor"/> wiring: a run that keeps failing backs off
/// (skipped until eligible) and is parked after K attempts (skipped indefinitely), replacing the old ~30s
/// hot-loop that re-ran a stuck run every poll. Driven with a mutable fake clock and an always-failing
/// executor.
/// </summary>
public sealed class PrOrchestratorRetryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly ReviewStore _store;
    private DateTimeOffset _now = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    public PrOrchestratorRetryTests() => _store = new ReviewStore(_db.ConnectionString);

    public void Dispose()
    {
        _store.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_parked_run_is_skipped_on_the_next_poll_without_calling_the_executor()
    {
        var governor = Governor(maxAttempts: 1);
        var executor = new CountingFailingExecutor();
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        // First poll: the stage throws → recorded as a failure → parked (maxAttempts=1).
        var first = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCalls.Should().Be(1);

        // Second poll: the run is parked, so the orchestrator skips it entirely.
        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.ExecuteCalls.Should().Be(1, "a parked run is not attempted again until a new commit or restart");
    }

    [Fact]
    public async Task A_parked_run_handed_to_the_reconcile_entry_is_attempted_again()
    {
        // The reconciler is the only route back for a run the poll no longer enumerates, and a parked run is
        // exactly such a run. Through the poll entry the governor refused it before any stage ran, so the
        // "resume" did no work, wrote nothing, and left the row stranded for the next pass to pick up and
        // refuse identically — a permanent loop that also burned one of the pass's capped resume slots each
        // time. The reconcile entry is where the caller's decision to spend another attempt is honoured.
        var governor = Governor(maxAttempts: 1);
        var executor = new CountingFailingExecutor();
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var poll = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await poll.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCalls.Should().Be(1);

        var reconcile = async () => await orchestrator.ReconcileAsync(run, CancellationToken.None);
        await reconcile.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCalls.Should().Be(2, "the reconcile entry clears the park so the stage actually runs");
    }

    [Fact]
    public async Task The_reconcile_entry_does_not_leave_the_park_lifted_for_the_poll()
    {
        // Reviving a run is a decision the CALLER makes per resume, not a downgrade of the policy: if a
        // reconciled run fails again it must re-park, or the ~30s hot-loop the governor exists to bound comes
        // back through the reconciler's door.
        var governor = Governor(maxAttempts: 1);
        var executor = new CountingFailingExecutor();
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var reconcile = async () => await orchestrator.ReconcileAsync(run, CancellationToken.None);
        await reconcile.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCalls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.ExecuteCalls.Should().Be(1, "the failed reconcile re-parked the run against the poll path");
    }

    [Fact]
    public async Task A_backing_off_run_is_skipped_until_the_backoff_elapses()
    {
        var governor = Governor(maxAttempts: 5);
        var executor = new CountingFailingExecutor();
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCalls.Should().Be(1);

        // Still within the 30s backoff → skipped, the executor is not called again.
        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.ExecuteCalls.Should().Be(1);

        // Backoff elapsed → attempted again (and fails again).
        _now = _now.AddSeconds(31);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCalls.Should().Be(2, "after the backoff elapses the run is attempted again");
    }

    [Fact]
    public async Task A_failure_after_ContextReady_is_not_parked_by_the_context_retry_budget()
    {
        // The RetryGovernor bounds ONLY the ContextReady hot-loop. A later-stage failure (e.g. a Posted-stage
        // lock that the next lease's clean-on-entry heals) must NOT consume the context-retry budget, so even
        // at maxAttempts=1 the run is attempted again rather than parked.
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(ReviewStage.Posted);
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        executor.FailStageCalls.Should().Be(1);

        // Not parked (a ContextReady failure at maxAttempts=1 WOULD be): the Posted stage is attempted again.
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        executor
            .FailStageCalls.Should()
            .Be(2, "a non-ContextReady failure is not governed by the context-retry budget");
    }

    [Fact]
    public async Task A_barrier_deadline_at_Reviewed_is_charged_to_the_retry_budget()
    {
        // A review whose sub-agent tree never settled inside the stage's whole absolute deadline would wait
        // exactly as long on exactly the same tree next poll. That is a stuck review, so it has to park —
        // the same reason the ContextReady hot-loop is governed.
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        executor.FailStageCalls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.FailStageCalls.Should().Be(1, "the budget is spent, so the stuck review is parked rather than re-run");
    }

    [Fact]
    public async Task An_unreadable_review_checkpoint_is_charged_to_the_retry_budget()
    {
        // An unreadable checkpoint cannot heal itself: the artifact is append-only, so every poll reads the
        // same broken row and refuses the same way. Ungoverned, that is an unbounded loop; ignoring the row
        // instead would be worse, since it may describe a sub-agent tree still running on the host and each
        // round would fan out another on top of it. Bounded attempts then park is the only terminating option.
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(
            ReviewStage.Reviewed,
            () => new ReviewCheckpointCorruptException("unreadable", new FormatException())
        );
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewCheckpointCorruptException>();
        executor.FailStageCalls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.FailStageCalls.Should().Be(1, "the budget is spent, so the run is parked rather than retried forever");
    }

    [Fact]
    public async Task A_review_host_contract_failure_at_Reviewed_is_charged_to_the_retry_budget()
    {
        // A host that cannot keep the message contracts the turn depends on refuses identically every poll —
        // the deployment does not change between two 30s polls. Ungoverned that is an unbounded loop, and its
        // attempts are not free: each one can leave another turn running on the host. Bounded then parked.
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(
            ReviewStage.Reviewed,
            () => new ReviewHostContractException("host predates message idempotency")
        );
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewHostContractException>();
        executor.FailStageCalls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.FailStageCalls.Should().Be(1, "the budget is spent, so the skewed host is parked rather than re-run");
    }

    [Fact]
    public async Task An_ordinary_failure_at_Reviewed_is_not_charged_to_the_retry_budget()
    {
        // Everything else that can fail a review — a provider blip, a host 5xx, a blank synthesis — is
        // usually transient. Charging those to the budget would park recoverable reviews on a bad minute.
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new InvalidOperationException("host 503"));
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        await attempt.Should().ThrowAsync<InvalidOperationException>();

        executor.FailStageCalls.Should().Be(2, "a transient review failure keeps retrying on the poll interval");
    }

    /// <summary>
    /// Issue #218 item 7 — slot preparation does not only run under ContextReady. The slot lease lives in
    /// memory only, so a run that persisted Stage=ContextReady in an earlier process (a daemon restart, or a
    /// resume after RetryPending) arrives at Reviewed/Judged/Posted with no lease and RE-PREPARES a slot
    /// there. The prep failures are the same stuck-store conditions ContextReady governs — a store that will
    /// not clone, a path that cannot be established as contained — but under a later stage tag they escaped
    /// the budget entirely and busy-looped every poll forever.
    /// <para>
    /// Governance follows the FAILURE, not the stage that happened to host it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(nameof(ReviewStage.Reviewed))]
    [InlineData(nameof(ReviewStage.Judged))]
    [InlineData(nameof(ReviewStage.Posted))]
    public async Task A_slot_prep_failure_is_charged_to_the_retry_budget_at_every_stage_that_re_prepares(
        string stageName
    )
    {
        var stage = Enum.Parse<ReviewStage>(stageName);
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(stage, () => new SlotNeedsRecloneException("store has no .git"));
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<SlotNeedsRecloneException>();
        executor.FailStageCalls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor
            .FailStageCalls.Should()
            .Be(1, "a store that cannot be prepared is stuck wherever the prep ran, so it must park, not busy-loop");
    }

    /// <summary>
    /// All four slot-recovery conditions are the same class of stuck: re-cloning, re-addressing or a probe
    /// that will answer is what they need, and nothing about waiting one more poll interval supplies it. The
    /// fourth is the mildest — nothing re-clones or retires the slot for an unanswered probe, so it retries by
    /// construction, which is exactly why it must be BOUNDED: a probe that loses its output every time would
    /// otherwise busy-loop a stage that can never make progress.
    /// </summary>
    [Theory]
    [InlineData("reclone")]
    [InlineData("corrupt")]
    [InlineData("address")]
    [InlineData("unanswered")]
    public async Task Every_slot_recovery_failure_at_a_later_stage_is_charged_to_the_retry_budget(string kind)
    {
        Func<Exception> error = kind switch
        {
            "reclone" => () => new SlotNeedsRecloneException("store has no .git"),
            "corrupt" => () => new SlotCorruptException("stale index.lock survived cleaning"),
            "unanswered" => () => new SlotProbeUnansweredException("the cleanliness probe returned no answer"),
            _ => () => new SlotAddressUnusableException("store path is a junction"),
        };
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(ReviewStage.Judged, error);
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<Exception>();
        executor.FailStageCalls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        executor.FailStageCalls.Should().Be(1, "every slot-recovery condition parks rather than busy-looping");
    }

    /// <summary>
    /// The widened budget must not park work that heals itself. A transient at a later stage still retries
    /// every poll, exactly as before — the distinction is the exception type, not the stage.
    /// </summary>
    [Fact]
    public async Task A_transient_failure_at_Posted_still_retries_after_the_slot_prep_widening()
    {
        var governor = Governor(maxAttempts: 1);
        var executor = new FailsAtStageExecutor(ReviewStage.Posted, () => new InvalidOperationException("502"));
        var orchestrator = new PrOrchestrator(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        await attempt.Should().ThrowAsync<InvalidOperationException>();

        executor.FailStageCalls.Should().Be(2, "a transient posting failure must not be parked");
    }

    // ── the durable budget and the permanent park ─────────────────────────────────────────────────
    //
    // The in-memory budget above is real but erasable: StrandedRunReconciler resumes a stuck run roughly
    // every 45 minutes through ReconcileAsync, which resets the governor for that run. Measured on the mcqdb
    // daemon — 19 parks on 2026-08-28, then zero from 08-29 onward, the transition landing exactly on the
    // reconciler's first resume, and three pull requests re-reviewed for 33 hours at 30 minutes of model
    // work each. These pin the bound that the reset cannot reach.

    [Fact]
    public async Task A_run_parks_permanently_after_the_durable_attempt_budget_is_spent()
    {
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 2);
        var run = SeedRun();
        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);

        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        _store.GetReviewRun(run.Id)!.ParkedAt.Should().BeNull("one failure has not spent a budget of two");

        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();

        var parked = _store.GetReviewRun(run.Id)!;
        parked.ParkedAt.Should().Be(_now, "the second failure is the one that spends the budget");
        parked
            .WorkflowStatus.Should()
            .Be(
                WorkflowStatus.Failed,
                "Failed is the ops-visible half of a park — the row must not still read as work owed a retry"
            );
        parked.ParkReason.Should().Contain(nameof(ReviewStage.Reviewed));
        executor.FailStageCalls.Should().Be(2, "the budget bought exactly two attempts");
    }

    [Fact]
    public async Task A_reconcile_entry_cannot_revive_a_permanently_parked_run()
    {
        // THE REGRESSION. ReconcileAsync resets the RetryGovernor for the run it is handed — by design, so an
        // operator or the reconciler can spend another attempt — and that reset is what erased the only bound
        // there was. A permanent park has to survive it, or the fix is the old one wearing a new column.
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 1);
        var run = SeedRun();

        var poll = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await poll.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        _store.GetReviewRun(run.Id)!.ParkedAt.Should().NotBeNull();

        _ = await orchestrator.ReconcileAsync(run, CancellationToken.None);

        executor
            .FailStageCalls.Should()
            .Be(
                1,
                "the reconciler's resume must not run a stage on a parked run — asserting only that the row is "
                    + "still parked would pass even if the whole 30-minute review had just re-run"
            );
    }

    [Fact]
    public async Task The_governor_reset_does_not_clear_the_durable_failure_count()
    {
        // The bug itself, stated as a property. Every attempt below arrives through ReconcileAsync, which
        // resets the in-memory governor each time, so the in-memory count is 1 forever. The durable one has to
        // keep climbing anyway — that is the entire difference between the two.
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 3, governor: Governor(maxAttempts: 5));
        var run = SeedRun();
        var reconcile = async () => await orchestrator.ReconcileAsync(run, CancellationToken.None);

        await reconcile.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        _store.GetReviewRun(run.Id)!.GovernedFailureCount.Should().Be(1);

        await reconcile.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        _store
            .GetReviewRun(run.Id)!
            .GovernedFailureCount.Should()
            .Be(2, "a resume grants an attempt; it does not forgive the ones already spent");

        await reconcile.Should().ThrowAsync<ReviewBarrierDeadlineException>();

        _store.GetReviewRun(run.Id)!.GovernedFailureCount.Should().Be(3);
        _store
            .GetReviewRun(run.Id)!
            .ParkedAt.Should()
            .NotBeNull("three resumes reached the budget, which the erasable count never could");
    }

    [Fact]
    public async Task A_governed_stage_success_clears_the_durable_failure_count()
    {
        // The un-charge path. Without it a run that failed persistently and then RECOVERED still carries those
        // failures toward a park it no longer deserves — one bad afternoon followed by a healthy week would
        // still end permanently parked. Mirrors the in-memory contract (RecordSuccess) exactly.
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 5);
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        _store.GetReviewRun(run.Id)!.GovernedFailureCount.Should().Be(1, "the budget was genuinely charged");

        executor.Enabled = false;
        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        _store
            .GetReviewRun(run.Id)!
            .GovernedFailureCount.Should()
            .Be(0, "the stage that was failing has now succeeded, so nothing is owed against the budget");
    }

    [Fact]
    public async Task Only_a_governed_failure_charges_the_durable_budget()
    {
        // The blast radius. A provider blip, a host 5xx or a blank synthesis is not a stuck review, and
        // charging it would permanently park recoverable work on a bad minute. The budget is 1, so anything
        // that charged at all would park on the first failure.
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new InvalidOperationException("host 503"));
        var orchestrator = Orchestrator(executor, durableBudget: 1);
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        await attempt.Should().ThrowAsync<InvalidOperationException>();

        var after = _store.GetReviewRun(run.Id)!;
        after.GovernedFailureCount.Should().Be(0, "an ungoverned failure spends none of the budget");
        after.ParkedAt.Should().BeNull();
        executor.FailStageCalls.Should().Be(2, "a transient review failure keeps retrying on the poll interval");
    }

    [Fact]
    public async Task Parking_is_once_only_and_notifies_once()
    {
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var notifier = new RecordingParkNotifier(_store);
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: notifier);
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();

        notifier.Notified.Should().ContainSingle().Which.Should().Be(run.Id);

        // The store is where once-only actually lives, and it is what a second park attempt would meet — a
        // crash between the park write and the notice, or a future caller reaching the park path twice. The
        // notice hangs off this boolean, so a park that reported success twice would post twice.
        _store
            .TryMarkReviewRunParked(run.Id, _now.AddHours(1), "a second park attempt")
            .Should()
            .BeFalse("the row is already parked, and the first park's instant and reason are the true ones");
        _store.GetReviewRun(run.Id)!.ParkedAt.Should().Be(_now, "a re-park must not move the park instant");

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        notifier.Notified.Should().ContainSingle("a later poll of a parked run announces nothing new");
    }

    // ── the park NOTICE: who it reaches, and what it is allowed to say ────────────────────────────

    [Fact]
    public async Task An_azure_devops_park_notice_reaches_the_publisher_that_serves_it()
    {
        // RepoIdentity.Provider is the STORAGE namespace, so an ADO repo row reads "azure-devops", while
        // AdoReviewCommentPublisher.Provider answers to "ado". Compared raw, the lookup resolved to nothing on
        // every Azure DevOps pull request: the park was durable, the daemon logged a warning nobody reads, and
        // the PR was silently abandoned. The github case cannot catch this — it is the same word in both.
        var publisher = new FakeReviewCommentPublisher("ado");
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: Notifier(publisher));
        var run = SeedRun(provider: "azure-devops");

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();

        publisher.PostCount.Should().Be(1, "the ADO publisher is the one registered for this repo's provider");
        _store
            .GetOutboxForRun(run.Id)
            .Should()
            .ContainSingle(entry =>
                string.Equals(entry.Operation, ReviewParkNotifier.PostParkNoticeOperation, StringComparison.Ordinal)
            )
            .Which.Provider.Should()
            .Be(
                "ado",
                "the review's own key is built from DaemonReviewStageExecutor.ResolveRepo's MAPPED provider, so "
                    + "a notice keyed on the stored spelling would sit in a namespace nothing else for this PR uses"
            );
    }

    [Fact]
    public async Task A_park_notice_that_failed_to_deliver_is_retried_on_a_later_poll()
    {
        // The park is committed BEFORE the notice is sent and TryMarkReviewRunParked refuses a second park, so
        // a publisher blip in between used to lose the notice permanently while the park persisted. The poller
        // still calls RunAsync for an open PR every cycle and lands in the park guard — that is the only retry
        // cadence available, and it must not resurrect the run to use it.
        var publisher = new FakeReviewCommentPublisher { PostFailure = new InvalidOperationException("provider 503") };
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: Notifier(publisher));
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        publisher.PostCount.Should().Be(0, "the first delivery attempt threw inside the publisher");
        _store.GetReviewRun(run.Id)!.ParkedAt.Should().NotBeNull("the park is durable regardless of the notice");

        publisher.PostFailure = null;
        var after = await orchestrator.RunAsync(run, CancellationToken.None);

        publisher.PostCount.Should().Be(1, "the park guard re-attempts a notice that never reached the PR");
        after.ParkedAt.Should().NotBeNull("delivering the notice must not unpark the run");
        executor.FailStageCalls.Should().Be(1, "no stage may execute on the poll that retries the notice");
    }

    [Fact]
    public async Task A_delivered_park_notice_is_never_sent_a_second_time()
    {
        // Exactly-once across arbitrarily many polls, pinned at both layers it is owned by. ReviewPoster is
        // what makes a REPEATED delivery harmless — a Posted outbox row is a terminal replay no-op that never
        // reaches the publisher — and the park guard is what stops the poll re-entering the notifier at all,
        // which a PR parked for weeks needs so the retry does not cost an enqueue and a log line every cycle.
        var publisher = new FakeReviewCommentPublisher();
        var notifier = new CountingParkNotifier(Notifier(publisher));
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: notifier);
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        publisher.PostCount.Should().Be(1);
        publisher.FindCallCount.Should().Be(1, "the delivering attempt ran the backstop scan once");
        notifier.Calls.Should().Be(1);

        _ = await orchestrator.RunAsync(run, CancellationToken.None);
        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        publisher.PostCount.Should().Be(1, "the notice is on the PR; a second one would be spam");
        publisher
            .FindCallCount.Should()
            .Be(1, "ReviewPoster's terminal replay short-circuits before the provider-side backstop scan");
        notifier
            .Calls.Should()
            .Be(1, "the outbox already says the notice landed, so the park guard must not re-enter the notifier");
    }

    [Fact]
    public async Task A_park_reason_carries_a_fixed_phrase_and_never_the_exception_text()
    {
        // park_reason is persisted AND posted to a public pull request, and the governed exception types carry
        // raw external output in their messages — ReviewHostContractException embeds the review host's HTTP
        // response body, SlotCorruptException embeds git's stderr. The vocabulary is chosen by TYPE so nothing
        // the outside world wrote can be republished.
        const string Sentinel = "SENTINEL-SECRET-abc123";
        var publisher = new FakeReviewCommentPublisher();
        var executor = new FailsAtStageExecutor(
            ReviewStage.Reviewed,
            () => new ReviewHostContractException($"host replied 401 with body {Sentinel}")
        );
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: Notifier(publisher));
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewHostContractException>();

        var parked = _store.GetReviewRun(run.Id)!;
        parked.ParkReason.Should().NotContain(Sentinel, "the exception's own text must never be persisted here");
        parked.ParkReason.Should().Contain("the review host rejected the request", "the phrase is chosen by type");
        parked.ParkReason.Should().Contain(nameof(ReviewStage.Reviewed), "the stage is the daemon's own vocabulary");
        publisher
            .PostedBodies.Should()
            .ContainSingle()
            .Which.Should()
            .NotContain(Sentinel, "the notice is a public comment on the pull request");
    }

    [Fact]
    public async Task A_replayed_park_notice_refuses_a_park_reason_the_current_vocabulary_could_not_have_written()
    {
        // The park path sanitizes; the REPLAY path re-publishes a column it did not write. A row written by any
        // build whose parking path predates that sanitizer holds raw exception text — the review host's HTTP
        // response body, git's stderr — and forwarding it here puts it in a public pull-request comment. The
        // boundary therefore validates against the vocabulary THIS build can produce, so anything else degrades
        // to the neutral phrase.
        const string Sentinel = "SENTINEL-LEGACY-xyz789";
        var publisher = new FakeReviewCommentPublisher { PostFailure = new InvalidOperationException("provider 503") };
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: Notifier(publisher));
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        publisher.PostCount.Should().Be(0, "the first delivery attempt threw inside the publisher, so it is owed");

        // Raw SQL, not TryMarkReviewRunParked: the store's park write is fed by the sanitizer, so a value put
        // there through it would be laundered into something the allow-list accepts and prove nothing. This is
        // the legacy row as it would actually be found on disk.
        StampParkReason(_db, run.Id, $"Reviewed: host replied 401 with body {Sentinel}");

        publisher.PostFailure = null;
        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        var body = publisher.PostedBodies.Should().ContainSingle().Subject;
        body.Should().NotContain(Sentinel, "a replayed notice must not republish text the daemon never wrote");
        body.Should()
            .Contain(
                "the review could not be completed",
                "an unrecognised reason degrades to the neutral phrase rather than being dropped or forwarded"
            );
    }

    [Fact]
    public async Task A_replayed_park_notice_still_carries_the_specific_phrase_a_real_park_wrote()
    {
        // The non-vacuity half of the pair above. An allow-list that always returned the neutral phrase would
        // satisfy that test perfectly while destroying the only thing the notice is for — telling the author
        // WHICH governed failure parked their review. The reason has to survive the round-trip through the
        // column and out to the comment.
        var publisher = new FakeReviewCommentPublisher { PostFailure = new InvalidOperationException("provider 503") };
        var executor = new FailsAtStageExecutor(
            ReviewStage.Reviewed,
            () => new SentinelUnauthorizedException("the sentinel returned 401 for https://host/internal")
        );
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: Notifier(publisher));
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<SentinelUnauthorizedException>();
        publisher.PostCount.Should().Be(0, "the first delivery attempt threw inside the publisher, so it is owed");

        publisher.PostFailure = null;
        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        var body = publisher.PostedBodies.Should().ContainSingle().Subject;
        body.Should()
            .Contain(
                "the review host refused the daemon's credentials",
                "a reason the current vocabulary DID write is trustworthy and must reach the pull request intact"
            );
        body.Should().Contain(nameof(ReviewStage.Reviewed), "the stage half of the reason survives too");
        body.Should()
            .NotContain(
                "the review could not be completed",
                "degrading a recognised reason would make the notice useless and the allow-list unfalsifiable"
            );
    }

    [Fact]
    public async Task A_replayed_park_notice_refuses_a_recognised_phrase_under_a_stage_that_does_not_exist()
    {
        // The stage half of the allow-list, pinned separately. The two tests above turn only on the PHRASE:
        // the legacy row they use carries a real stage ("Reviewed: {raw message}"), because that is the shape
        // the old park path wrote — so neutralising the stage check leaves both of them green and the conjunct
        // unfalsified. A reason has to fail this boundary on EITHER half independently, so the case that
        // distinguishes them is a phrase the vocabulary does emit under a stage no ReviewStage spells.
        var publisher = new FakeReviewCommentPublisher { PostFailure = new InvalidOperationException("provider 503") };
        var executor = new FailsAtStageExecutor(ReviewStage.Reviewed, () => new ReviewBarrierDeadlineException());
        var orchestrator = Orchestrator(executor, durableBudget: 1, notifier: Notifier(publisher));
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        publisher.PostCount.Should().Be(0, "the first delivery attempt threw inside the publisher, so it is owed");

        StampParkReason(_db, run.Id, "Sharpened: the review host rejected the request");

        publisher.PostFailure = null;
        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        var body = publisher.PostedBodies.Should().ContainSingle().Subject;
        body.Should()
            .NotContain(
                "Sharpened",
                "a stage this build does not spell means the reason was not written by its park path, whatever "
                    + "the phrase after it looks like"
            );
        body.Should().Contain("the review could not be completed", "so it degrades to the neutral phrase");
    }

    [Fact]
    public async Task The_permanent_park_debug_log_refuses_a_park_reason_the_current_vocabulary_could_not_have_written()
    {
        // Issue #666 review (SHOULD #3): the poll's park-guard debug log replays a PERSISTED column, exactly
        // like the notifier replay above, and must go through the same TrustedParkReasonForReplay allow-list
        // rather than logging run.ParkReason raw. A legacy/tampered row can carry arbitrary text (e.g. a raw
        // exception message from a build predating this vocabulary).
        const string Sentinel = "SENTINEL-DEBUGLOG-def456";
        var run = SeedRun();
        _store.TryMarkReviewRunParked(run.Id, _now, "Reviewed: the review host rejected the request").Should().BeTrue();
        StampParkReason(_db, run.Id, $"Reviewed: host replied 401 with body {Sentinel}");

        var logger = new CapturingLogger<PrOrchestrator>();
        var orchestrator = new PrOrchestrator(
            _store,
            new CountingFailingExecutor(),
            logger,
            maxDurableRetryAttempts: 1,
            clock: () => _now
        );

        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        logger
            .MessagesAtLevel(LogLevel.Debug)
            .Should()
            .OnlyContain(
                message => !message.Contains(Sentinel, StringComparison.Ordinal),
                "the park-guard debug log must never replay text the daemon never wrote"
            );
        logger
            .CountAtLevel(LogLevel.Debug, "the review could not be completed")
            .Should()
            .BePositive("an unrecognised reason degrades to the neutral phrase, exactly like the notifier replay");
    }

    [Fact]
    public async Task The_permanent_park_debug_log_still_carries_the_specific_phrase_a_real_park_wrote()
    {
        // The non-vacuity half of the pair above: an allow-list that always returned the neutral phrase would
        // pass the test above while destroying the log's only purpose — telling an operator polling logs WHY
        // a run is stuck.
        var run = SeedRun();
        _store.TryMarkReviewRunParked(run.Id, _now, "Reviewed: the review host rejected the request").Should().BeTrue();

        var logger = new CapturingLogger<PrOrchestrator>();
        var orchestrator = new PrOrchestrator(
            _store,
            new CountingFailingExecutor(),
            logger,
            maxDurableRetryAttempts: 1,
            clock: () => _now
        );

        _ = await orchestrator.RunAsync(run, CancellationToken.None);

        logger
            .CountAtLevel(LogLevel.Debug, "the review host rejected the request")
            .Should()
            .BePositive("a reason the current vocabulary DID write must still reach the debug log intact");
    }

    /// <summary>
    /// Overwrites <c>park_reason</c> with a value the sanitizer would never emit. Written directly rather than
    /// through <see cref="ReviewStore.TryMarkReviewRunParked"/> because that path runs the value through
    /// <c>DescribeGovernedFailure</c> — anything seeded through it is by construction inside the allow-list,
    /// which is exactly what the test needs to be outside.
    /// </summary>
    private static void StampParkReason(TempSqliteDatabase db, long runId, string reason)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_run SET park_reason = $reason WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$reason", reason);
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Counts entries into the real notifier without changing what it does. The count is the only way
    /// to see the park guard's own short-circuit: past it, <see cref="ReviewPoster"/>'s terminal replay makes a
    /// redundant call invisible at the publisher, so asserting on the publisher alone would pass either way.
    /// </summary>
    private sealed class CountingParkNotifier(IReviewParkNotifier inner) : IReviewParkNotifier
    {
        public int Calls { get; private set; }

        public Task NotifyParkedAsync(ReviewRun run, string reason, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.NotifyParkedAsync(run, reason, cancellationToken);
        }
    }

    /// <summary>The real notifier over a fake publisher: the park path's delivery evidence has to be the real
    /// outbox row, because that row is what the orchestrator's retry guard reads.</summary>
    private ReviewParkNotifier Notifier(FakeReviewCommentPublisher publisher) =>
        new(
            _store,
            [publisher],
            new CodeReviewDaemonOptions { EnableCommentPosting = true },
            NullLoggerFactory.Instance
        );

    /// <summary>Builds an orchestrator over the real store with an explicit durable budget and fake clock.</summary>
    private PrOrchestrator Orchestrator(
        IReviewStageExecutor executor,
        int durableBudget,
        RetryGovernor? governor = null,
        IReviewParkNotifier? notifier = null
    ) =>
        new(
            _store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor,
            maxDurableRetryAttempts: durableBudget,
            clock: () => _now,
            parkNotifier: notifier
        );

    /// <summary>
    /// Records the call and leaves delivery evidence where the real notifier leaves it — a terminal
    /// <c>post-park-notice</c> outbox row. The evidence is not decoration: the orchestrator's park guard reads
    /// exactly that row to decide whether a notice is still owed, so a fake that delivered without recording
    /// would be a fake that failed, and the guard would rightly keep re-attempting it.
    /// </summary>
    private sealed class RecordingParkNotifier(ReviewStore store) : IReviewParkNotifier
    {
        public List<long> Notified { get; } = [];

        public Task NotifyParkedAsync(ReviewRun run, string reason, CancellationToken cancellationToken)
        {
            Notified.Add(run.Id);
            _ = store.EnqueueOutbox(
                new OutboxEntry
                {
                    IdempotencyKey = $"park-notice-{run.Id}",
                    Provider = "github",
                    ReviewRunId = run.Id,
                    Operation = ReviewParkNotifier.PostParkNoticeOperation,
                    ArtifactKind = ReviewParkNotifier.PostParkNoticeOperation,
                    Status = OutboxStatus.Posted,
                }
            );
            return Task.CompletedTask;
        }
    }

    private RetryGovernor Governor(int maxAttempts) =>
        new(
            maxAttempts,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(900),
            () => _now,
            NullLogger<RetryGovernor>.Instance
        );

    /// <summary><paramref name="provider"/> is the STORAGE spelling, which is the whole point for the ADO
    /// case: the row says <c>azure-devops</c> and the publisher registry says <c>ado</c>.</summary>
    private ReviewRun SeedRun(string provider = "github")
    {
        var repoId = _store.EnsureRepo(
            new RepoIdentity
            {
                Provider = provider,
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-1",
            }
        );
        return _store.CreateOrGetReviewRun(
            new ReviewRun
            {
                RepoId = repoId,
                PrId = "1",
                HeadSha = "h",
                BaseSha = "b",
                TriggerWatermark = "wm",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "collect-only",
                Stage = ReviewStage.Discovered,
                WorkflowStatus = WorkflowStatus.Running,
                PrLifecycleState = PrLifecycleState.Open,
            }
        );
    }

    private sealed class CountingFailingExecutor : IReviewStageExecutor
    {
        public int ExecuteCalls { get; private set; }

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            throw new InvalidOperationException("simulated ContextReady failure");
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Succeeds every stage except <paramref name="failAt"/>, where it throws what
    /// <paramref name="error"/> produces (an <see cref="InvalidOperationException"/> by default); counts
    /// those failures. The exception TYPE is load-bearing: the governor charges only some of them.
    /// <para>
    /// <see cref="Enabled"/> switches the failure off mid-test, which is how the un-charge path is driven: a
    /// run has to actually SUCCEED at the stage that was failing for the clear to be exercised at all.
    /// </para>
    /// </summary>
    private sealed class FailsAtStageExecutor(ReviewStage failAt, Func<Exception>? error = null) : IReviewStageExecutor
    {
        public int FailStageCalls { get; private set; }

        /// <summary>When false the stage stops failing, so the next attempt gets through it.</summary>
        public bool Enabled { get; set; } = true;

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            if (stage == failAt && Enabled)
            {
                FailStageCalls++;
                throw error?.Invoke() ?? new InvalidOperationException($"simulated {failAt} failure");
            }

            return Task.CompletedTask;
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
