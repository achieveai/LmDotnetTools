using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// "No new findings since the last review" is a claim ABOUT AN EARLIER REVIEW, and a run that has no earlier
/// review cannot make it. The daemon made it 57 times: of 116 first-ever primary rounds in the live store,
/// 57 came back as nothing but that sentence — 51 with no prior run on the PR at all, and 6 whose only prior
/// runs were parked at <see cref="ReviewStage.Discovered"/> or <see cref="ReviewStage.ContextReady"/>. Each
/// took the sentinel exit on turn 1, never fanned out to a sub-agent, and was recorded as a completed review
/// of a PR nobody had reviewed.
/// <para>
/// The condition was already DETECTED — the Reviewed stage has logged a warning naming it exactly since the
/// check was added — and all 57 sailed past it, because a log line is not a control
/// (the daemon's own <c>a-documented-control-is-not-a-control</c> lesson, one level down). These tests pin
/// the enforced version: the store is asked, at the point of use, whether a prior review BODY exists, and a
/// sentinel that cannot answer yes fails the run instead of being persisted.
/// </para>
/// <para>
/// Every test asserts the persisted artifact as well as the throw. The throw alone is not the property that
/// matters — a guard that throws AFTER writing the body has still shipped the false claim to everything that
/// reads the artifact later.
/// </para>
/// </summary>
public sealed class SentinelAuthorizationTests
{
    /// <summary>Exactly the body the prompt mandates for the no-op exit, and the one all 57 live runs emitted.</summary>
    private const string Sentinel = "No new findings since the last review.";

    private const string RealFindings = "## Review\nMust: null check missing in Foo.cs:10.";

    private const string ProviderId = "gpt-5.6-luna";

    /// <summary>
    /// The 51-run case. Nothing on this PR precedes this run, so there is no "last review" for findings to be
    /// new since, and the only honest outcome is to fail and retry.
    /// </summary>
    [Fact]
    public async Task A_first_ever_review_may_not_answer_that_nothing_changed()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store, Sentinel);
        var run = SeedRun(store);

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no earlier primary round*");
        store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind).Should().BeNull(
            "a claim the run is not entitled to make must not survive the run that made it");
    }

    /// <summary>
    /// The 6-run case, and the one a naive fix gets wrong. A prior <c>review_run</c> row EXISTS on this PR —
    /// so "has this PR been seen before?" answers yes — but that run died at
    /// <see cref="ReviewStage.ContextReady"/> without reviewing anything. A row is not a review.
    /// </summary>
    [Fact]
    public async Task A_prior_run_that_never_reviewed_does_not_authorize_the_no_change_answer()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store, Sentinel);
        var repoId = EnsureRepo(store);
        _ = SeedPriorRound(store, repoId, ReviewStage.ContextReady, reviewText: null);
        var run = store.CreateOrGetReviewRun(RunSeed(repoId, "head-sha", "wm-1", ReviewStage.Discovered));

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind).Should().BeNull();
    }

    /// <summary>
    /// The same shape one step further along: the prior run got as far as PERSISTING a review body, then died
    /// before its stage advanced — reachable, because the orchestrator writes the artifact inside the stage
    /// and advances the stage only after the stage returns. That body was never delivered and is not carried
    /// forward either (<c>GetUndeliveredPriorReviews</c> applies the same stage filter), so a reader of this
    /// PR has still never seen a review, and the sentence would be false for them.
    /// </summary>
    [Fact]
    public async Task A_prior_round_that_persisted_a_body_but_never_reached_the_reviewed_stage_does_not_authorize_it()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store, Sentinel);
        var repoId = EnsureRepo(store);
        _ = SeedPriorRound(store, repoId, ReviewStage.ContextReady, RealFindings);
        var run = store.CreateOrGetReviewRun(RunSeed(repoId, "head-sha", "wm-1", ReviewStage.Discovered));

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind).Should().BeNull();
    }

    /// <summary>
    /// The guard must not cost the daemon the exit it exists for. A genuine second round, whose predecessor
    /// reached <see cref="ReviewStage.Posted"/> with real findings, is entitled to say nothing changed — and
    /// the body it says it with is what gets persisted.
    /// </summary>
    [Fact]
    public async Task A_genuine_re_review_may_still_answer_that_nothing_changed()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store, Sentinel);
        var repoId = EnsureRepo(store);
        _ = SeedPriorRound(store, repoId, ReviewStage.Posted, RealFindings);
        var run = store.CreateOrGetReviewRun(RunSeed(repoId, "head-sha", "wm-1", ReviewStage.Discovered));

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var artifact = store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind);
        artifact.Should().NotBeNull("the answer is true here, and the run is a completed review");
        JsonSerializer.Deserialize<ReviewArtifactPayload>(artifact!.Payload)!.ReviewText.Should().Be(Sentinel);
    }

    /// <summary>
    /// A chain of sentinels bottoms out nowhere. If round 01 said "nothing new since the last review" without
    /// a last review, round 02 may not point at round 01 as its authority — otherwise the exact runs this
    /// guard refuses would re-authorize themselves on their next round, and the live store already holds 58
    /// such bodies to point at.
    /// </summary>
    [Fact]
    public async Task A_prior_round_that_itself_answered_nothing_changed_does_not_authorize_another()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store, Sentinel);
        var repoId = EnsureRepo(store);
        _ = SeedPriorRound(store, repoId, ReviewStage.Posted, Sentinel);
        var run = store.CreateOrGetReviewRun(RunSeed(repoId, "head-sha", "wm-1", ReviewStage.Discovered));

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind).Should().BeNull();
    }

    /// <summary>
    /// The same hole as the chained sentinel, one shape over: a prior round that reached
    /// <see cref="ReviewStage.Posted"/> having produced NO prose. That state is reachable and the suite
    /// already pins its downstream behaviour (an empty review posts nothing rather than claiming the head's
    /// dedup slot), so a later round can find an artifact row whose body says nothing at all. Pointing "no new
    /// findings since the last review" at it names a review that reported nothing as the thing findings would
    /// be new since.
    /// </summary>
    [Fact]
    public async Task A_prior_round_that_produced_no_prose_does_not_authorize_the_no_change_answer()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store, Sentinel);
        var repoId = EnsureRepo(store);
        _ = SeedPriorRound(store, repoId, ReviewStage.Posted, reviewText: "   \n");
        var run = store.CreateOrGetReviewRun(RunSeed(repoId, "head-sha", "wm-1", ReviewStage.Discovered));

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ReviewArtifactKind).Should().BeNull();
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static DaemonReviewStageExecutor Executor(ReviewStore store, string reviewText)
    {
        var sandbox = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree",
                new SandboxCommandResult(1, string.Empty, "not a git repo")
            )
            .OnArgvContains(
                "diff",
                new SandboxCommandResult(
                    0,
                    "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;",
                    string.Empty
                )
            );

        return new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory { Resumable = true, DefaultText = reviewText },
            sandbox,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                LmStreamingProviderId = ProviderId,
            },
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance
        );
    }

    private static long EnsureRepo(ReviewStore store) =>
        store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );

    /// <summary>
    /// The standing regression detector, tested rather than assumed. An untested detector is the same shape of
    /// mistake as the warning this guard replaced — present, plausible, and never once exercised.
    /// </summary>
    [Fact]
    public void The_standing_check_counts_a_first_review_that_claimed_nothing_changed()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        _ = SeedPriorRound(store, EnsureRepo(store), ReviewStage.Posted, Sentinel);

        var payloads = store.GetFirstReviewPayloadsSince(
            EpochStart,
            DaemonReviewStageExecutor.ReviewArtifactKind
        );

        payloads.Should().ContainSingle();
        DaemonReviewStageExecutor
            .IsNoNewFindingsSentinel(ReviewTextOf(payloads[0]))
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// A later round's sentinel is legitimate and must not inflate the rate: only each PR's FIRST review is
    /// counted, so healthy re-review traffic cannot read as a regression.
    /// </summary>
    [Fact]
    public void The_standing_check_ignores_a_later_rounds_sentinel()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var repoId = EnsureRepo(store);
        _ = SeedPriorRound(store, repoId, ReviewStage.Posted, RealFindings);

        var later = store.CreateOrGetReviewRun(
            RunSeed(repoId, "head-sha", "wm-1", ReviewStage.Posted)
        );
        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = later.Id,
                ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = "github",
                Payload = JsonSerializer.Serialize(
                    new ReviewArtifactPayload(Sentinel, "later-run", "primary")
                ),
            }
        );

        var payloads = store.GetFirstReviewPayloadsSince(
            EpochStart,
            DaemonReviewStageExecutor.ReviewArtifactKind
        );

        payloads.Should().ContainSingle("only the PR's first review counts");
        DaemonReviewStageExecutor
            .IsNoNewFindingsSentinel(ReviewTextOf(payloads[0]))
            .Should()
            .BeFalse();
    }

    /// <summary>A cutoff old enough to include every seeded artifact, so these two pin the FIRST-review rule
    /// rather than the time window.</summary>
    private const string EpochStart = "2000-01-01T00:00:00.0000000+00:00";

    private static string? ReviewTextOf(string payload) =>
        JsonSerializer
            .Deserialize<ReviewArtifactPayload>(payload, DaemonReviewStageExecutor.PayloadOptions)
            ?.ReviewText;

    private static ReviewRun SeedRun(ReviewStore store) =>
        store.CreateOrGetReviewRun(
            RunSeed(EnsureRepo(store), "head-sha", "wm-1", ReviewStage.Discovered)
        );

    /// <summary>
    /// An earlier round on the same PR, left at <paramref name="stage"/>. <paramref name="reviewText"/> null
    /// means it persisted no review artifact — a run that was discovered and then died, which is the state
    /// all six of the live prior runs were in.
    /// </summary>
    private static ReviewRun SeedPriorRound(
        ReviewStore store,
        long repoId,
        ReviewStage stage,
        string? reviewText
    )
    {
        var prior = store.CreateOrGetReviewRun(RunSeed(repoId, "old-head-sha", "wm-0", stage));
        if (reviewText is not null)
        {
            _ = store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = prior.Id,
                    ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
                    ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                    Provider = "github",
                    Payload = JsonSerializer.Serialize(
                        new ReviewArtifactPayload(reviewText, "prior-run", "primary")
                    ),
                }
            );
        }

        return prior;
    }

    private static ReviewRun RunSeed(long repoId, string headSha, string watermark, ReviewStage stage) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = headSha,
            BaseSha = "base-sha",
            TriggerWatermark = watermark,
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = stage,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };
}
