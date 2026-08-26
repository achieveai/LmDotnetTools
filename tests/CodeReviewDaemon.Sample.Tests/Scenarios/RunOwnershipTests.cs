using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Run ownership (task 29). <see cref="WorkflowStatus.Running"/> asserts that a process is working on a
/// run right now, and nothing ever withdrew the claim: a process that died mid-run left the row Running
/// forever, and because no query anywhere selects a run by status, no code path would ever look at it
/// again. Measured on the live store: four rows stranded at <c>ContextReady</c>, two from before the
/// day's first restart, one holding a real 158 KB context artifact that was computed and then abandoned.
/// <para>
/// The whole difficulty is that reclaiming a row requires knowing no live process owns it. Getting that
/// wrong is worse than the leak: a startup that steals runs from a concurrently-running daemon puts two
/// processes on the same PR, writing into the same notes branch. Every test here exists to pin one side
/// or the other of that line, and the bias is deliberate — when ownership cannot be established, the
/// reclaim declines. A delayed retry costs minutes; a double review costs correctness.
/// </para>
/// </summary>
public sealed class RunOwnershipTests
{
    private const string ThisInstance = "instance-A";
    private const string OtherInstance = "instance-B";

    private static ReviewRun Seed(long repoId, string prId, WorkflowStatus status, ReviewStage stage) =>
        new()
        {
            RepoId = repoId,
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = "base-sha",
            TriggerWatermark = $"head-{prId}",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "post",
            Stage = stage,
            WorkflowStatus = status,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private static long Repo(ReviewStore store) =>
        store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "azure-devops",
                OrgOrOwner = "o365exchange",
                Project = "Weve_DA",
                RepoName = "Nova",
            }
        );

    /// <summary>
    /// The four live rows: written before ownership existed, so their owner is NULL. A NULL owner cannot
    /// belong to a running process — nothing that claims a run today leaves it NULL — so these need no
    /// stale window at all. This is the case that recovers the actual stranded work.
    /// </summary>
    [Fact]
    public void A_running_run_with_no_owner_at_all_is_reclaimed_immediately()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5501205", WorkflowStatus.Running, ReviewStage.ContextReady));

        var reclaimed = store.ReclaimOrphanedRuns();

        reclaimed.Should().Be(1);
        var after = store.GetReviewRun(run.Id)!;
        after
            .WorkflowStatus.Should()
            .Be(WorkflowStatus.RetryPending, "an orphan must rejoin the work the resume path already handles");
        after
            .Stage.Should()
            .Be(
                ReviewStage.ContextReady,
                "the stage is the work already done — resetting it would discard the very artifact the leak stranded"
            );
    }

    /// <summary>The safety-critical direction. A live daemon heartbeating its run must never have it taken:
    /// that is how two processes end up reviewing one PR into one notes branch.</summary>
    [Fact]
    public void A_running_run_whose_owner_is_still_heartbeating_is_never_reclaimed()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5504310", WorkflowStatus.Running, ReviewStage.ContextReady));
        store.TryAcquireReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow);

        var reclaimed = store.ReclaimOrphanedRuns();

        reclaimed.Should().Be(0, "another live daemon owns this run and is saying so");
        store.GetReviewRun(run.Id)!.WorkflowStatus.Should().Be(WorkflowStatus.Running);
    }

    /// <summary>A claim whose heartbeat has gone quiet past the stale window is an owner that died.</summary>
    [Fact]
    public void A_running_run_whose_owner_stopped_heartbeating_is_reclaimed()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5502602", WorkflowStatus.Running, ReviewStage.ContextReady));
        store.TryAcquireReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        var reclaimed = store.ReclaimOrphanedRuns();

        reclaimed.Should().Be(1);
        store.GetReviewRun(run.Id)!.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);
    }

    /// <summary>
    /// The boundary, on the safe side. A heartbeat that is merely OLD but inside the window is a process
    /// mid-GC-pause, mid-slow-disk, or mid-long-stage — not a dead one. The window is five heartbeat
    /// intervals precisely so that ordinary stalls cannot get a live run stolen.
    /// </summary>
    [Fact]
    public void A_heartbeat_inside_the_stale_window_still_counts_as_alive()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5504919", WorkflowStatus.Running, ReviewStage.ContextReady));
        store.TryAcquireReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(100));

        store
            .ReclaimOrphanedRuns()
            .Should()
            .Be(0, "100s of silence inside a 150s window is a slow process, not a dead one");
    }

    /// <summary>
    /// Only <c>Running</c> is a claim of ownership. <c>RetryPending</c> is a live, working state — 18 rows
    /// of it on the live store, all handled correctly by the resume machinery — and rewriting those would
    /// be churn at best. <c>Completed</c> is finished work and must never be reopened.
    /// </summary>
    [Theory]
    [InlineData(nameof(WorkflowStatus.Completed))]
    [InlineData(nameof(WorkflowStatus.RetryPending))]
    [InlineData(nameof(WorkflowStatus.Pending))]
    [InlineData(nameof(WorkflowStatus.Failed))]
    public void Only_running_runs_are_reclaimed(string statusName)
    {
        var status = Enum.Parse<WorkflowStatus>(statusName);
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5500001", status, ReviewStage.ContextReady));

        store.ReclaimOrphanedRuns().Should().Be(0);
        store.GetReviewRun(run.Id)!.WorkflowStatus.Should().Be(status);
    }

    /// <summary>
    /// Reclaiming clears the owner too. Leaving a dead instance's id behind would make the row look owned
    /// to the next startup, so a second crash in the same window would strand it again — the leak coming
    /// back by a slightly longer route.
    /// </summary>
    [Fact]
    public void Reclaiming_clears_the_dead_owner_so_the_row_is_not_stranded_twice()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5500002", WorkflowStatus.Running, ReviewStage.ContextReady));
        store.TryAcquireReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        store.ReclaimOrphanedRuns().Should().Be(1);
        store.ReadOwner(run.Id).Should().BeNull("a reclaimed row belongs to nobody until someone claims it");
    }

    /// <summary>
    /// A claim starts before the orchestrator dispatches the first stage, so its persisted status is still
    /// Pending. RetryPending has the same interval on a resumed attempt. A heartbeat filtered to Running
    /// silently skips both states; after one stale window another daemon can steal the live claim. Drive the
    /// clock in heartbeat-sized steps to prove that every tick protects the owner without any real sleep.
    /// </summary>
    [Theory]
    [InlineData(nameof(WorkflowStatus.Pending))]
    [InlineData(nameof(WorkflowStatus.RetryPending))]
    public void Heartbeats_keep_pre_running_claims_contended_past_the_stale_window(string statusName)
    {
        var status = Enum.Parse<WorkflowStatus>(statusName);
        using var db = new TempSqliteDatabase();
        using var ownerStore = new ReviewStore(db.ConnectionString);
        using var competitorStore = new ReviewStore(db.ConnectionString);
        var run = ownerStore.CreateOrGetReviewRun(
            Seed(Repo(ownerStore), $"heartbeat-{statusName}", status, ReviewStage.Discovered)
        );
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        ownerStore.TryAcquireReviewRun(run.Id, ThisInstance, now).Should().Be(RunOwnershipAcquisition.Acquired);

        for (
            var elapsed = RunOwnershipPolicy.HeartbeatInterval;
            elapsed <= RunOwnershipPolicy.StaleAfter + RunOwnershipPolicy.HeartbeatInterval;
            elapsed += RunOwnershipPolicy.HeartbeatInterval
        )
        {
            ownerStore.HeartbeatOwnedRuns(ThisInstance, now + elapsed).Should().Be(1);
        }

        competitorStore
            .TryAcquireReviewRun(
                run.Id,
                OtherInstance,
                now + RunOwnershipPolicy.StaleAfter + RunOwnershipPolicy.HeartbeatInterval
            )
            .Should()
            .Be(RunOwnershipAcquisition.Contended, "the current instance kept its live claim fresh on every tick");
        ownerStore.ReadOwner(run.Id).Should().Be(ThisInstance);
    }

    /// <summary>
    /// Counterfactual for the heartbeat test. The same Pending and RetryPending claims, over the same fixed
    /// clock span, become acquirable when no heartbeat executes. This proves contention above comes from the
    /// heartbeat rather than from a status-specific acquisition rule or an insufficient clock advance.
    /// </summary>
    [Theory]
    [InlineData(nameof(WorkflowStatus.Pending))]
    [InlineData(nameof(WorkflowStatus.RetryPending))]
    public void Pre_running_claims_without_heartbeats_become_stale_and_acquirable(string statusName)
    {
        var status = Enum.Parse<WorkflowStatus>(statusName);
        using var db = new TempSqliteDatabase();
        using var ownerStore = new ReviewStore(db.ConnectionString);
        using var competitorStore = new ReviewStore(db.ConnectionString);
        var run = ownerStore.CreateOrGetReviewRun(
            Seed(Repo(ownerStore), $"no-heartbeat-{statusName}", status, ReviewStage.Discovered)
        );
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        ownerStore.TryAcquireReviewRun(run.Id, ThisInstance, now).Should().Be(RunOwnershipAcquisition.Acquired);

        competitorStore
            .TryAcquireReviewRun(
                run.Id,
                OtherInstance,
                now + RunOwnershipPolicy.StaleAfter + RunOwnershipPolicy.HeartbeatInterval
            )
            .Should()
            .Be(RunOwnershipAcquisition.Acquired, "without heartbeats the original claim is provably stale");
        ownerStore.ReadOwner(run.Id).Should().Be(OtherInstance);
    }

    /// <summary>
    /// Heartbeat is an owner-scoped timestamp refresh, never an ownership write. Released and completed
    /// ownerless rows must stay ownerless, while a foreign claim must keep both its owner and old heartbeat so
    /// the rightful competitor can acquire it once stale.
    /// </summary>
    [Fact]
    public void Heartbeat_does_not_revive_released_foreign_or_completed_ownerless_rows()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var released = store.CreateOrGetReviewRun(
            Seed(repoId, "released", WorkflowStatus.Pending, ReviewStage.Discovered)
        );
        var foreign = store.CreateOrGetReviewRun(
            Seed(repoId, "foreign", WorkflowStatus.RetryPending, ReviewStage.ContextReady)
        );
        var completed = store.CreateOrGetReviewRun(
            Seed(repoId, "completed", WorkflowStatus.Completed, ReviewStage.Posted)
        );
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        store.TryAcquireReviewRun(released.Id, ThisInstance, now).Should().Be(RunOwnershipAcquisition.Acquired);
        store.ReleaseReviewRun(released.Id, ThisInstance);
        store.TryAcquireReviewRun(foreign.Id, OtherInstance, now).Should().Be(RunOwnershipAcquisition.Acquired);

        store.HeartbeatOwnedRuns(ThisInstance, now + RunOwnershipPolicy.StaleAfter).Should().Be(0);

        store.ReadOwner(released.Id).Should().BeNull();
        store.ReadOwner(completed.Id).Should().BeNull();
        store
            .TryAcquireReviewRun(
                foreign.Id,
                ThisInstance,
                now + RunOwnershipPolicy.StaleAfter + TimeSpan.FromSeconds(1)
            )
            .Should()
            .Be(RunOwnershipAcquisition.Acquired, "this instance must not keep a foreign stale claim alive");
    }

    /// <summary>Claiming, heartbeating and releasing round-trip — the mechanism the reclaim reads.</summary>
    [Fact]
    public void Claim_then_heartbeat_then_release_round_trips()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5500003", WorkflowStatus.Running, ReviewStage.ContextReady));

        store.TryAcquireReviewRun(run.Id, ThisInstance, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        store.ReadOwner(run.Id).Should().Be(ThisInstance);

        // A heartbeat refreshes the claim, which is what keeps a long stage from looking dead.
        store.HeartbeatOwnedRuns(ThisInstance, DateTimeOffset.UtcNow);
        store.ReclaimOrphanedRuns().Should().Be(0, "the heartbeat brought it back inside the window");

        store.ReleaseReviewRun(run.Id, ThisInstance);
        store.ReadOwner(run.Id).Should().BeNull();
    }

    /// <summary>A heartbeat only refreshes rows this instance owns. Refreshing another instance's rows
    /// would keep a dead daemon's claims alive forever and make the reclaim unreachable.</summary>
    [Fact]
    public void A_heartbeat_does_not_refresh_another_instances_runs()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var mine = store.CreateOrGetReviewRun(
            Seed(repoId, "5500004", WorkflowStatus.Running, ReviewStage.ContextReady)
        );
        var theirs = store.CreateOrGetReviewRun(
            Seed(repoId, "5500005", WorkflowStatus.Running, ReviewStage.ContextReady)
        );
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        store.TryAcquireReviewRun(mine.Id, ThisInstance, longAgo);
        store.TryAcquireReviewRun(theirs.Id, OtherInstance, longAgo);

        store.HeartbeatOwnedRuns(ThisInstance, DateTimeOffset.UtcNow);

        store
            .ReclaimOrphanedRuns()
            .Should()
            .Be(1, "only the other instance's dead claim is reclaimable; mine was just refreshed");
        store.GetReviewRun(mine.Id)!.WorkflowStatus.Should().Be(WorkflowStatus.Running);
        store.GetReviewRun(theirs.Id)!.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);
    }

    /// <summary>Releasing is owner-scoped for the same reason: a process must not be able to drop a claim
    /// it does not hold, which is what would let a reclaim race undo a live daemon's ownership.</summary>
    [Fact]
    public void Releasing_a_run_owned_by_someone_else_does_nothing()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5500006", WorkflowStatus.Running, ReviewStage.ContextReady));
        store.TryAcquireReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow);

        store.ReleaseReviewRun(run.Id, ThisInstance);

        store.ReadOwner(run.Id).Should().Be(OtherInstance, "only the holder may drop a claim");
    }

    [Fact]
    public async Task Competing_sqlite_claimants_have_exactly_one_winner_and_loser_release_cannot_clear_it()
    {
        using var db = new TempSqliteDatabase();
        using var setupStore = new ReviewStore(db.ConnectionString);
        var run = setupStore.CreateOrGetReviewRun(
            Seed(Repo(setupStore), "5500009", WorkflowStatus.Running, ReviewStage.ContextReady)
        );
        using var firstStore = new ReviewStore(db.ConnectionString);
        using var secondStore = new ReviewStore(db.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        using var start = new ManualResetEventSlim(false);

        var first = Task.Run(() =>
        {
            start.Wait();
            return firstStore.TryAcquireReviewRun(run.Id, ThisInstance, now);
        });
        var second = Task.Run(() =>
        {
            start.Wait();
            return secondStore.TryAcquireReviewRun(run.Id, OtherInstance, now);
        });
        start.Set();

        var results = await Task.WhenAll(first, second);

        results.Should().ContainSingle(result => result == RunOwnershipAcquisition.Acquired);
        results.Should().ContainSingle(result => result == RunOwnershipAcquisition.Contended);
        var winner = results[0] == RunOwnershipAcquisition.Acquired ? ThisInstance : OtherInstance;
        var loser = winner == ThisInstance ? OtherInstance : ThisInstance;
        setupStore.ReadOwner(run.Id).Should().Be(winner);

        setupStore.ReleaseReviewRun(run.Id, loser);

        setupStore.ReadOwner(run.Id).Should().Be(winner, "a losing claimant never owns a releasable claim");
    }

    [Fact]
    public void A_fresh_same_owner_claim_is_not_reentrant_because_process_identity_is_not_invocation_identity()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.CreateOrGetReviewRun(
            Seed(Repo(store), "5500010", WorkflowStatus.Running, ReviewStage.ContextReady)
        );
        var now = DateTimeOffset.UtcNow;

        store.TryAcquireReviewRun(run.Id, ThisInstance, now).Should().Be(RunOwnershipAcquisition.Acquired);
        store
            .TryAcquireReviewRun(run.Id, ThisInstance, now.AddSeconds(1))
            .Should()
            .Be(
                RunOwnershipAcquisition.Contended,
                "two concurrent calls in one daemon share the process id and must not both dispatch"
            );
        store.ReadOwner(run.Id).Should().Be(ThisInstance);
    }

    [Fact]
    public void Acquisition_reclaims_only_an_owner_outside_the_central_stale_window()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var fresh = store.CreateOrGetReviewRun(
            Seed(Repo(store), "5500011", WorkflowStatus.Running, ReviewStage.ContextReady)
        );
        var stale = store.CreateOrGetReviewRun(
            Seed(fresh.RepoId, "5500012", WorkflowStatus.Running, ReviewStage.ContextReady)
        );
        var now = DateTimeOffset.UtcNow;
        store.TryAcquireReviewRun(
            fresh.Id,
            OtherInstance,
            now - RunOwnershipPolicy.StaleAfter + TimeSpan.FromSeconds(1)
        );
        store.TryAcquireReviewRun(
            stale.Id,
            OtherInstance,
            now - RunOwnershipPolicy.StaleAfter - TimeSpan.FromSeconds(1)
        );

        store.TryAcquireReviewRun(fresh.Id, ThisInstance, now).Should().Be(RunOwnershipAcquisition.Contended);
        store.TryAcquireReviewRun(stale.Id, ThisInstance, now).Should().Be(RunOwnershipAcquisition.Acquired);
        store.ReadOwner(fresh.Id).Should().Be(OtherInstance);
        store.ReadOwner(stale.Id).Should().Be(ThisInstance);
    }

    [Fact]
    public async Task A_contending_orchestrator_dispatches_zero_stages_and_does_not_release_the_winner()
    {
        using var db = new TempSqliteDatabase();
        using var firstStore = new ReviewStore(db.ConnectionString);
        using var secondStore = new ReviewStore(db.ConnectionString);
        var repoId = Repo(firstStore);
        var seed = Seed(repoId, "5500013", WorkflowStatus.Pending, ReviewStage.Discovered);
        var winnerExecutor = new BlockingStageExecutor();
        var loserExecutor = new RecordingStageExecutor();
        var winner = new PrOrchestrator(
            firstStore,
            winnerExecutor,
            NullLogger<PrOrchestrator>.Instance,
            providers: [new ReadyPrProvider("azure-devops")]
        );
        var loser = new PrOrchestrator(
            secondStore,
            loserExecutor,
            NullLogger<PrOrchestrator>.Instance,
            providers: [new ReadyPrProvider("azure-devops")]
        );

        var winnerTask = winner.ExecuteAsync(seed, CancellationToken.None);
        await winnerExecutor.StageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var loserResult = await loser.ExecuteAsync(seed, CancellationToken.None);

        loserResult.Outcome.Should().Be(PrExecutionOutcome.OwnershipDeferred);
        loserResult.ConsumedReviewAttempt.Should().BeFalse("ownership contention is not a review attempt");
        loserExecutor.ExecutedStages.Should().BeEmpty();
        loserExecutor.ReleaseCount.Should().Be(0, "the loser must not release the winner's pooled lease");
        secondStore.ReadOwner(loserResult.Run.Id).Should().Be(DaemonInstance.Id);

        winnerExecutor.AllowCompletion.SetResult();
        var winnerResult = await winnerTask;
        winnerResult.Outcome.Should().Be(PrExecutionOutcome.StageProgress);
        winnerExecutor.ExecutedStageCount.Should().Be(4);
        firstStore.ReadOwner(winnerResult.Run.Id).Should().BeNull();
    }

    /// <summary>
    /// The orchestrator claims a run while it is working it and drops the claim when it stops — which is
    /// what makes the reclaim's "no live process holds this" test mean anything. Without a claim written
    /// here, every Running row looks unowned and the startup reclaim would take live runs from a
    /// concurrently-running daemon: the exact failure this whole design is arranged to avoid.
    /// </summary>
    [Fact]
    public async Task The_orchestrator_holds_a_claim_while_working_and_drops_it_when_done()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var seed = Seed(repoId, "5500007", WorkflowStatus.Pending, ReviewStage.Discovered);
        string? ownerWhileWorking = null;

        var executor = new OwnerProbingStageExecutor(store, id => ownerWhileWorking ??= store.ReadOwner(id));
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            providers: [new ReadyPrProvider("azure-devops")]
        );

        var run = await orchestrator.RunAsync(seed, CancellationToken.None);

        ownerWhileWorking
            .Should()
            .NotBeNull("a run being executed must carry an owner, or the reclaim cannot tell it from an orphan");
        ownerWhileWorking.Should().Be(DaemonInstance.Id);
        store.ReadOwner(run.Id).Should().BeNull("the claim is dropped once this process stops working the run");
    }

    /// <summary>A run this process is actively working must survive a concurrent reclaim pass — the
    /// in-process version of the two-daemons hazard, and the cheapest place to pin it.</summary>
    [Fact]
    public async Task A_run_being_worked_right_now_survives_a_concurrent_reclaim()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var seed = Seed(repoId, "5500008", WorkflowStatus.Pending, ReviewStage.Discovered);
        var reclaimedMidFlight = -1;

        var executor = new OwnerProbingStageExecutor(store, _ => reclaimedMidFlight = store.ReclaimOrphanedRuns());
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            NullLogger<PrOrchestrator>.Instance,
            providers: [new ReadyPrProvider("azure-devops")]
        );

        _ = await orchestrator.RunAsync(seed, CancellationToken.None);

        reclaimedMidFlight
            .Should()
            .Be(0, "the run was claimed with a fresh heartbeat, so a reclaim running alongside must pass it by");
    }

    private sealed class BlockingStageExecutor : IReviewStageExecutor
    {
        public TaskCompletionSource StageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutedStageCount { get; private set; }

        public async Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            ExecutedStageCount++;
            StageEntered.TrySetResult();
            await AllowCompletion.Task.WaitAsync(cancellationToken);
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Runs a probe on the first stage so a test can observe store state mid-run.</summary>
    private sealed class OwnerProbingStageExecutor(ReviewStore store, Action<long> probe) : IReviewStageExecutor
    {
        private bool _probed;

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            if (!_probed)
            {
                _probed = true;
                probe(run.Id);
            }

            _ = store;
            return Task.CompletedTask;
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
