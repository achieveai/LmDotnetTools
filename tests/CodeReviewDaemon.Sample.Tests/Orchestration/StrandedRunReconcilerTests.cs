using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System.Net;

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
    public async Task A_run_whose_stages_are_all_done_is_retired_rather_than_resumed()
    {
        // A crash between the last stage's write and its terminal status leaves a row at the final stage with a
        // non-terminal status — stranded by the letter of the sweep, but with nothing left to do:
        // StageMachine.RemainingStages of a complete stage is empty, so the orchestrator would execute no stage
        // and return. Resuming it therefore burned a resume slot every pass to accomplish nothing, and the pass
        // whose job is to drain stranded runs could never drain this one. It is a pure function of the row, so
        // it is answered before the provider is asked at all.
        var harness = new Harness().WithRows(Row(id: 11, stage: StageMachine.Terminal));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty("there is no remaining stage to run");
        harness.LifecycleLookups.Should().Be(0, "a row with no work left needs no provider call to settle");
        harness.Retired.Should().ContainSingle().Which.Item3.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task A_run_still_short_of_the_final_stage_is_resumed_rather_than_retired()
    {
        // The over-refusal pin for the retirement above: it must key on the run being COMPLETE, not merely on
        // being far along. Retiring at the second-to-last stage would silently write off reviews that still owe
        // their final stage — the exact permanent-abandonment this whole sweep exists to prevent.
        var lastIncomplete = StageMachine.Order[^2];
        var harness = new Harness().WithRows(Row(id: 11, stage: lastIncomplete));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().ContainSingle().Which.Id.Should().Be(11);
        harness.Retired.Should().BeEmpty();
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
    public async Task A_run_whose_pr_the_provider_cannot_find_is_retired_rather_than_stranded_again()
    {
        var harness = new Harness()
            .WithRows(Row(id: 141))
            .WithLifecycleThrowingFor(141, new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Retired.Should().ContainSingle(
            "the daemon's own store holds a run seeded against a number that is not a PR; without this the "
                + "lookup throws on every pass and the run stays stranded, one level further out")
            .Which.Should().Be((141L, ReviewStage.Discovered, WorkflowStatus.Completed, PrLifecycleState.Abandoned));
        harness.Log.Should().NotContain(e => e.Contains("failed to settle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_that_is_merely_unreachable_does_not_retire_the_run()
    {
        var harness = new Harness()
            .WithRows(Row(id: 11))
            .WithLifecycleThrowingFor(
                11, new HttpRequestException("Bad gateway", null, HttpStatusCode.ServiceUnavailable));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Retired.Should().BeEmpty(
            "a 5xx, a 401 or a timeout says nothing about the PR's state — writing the run off on one would "
                + "discard live work over a blip");
        harness.Log.Should().Contain(e => e.Contains("failed to settle run 11", StringComparison.Ordinal));
    }

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
    public async Task A_run_whose_resume_throws_is_logged_and_the_next_run_is_still_settled()
    {
        var harness = new Harness()
            .WithRows(Row(11), Row(12))
            .WithResumeThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal(
            [12L], "a resume runs the review's remaining stages, so it fails for far more reasons than the "
                + "lifecycle lookup does — one failing review must not re-strand the rest of the backlog");
        harness.Log.Should().Contain(e => e.Contains("failed to settle run 11", StringComparison.Ordinal));
        harness.Retired.Should().BeEmpty(
            "the run is still open work: leaving it non-terminal is what keeps it eligible for the next pass");
    }

    [Fact]
    public async Task An_open_run_is_claimed_before_the_resume_rather_than_after_it()
    {
        // `updated_at` is the ONLY thing that takes a row out of the stranded listing short of a terminal
        // status, and the resume is not guaranteed to write it: the orchestrator returns early for a run with no
        // stages left, and a resume that throws leaves the row exactly as it found it. Without a write of its
        // own the reconciler re-lists the same row on the very next pass, re-logs "resuming", and re-charges it
        // against the cap — forever, crowding out the backlog the pass exists to drain. Ordering matters as much
        // as the write: a stamp taken afterwards would leave the takeover invisible for the whole review.
        var harness = new Harness().WithRows(Row(id: 11, stage: ReviewStage.Judged));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Order.Should().Equal(
            ["write:11", "resume:11"], "the claim is what makes the takeover survive a resume that does nothing");
        harness.StateWrites.Should().ContainSingle().Which.Should().Be(
            (11L, ReviewStage.Judged, WorkflowStatus.RetryPending, PrLifecycleState.Open),
            "the claim re-writes the state the row already had — it advances the timestamp, it does not decide "
                + "anything about the run");
        harness.Retired.Should().BeEmpty("an open PR's run is claimed, not written off");
    }

    [Fact]
    public async Task A_run_whose_resume_throws_is_still_left_claimed()
    {
        var harness = new Harness()
            .WithRows(Row(id: 11, stage: ReviewStage.Judged))
            .WithResumeThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.StateWrites.Should().ContainSingle(
            "a failing resume is the case that most needs the claim: it writes nothing itself, so this row would "
                + "otherwise be re-picked and re-failed on every pass with nothing in the store to show for it")
            .Which.Item3.Should().Be(
                WorkflowStatus.RetryPending, "the run is still open work and must stay eligible for a later pass");
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

    [Theory]
    [InlineData("security", "full")] // another variant reviews with its own prompt and its own output
    [InlineData("primary", "incremental")] // another kind reviews a different span of the PR
    public void The_store_does_not_let_a_different_reviews_later_run_supersede(string variantId, string kind)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var stranded = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var unrelated = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with { HeadSha = "head-2", VariantId = variantId, ReviewKind = kind });
        foreach (var id in new[] { stranded.Id, unrelated.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store.ListStrandedRuns(Now - Grace, limit: 50).Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should().BeFalse(
                "run {0} never produced the review this run owes; retiring on it would drop that review "
                    + "silently and forever, because this listing is the run's only remaining route back",
                unrelated.Id);
    }

    [Fact]
    public void The_store_does_not_let_a_duplicate_row_at_the_same_head_supersede()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // The identity lookup is watermark-agnostic, so a duplicate at the same head is not reachable through
        // CreateOrGetReviewRun — it is the shape left behind by an earlier build that keyed identity on the
        // watermark, which FindReviewRunByIdentity still tolerates. The stranded listing meets those rows too.
        var stranded = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var duplicateId = CloneRunAtSameHead(db, stranded.Id, watermark: "wm-2");
        foreach (var id in new[] { stranded.Id, duplicateId })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store.ListStrandedRuns(Now - Grace, limit: 50).Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should().BeFalse(
                "retirement is justified by a newer head making this diff stale, and run {0} sits at the same "
                    + "head — a higher row id on its own is not evidence that anything went stale",
                duplicateId);
    }

    [Fact]
    public void The_store_supersedes_when_a_later_run_reviewed_the_same_head_against_a_new_base()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // A target-branch rebase moves base_sha under an unchanged head. The later run reviewed a genuinely
        // different diff — the PR as it now stands — while this one still owes findings about changes that have
        // since landed in the target branch. Keying supersession on the head alone would resume it and, on a
        // posting daemon, publish them. base_sha is part of the identity tuple for the same reason, so these are
        // two legitimately distinct runs and the later one is the current one.
        var stranded = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with { HeadSha = "head-1", BaseSha = "base-1" });
        var newer = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with { HeadSha = "head-1", BaseSha = "base-2" });
        newer.Id.Should().NotBe(stranded.Id, "a moved base is a different identity, not the same run");
        foreach (var id in new[] { stranded.Id, newer.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store.ListStrandedRuns(Now - Grace, limit: 50).Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should().BeTrue(
                "run {0} reviewed the same head against the current base, so this run's diff is the stale one",
                newer.Id);
    }

    [Fact]
    public void The_store_still_supersedes_across_a_mode_change()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var stranded = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var newer = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with { HeadSha = "head-2", Mode = "post" });
        foreach (var id in new[] { stranded.Id, newer.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store.ListStrandedRuns(Now - Grace, limit: 50).Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should().BeTrue(
                "mode is an authorization decision made at post time, not part of what the review is (see "
                    + "CreateOrGetReviewRun) — toggling posting between the two runs does not make run {0}'s "
                    + "newer head any less of a replacement for this one's diff",
                newer.Id);
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

    /// <summary>
    /// Copies a run onto a second row at the same head, differing only by <c>trigger_watermark</c> — the
    /// duplicate an earlier build's identity key could produce, and the table's UNIQUE constraint still
    /// permits. Written directly because the store's own lookup is watermark-agnostic and would hand back
    /// the original. Returns the new row's id.
    /// </summary>
    private static long CloneRunAtSameHead(TempSqliteDatabase db, long runId, string watermark)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                stage, workflow_status, pr_lifecycle_state, is_fork_pr, is_target_repo_public,
                created_at, updated_at)
            SELECT repo_id, pr_id, head_sha, base_sha, $watermark, review_kind, variant_id, mode,
                   stage, workflow_status, pr_lifecycle_state, is_fork_pr, is_target_repo_public,
                   created_at, updated_at
            FROM review_run WHERE id = $id
            RETURNING id;
            """;
        _ = command.Parameters.AddWithValue("$watermark", watermark);
        _ = command.Parameters.AddWithValue("$id", runId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
        private Exception _failure = new InvalidOperationException("provider unreachable");
        private long? _resolvesTo;
        private long? _resumeThrowsFor;
        private int _maxResumes = 10;

        public List<ReviewRun> Resumed { get; } = [];

        /// <summary>Every <c>review_run</c> state write the reconciler made, in order.</summary>
        public List<(long, ReviewStage, WorkflowStatus, PrLifecycleState)> StateWrites { get; } = [];

        /// <summary>
        /// The subset of <see cref="StateWrites"/> that retired a run. Retirement is the only write that marks a
        /// run <see cref="WorkflowStatus.Completed"/> — the claim stamp taken before a resume deliberately
        /// re-writes the status the row already had — so the status distinguishes the two without the harness
        /// having to guess which call was which.
        /// </summary>
        public IEnumerable<(long, ReviewStage, WorkflowStatus, PrLifecycleState)> Retired =>
            StateWrites.Where(w => w.Item3 == WorkflowStatus.Completed);

        public List<string> Log { get; } = [];

        /// <summary>
        /// Every state write and every resume, interleaved in the order they happened. The claim stamp is only
        /// worth anything if it lands BEFORE the resume, and two separate lists cannot show that.
        /// </summary>
        public List<string> Order { get; } = [];

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

        public Harness WithLifecycleThrowingFor(long runId, Exception? failure = null)
        {
            _throwFor = runId;
            _failure = failure ?? new InvalidOperationException("provider unreachable");
            return this;
        }

        public Harness WithResumeResolvingTo(long runId)
        {
            _resolvesTo = runId;
            return this;
        }

        public Harness WithResumeThrowingFor(long runId)
        {
            _resumeThrowsFor = runId;
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
                return row.Run.Id == _throwFor ? throw _failure : Task.FromResult(_lifecycle);
            },
            resumeAsync: (run, _) =>
            {
                Order.Add($"resume:{run.Id}");
                if (run.Id == _resumeThrowsFor)
                {
                    throw new TimeoutException("the review's remaining stages timed out");
                }

                Resumed.Add(run);
                return Task.FromResult(_resolvesTo is { } id ? run with { Id = id } : run);
            },
            updateRunState: (id, stage, status, state) =>
            {
                Order.Add($"write:{id}");
                StateWrites.Add((id, stage, status, state));
            },
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
