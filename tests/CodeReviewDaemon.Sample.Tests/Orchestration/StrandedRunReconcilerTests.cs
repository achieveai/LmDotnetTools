using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins the route back for a run the poll can no longer reach. The poll enumerates OPEN PRs inside a recency
/// window and nothing else in the daemon reads <c>review_run</c> again, so a run left non-terminal when its PR
/// merges, closes, or goes quiet is orphaned permanently — the retry that would have healed it never arrives.
/// Two properties carry the weight: every stranded run must reach a terminal status, and a run whose head has
/// already been re-reviewed must never be resumed (on a posting daemon that would publish a stale diff).
/// </summary>
public sealed class StrandedRunReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(6);

    // ── the defect: a stranded run is never retried ───────────────────────────────────────────────

    [Fact]
    public async Task A_stranded_run_whose_pr_is_still_open_is_handed_back_to_the_orchestrator()
    {
        var harness = new Harness().WithRows(Row(id: 11, stage: ReviewStage.Judged));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().ContainSingle(
            "the poll can no longer reach this run, so the reconciler is the only thing that can retry it")
            .Which.Id.Should().Be(11);
        harness.Retired.Should().BeEmpty("an open PR's run is resumed, not written off");
    }

    [Fact]
    public async Task A_resumed_run_carries_the_freshly_observed_lifecycle_not_the_stale_persisted_one()
    {
        var harness = new Harness()
            .WithRows(Row(id: 11, lifecycle: PrLifecycleState.Closed))
            .WithLifecycle(PrLifecycle.Open);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().ContainSingle()
            .Which.PrLifecycleState.Should().Be(
                PrLifecycleState.Open,
                "the orchestrator halts any run it is handed with a non-open lifecycle, so a stale persisted "
                    + "state would silently turn every resume into a no-op");
    }

    [Fact]
    public async Task A_stranded_run_whose_pr_has_merged_is_retired_without_being_resumed() =>
        await AssertClosedPrIsRetired(PrLifecycle.Merged, PrLifecycleState.Merged);

    [Fact]
    public async Task A_stranded_run_whose_pr_was_abandoned_is_retired_without_being_resumed() =>
        await AssertClosedPrIsRetired(PrLifecycle.Abandoned, PrLifecycleState.Abandoned);

    // Takes the internal enums, so it cannot be a public [Theory] — the two facts above supply the cases.
    private static async Task AssertClosedPrIsRetired(PrLifecycle lifecycle, PrLifecycleState expected)
    {
        var harness = new Harness()
            .WithRows(Row(id: 11, stage: ReviewStage.Reviewed))
            .WithLifecycle(lifecycle);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty("there is nothing left to review on a PR that has closed");
        harness.Retired.Should().ContainSingle().Which.Should().Be(
            (11L, ReviewStage.Reviewed, WorkflowStatus.Completed, expected),
            "this is the same rule PrOrchestrator applies to a PR it observes as no longer open: stop working "
                + "the run, at the stage it reached, without marking it failed");
    }

    // ── the safety rail: a superseded run must never be resumed ───────────────────────────────────

    [Fact]
    public async Task A_superseded_run_is_retired_without_even_asking_the_provider()
    {
        var harness = new Harness().WithRows(Row(id: 11, superseded: true));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty(
            "a later run has already reviewed a newer head; resuming this one would review — and on a posting "
                + "daemon publish — a diff that no longer stands");
        harness.LifecycleLookups.Should().Be(0, "supersession is decided from the store alone");
        harness.Retired.Should().ContainSingle().Which.Item3.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task A_run_the_orchestrator_resolved_to_a_different_row_is_retired_so_it_cannot_be_re_picked()
    {
        var harness = new Harness()
            .WithRows(Row(id: 11))
            .WithResumeResolvingTo(runId: 48);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Retired.Should().ContainSingle(
            "the orchestrator resolves a run by identity tuple, so it can settle a further-progressed sibling "
                + "at the same head instead — leaving this row stranded and re-picked on every later pass")
            .Which.Item1.Should().Be(11L);
    }

    // ── the cap: a weeks-old backlog must not release all at once ─────────────────────────────────

    [Fact]
    public async Task The_resume_cap_bounds_one_pass_and_the_rest_are_deferred_not_dropped()
    {
        var harness = new Harness()
            .WithRows(Row(11), Row(12), Row(13), Row(14))
            .WithMaxResumes(2);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal([11L, 12L], "the cap is two per pass, oldest first");
        harness.Retired.Should().BeEmpty("a deferred run is still open work — it must not be written off");
        harness.Log.Should().Contain(
            e => e.Contains("deferred", StringComparison.OrdinalIgnoreCase) && e.Contains("13", StringComparison.Ordinal),
            "a cap that silently shortens the pass reads as 'nothing left to do'");
    }

    [Fact]
    public async Task Retiring_a_closed_pr_never_consumes_a_resume_slot()
    {
        var harness = new Harness()
            .WithRows(Row(11, superseded: true), Row(12, superseded: true), Row(13))
            .WithMaxResumes(1);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal(
            [13L], "bookkeeping costs nothing, so it must not crowd out the one run that needed real work");
    }

    // ── isolation: one bad run never aborts the pass ──────────────────────────────────────────────

    [Fact]
    public async Task A_run_whose_provider_lookup_throws_is_logged_and_the_pass_continues()
    {
        var harness = new Harness()
            .WithRows(Row(11), Row(12))
            .WithLifecycleThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal(
            [12L], "one unreachable provider must not strand the rest of the backlog all over again");
        harness.Log.Should().Contain(e => e.Contains("failed to settle run 11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_backlog_is_silent()
    {
        var harness = new Harness();

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Log.Should().BeEmpty("the steady state is no stranded runs, on every poll cycle, forever");
    }

    // ── the store query ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_store_lists_only_non_terminal_runs_older_than_the_grace_period()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var stale = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        var fresh = store.CreateOrGetReviewRun(SampleRun(repoId, "102"));
        var finished = store.CreateOrGetReviewRun(SampleRun(repoId, "103"));
        store.UpdateReviewRunState(finished.Id, ReviewStage.Posted, WorkflowStatus.Completed, PrLifecycleState.Open);
        Backdate(db, stale.Id, Now - TimeSpan.FromDays(9));
        Backdate(db, finished.Id, Now - TimeSpan.FromDays(9));

        var stranded = store.ListStrandedRuns(Now - Grace, limit: 50);

        stranded.Select(s => s.Run.Id).Should().Equal(
            [stale.Id],
            "a completed run needs no route back, and a run inside the grace period is still the poll's to "
                + "work — a healthy run stamps updated_at at every stage boundary (run {0} is fresh)",
            fresh.Id);
    }

    [Fact]
    public void The_store_flags_a_run_that_a_later_run_for_the_same_pr_has_superseded()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var older = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var newer = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-2" });
        var only = store.CreateOrGetReviewRun(SampleRun(repoId, "102"));
        foreach (var id in new[] { older.Id, newer.Id, only.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        var stranded = store.ListStrandedRuns(Now - Grace, limit: 50);

        stranded.Should().HaveCount(3);
        stranded.Single(s => s.Run.Id == older.Id).Superseded.Should().BeTrue(
            "run {0} reviewed a later head of the same PR", newer.Id);
        stranded.Single(s => s.Run.Id == newer.Id).Superseded.Should().BeFalse();
        stranded.Single(s => s.Run.Id == only.Id).Superseded.Should().BeFalse(
            "supersession is per PR — another PR's runs say nothing about this one");
    }

    [Fact]
    public void The_store_caps_one_read_and_leaves_the_rest_for_the_next_pass()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var ids = new List<long>();
        foreach (var pr in new[] { "101", "102", "103" })
        {
            var run = store.CreateOrGetReviewRun(SampleRun(repoId, pr));
            Backdate(db, run.Id, Now - TimeSpan.FromDays(9));
            ids.Add(run.Id);
        }

        var stranded = store.ListStrandedRuns(Now - Grace, limit: 2);

        stranded.Select(s => s.Run.Id).Should().Equal(
            ids.Take(2), "the cap takes the oldest rows by id, in one query — never a second page by offset "
                + "over a predicate the caller is mutating as it works");
        stranded.Should().AllSatisfy(s => s.Repo.RepoName.Should().Be(SampleRepo().RepoName),
            "the caller needs the repo identity to ask the provider what became of the PR");
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static void Backdate(TempSqliteDatabase db, long runId, DateTimeOffset updatedAt)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_run SET updated_at = $at WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$at", updatedAt.ToUniversalTime().ToString("O"));
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    private static RepoIdentity SampleRepo() => new()
    {
        Provider = "github",
        OrgOrOwner = "achieveai",
        RepoName = "LmDotnetTools",
    };

    private static ReviewRun SampleRun(long repoId, string prId) => new()
    {
        RepoId = repoId,
        PrId = prId,
        HeadSha = "head-sha",
        BaseSha = "base-sha",
        TriggerWatermark = "wm-1",
        ReviewKind = "full",
        VariantId = "primary",
        Mode = "collect-only",
        Stage = ReviewStage.Discovered,
        WorkflowStatus = WorkflowStatus.RetryPending,
        PrLifecycleState = PrLifecycleState.Open,
    };

    private static StrandedRunRow Row(
        long id,
        ReviewStage stage = ReviewStage.Discovered,
        bool superseded = false,
        PrLifecycleState lifecycle = PrLifecycleState.Open) =>
        new(
            new ReviewRun
            {
                Id = id,
                RepoId = 1,
                PrId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                HeadSha = "head-sha",
                BaseSha = "base-sha",
                TriggerWatermark = "wm-1",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "post",
                Stage = stage,
                WorkflowStatus = WorkflowStatus.RetryPending,
                PrLifecycleState = lifecycle,
            },
            SampleRepo(),
            superseded);

    private sealed class Harness
    {
        private StrandedRunRow[] _rows = [];
        private PrLifecycle _lifecycle = PrLifecycle.Open;
        private long? _throwFor;
        private long? _resolvesTo;
        private int _maxResumes = 10;

        public List<ReviewRun> Resumed { get; } = [];

        public List<(long, ReviewStage, WorkflowStatus, PrLifecycleState)> Retired { get; } = [];

        public List<string> Log { get; } = [];

        public int LifecycleLookups { get; private set; }

        public Harness WithRows(params StrandedRunRow[] rows)
        {
            _rows = rows;
            return this;
        }

        public Harness WithLifecycle(PrLifecycle lifecycle)
        {
            _lifecycle = lifecycle;
            return this;
        }

        public Harness WithLifecycleThrowingFor(long runId)
        {
            _throwFor = runId;
            return this;
        }

        public Harness WithResumeResolvingTo(long runId)
        {
            _resolvesTo = runId;
            return this;
        }

        public Harness WithMaxResumes(int max)
        {
            _maxResumes = max;
            return this;
        }

        public StrandedRunReconciler Reconciler() => new(
            listStrandedRuns: (staleBefore, limit) =>
            {
                staleBefore.Should().Be(Now - Grace, "the grace period is subtracted from the current time");
                return [.. _rows.Take(limit)];
            },
            getPrLifecycleAsync: (row, _) =>
            {
                LifecycleLookups++;
                return row.Run.Id == _throwFor
                    ? throw new InvalidOperationException("provider unreachable")
                    : Task.FromResult(_lifecycle);
            },
            resumeAsync: (run, _) =>
            {
                Resumed.Add(run);
                return Task.FromResult(_resolvesTo is { } id ? run with { Id = id } : run);
            },
            retire: (id, stage, status, state) => Retired.Add((id, stage, status, state)),
            timeProvider: new FakeTimeProvider(Now),
            grace: Grace,
            scanLimit: 50,
            maxResumesPerPass: _maxResumes,
            logger: new CapturingLogger<StrandedRunReconciler>(Log));
    }

    /// <summary>Records the formatted message of every log entry so the deferral notices can be asserted.</summary>
    private sealed class CapturingLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
