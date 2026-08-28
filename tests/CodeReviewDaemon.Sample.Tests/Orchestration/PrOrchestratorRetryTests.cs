using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
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

    private RetryGovernor Governor(int maxAttempts) =>
        new(
            maxAttempts,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(900),
            () => _now,
            NullLogger<RetryGovernor>.Instance
        );

    private ReviewRun SeedRun()
    {
        var repoId = _store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
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
    /// those failures. The exception TYPE is load-bearing: the governor charges only some of them.</summary>
    private sealed class FailsAtStageExecutor(ReviewStage failAt, Func<Exception>? error = null) : IReviewStageExecutor
    {
        public int FailStageCalls { get; private set; }

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            if (stage == failAt)
            {
                FailStageCalls++;
                throw error?.Invoke() ?? new InvalidOperationException($"simulated {failAt} failure");
            }

            return Task.CompletedTask;
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
