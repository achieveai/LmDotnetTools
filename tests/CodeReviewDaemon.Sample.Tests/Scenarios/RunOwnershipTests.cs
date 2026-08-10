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
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(150);

    private static ReviewRun Seed(long repoId, string prId, WorkflowStatus status, ReviewStage stage) => new()
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

    private static long Repo(ReviewStore store) => store.EnsureRepo(new RepoIdentity
    {
        Provider = "azure-devops",
        OrgOrOwner = "o365exchange",
        Project = "Weve_DA",
        RepoName = "Nova",
    });

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

        var reclaimed = store.ReclaimOrphanedRuns(StaleAfter);

        reclaimed.Should().Be(1);
        var after = store.GetReviewRun(run.Id)!;
        after.WorkflowStatus.Should().Be(
            WorkflowStatus.RetryPending, "an orphan must rejoin the work the resume path already handles");
        after.Stage.Should().Be(
            ReviewStage.ContextReady,
            "the stage is the work already done — resetting it would discard the very artifact the leak stranded");
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
        store.ClaimReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow);

        var reclaimed = store.ReclaimOrphanedRuns(StaleAfter);

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
        store.ClaimReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        var reclaimed = store.ReclaimOrphanedRuns(StaleAfter);

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
        store.ClaimReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(100));

        store.ReclaimOrphanedRuns(StaleAfter).Should().Be(
            0, "100s of silence inside a 150s window is a slow process, not a dead one");
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

        store.ReclaimOrphanedRuns(StaleAfter).Should().Be(0);
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
        store.ClaimReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        store.ReclaimOrphanedRuns(StaleAfter).Should().Be(1);
        store.ReadOwner(run.Id).Should().BeNull("a reclaimed row belongs to nobody until someone claims it");
    }

    /// <summary>Claiming, heartbeating and releasing round-trip — the mechanism the reclaim reads.</summary>
    [Fact]
    public void Claim_then_heartbeat_then_release_round_trips()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var run = store.CreateOrGetReviewRun(Seed(repoId, "5500003", WorkflowStatus.Running, ReviewStage.ContextReady));

        store.ClaimReviewRun(run.Id, ThisInstance, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        store.ReadOwner(run.Id).Should().Be(ThisInstance);

        // A heartbeat refreshes the claim, which is what keeps a long stage from looking dead.
        store.HeartbeatOwnedRuns(ThisInstance, DateTimeOffset.UtcNow);
        store.ReclaimOrphanedRuns(StaleAfter).Should().Be(0, "the heartbeat brought it back inside the window");

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
        var mine = store.CreateOrGetReviewRun(Seed(repoId, "5500004", WorkflowStatus.Running, ReviewStage.ContextReady));
        var theirs = store.CreateOrGetReviewRun(Seed(repoId, "5500005", WorkflowStatus.Running, ReviewStage.ContextReady));
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        store.ClaimReviewRun(mine.Id, ThisInstance, longAgo);
        store.ClaimReviewRun(theirs.Id, OtherInstance, longAgo);

        store.HeartbeatOwnedRuns(ThisInstance, DateTimeOffset.UtcNow);

        store.ReclaimOrphanedRuns(StaleAfter).Should().Be(
            1, "only the other instance's dead claim is reclaimable; mine was just refreshed");
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
        store.ClaimReviewRun(run.Id, OtherInstance, DateTimeOffset.UtcNow);

        store.ReleaseReviewRun(run.Id, ThisInstance);

        store.ReadOwner(run.Id).Should().Be(OtherInstance, "only the holder may drop a claim");
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
            store, executor, NullLogger<PrOrchestrator>.Instance);

        var run = await orchestrator.RunAsync(seed, CancellationToken.None);

        ownerWhileWorking.Should().NotBeNull(
            "a run being executed must carry an owner, or the reclaim cannot tell it from an orphan");
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

        var executor = new OwnerProbingStageExecutor(
            store, _ => reclaimedMidFlight = store.ReclaimOrphanedRuns(StaleAfter));
        var orchestrator = new PrOrchestrator(store, executor, NullLogger<PrOrchestrator>.Instance);

        _ = await orchestrator.RunAsync(seed, CancellationToken.None);

        reclaimedMidFlight.Should().Be(
            0, "the run was claimed with a fresh heartbeat, so a reclaim running alongside must pass it by");
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
