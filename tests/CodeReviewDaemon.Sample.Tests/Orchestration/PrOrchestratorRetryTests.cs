using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
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
        var orchestrator = new PrOrchestrator(_store, executor, NullLogger<PrOrchestrator>.Instance, retryGovernor: governor);
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
    public async Task A_backing_off_run_is_skipped_until_the_backoff_elapses()
    {
        var governor = Governor(maxAttempts: 5);
        var executor = new CountingFailingExecutor();
        var orchestrator = new PrOrchestrator(_store, executor, NullLogger<PrOrchestrator>.Instance, retryGovernor: governor);
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
        var orchestrator = new PrOrchestrator(_store, executor, NullLogger<PrOrchestrator>.Instance, retryGovernor: governor);
        var run = SeedRun();

        var attempt = async () => await orchestrator.RunAsync(run, CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        executor.FailStageCalls.Should().Be(1);

        // Not parked (a ContextReady failure at maxAttempts=1 WOULD be): the Posted stage is attempted again.
        await attempt.Should().ThrowAsync<InvalidOperationException>();
        executor.FailStageCalls.Should().Be(2, "a non-ContextReady failure is not governed by the context-retry budget");
    }

    private RetryGovernor Governor(int maxAttempts) => new(
        maxAttempts,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(900),
        () => _now,
        NullLogger<RetryGovernor>.Instance);

    private ReviewRun SeedRun()
    {
        var repoId = _store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-1",
        });
        return _store.CreateOrGetReviewRun(new ReviewRun
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
        });
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

        public Task ReconcilePendingRetentionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Succeeds every stage except <paramref name="failAt"/>, where it throws; counts those failures.</summary>
    private sealed class FailsAtStageExecutor(ReviewStage failAt) : IReviewStageExecutor
    {
        public int FailStageCalls { get; private set; }

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            if (stage == failAt)
            {
                FailStageCalls++;
                throw new InvalidOperationException($"simulated {failAt} failure");
            }

            return Task.CompletedTask;
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReconcilePendingRetentionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
