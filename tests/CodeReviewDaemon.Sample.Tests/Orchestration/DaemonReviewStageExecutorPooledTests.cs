using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 9 — the pooled scoped-writable review flow. When <c>EnableToolAssistedReview</c> +
/// <c>EnableReviewerWrites</c> are on and a store is resolved, <c>ContextReady</c> leases a warm slot and
/// prepares it host-side (branch reuse carries prior notes), the diff comes from the prepared submodule,
/// the review runs with a scoped Write/Edit/Bash tool context, <c>Posted</c> commits ONLY the PR notes dir
/// onto the persistent notes branch (no merge/delete) and returns the slot. Driven entirely against fakes
/// for the pool/preparer/host-git so the wiring is verified without a live gateway.
/// </summary>
public sealed class DaemonReviewStageExecutorPooledTests
{
    private const string StoreUrl = "https://github.com/achieveai/AchieveAiReviews.git";
    private const string Branch = "review/lmdotnettools-118";
    private const string NotesRelPath = "PRs/lmdotnettools-118";
    private const string SubmoduleRelPath = "repos/LmDotnetTools";

    [Fact]
    public async Task ContextReady_leases_a_slot_prepares_it_and_diffs_the_prepared_target()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(1);
        fixture.Pool.ReturnCount.Should().Be(0, "the slot is held for the review + commit-notes + terminal return");
        fixture.Preparer.PrepareCount.Should().Be(1);
        fixture.Preparer.LastSubmoduleRelPath.Should().Be(SubmoduleRelPath);
        fixture.Preparer.LastBranch.Should().Be(Branch);
        fixture.Preparer.LastNotesRelPath.Should().Be(NotesRelPath);
        fixture.Preparer.LastDefaultBranch.Should().Be("main");

        // The diff is taken HOST-side against the prepared submodule working tree, not the boot sandbox.
        fixture.HostRunner.Commands.Select(Join)
            .Should().Contain(a => a.Contains("/slot-0/store/repos/LmDotnetTools") && a.Contains("diff"));
        fixture.BootRunner.Commands.Should().BeEmpty("the pooled path never touches the boot-lifetime runner");

        // The artifact records the CONTAINER paths the agent's tools address (slot mounted at /workspace).
        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var payload = JsonDocument.Parse(artifact.Payload).RootElement;
        payload.GetProperty("CheckoutRoot").GetString().Should().Be("/workspace/store/repos/LmDotnetTools");
        payload.GetProperty("StoreRoot").GetString().Should().Be("/workspace/store");
        payload.GetProperty("Diff").GetString().Should().Contain("Foo.cs");
    }

    [Fact]
    public async Task ContextReady_falls_back_to_the_per_run_checkout_when_the_repo_is_not_a_store_submodule()
    {
        using var fixture = Fixture.Create();
        // The store declares a DIFFERENT submodule, so the reviewed repo is not in it: the pooled path
        // declines (returns the slot) and the executor uses the existing per-run/diff-only checkout.
        fixture.HostFileSystem.Files.Clear();
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/.gitmodules",
            "[submodule \"other\"]\n\tpath = repos/other\n\turl = https://github.com/achieveai/other.git\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(1);
        fixture.Pool.ReturnCount.Should().Be(1, "a declined lease is returned immediately so it can't leak pool capacity");
        fixture.Preparer.PrepareCount.Should().Be(0, "the reviewed repo is not a store submodule, so no prepare runs");
        // The stage still completed via the fallback checkout — a context artifact was persisted.
        fixture.Store.GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ContextArtifactKind);
    }

    [Fact]
    public async Task ContextReady_reclones_and_retries_prepare_once_when_the_slot_is_corrupt()
    {
        using var fixture = Fixture.Create();
        // The warm store is corrupt: the first prepare reports it, the executor re-clones and retries once.
        fixture.Preparer.ThrowThenSucceed.Enqueue(new SlotCorruptException("stale lock survived"));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Pool.RecloneCount.Should().Be(1, "a corrupt store is re-cloned before the retry");
        fixture.Preparer.PrepareCount.Should().Be(2, "prepare is retried exactly once after the re-clone");
        fixture.Store.GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ContextArtifactKind,
                "the retried prepare succeeded, so the stage completed with a context artifact");
    }

    [Fact]
    public async Task ContextReady_surfaces_and_returns_the_slot_when_prepare_still_fails_after_reclone()
    {
        using var fixture = Fixture.Create();
        // Corrupt twice: re-clone + retry does not help, so the failure surfaces (the retry governor bounds it)
        // and the slot must be returned so it cannot leak pool capacity.
        fixture.Preparer.ThrowThenSucceed.Enqueue(new SlotCorruptException("corrupt 1"));
        fixture.Preparer.ThrowThenSucceed.Enqueue(new SlotCorruptException("corrupt 2"));
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        await act.Should().ThrowAsync<SlotCorruptException>();
        fixture.Pool.RecloneCount.Should().Be(1, "re-clone is attempted once");
        fixture.Preparer.PrepareCount.Should().Be(2, "prepare is attempted once, then once more after the re-clone");
        fixture.Pool.ReturnCount.Should().Be(1, "the failed lease is returned so it cannot leak pool capacity");
    }

    [Fact]
    public async Task Reviewed_builds_a_scoped_write_tool_context_with_the_notes_and_scratch_roots()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var toolContext = fixture.Factory.ToolContexts.Where(t => t is not null).Should().ContainSingle().Subject!;
        toolContext.EnableReviewerWrites.Should().BeTrue();
        toolContext.WritableToolAllowList.Should().BeEquivalentTo(["Write", "Edit", "Bash"]);
        toolContext.ReadOnlyToolAllowList.Should().BeEquivalentTo(["Read", "Grep", "Glob", "Skill"]);
        toolContext.NotesDir.Should().Be("/workspace/store/PRs/lmdotnettools-118");
        toolContext.ScratchDir.Should().Be("/workspace/scratch");
    }

    [Fact]
    public async Task Reviewed_retries_diff_only_when_the_tool_assisted_review_exceeds_the_context_window()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The tool-assisted attempt (non-null toolContext) is rejected by the model API with the context-window
        // 400 the daemon saw live (the PR diff + fanned-out sub-agent results overflow the window). The diff-only
        // retry (null toolContext) succeeds — so the run yields a leaner review instead of producing nothing.
        fixture.Factory.ThrowWhenToolAssisted = new HttpRequestException(
            "HTTP request failed with status BadRequest (Bad Request). Response body: "
                + "{\"error\":{\"message\":\"Your input exceeds the context window of this model.\"}}");
        fixture.Factory.DefaultText = "## Review (diff-only)\nMust: null check missing in Foo.cs:10.";

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Two attempts: the tool-assisted one (non-null context) then the diff-only retry (null context).
        fixture.Factory.ToolContexts.Should().HaveCount(2);
        fixture.Factory.ToolContexts[0].Should().NotBeNull();
        fixture.Factory.ToolContexts[1].Should().BeNull();

        // The retry runs on a DISTINCT thread so it starts a clean conversation rather than reloading the
        // overflowing history that just blew the window.
        fixture.Factory.ThreadIds[0].Should().NotBe(fixture.Factory.ThreadIds[1]);

        // The review artifact holds the diff-only review — the run produced content, not a silent nothing.
        var artifact = fixture.Store.GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind).Subject;
        artifact.Payload.Should().Contain("diff-only");
    }

    [Fact]
    public async Task Reviewed_templates_the_notes_dir_into_the_review_system_prompt()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The reviewer is TOLD exactly where it may read/write for THIS run — not left to guess — via the
        // templated "Workspace layout" section of the review system prompt. The notes dir must be the
        // identical value the tool context scoped Write/Edit/Bash to (asserted above).
        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().Contain("/workspace/store/repos/LmDotnetTools");
        profile.SystemPrompt.Should().Contain("cross-repo store at /workspace/store");
        profile.SystemPrompt.Should().Contain("/workspace/store/PRs/lmdotnettools-118");
        profile.SystemPrompt.Should().MatchRegex("(?i)only writable location");
    }

    [Fact]
    public async Task Reviewed_templates_rereview_context_and_prior_notes_files_into_the_system_prompt()
    {
        using var fixture = Fixture.Create();

        // A prior round already completed for this PR at an older head — the current run is round 2.
        var repoId = fixture.Store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        _ = fixture.Store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "sha-old",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-0",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Posted,
            WorkflowStatus = WorkflowStatus.Completed,
            PrLifecycleState = PrLifecycleState.Open,
        });
        var run = fixture.SeedRun(); // head-sha "head-sha" — this round's head

        // The prior round's own notes are already on the notes dir the reviewer's tools address.
        fixture.BootFileSystem.Seed(
            "/workspace/store/PRs/lmdotnettools-118/PR_Findings_01.md", "prior findings");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().MatchRegex("(?i)RE-REVIEW");
        profile.SystemPrompt.Should().Contain("round 02");
        profile.SystemPrompt.Should().Contain("sha-old"); // the previously-reviewed commit
        profile.SystemPrompt.Should().Contain("head-sha"); // the current head
        profile.SystemPrompt.Should().Contain("PR_Findings_01.md"); // prior notes file, read-first
    }

    [Fact]
    public async Task Reviewed_mounts_the_agent_session_over_the_leased_slot()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // A pooled run provisions the review agent's session by mounting it OVER the slot leased in
        // ContextReady (GetOrCreateForSlotAsync) — never the per-run mount — and the provisioner saw the
        // very slot that was leased (index 0).
        fixture.Provisioner.GetOrCreateForSlotCalls.Should().Be(1);
        fixture.Provisioner.LastSlot.Should().NotBeNull();
        fixture.Provisioner.LastSlot!.Index.Should().Be(0);
    }

    [Fact]
    public async Task Reviewed_re_leases_a_slot_when_resuming_after_a_restart_dropped_the_in_memory_lease()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // Process A: ContextReady leases slot 0 and persists the context artifact to the shared store.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.Pool.LeaseCount.Should().Be(1);

        // A restart drops the in-memory _leasedReviews (the persisted context + Stage survive in the store),
        // so the NEXT process resumes straight into Reviewed with NO recorded lease. Seed the gitmodules for
        // the slot the resumed run will lease next (slot-1), mirroring slot-0.
        fixture.HostFileSystem.Seed(
            "/pool/slot-1/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The resumed review must RE-LEASE a slot and mount the agent session OVER it — never the per-run
        // review-run-{id} mount, which does not exist under the gateway's read-only workspace base and 400s
        // (the silent degrade-to-diff-only these resumed runs were stuck in).
        fixture.Pool.LeaseCount.Should().Be(2, "the resumed review re-leases a slot because the prior lease was lost on restart");
        fixture.Provisioner.GetOrCreateForSlotCalls.Should().Be(1, "the resumed review mounts over the re-leased slot");
        fixture.Provisioner.GetOrCreateCalls.Should().Be(0, "the resumed review must never fall back to the broken per-run mount");
    }

    [Fact]
    public async Task Posted_commits_only_the_pr_notes_dir_onto_the_notes_branch_and_never_merges()
    {
        using var fixture = Fixture.Create();
        // First review of the PR: the notes branch does not exist yet, so it is cut from the default branch.
        fixture.HostRunner.OnArgvContains(
            $"rev-parse --verify {Branch}", new SandboxCommandResult(1, string.Empty, "unknown revision"));
        fixture.HostRunner.OnArgvContains(
            $"rev-parse {Branch}", new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.HostRunner.Commands.Select(Join).ToList();
        commands.Should().Contain(a => a.Contains($"checkout -B {Branch} main"));
        // The commit gate stages ONLY the PR notes dir — never `add -A` (which would stage the moved
        // code-submodule pointer), never a merge, never a branch delete, never a default-branch push.
        commands.Should().Contain(a => a.Contains($"add -- {NotesRelPath}"));
        commands.Should().NotContain(a => a.Contains("add -A"));
        commands.Should().Contain(a => a.Contains("commit -m"));
        commands.Should().Contain(a => a.Contains($"push origin {Branch}"));
        commands.Should().NotContain(a => a.Contains("merge"));
        commands.Should().NotContain(a => a.Contains($"branch -D {Branch}"));
        commands.Should().NotContain(a => a.Contains("push origin main"));

        // The review.md landed inside the per-PR notes dir on the slot's store checkout.
        fixture.HostFileSystem.Writes.Should().Contain(
            p => p.Contains($"/{NotesRelPath}/") && p.EndsWith("review.md"));

        // The retention push outcome is persisted (terminal Posted, carrying the pushed SHA).
        var push = fixture.Store.GetOutboxForRun(run.Id)
            .Should().ContainSingle(o => o.Operation == DaemonReviewStageExecutor.PushReviewBotOperation).Subject;
        push.Status.Should().Be(OutboxStatus.Posted);
        push.ProviderResponseId.Should().Be("f00dcafef00dcafe");
    }

    [Fact]
    public async Task Posted_returns_the_leased_slot_on_the_terminal_stage()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        fixture.Pool.ReturnCount.Should().Be(1);
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);
    }

    [Fact]
    public async Task ReleaseReviewLease_returns_the_leased_slot_and_is_idempotent()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // ContextReady leases a slot and holds it (for the review + commit-notes + terminal return).
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(0);

        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(1);
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);

        // Idempotent: a second release (e.g. the Posted stage already returned it) is a no-op, so the slot
        // is never double-returned to the pool.
        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(1, "the lease was already removed, so a second release is a no-op");
    }

    [Fact]
    public async Task Orchestrator_returns_the_leased_slot_when_a_stage_throws_after_ContextReady_leased()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // ContextReady (delegated to the real executor) leases a slot; a later stage then throws, so the run
        // never reaches Posted. Only the orchestrator's terminal finally can return the slot.
        var executor = new ThrowAfterStageExecutor(fixture.Executor, throwAt: ReviewStage.Reviewed);
        var orchestrator = new PrOrchestrator(
            fixture.Store, executor, NullLogger<PrOrchestrator>.Instance);

        var act = () => orchestrator.RunAsync(run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fixture.Pool.LeaseCount.Should().Be(1, "ContextReady leased a slot");
        fixture.Pool.ReturnCount.Should().Be(1, "the orchestrator's terminal finally returned the slot despite the failure");
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);
    }

    [Fact]
    public async Task Orchestrator_returns_the_leased_slot_when_the_pr_is_no_longer_open()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // A slot is leased (ContextReady) and then the PR is observed closed on the next poll, so RunAsync
        // short-circuits to Completed WITHOUT running the Posted stage that would normally return it.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(0);

        var orchestrator = new PrOrchestrator(
            fixture.Store, fixture.Executor, NullLogger<PrOrchestrator>.Instance);
        var closed = run with { PrLifecycleState = PrLifecycleState.Merged };

        var result = await orchestrator.RunAsync(closed, CancellationToken.None);

        result.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
        fixture.Pool.ReturnCount.Should().Be(1, "the short-circuit finally returned the held slot");
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);
    }

    [Fact]
    public async Task Posted_strips_the_slot_store_to_pristine_after_committing_the_notes()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        // Commit-then-strip: after the notes are committed, the store working tree is reset + cleaned so the
        // next lease starts clean with nothing left around (the user's durability requirement).
        var commands = fixture.HostRunner.Commands.Select(Join).ToList();
        commands.Should().Contain(a => a.Contains("reset --hard"), "the slot store is reset on terminal return");
        commands.Should().Contain(a => a.Contains("clean -ffdx"), "untracked review byproduct is cleaned on return");
        fixture.Pool.ReturnCount.Should().Be(1, "the slot is still returned after the strip");
    }

    private static string Join(SandboxCommand command) => string.Join(' ', command.Argv);

    private static async Task RunAllStagesAsync(Fixture fixture, ReviewRun run)
    {
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _db;
        private readonly CodeReviewDaemonOptions _options;
        private readonly ReviewSlotWorkspace _slotWorkspace;

        private Fixture()
        {
            _db = new TempSqliteDatabase();
            Store = new ReviewStore(_db.ConnectionString);
            BootRunner = new FakeSandboxCommandRunner()
                .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
                .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ x", string.Empty));
            HostRunner = new FakeSandboxCommandRunner()
                .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ x", string.Empty));
            HostFileSystem = new FakeSandboxFileSystem().Seed(
                "/pool/slot-0/store/.gitmodules",
                "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
            Pool = new FakeReviewSlotPool("/pool");
            Preparer = new FakeReviewSlotPreparer();

            _options = new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                EnableReviewerWrites = true,
                CrossRepoStoreUrl = StoreUrl,
            };
            _slotWorkspace = new ReviewSlotWorkspace(Pool, Preparer, HostRunner, HostFileSystem);

            Executor = BuildExecutor();
        }

        /// <summary>
        /// Builds an executor over the fixture's SHARED store/pool/preparer/provisioner. Each executor has its
        /// own in-memory <c>_leasedReviews</c>, so calling this a second time simulates a daemon RESTART: the
        /// persisted context artifact survives (shared store) while the process-local pooled lease does not.
        /// </summary>
        public DaemonReviewStageExecutor BuildExecutor() =>
            new(
                Store,
                Factory,
                BootRunner,
                BootFileSystem,
                _options,
                [new FakeReviewCommentPublisher("github")],
                NullLoggerFactory.Instance,
                provisioner: Provisioner,
                slotWorkspace: _slotWorkspace);

        public ReviewStore Store { get; }
        public FakeReviewAgentLoopFactory Factory { get; } = new();
        public RecordingProvisioner Provisioner { get; } = new();
        public FakeSandboxCommandRunner BootRunner { get; }
        public FakeSandboxCommandRunner HostRunner { get; }
        public FakeSandboxFileSystem HostFileSystem { get; }
        public FakeSandboxFileSystem BootFileSystem { get; } = new();
        public FakeReviewSlotPool Pool { get; }
        public FakeReviewSlotPreparer Preparer { get; }
        public DaemonReviewStageExecutor Executor { get; }

        public static Fixture Create() => new();

        public ReviewRun SeedRun()
        {
            var repoId = Store.EnsureRepo(new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            });
            return Store.CreateOrGetReviewRun(new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = "head-sha",
                BaseSha = "base-sha",
                TriggerWatermark = "wm-1",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "collect-only",
                Stage = ReviewStage.Discovered,
                WorkflowStatus = WorkflowStatus.Running,
                PrLifecycleState = PrLifecycleState.Open,
            });
        }

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
        }
    }

    /// <summary>Records lease/return calls and hands out forward-slash slot paths so the in-memory host
    /// file-system keys line up regardless of the OS path separator.</summary>
    private sealed class FakeReviewSlotPool : IReviewSlotPool
    {
        private readonly string _root;
        private int _next;

        public FakeReviewSlotPool(string root) => _root = root;

        public int LeaseCount { get; private set; }
        public int ReturnCount { get; private set; }
        public int RecloneCount { get; private set; }
        public List<ReviewSlot> Returned { get; } = [];

        public Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken)
        {
            LeaseCount++;
            var index = _next++;
            var host = $"{_root}/slot-{index}";
            return Task.FromResult(new ReviewSlot(index, host, $"{host}/store", $"{host}/scratch"));
        }

        public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
        {
            ReturnCount++;
            Returned.Add(slot);
            return Task.CompletedTask;
        }

        public Task RecloneStoreAsync(ReviewSlot slot, CancellationToken cancellationToken)
        {
            RecloneCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Records the prepare inputs and returns a <see cref="PreparedCheckout"/> whose paths are the
    /// forward-slash join of the slot store + the supplied relative paths (mirrors the real preparer).</summary>
    private sealed class FakeReviewSlotPreparer : IReviewSlotPreparer
    {
        public int PrepareCount { get; private set; }
        public string? LastSubmoduleRelPath { get; private set; }
        public string? LastBranch { get; private set; }
        public string? LastNotesRelPath { get; private set; }
        public string? LastDefaultBranch { get; private set; }

        /// <summary>Exceptions to throw on the first N prepare calls (then succeed) — drives the re-clone ladder.</summary>
        public Queue<Exception> ThrowThenSucceed { get; } = new();

        public Task<PreparedCheckout> PrepareAsync(
            ReviewSlot slot,
            ReviewRun run,
            string storeUrl,
            string submoduleRelPath,
            string branch,
            string defaultBranch,
            string notesRelPath,
            OperationPolicy policy,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            if (ThrowThenSucceed.Count > 0)
            {
                throw ThrowThenSucceed.Dequeue();
            }

            LastSubmoduleRelPath = submoduleRelPath;
            LastBranch = branch;
            LastNotesRelPath = notesRelPath;
            LastDefaultBranch = defaultBranch;
            var storeRoot = slot.StorePath;
            return Task.FromResult(new PreparedCheckout(
                storeRoot, $"{storeRoot}/{submoduleRelPath}", $"{storeRoot}/{notesRelPath}", branch));
        }
    }

    /// <summary>Hands back a session so the review stage can build a (scoped) tool context; the fake agent
    /// loop factory ignores the gateway details and just records the context it was given. Records which
    /// provisioning entry point the executor used (per-run vs slot-mount) and the slot it saw.</summary>
    private sealed class RecordingProvisioner : IReviewSessionProvisioner
    {
        public int GetOrCreateForSlotCalls { get; private set; }
        public int GetOrCreateCalls { get; private set; }
        public ReviewSlot? LastSlot { get; private set; }

        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct)
        {
            GetOrCreateCalls++;
            return Task.FromResult<ReviewRunSession?>(new ReviewRunSession(
                $"session-{run.Id}", $"/workspace/review-run-{run.Id}",
                new FakeSandboxCommandRunner(), new FakeSandboxFileSystem()));
        }

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)
        {
            GetOrCreateForSlotCalls++;
            LastSlot = slot;
            // Mirror the real provisioner: same per-run session id/key, but the mount HostPath is the slot.
            return Task.FromResult<ReviewRunSession?>(new ReviewRunSession(
                $"session-{run.Id}", slot.HostPath,
                new FakeSandboxCommandRunner(), new FakeSandboxFileSystem()));
        }

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Delegates every stage to a real executor but throws at a chosen stage, so a run driven
    /// through the orchestrator leases a slot in ContextReady and then fails before Posted — proving the
    /// slot is returned by the orchestrator's terminal <c>finally</c> (via the delegated
    /// <see cref="IReviewStageExecutor.ReleaseReviewLeaseAsync"/>), not by the Posted stage.</summary>
    private sealed class ThrowAfterStageExecutor : IReviewStageExecutor
    {
        private readonly IReviewStageExecutor _inner;
        private readonly ReviewStage _throwAt;

        public ThrowAfterStageExecutor(IReviewStageExecutor inner, ReviewStage throwAt)
        {
            _inner = inner;
            _throwAt = throwAt;
        }

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            if (stage == _throwAt)
            {
                throw new InvalidOperationException($"Simulated failure at stage {stage}.");
            }

            return _inner.ExecuteStageAsync(stage, run, cancellationToken);
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) =>
            _inner.ReleaseReviewLeaseAsync(runId, cancellationToken);
    }
}
