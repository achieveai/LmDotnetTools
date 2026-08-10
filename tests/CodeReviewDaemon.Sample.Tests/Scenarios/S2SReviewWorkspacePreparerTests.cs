using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="S2SReviewWorkspacePreparer"/> — the "name a host directory to LmStreaming as
/// the review's workspace" step that runs before an S2S review provisions. They pin the contracts the
/// shared-host topology depends on: the derived leaf is <c>SanitizeDirectory</c>-stable AND repo-unique (so
/// the daemon's host dir and LmStreaming's stored <c>DirectoryRelPath</c> name the SAME directory, and two
/// repos sharing a PR number do not name the same one), the degraded per-PR clone drives the exact
/// probe→clone→fetch→checkout git sequence against the PR's base/head shas and the provider-correct remote,
/// the pooled path adopts an already-populated leased slot without running any git, and an existing
/// workspace for the same leaf is reused rather than duplicated.
/// </summary>
public sealed class S2SReviewWorkspacePreparerTests
{
    private const string BasePath = "/sandbox/workspaces";
    private const string GithubLeaf = "review-github-achieveai-lmdotnettools-pr-118";

    [Theory]
    [InlineData("118", "review-github-achieveai-lmdotnettools-pr-118")]
    [InlineData("feature/x", "review-github-achieveai-lmdotnettools-pr-featurex")] // path separators stripped
    [InlineData("1 2", "review-github-achieveai-lmdotnettools-pr-1-2")] // whitespace runs collapse to '-'
    [InlineData("..", "review-github-achieveai-lmdotnettools-pr")] // '..' removed then trailing '-' trimmed
    public void DeriveLeaf_produces_a_sanitize_stable_single_segment_leaf(string prId, string expected)
    {
        S2SReviewWorkspacePreparer
            .DeriveLeaf(MakeRepo("github", project: null), "github", prId)
            .Should().Be(expected);
    }

    [Fact]
    public void DeriveLeaf_gives_repos_that_share_a_pr_number_different_leaves()
    {
        // Two repos reviewed by the same daemon routinely share a PR number. A number-only leaf would put
        // both reviews in ONE directory, each clobbering the other's checkout — the exact interference the
        // per-review workspace exists to prevent.
        var target = S2SReviewWorkspacePreparer.DeriveLeaf(MakeRepo("github", project: null), "github", "42");
        var other = S2SReviewWorkspacePreparer.DeriveLeaf(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "AchieveAiReviews",
                RepoStableId = "repo-stable-2",
            },
            "github",
            "42");

        other.Should().NotBe(target, "the leaf carries provider + owner + repo, not just the PR number");
    }

    [Fact]
    public async Task PrepareAsync_host_clones_the_pr_base_and_head_into_the_leaf_dir_for_github()
    {
        // A fresh host dir: the rev-parse probe fails, so the preparer must clone → fetch the exact base+head
        // → force-checkout the head. Fetch/checkout succeed via the runner's default result.
        var git = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a repo"));
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-new\",\"name\":\"Review PR #118\",\"directoryRelPath\":\"" + GithubLeaf + "\","
                    + "\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.PrepareAsync(
            MakeRun("118"), MakeRepo("github", project: null), "github", CancellationToken.None);

        prepared.Leaf.Should().Be(GithubLeaf);
        prepared.WorkspaceId.Should().Be("ws-new");

        var commands = git.Commands.Select(c => string.Join(" ", c.Argv)).ToList();
        commands.Should().Contain(
            c => c.Contains("clone")
                && c.Contains("https://github.com/achieveai/LmDotnetTools.git")
                && c.Contains($"{BasePath}/{GithubLeaf}"),
            "the checkout is host-cloned into {base}/{leaf}");
        commands.Should().Contain(c => c.Contains("fetch origin base-sha head-sha"), "the exact PR commits are fetched");
        commands.Should().Contain(c => c.Contains("checkout --force head-sha"), "the PR head is force-checked-out");

        var createBody = handler.Requests
            .Single(r => r.Method == HttpMethod.Post && r.Uri.ToString().Contains("api/workspaces", StringComparison.Ordinal))
            .Body;
        createBody.Should().Contain($"\"directoryRelPath\":\"{GithubLeaf}\"")
            .And.Contain("\"marketplaces\":[\"code-reviewer\"]", "the code-reviewer marketplace surfaces the sub-agent tree");
    }

    [Fact]
    public async Task PrepareAsync_builds_the_ado_dev_azure_remote_from_org_project_and_repo()
    {
        var git = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a repo"));
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-ado\",\"name\":\"Review PR #200\","
                    + "\"directoryRelPath\":\"review-ado-achieveai-lmdotnettools-pr-200\",\"marketplaces\":[]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        _ = await preparer.PrepareAsync(
            MakeRun("200"), MakeRepo("ado", project: "Platform"), "ado", CancellationToken.None);

        var commands = git.Commands.Select(c => string.Join(" ", c.Argv)).ToList();
        commands.Should().Contain(
            c => c.Contains("clone") && c.Contains("https://dev.azure.com/achieveai/Platform/_git/LmDotnetTools"),
            "ADO clones from dev.azure.com/{org}/{project}/_git/{repo}");
    }

    [Fact]
    public async Task PrepareAsync_preserves_a_failed_probe_for_an_existing_nonempty_checkout()
    {
        var hostDir = $"{BasePath}/{GithubLeaf}";
        var git = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree",
                new SandboxCommandResult(128, string.Empty, "fatal: unsafe repository ownership"))
            .OnArgvContains(
                $"ls -1A -- {hostDir}",
                new SandboxCommandResult(0, "existing-file\n", string.Empty));
        var handler = new FakeHttpMessageHandler();
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        Func<Task> act = async () => _ = await preparer.PrepareAsync(
            MakeRun("118"), MakeRepo("github", project: null), "github", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsafe repository ownership*");
        git.Commands.Select(c => string.Join(" ", c.Argv)).Should().NotContain(
            command => command.Contains(" clone ", StringComparison.Ordinal),
            "cloning cannot repair or explain a nonempty checkout whose probe failed");
        handler.Requests.Should().BeEmpty("the checkout fails before a workspace is created");
    }

    [Fact]
    public async Task PrepareAsync_reuses_an_existing_workspace_for_the_same_leaf_without_creating_a_duplicate()
    {
        // The probe succeeds (default result) so no clone happens; the existing workspace whose DirectoryRelPath
        // matches the derived leaf is reused — so only ONE api/workspaces call (the GET), never a POST.
        var git = new FakeSandboxCommandRunner();
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-existing\",\"name\":\"Review PR #118\",\"directoryRelPath\":\"" + GithubLeaf + "\","
                    + "\"marketplaces\":[\"code-reviewer\"]}]");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.PrepareAsync(
            MakeRun("118"), MakeRepo("github", project: null), "github", CancellationToken.None);

        prepared.WorkspaceId.Should().Be("ws-existing", "the workspace pointing at the leaf is reused, not recreated");
        handler.Requests
            .Where(r => r.Uri.ToString().Contains("api/workspaces", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Method == HttpMethod.Get, "reuse must not POST a duplicate workspace");
    }

    [Fact]
    public async Task AdoptSlotAsync_names_the_leased_slot_as_the_workspace_and_runs_no_git()
    {
        // The Layer-1 preparer has ALREADY cloned/fetched/checked out inside the slot. Re-running git here
        // would fight it for the same working tree, so adoption must be a pure naming operation — and the
        // workspace must point at the slot ROOT so the gateway's /workspace mount exposes store/ as
        // /workspace/store, exactly where the pooled review stage tells the agent to look.
        var git = new FakeSandboxCommandRunner();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-slot-0\",\"name\":\"Review slot 0\",\"directoryRelPath\":\"review-slot-0\","
                    + "\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.AdoptSlotAsync(MakeSlot(0, "review-slot-0"), MakeRun("118"), CancellationToken.None);

        prepared.Leaf.Should().Be("review-slot-0", "the slot's directory name IS the workspace leaf");
        prepared.WorkspaceId.Should().Be("ws-slot-0");
        prepared.HostDir.Should().Be($"{BasePath}/review-slot-0", "the workspace points at the slot root, not a child");
        git.Commands.Should().BeEmpty("adoption must not touch the working tree the pool already prepared");
    }

    [Fact]
    public async Task AdoptSlotAsync_reuses_the_existing_workspace_for_a_recycled_slot()
    {
        // Slots are warm and recycled: the second review to lease slot 1 must reuse slot 1's workspace
        // rather than minting a duplicate pointing at the same directory.
        var git = new FakeSandboxCommandRunner();
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-slot-1\",\"name\":\"Review slot 1\",\"directoryRelPath\":\"review-slot-1\","
                    + "\"marketplaces\":[\"code-reviewer\"]}]");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.AdoptSlotAsync(MakeSlot(1, "review-slot-1"), MakeRun("222"), CancellationToken.None);

        prepared.WorkspaceId.Should().Be("ws-slot-1");
        handler.Requests
            .Where(r => r.Uri.ToString().Contains("api/workspaces", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Method == HttpMethod.Get, "a recycled slot must not POST a duplicate workspace");
    }

    [Fact]
    public async Task AdoptSlotAsync_rejects_a_slot_dir_that_would_be_renamed_by_the_sanitizer()
    {
        // The one failure mode that LOOKS like it worked: LmStreaming would happily create the renamed
        // (empty) directory, mount it, and the agent would review nothing while reporting no findings.
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "api/workspaces", "[]");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, new FakeSandboxCommandRunner());

        var adopt = async () => await preparer.AdoptSlotAsync(
            MakeSlot(0, "Review Slot 0"), MakeRun("118"), CancellationToken.None);

        _ = await adopt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*review-slot-0*", "the failure names what the leaf would silently become");
        handler.Requests.Should().BeEmpty("it fails before naming anything to LmStreaming");
    }

    [Fact]
    public async Task AdoptSlotAsync_on_the_worktree_layout_opens_the_agent_in_its_own_slots_checkout()
    {
        // The mount is per REPOSITORY now, so the leaf alone no longer says where this run's code is: two
        // concurrent reviews of one repo are mounted on the SAME directory and differ only by which worktree
        // inside it they open on. The gateway home is what carries that difference — it is created, exported
        // as SANDBOX_HOME and becomes the operation's working directory, so a review that is handed the mount
        // root would start in a directory holding every slot's worktree and the store, and its first
        // relative-path tool call would land in the wrong PR.
        var git = new FakeSandboxCommandRunner();
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-nova\",\"name\":\"Review review-nova slot 1\",\"directoryRelPath\":\"review-nova\","
                    + "\"homeRelPath\":\"slot-1/repo\",\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, git);

        var prepared = await preparer.AdoptSlotAsync(
            MakeWorktreeSlot(1, "review-nova"), MakeRun("118"), CancellationToken.None);

        prepared.Leaf.Should().Be("review-nova", "the REPO's mount is the leaf; the slot is addressed by home");
        prepared.WorkspaceId.Should().Be("ws-nova");
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        JsonDocument.Parse(post.Body!).RootElement.GetProperty("homeRelPath").GetString()
            .Should().Be(
                "slot-1/repo",
                "home is relative to the mount and names the reviewed checkout, not the store or the mount root");
        git.Commands.Should().BeEmpty("adoption must not touch the working tree the pool already prepared");
    }

    [Fact]
    public async Task AdoptSlotAsync_on_the_legacy_layout_sends_no_home_and_starts_at_the_mount_root()
    {
        // Pre-worktree, the slot OWNS its whole mount and its root is already the right place to start. Sending
        // a home there would create a directory the pool never populated and start the review inside it.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-slot-0\",\"name\":\"Review slot 0\",\"directoryRelPath\":\"review-slot-0\","
                    + "\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, new FakeSandboxCommandRunner());

        _ = await preparer.AdoptSlotAsync(MakeSlot(0, "review-slot-0"), MakeRun("118"), CancellationToken.None);

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        JsonDocument.Parse(post.Body!).RootElement.GetProperty("homeRelPath").ValueKind
            .Should().Be(JsonValueKind.Null, "no home means the gateway keeps its historical mount-root start");
    }

    [Fact]
    public async Task AdoptSlotAsync_for_two_slots_of_one_repo_does_not_reuse_the_first_ones_workspace()
    {
        // Both slots of a repo share ONE mount, so their workspaces share a directoryRelPath and are told apart
        // only by home. Matching on the leaf alone would hand slot 1 the workspace already opened on slot 0's
        // worktree — the review would run, produce findings, and file them against a DIFFERENT PR's code.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-slot-0\",\"name\":\"Review review-nova slot 0\",\"directoryRelPath\":\"review-nova\","
                    + "\"homeRelPath\":\"slot-0/repo\",\"marketplaces\":[\"code-reviewer\"]}]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-slot-1\",\"name\":\"Review review-nova slot 1\",\"directoryRelPath\":\"review-nova\","
                    + "\"homeRelPath\":\"slot-1/repo\",\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, new FakeSandboxCommandRunner());

        var prepared = await preparer.AdoptSlotAsync(
            MakeWorktreeSlot(1, "review-nova"), MakeRun("222"), CancellationToken.None);

        prepared.WorkspaceId.Should().Be("ws-slot-1", "a different home is a different place, so a new workspace");
    }

    [Fact]
    public async Task AdoptSlotAsync_for_a_recycled_worktree_slot_reuses_the_workspace_with_the_same_home()
    {
        // The counterpart of the test above: same mount AND same home is the same place, so a recycled slot
        // must not mint a duplicate workspace on every review it serves.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-slot-1\",\"name\":\"Review review-nova slot 1\",\"directoryRelPath\":\"review-nova\","
                    + "\"homeRelPath\":\"slot-1/repo\",\"marketplaces\":[\"code-reviewer\"]}]");
        using var http = NewHttp(handler);
        var preparer = NewPreparer(http, new FakeSandboxCommandRunner());

        var prepared = await preparer.AdoptSlotAsync(
            MakeWorktreeSlot(1, "review-nova"), MakeRun("333"), CancellationToken.None);

        prepared.WorkspaceId.Should().Be("ws-slot-1");
        handler.Requests
            .Where(r => r.Uri.ToString().Contains("api/workspaces", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Method == HttpMethod.Get, "a recycled slot must not POST a duplicate");
    }

    private static ReviewSlot MakeSlot(int index, string dirName)
    {
        var hostPath = $"{BasePath}/{dirName}";
        return new ReviewSlot(index, hostPath, $"{hostPath}/store", $"{hostPath}/scratch");
    }

    /// <summary>A slot on the shared-object-store worktree layout: the mount belongs to the REPO and the slot
    /// is a named directory inside it, which is what the gateway home addresses.</summary>
    private static ReviewSlot MakeWorktreeSlot(int index, string mountDirName)
    {
        var mount = $"{BasePath}/{mountDirName}";
        var slotDir = $"slot-{index}";
        return new ReviewSlot(
            index,
            mount,
            $"{mount}/{slotDir}/notes",
            $"{mount}/{slotDir}/scratch",
            RepoKey: "dev.azure.com/o365exchange/Weve_DA/_git/Nova",
            SharedStorePath: $"{mount}/store",
            TargetPath: $"{mount}/{slotDir}/repo",
            SlotDirName: slotDir);
    }

    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    private static S2SReviewWorkspacePreparer NewPreparer(HttpClient http, FakeSandboxCommandRunner git) =>
        new(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            new GitRunner(git),
            BasePath,
            reviewMarketplace: "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance);

    private static ReviewRun MakeRun(string prId) =>
        new()
        {
            RepoId = 1,
            PrId = prId,
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private static RepoIdentity MakeRepo(string provider, string? project) =>
        new()
        {
            Provider = provider,
            OrgOrOwner = "achieveai",
            Project = project,
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        };
}
