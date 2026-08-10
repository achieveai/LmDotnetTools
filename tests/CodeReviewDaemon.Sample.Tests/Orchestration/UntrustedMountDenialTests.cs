using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The confidentiality contract is that an UNTRUSTED pull request — a fork, a public target, or one whose
/// trust nothing established — never gets another repository co-located beside it, so a prompt-injected diff
/// has nothing extra to read and surface in the review the daemon posts.
/// <para>
/// Until now that contract was enforced by OMISSION: <c>BuildStoreSubmoduleAllowList</c> declined to
/// initialize the sibling submodules, and the siblings were therefore absent from disk. That only held
/// because each reviewed repo got its own mount. The moment one depot serves every repo the siblings are
/// already checked out from some other repo's review, and the gate does not fail — it simply stops being
/// true, with nothing in the code stating the layout property it was relying on.
/// </para>
/// <para>
/// These tests pin the property itself rather than the mechanism, deliberately: they assert on the paths a
/// run RESOLVES. A test that inspected the allow-list would keep passing while the thing the allow-list
/// stands for became false, which is the exact failure mode being closed.
/// </para>
/// </summary>
public sealed class UntrustedMountDenialTests
{
    private const string SubmoduleRelPath = "repos/LmDotnetTools";

    // ---------------------------------------------------------------------------------------------------
    // The sibling POINTERS: the resolved paths the daemon hands the reviewer in its brief.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The brief's "Related repositories" block names exact, openable paths. It advertises whichever store
    /// submodules are POPULATED — a rule that silently doubled as the confidentiality gate for as long as
    /// "populated" meant "this run was granted it". On a shared depot they are populated by someone else's
    /// review, so an untrusted run would be handed the paths outright.
    /// </summary>
    [Theory]
    [InlineData(true, false, "a fork PR's diff is written by someone outside the trust domain")]
    [InlineData(false, true, "a public target means the diff is world-writable input")]
    [InlineData(true, true, "nothing established this run's trust, which fails closed like a confirmed fork")]
    public async Task An_untrusted_run_is_handed_no_sibling_path_even_when_the_siblings_are_on_disk(
        bool isForkPr, bool isTargetRepoPublic, string because)
    {
        using var fixture = new ContextStageHarness(ContextStageHarness.DedicatedMount, s2s: true);
        fixture.SeedPopulatedSiblings();
        var run = fixture.SeedRun(isForkPr, isTargetRepoPublic);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.SiblingPaths(run).Should().BeEmpty(because);
    }

    [Fact]
    public async Task A_trusted_run_is_still_handed_the_sibling_paths()
    {
        // The denial has to be scoped to untrusted runs, or it is just a removal of the feature: co-located
        // siblings are the whole reason the store exists, and a same-trust-domain PR is entitled to them.
        using var fixture = new ContextStageHarness(ContextStageHarness.DedicatedMount, s2s: true);
        fixture.SeedPopulatedSiblings();
        var run = fixture.SeedRun(isForkPr: false, isTargetRepoPublic: false);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.SiblingPaths(run).Should().Contain(p => p.EndsWith("repos/Nova", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bite for the two tests above, held in ONE method so it cannot be deleted independently of the
    /// assertion it protects. Identical fixture, identical siblings seeded on disk, identical mount: the trust
    /// flag is the only input that varies between the two calls.
    /// <para>
    /// Without the pairing, <c>An_untrusted_run_is_handed_no_sibling_path…</c> keeps passing if the fixture
    /// ever loses the ability to produce siblings at all — the gate would read as enforced while nothing was
    /// enforcing it. An empty result is only evidence of a gate when the same setup demonstrably yields a
    /// non-empty one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Trust_is_the_only_input_that_decides_whether_the_siblings_are_handed_over()
    {
        static async Task<IReadOnlyList<string>> SiblingsForAsync(bool isForkPr, bool isTargetRepoPublic)
        {
            using var fixture = new ContextStageHarness(ContextStageHarness.DedicatedMount, s2s: true);
            fixture.SeedPopulatedSiblings();
            var run = fixture.SeedRun(isForkPr, isTargetRepoPublic);
            await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
            return fixture.SiblingPaths(run);
        }

        var trusted = await SiblingsForAsync(isForkPr: false, isTargetRepoPublic: false);
        var untrusted = await SiblingsForAsync(isForkPr: true, isTargetRepoPublic: false);

        // EXACT counts and EXACT paths, not NotBeEmpty/NotContain. An "is empty" that passes because nothing
        // could ever have been added asserts nothing, and a NotContain passes just as happily against a
        // collection the fixture is incapable of filling. The trusted leg names what is there; the untrusted
        // leg names how many are there. Either alone is vacuous — the pair is what makes both load-bearing.
        trusted.Should().BeEquivalentTo(
            ["/workspace/store/repos/Nova", "/workspace/store/Contracts"],
            "both seeded siblings are populated and neither is the reviewed repo, which is excluded by design");
        untrusted.Should().HaveCount(
            0,
            "the trust flag is the only input that changed, so it is the only thing that can have withheld them");
    }

    // ---------------------------------------------------------------------------------------------------
    // The MOUNT itself.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The load-bearing test. On a depot mount an untrusted run must not resolve a checkout inside it at all
    /// — not merely be denied the siblings, because everything else in that depot is still one <c>cd</c>
    /// away from a checkout that lives in it. Asserted on the RESOLVED checkout root: the run falls back to
    /// the per-PR clone, whose root is provably outside the depot.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_untrusted_run_never_resolves_a_checkout_inside_a_cross_repo_depot(
        bool isForkPr, bool isTargetRepoPublic)
    {
        using var fixture = new ContextStageHarness(ContextStageHarness.DepotMount, s2s: false);
        var run = fixture.SeedRun(isForkPr, isTargetRepoPublic);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var payload = fixture.ContextPayload(run);
        payload.GetProperty("CheckoutRoot").GetString()
            .Should().Be("/workspace/target", "an untrusted run degrades to its own per-PR clone");
        payload.GetProperty("StoreRoot").ValueKind.Should().Be(
            JsonValueKind.Null, "the per-PR clone has no store, shared or otherwise");
        fixture.Pool.Returned.Should().ContainSingle(
            "a slot the run was refused must go straight back to the pool, not leak capacity");
    }

    [Fact]
    public async Task A_trusted_run_does_resolve_into_the_depot()
    {
        using var fixture = new ContextStageHarness(ContextStageHarness.DepotMount, s2s: true);
        var run = fixture.SeedRun(isForkPr: false, isTargetRepoPublic: false);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.ContextPayload(run).GetProperty("StoreRoot").GetString()
            .Should().NotBeNull("a same-trust-domain run keeps the pooled store path");
    }

    /// <summary>
    /// No regression on the layout in production today. One mount per repository is dedicated by
    /// construction, so an untrusted run on it is co-located with nothing and keeps the pooled path — the
    /// warm slot, the notes branch and the Knowledge Base — exactly as before.
    /// </summary>
    [Fact]
    public async Task An_untrusted_run_keeps_the_pooled_path_on_a_mount_dedicated_to_its_own_repo()
    {
        using var fixture = new ContextStageHarness(ContextStageHarness.DedicatedMount, s2s: true);
        var run = fixture.SeedRun(isForkPr: true, isTargetRepoPublic: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.ContextPayload(run).GetProperty("StoreRoot").GetString()
            .Should().NotBeNull("a mount holding only this repo co-locates nothing to withhold");
    }

    /// <summary>
    /// On S2S with a workspace preparer wired — the shape EVERY live profile runs — there is no per-PR
    /// fallback to degrade to: the executor deliberately refuses to mint an unmanaged per-PR host clone and
    /// LmStreaming workspace, because nothing reclaims them. So a depot mount and an untrusted run is a
    /// configuration with no safe outcome, and it has to say so rather than review the PR with the depot
    /// mounted.
    /// </summary>
    [Fact]
    public async Task On_s2s_an_untrusted_run_on_a_depot_is_refused_rather_than_reviewed()
    {
        using var fixture = new ContextStageHarness(ContextStageHarness.DepotMount, s2s: true, wireS2SPreparer: true);
        var run = fixture.SeedRun(isForkPr: true, isTargetRepoPublic: true);

        var act = async () =>
            await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
                .Contain(ContextStageHarness.DepotMount, "the operator has to be told which mount was refused")
                .And.Contain("untrusted", "and why");
        fixture.Store.GetArtifacts(run.Id).Should().BeEmpty("a refused run reviews nothing");
        fixture.Pool.Returned.Should().ContainSingle("the slot goes back even when the run is refused");
    }

    // ---------------------------------------------------------------------------------------------------
}
