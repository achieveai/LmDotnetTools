using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Refreshing the confidentiality trust signal on an existing run from the current poll.
/// <para>
/// <c>CreateOrGetReviewRun</c> is an <c>INSERT … ON CONFLICT</c> followed by a <c>SELECT</c>, so
/// <see cref="ReviewRun.IsForkPr"/> and <see cref="ReviewRun.IsTargetRepoPublic"/> were written once, at
/// creation, and never again. The orchestrator's reconcile block refreshed <see cref="PrLifecycleState"/>
/// from the fresh seed and nothing else — so a run created before a provider fix replayed its stale answer
/// on EVERY later poll, not merely on resume. Live: run 144 got the sibling gate CLOSED with a 2-rule
/// allow-list while runs 145 and 146, on the same binary and the same repo, got it OPEN with 7.
/// </para>
/// <para>
/// Today's risk direction happens to be benign — a stale signal IS the fail-closed default, so it denies
/// siblings rather than granting them. The MECHANISM is not benign: a run seeded while a repo was
/// same-trust keeps that answer even after the repo goes public, and then the staleness grants access
/// instead of withholding it. That asymmetry is what these tests exist to hold shut.
/// </para>
/// </summary>
public sealed class TrustSignalRefreshTests
{
    private static long Repo(ReviewStore store) => store.EnsureRepo(new RepoIdentity
    {
        Provider = "azure-devops",
        OrgOrOwner = "o365exchange",
        Project = "Weve_DA",
        RepoName = "Nova",
    });

    private static ReviewRun Seed(long repoId, bool isForkPr, bool isTargetRepoPublic) => new()
    {
        RepoId = repoId,
        PrId = "5504919",
        HeadSha = "head-sha",
        BaseSha = "base-sha",
        TriggerWatermark = "head-sha",
        ReviewKind = "full",
        VariantId = "primary",
        Mode = "post",
        Stage = ReviewStage.Discovered,
        WorkflowStatus = WorkflowStatus.Pending,
        PrLifecycleState = PrLifecycleState.Open,
        IsForkPr = isForkPr,
        IsTargetRepoPublic = isTargetRepoPublic,
    };

    private static PrOrchestrator Orchestrator(ReviewStore store) =>
        new(store, new RecordingStageExecutor(), NullLogger<PrOrchestrator>.Instance);

    /// <summary>
    /// Run 144's case. The run was created while the provider could not establish visibility, so it holds
    /// the fail-closed default; the provider can now answer. The next poll must adopt the answer, or the
    /// run carries a signal contradicted by evidence the daemon already has, forever.
    /// </summary>
    [Fact]
    public async Task A_stale_fail_closed_signal_is_refreshed_once_the_provider_can_answer()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var created = store.CreateOrGetReviewRun(Seed(repoId, isForkPr: true, isTargetRepoPublic: true));

        // Same identity tuple, so this re-polls the SAME row — now with a provider that answered.
        var run = await Orchestrator(store)
            .RunAsync(Seed(repoId, isForkPr: false, isTargetRepoPublic: false), CancellationToken.None);

        run.Id.Should().Be(created.Id, "the identity tuple is unchanged, so this must be the same run");
        store.GetReviewRun(created.Id)!.IsForkPr.Should().BeFalse();
        store.GetReviewRun(created.Id)!.IsTargetRepoPublic.Should().BeFalse(
            "the provider can now establish the signal, and a run must not keep contradicting it");
    }

    /// <summary>
    /// The direction that actually matters for safety, and the one a refresh could get catastrophically
    /// wrong: a repo that was same-trust when the run was created has since gone public. The refresh must
    /// TIGHTEN. A design that only ever relaxed a stale signal would hold a review's siblings open against
    /// a repo the world can now read.
    /// </summary>
    [Fact]
    public async Task A_signal_that_has_become_untrusted_is_tightened_not_kept()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var created = store.CreateOrGetReviewRun(Seed(repoId, isForkPr: false, isTargetRepoPublic: false));

        _ = await Orchestrator(store)
            .RunAsync(Seed(repoId, isForkPr: false, isTargetRepoPublic: true), CancellationToken.None);

        store.GetReviewRun(created.Id)!.IsTargetRepoPublic.Should().BeTrue(
            "the repo is public now; keeping the old same-trust answer would co-locate private siblings "
                + "beside a diff anyone can read");
    }

    /// <summary>
    /// The property the shared collapse is supposed to guarantee, pinned directly rather than trusted.
    /// A refresh must leave the run with EXACTLY the seed's signal — and in particular the gate may only
    /// end up open if the seed itself was open.
    /// <para>
    /// Asserting the implication separately from the equality is deliberate. Equality is what the current
    /// code happens to do; the implication is the invariant that must survive whatever it is replaced
    /// with. A future edit that read a nullable provider value directly, skipping
    /// <c>PrPollingService</c>'s <c>?? true</c> collapse, would still satisfy "refreshed from the seed"
    /// while quietly opening the gate on an unknown.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_refresh_is_never_more_permissive_than_the_seed_it_came_from(
        bool seedIsForkPr, bool seedIsTargetRepoPublic)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        // Start from the most permissive possible state, so any failure to tighten shows up.
        var created = store.CreateOrGetReviewRun(Seed(repoId, isForkPr: false, isTargetRepoPublic: false));

        _ = await Orchestrator(store)
            .RunAsync(Seed(repoId, seedIsForkPr, seedIsTargetRepoPublic), CancellationToken.None);

        var refreshed = store.GetReviewRun(created.Id)!;
        refreshed.IsForkPr.Should().Be(seedIsForkPr);
        refreshed.IsTargetRepoPublic.Should().Be(seedIsTargetRepoPublic);

        // The invariant, stated as itself: co-location may be permitted after a refresh only if the seed
        // permitted it. `!IsForkPr && !IsTargetRepoPublic` is DaemonReviewStageExecutor's gate condition.
        var gateOpenAfter = !refreshed.IsForkPr && !refreshed.IsTargetRepoPublic;
        var gateOpenOnSeed = !seedIsForkPr && !seedIsTargetRepoPublic;
        (!gateOpenAfter || gateOpenOnSeed).Should().BeTrue(
            "a refreshed run may only open the cross-repo gate on evidence the current poll actually "
                + "carried — never on anything weaker");
    }

    /// <summary>
    /// A refreshed run and a brand-new one built from the same poll must be indistinguishable in trust
    /// terms. If they can differ, then whether a PR gets siblings depends on when its run happened to be
    /// created, which is exactly the bug — just with a different set of victims.
    /// </summary>
    [Fact]
    public async Task A_refreshed_run_ends_up_where_a_fresh_run_from_the_same_poll_would()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var stale = store.CreateOrGetReviewRun(Seed(repoId, isForkPr: true, isTargetRepoPublic: true));

        _ = await Orchestrator(store)
            .RunAsync(Seed(repoId, isForkPr: false, isTargetRepoPublic: false), CancellationToken.None);

        // A different PR polled at the same moment, with the same provider evidence.
        var freshSeed = Seed(repoId, isForkPr: false, isTargetRepoPublic: false) with { PrId = "5504920" };
        var fresh = store.CreateOrGetReviewRun(freshSeed);

        var refreshed = store.GetReviewRun(stale.Id)!;
        refreshed.IsForkPr.Should().Be(fresh.IsForkPr);
        refreshed.IsTargetRepoPublic.Should().Be(
            fresh.IsTargetRepoPublic,
            "two PRs polled together must not get different sibling access because one's run row is older");
    }
}
