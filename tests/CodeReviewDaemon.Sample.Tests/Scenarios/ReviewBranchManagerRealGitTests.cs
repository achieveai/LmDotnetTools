using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The KB merge paths against REAL git (issue #471). Everything else about
/// <see cref="ReviewBranchManager"/> stays on <see cref="FakeSandboxCommandRunner"/>, which pins argv ORDER
/// — something real git cannot show. What real git shows, and an argv matcher structurally cannot, is
/// CONTENT: whether the entries a merge kept are still reachable from the listings afterwards.
/// <para>
/// The scenario every test here builds is the one issue #218 item 6 is about. Two PRs write knowledge
/// concurrently. PR A's notes branch adds <c>system/a.md</c> and regenerates the listings; PR B's merge
/// lands <c>system/b.md</c> and its own regenerated listings on the default branch first. Merging A is then
/// not a fast-forward, and <c>-X theirs</c> resolves the two whole-file listing rewrites in A's favour: A's
/// copy never mentioned B, so <c>b.md</c> stays in the tree while disappearing from the only two files
/// anything uses to find it.
/// </para>
/// </summary>
public sealed class ReviewBranchManagerRealGitTests : LoggingTestBase
{
    private const string DefaultBranch = "main";
    private const string NotesBranch = "review/widgets-42";

    public ReviewBranchManagerRealGitTests(ITestOutputHelper output)
        : base(output) { }

    /// <summary>
    /// The item-6 acceptance, end to end: after the merge the entries BOTH survive and both listings
    /// describe them. Nothing here is scripted — the merge, the conflict and its <c>-X theirs</c>
    /// resolution are real, and the listings are recomputed by the real
    /// <see cref="KnowledgeIndexRegenerator"/> from the tree that merge produced.
    /// </summary>
    [Fact]
    public async Task MergeToDefault_rebuilds_the_listings_over_a_real_contended_merge_so_no_entry_is_unindexed()
    {
        using var fixture = await RealGitFixture.CreateAsync();
        var store = await SeedContendedMergeAsync(fixture);

        var merged = await CreateManager(fixture, store, wireRebuild: true)
            .MergeToDefaultAsync(store, NotesBranch, DefaultBranch, CancellationToken.None);

        merged.Should().BeTrue();

        // Both entry files are in the tree — `-X theirs` never had to choose, they are different paths.
        RealGitFixture.Read(store, "KnowledgeBase/system/a.md").Should().NotBeNull();
        RealGitFixture.Read(store, "KnowledgeBase/system/b.md").Should().NotBeNull();

        // And both are reachable. This is the assertion an argv matcher cannot make: the listings are the
        // only route to an entry, so one that survives the merge but not the index is lost in every way that
        // matters to a reader.
        var index = RealGitFixture.Read(store, "KnowledgeBase/_index.jsonl");
        index.Should().Contain("system/a.md").And.Contain("system/b.md");
        var toc = RealGitFixture.Read(store, "KnowledgeBase/_toc.md");
        toc.Should().Contain("system/a.md").And.Contain("system/b.md");

        // Pushed, not merely resolved in the working tree: origin's default branch must carry it, because a
        // repair that never leaves the store checkout is repaired for nobody.
        var pushedIndex = await fixture.GitAsync(
            store,
            CancellationToken.None,
            "show",
            $"origin/{DefaultBranch}:KnowledgeBase/_index.jsonl"
        );
        pushedIndex.Should().Contain("system/b.md");

        // The notes branch is deleted only after a successful push, so its absence is the merge's receipt.
        var branches = await fixture.GitAsync(store, CancellationToken.None, "branch", "-r");
        branches.Should().NotContain(NotesBranch);
    }

    /// <summary>
    /// The non-vacuity proof for the test above. Wired with no regenerator — exactly what
    /// <see cref="ReviewBranchManager"/> did before issue #218 item 6 — the SAME fixture loses <c>b.md</c>
    /// from both listings while keeping its file. Without this, a rebuild that silently did nothing would
    /// still pass, as long as git happened to resolve the merge favourably.
    /// </summary>
    [Fact]
    public async Task MergeToDefault_without_the_rebuild_keeps_the_entry_file_but_drops_it_from_both_listings()
    {
        using var fixture = await RealGitFixture.CreateAsync();
        var store = await SeedContendedMergeAsync(fixture);

        var merged = await CreateManager(fixture, store, wireRebuild: false)
            .MergeToDefaultAsync(store, NotesBranch, DefaultBranch, CancellationToken.None);

        merged.Should().BeTrue();

        RealGitFixture
            .Read(store, "KnowledgeBase/system/b.md")
            .Should()
            .NotBeNull("`-X theirs` resolves per conflicting hunk; b.md is a different path, so nothing touched it");
        RealGitFixture
            .Read(store, "KnowledgeBase/_index.jsonl")
            .Should()
            .Contain("system/a.md")
            .And.NotContain(
                "system/b.md",
                "the listings are whole-file rewrites, so `theirs` took the notes branch's copy wholesale"
            );
        RealGitFixture.Read(store, "KnowledgeBase/_toc.md").Should().NotContain("system/b.md");
    }

    /// <summary>
    /// A fast-forward merge, with default's listings already broken — the state a swallowed rebuild failure
    /// leaves behind (issue #469 item 2). The notes branch adds only a PR note, so extraction never ran and
    /// never regenerated anything: nothing on either side repairs the listings except the merge itself.
    /// </summary>
    [Fact]
    public async Task MergeToDefault_rebuilds_the_listings_on_a_fast_forward_when_the_default_left_them_broken()
    {
        using var fixture = await RealGitFixture.CreateAsync();
        var store = await SeedBrokenListingsFastForwardAsync(fixture);

        var merged = await CreateManager(fixture, store, wireRebuild: true)
            .MergeToDefaultAsync(store, NotesBranch, DefaultBranch, CancellationToken.None);

        merged.Should().BeTrue();

        // b.md was in the tree and in neither listing before the merge. A fast-forward carries the notes
        // branch's tree over wholesale — including the broken listings it inherited — so if the rebuild is
        // skipped here nothing else will ever run: the extraction's own regen is gated on an entry WRITE
        // (KnowledgeAgent), and a run of declined extractions writes none.
        var index = RealGitFixture.Read(store, "KnowledgeBase/_index.jsonl");
        index.Should().Contain("system/a.md").And.Contain("system/b.md");
        RealGitFixture.Read(store, "KnowledgeBase/_toc.md").Should().Contain("system/b.md");

        var pushedIndex = await fixture.GitAsync(
            store,
            CancellationToken.None,
            "show",
            $"origin/{DefaultBranch}:KnowledgeBase/_index.jsonl"
        );
        pushedIndex.Should().Contain("system/b.md");
    }

    /// <summary>
    /// The ordinary fast-forward — the overwhelming majority of merges — must still commit nothing. The
    /// renderers are deterministic and sorted, so a rebuild over a tree whose listings already describe it
    /// stages nothing and the <c>diff --cached --quiet</c> gate returns before <c>commit</c>. Rebuilding on
    /// every fast-forward would otherwise put an empty commit on the default branch on every sweep.
    /// </summary>
    [Fact]
    public async Task MergeToDefault_adds_no_commit_on_a_fast_forward_whose_listings_already_match()
    {
        using var fixture = await RealGitFixture.CreateAsync();
        var store = await SeedHealthyFastForwardAsync(fixture);
        var before = (await fixture.GitAsync(store, CancellationToken.None, "rev-list", "--count", "HEAD")).Trim();

        var merged = await CreateManager(fixture, store, wireRebuild: true)
            .MergeToDefaultAsync(store, NotesBranch, DefaultBranch, CancellationToken.None);

        merged.Should().BeTrue();

        var after = (await fixture.GitAsync(store, CancellationToken.None, "rev-list", "--count", "HEAD")).Trim();
        int.Parse(after, System.Globalization.CultureInfo.InvariantCulture)
            .Should()
            .Be(
                int.Parse(before, System.Globalization.CultureInfo.InvariantCulture) + 1,
                "the fast-forward advances by the notes commit alone; the rebuild must have nothing to add"
            );
        var subject = await fixture.GitAsync(store, CancellationToken.None, "log", "-1", "--format=%s");
        subject.Should().NotContain("rebuild the knowledge listings");
    }

    /// <summary>
    /// The push-rebase retry (<c>TryPushWithRebaseAsync</c>) replayed against a default branch that really
    /// advanced — issue #469 item 3. Real git is the only thing that can answer what the rebase does to the
    /// rebuild commit; an argv matcher scripts a push failure and a rebase success and asserts nothing about
    /// the tree either produced. The invariant this pins, and the mechanism behind it, are written up at
    /// <c>TryPushWithRebaseAsync</c>'s call site.
    /// </summary>
    [Fact]
    public async Task MergeToDefault_replays_over_a_default_that_advanced_and_still_lands_every_entry_indexed()
    {
        using var fixture = await RealGitFixture.CreateAsync();
        var store = await SeedContendedMergeAsync(fixture);

        // A third PR lands on origin AFTER this sweep fetched, so the first push is rejected non-fast-forward
        // and TryPushWithRebaseAsync pulls --rebase and retries.
        var other = await fixture.CloneAsync("racer");
        await fixture.GitAsync(other, CancellationToken.None, "checkout", DefaultBranch);
        RealGitFixture.Write(other, "KnowledgeBase/system/c.md", Entry("C"));
        await RegenerateAsync(other);
        await fixture.GitAsync(other, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(other, CancellationToken.None, "commit", "-m", "pr-c");
        await fixture.GitAsync(other, CancellationToken.None, "push", "origin", DefaultBranch);

        var merged = await CreateManager(fixture, store, wireRebuild: true)
            .MergeToDefaultAsync(store, NotesBranch, DefaultBranch, CancellationToken.None);

        // Non-vacuity: the first push really was rejected and the retry really ran. Without this the test
        // would keep passing if the race stopped racing — a fast-forwarding push asserts nothing at all.
        fixture
            .Commands.Should()
            .Contain(
                c => c.Contains($"pull --rebase origin {DefaultBranch}", StringComparison.Ordinal),
                "the point of this test is the retry path, which only runs after a rejected push"
            );

        // Either outcome is correct by design and both are non-losing: the rebase applies (and the retry
        // pushes) or it conflicts, in which case the branch is KEPT and the next sweep re-merges non-ff.
        // What must never happen is a push that lands a tree whose listings under-report it.
        if (merged)
        {
            var pushed = await fixture.GitAsync(
                store,
                CancellationToken.None,
                "show",
                $"origin/{DefaultBranch}:KnowledgeBase/_index.jsonl"
            );
            var entries = await ListedEntriesAsync(fixture, store);
            foreach (var entry in entries)
            {
                pushed
                    .Should()
                    .Contain(
                        entry,
                        "an entry that reached the default branch unindexed is unreachable to every reader"
                    );
            }
        }
        else
        {
            var branches = await fixture.GitAsync(store, CancellationToken.None, "branch", "-r");
            branches
                .Should()
                .Contain(NotesBranch, "a failed push must keep the notes branch so the next sweep can re-merge it");
        }
    }

    /// <summary>
    /// The cleanup half of issue #471's acceptance. Git creates loose objects read-only, so the customary
    /// <c>try { Directory.Delete } catch { }</c> around a temp repo silently leaks one per test run. This
    /// asserts the fixture's disposal actually removes the tree — including a repo with real objects in it.
    /// </summary>
    [Fact]
    public async Task Dispose_removes_the_temp_repositories_despite_gits_read_only_objects()
    {
        string root;
        using (var fixture = await RealGitFixture.CreateAsync())
        {
            root = fixture.Root;
            var repo = await fixture.CloneAsync("work");
            RealGitFixture.Write(repo, "a.txt", "hello");
            await fixture.GitAsync(repo, CancellationToken.None, "add", "-A");
            await fixture.GitAsync(repo, CancellationToken.None, "commit", "-m", "seed");

            // The hazard is real and not hypothetical: assert git actually produced read-only files, so a
            // future git that stopped doing so turns this test into a stated finding rather than a silent
            // pass that keeps claiming a guarantee nothing exercises.
            new DirectoryInfo(Path.Combine(repo, ".git"))
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Any(f => f.Attributes.HasFlag(FileAttributes.ReadOnly))
                .Should()
                .BeTrue("git writes loose objects read-only, which is what makes Directory.Delete throw");
        }

        Directory.Exists(root).Should().BeFalse();
    }

    private ReviewBranchManager CreateManager(RealGitFixture fixture, string repoRoot, bool wireRebuild)
    {
        var git = new GitRunner(fixture.Runner);
        var fileSystem = new HostFileSystem();
        Func<string, CancellationToken, Task<bool>>? rebuild = null;
        if (wireRebuild)
        {
            // Wired exactly as Program.cs wires the sweeper's manager, so what these tests exercise is the
            // production pairing and not a test-only regenerator.
            var regenerator = new KnowledgeIndexRegenerator(
                fileSystem,
                LoggerFactory.CreateLogger<KnowledgeIndexRegenerator>()
            );
            rebuild = (root, ct) =>
                regenerator.RegenerateAsync(
                    $"{root.TrimEnd('/', '\\')}/{KnowledgeIndexRegenerator.KnowledgeBaseDirectory}",
                    ct
                );
        }

        _ = repoRoot;
        return new ReviewBranchManager(git, fileSystem, LoggerFactory.CreateLogger<ReviewBranchManager>(), rebuild);
    }

    /// <summary>
    /// Builds the concurrent-knowledge collision: a notes branch carrying <c>a.md</c> and a default branch
    /// that advanced with <c>b.md</c>, each side having regenerated the listings for its own entry. Returns
    /// the sweeper's store clone, positioned as the sweeper finds it.
    /// </summary>
    private async Task<string> SeedContendedMergeAsync(RealGitFixture fixture)
    {
        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "KnowledgeBase/system/base.md", Entry("Base"));
        await RegenerateAsync(seed);
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", DefaultBranch);

        // PR A's notes branch: a new entry plus the listings its extraction regenerated.
        await fixture.GitAsync(seed, CancellationToken.None, "checkout", "-b", NotesBranch);
        RealGitFixture.Write(seed, "PRs/widgets-42/review.md", "# Review\n");
        RealGitFixture.Write(seed, "KnowledgeBase/system/a.md", Entry("A"));
        await RegenerateAsync(seed);
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "pr-a notes");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", NotesBranch);

        // PR B reached the default branch first, so A's merge is no longer a fast-forward and both sides
        // have rewritten the same two whole-file listings.
        await fixture.GitAsync(seed, CancellationToken.None, "checkout", DefaultBranch);
        RealGitFixture.Write(seed, "KnowledgeBase/system/b.md", Entry("B"));
        await RegenerateAsync(seed);
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "pr-b merged");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", DefaultBranch);

        return await fixture.CloneAsync("store");
    }

    /// <summary>
    /// Builds a fast-forward whose listings are ALREADY broken on the default branch — <c>b.md</c> present
    /// in the tree, absent from both listings — and a notes branch that adds only a PR note on top. This is
    /// the state a swallowed post-merge rebuild leaves, and it persists across sweeps because the
    /// extraction's own regen only runs after an entry write.
    /// </summary>
    private async Task<string> SeedBrokenListingsFastForwardAsync(RealGitFixture fixture)
    {
        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "KnowledgeBase/system/a.md", Entry("A"));
        // Regenerate while only a.md exists, THEN add b.md without regenerating: the listings are the real
        // renderers' output for a tree that no longer matches them, which is exactly what a swallowed
        // post-merge rebuild leaves behind.
        await RegenerateAsync(seed);
        RealGitFixture.Write(seed, "KnowledgeBase/system/b.md", Entry("B"));
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed with under-reporting listings");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", DefaultBranch);

        // The notes branch writes a PR note and NO knowledge entry — a declined extraction — so nothing
        // regenerates the listings on this side either.
        await fixture.GitAsync(seed, CancellationToken.None, "checkout", "-b", NotesBranch);
        RealGitFixture.Write(seed, "PRs/widgets-42/review.md", "# Review\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "pr-a notes");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", NotesBranch);

        return await fixture.CloneAsync("store");
    }

    /// <summary>The healthy fast-forward: the listings on both sides already describe the tree.</summary>
    private async Task<string> SeedHealthyFastForwardAsync(RealGitFixture fixture)
    {
        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "KnowledgeBase/system/a.md", Entry("A"));
        await RegenerateAsync(seed);
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", DefaultBranch);

        await fixture.GitAsync(seed, CancellationToken.None, "checkout", "-b", NotesBranch);
        RealGitFixture.Write(seed, "PRs/widgets-42/review.md", "# Review\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "pr-a notes");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", NotesBranch);

        return await fixture.CloneAsync("store");
    }

    /// <summary>The entry files present in the checkout, as KB-relative paths.</summary>
    private static async Task<IReadOnlyList<string>> ListedEntriesAsync(RealGitFixture fixture, string repoRoot)
    {
        var listed = await fixture.GitAsync(repoRoot, CancellationToken.None, "ls-files", "--", "KnowledgeBase/system");
        return
        [
            .. listed
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.EndsWith(".md", StringComparison.Ordinal))
                .Select(p => p["KnowledgeBase/".Length..]),
        ];
    }

    /// <summary>A Knowledge Base entry with the frontmatter the regenerator parses.</summary>
    private static string Entry(string title) =>
        $"---\ntitle: {title}\ntags: []\nscope: system\nsource_prs: []\nupdated: 2026-01-01\n---\n\n{title} body.\n";

    /// <summary>
    /// Writes the listings for whatever entries are on disk, using the REAL renderers — the same call the
    /// extraction makes after an entry write. Hand-rolling the two files instead looks equivalent and is not:
    /// any divergence from the renderers' exact bytes makes every later rebuild report "changed", so the
    /// no-op assertions would be measuring the fixture's formatting rather than the code's behaviour. (That
    /// is not hypothetical — the first draft of this fixture hand-rolled them and the healthy fast-forward
    /// grew a spurious repair commit.)
    /// </summary>
    private async Task RegenerateAsync(string repoPath) =>
        _ = await new KnowledgeIndexRegenerator(
            new HostFileSystem(),
            LoggerFactory.CreateLogger<KnowledgeIndexRegenerator>()
        ).RegenerateAsync(
            Path.Combine(repoPath, KnowledgeIndexRegenerator.KnowledgeBaseDirectory),
            CancellationToken.None
        );
}
