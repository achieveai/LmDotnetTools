using System.Globalization;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.4 — the stateless stage executor that the orchestrator drives. These tests pin: ContextReady
/// fetches the diff via the sandbox and persists a context artifact; Reviewed runs the review agent and
/// persists a review artifact, gating the B variant / Knowledge / Judge arms on their feature flags;
/// Posted is collect-only by default and posts exactly once only when comment posting is authorized;
/// and an Azure DevOps run maps to the <c>ado</c> provider/publisher. The executor re-reads the store
/// each stage (no state threaded through the run), so the tests drive the same run object across stages.
/// </summary>
public sealed class DaemonReviewStageExecutorTests : LoggingTestBase
{
    private const string DiffText = "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;";

    /// <summary>The SECOND turn's answer, deliberately unlike <c>FakeReviewAgentLoopFactory.DefaultText</c>
    /// so a test can tell the authoritative synthesis from the provisional answer that preceded it.</summary>
    private const string SynthesisText = "## Review\nMust: Foo.cs:10 dereferences bar after the child's rename.";

    /// <summary>A provisional answer seeded by a checkpoint, distinct from anything a live turn produces —
    /// if it ever appears in an authoritative artifact, a checkpoint was promoted.</summary>
    private const string StaleProvisionalText = "## Review\nProvisional answer from the interrupted attempt.";

    public DaemonReviewStageExecutorTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public async Task ContextReady_fetches_the_diff_and_persists_a_context_artifact()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Runner.Commands
            .Should().Contain(c => string.Join(' ', c.Argv).Contains("diff"), "the diff is fetched in the sandbox");

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        artifact.ArtifactKind.Should().Be(DaemonReviewStageExecutor.ContextArtifactKind);
        artifact.Provider.Should().Be("github");
        JsonDocument.Parse(artifact.Payload).RootElement.GetProperty("Diff").GetString().Should().Contain("Foo.cs");
    }

    [Fact]
    public async Task ContextReady_fetches_the_target_repo_and_diffs_the_target_checkout_not_reviewbot()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();

        // The target repo is cloned/fetched (PR #121 H1) — the diff must come from the TARGET checkout,
        // not the ReviewBot retention checkout that has none of the PR's commits.
        commands.Should().Contain(
            a => a.Contains("clone") && a.Contains("github.com/achieveai/LmDotnetTools"),
            "the target PR repo is cloned into its own checkout");
        commands.Should().Contain(
            a => a.Contains("/workspace/target") && a.Contains("diff"),
            "the diff is taken from the target checkout, not /workspace/reviewbot");
        commands.Should().NotContain(
            a => a.Contains("/workspace/reviewbot") && a.Contains("diff"),
            "diffing the ReviewBot checkout would produce an empty/incorrect diff (the H1 bug)");
    }

    [Fact]
    public async Task ContextReady_walks_target_submodules_under_the_per_run_policy_and_refuses_off_allowlist()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        // The target checkout declares a submodule that is NOT on the per-run allow-list (the executor
        // passes an empty submodule allow-list by default). The walk must REFUSE it — never blanket-init
        // untrusted submodule code — and continue to the diff (H1 selective submodule init, plan §3).
        fixture.FileSystem.Seed(
            "/workspace/target/.gitmodules",
            "[submodule \"libs/shared\"]\n\tpath = libs/shared\n\turl = https://evil.example.com/x/shared.git\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(
            a => a.Contains("submodule update --init"),
            "an off-allow-list submodule must be refused, not initialized");
        // The diff still ran (the walk continued past the denied submodule) and a context artifact landed.
        fixture.Store.GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ContextArtifactKind);
    }

    [Fact]
    public async Task ContextReady_caps_an_oversized_diff_in_the_persisted_artifact()
    {
        var hugeDiff = new string('x', 10_000);
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                Limits = new CodeReviewDaemon.Sample.Configuration.SandboxLimits { MaxArtifactPayloadChars = 256 },
            },
            diffResult: new SandboxCommandResult(0, hugeDiff, string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var diff = JsonDocument.Parse(artifact.Payload).RootElement.GetProperty("Diff").GetString()!;
        diff.Length.Should().BeLessThan(hugeDiff.Length, "an oversized diff must be capped before persisting (H4)");
        diff.Should().Contain("truncated");
    }

    [Fact]
    public async Task ContextReady_checks_out_the_pr_head_so_reads_reflect_the_proposed_code()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // The working tree must be moved to the PR head so Read/Grep/Glob and the file manifest reflect the
        // code the PR proposes, not the clone's default branch (which would ground findings in the wrong code).
        commands.Should().Contain(
            a => a.Contains("checkout --force") && a.Contains("head-sha"),
            "the PR head is checked out into the target working tree");
    }

    [Fact]
    public async Task ContextReady_persists_a_file_manifest_from_git_ls_files()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        // The head checkout's tracked files — the manifest the review agent Reads by exact path to ground
        // findings (the gateway's Glob/Grep cannot enumerate the repo root reliably).
        fixture.Runner.OnArgvContains(
            "ls-files", new SandboxCommandResult(0, "src/Foo/Bar.cs\nsrc/Foo/Baz.cs\nREADME.md\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(a => a.Contains("/workspace/target") && a.Contains("ls-files"));

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var manifest = JsonDocument.Parse(artifact.Payload).RootElement.GetProperty("FileManifest").GetString()!;
        manifest.Should().Contain("src/Foo/Bar.cs").And.Contain("README.md");
    }

    [Fact]
    public async Task Reviewed_passes_the_file_manifest_to_the_review_agent_input()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        fixture.Runner.OnArgvContains(
            "ls-files", new SandboxCommandResult(0, "src/Foo/Bar.cs\nsrc/Foo/Baz.cs\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The primary review agent must actually SEE the manifest in its user input so it can Read files by
        // exact path instead of globbing the repo root.
        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var input = reviewAgent.ReceivedInputs[0];
        var text = input.Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("src/Foo/Bar.cs");

        // The checkout root is now templated into the review agent's SYSTEM PROMPT (not duplicated into
        // the input) via DaemonAgentFactory.CreateReviewProfile's workspace-layout variables.
        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().Contain("/workspace/target");
    }

    [Fact]
    public async Task Reviewed_drives_the_provisional_turn_then_an_authoritative_synthesis_turn_on_one_agent()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // ONE agent — one conversation — drives both turns. The first is collect-only; the second is the
        // authoritative synthesis whose answer becomes the persisted review.
        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().HaveCount(2, "collect-only provisional, then synthesis on the same agent");
        agent.ReceivedInputs[1].Messages.OfType<TextMessage>().Single().Text
            .Should().Contain("sub-agent", "the synthesis turn is told what the settled sub-agent roster was");
    }

    [Fact]
    public async Task Reviewed_keeps_one_loop_alive_across_collect_barrier_and_synthesis()
    {
        // The central lifecycle guarantee of the completion barrier: the parent loop is NOT disposed between
        // the provisional turn and the barrier — disposing it would tear down the very SubAgentManager the
        // barrier polls (and the conversation synthesis must continue on). Disposal happens once, after
        // synthesis. The barrier observes the world mid-scope, so its observation is the proof.
        var observedRunsAtBarrier = new List<int>();
        // Resolved lazily: the agent under observation does not exist until the Reviewed stage creates it, and
        // the barrier only ever polls from inside that stage.
        FakeReviewAgentLoopFactory? factory = null;
        var source = new ObservingCompletionSource(
            () => observedRunsAtBarrier.Add(factory!.CreatedAgents[0].Lifecycle.Count));
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: source);
        factory = fixture.Factory;
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.Lifecycle.Should().Equal(
            FakeMultiTurnAgent.RunEvent,
            FakeMultiTurnAgent.RunEvent,
            FakeMultiTurnAgent.DisposeEvent);
        observedRunsAtBarrier.Should().NotBeEmpty("the barrier ran").And.AllBeEquivalentTo(
            1, "the barrier polls after exactly one turn, with the loop still alive (no dispose event yet)");
    }

    /// <summary>
    /// A completion source with an empty (therefore immediately all-terminal) roster that reports each poll
    /// to <paramref name="onPoll"/> — used to observe the parent loop's lifecycle from INSIDE the barrier.
    /// </summary>
    private sealed class ObservingCompletionSource(Action onPoll) : IReviewSubAgentCompletionSource
    {
        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run, string parentThreadId, CancellationToken ct)
        {
            onPoll();
            return Task.FromResult(new ReviewSubAgentTreeSnapshot([]));
        }
    }

    /// <summary>
    /// Task 5 (fix round 1) — the lifecycle/head check must run even when there is NO completion source. The
    /// barrier re-runs it just before it opens, but a review with no sub-agent source never reaches the
    /// barrier, and it still took real time to produce: synthesizing (and, on the posting path, delivering)
    /// against a PR that has since closed is exactly what the check exists to prevent.
    /// </summary>
    [Fact]
    public async Task Reviewed_with_no_completion_source_still_refuses_to_synthesize_against_a_closed_pr()
    {
        // No completion source injected and no tool context — the tier where nothing can be waited on.
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        // The PR closes while the review is running: the stage drives the run object it loaded BEFORE that,
        // so the store and the in-flight run have diverged — the divergence the check looks for.
        fixture.Store.UpdateReviewRunState(
            run.Id, ReviewStage.Reviewed, WorkflowStatus.Running, PrLifecycleState.Closed);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*became Closed*");

        // The provisional turn ran; the AUTHORITATIVE synthesis turn did not, and nothing was persisted.
        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().HaveCount(1, "the collect-only turn ran, the synthesis turn was refused");
        fixture.Store.GetArtifacts(run.Id).Should()
            .NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task ContextReady_uses_the_cross_repo_store_when_the_reviewed_repo_is_a_submodule()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        // The store declares the reviewed repo as a submodule under repos/LmDotnetTools.
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\nREADME.md\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("clone") && a.Contains("AchieveAiReviews") && a.Contains("/workspace/store"),
            "the cross-repo store is cloned as the review superproject");
        commands.Should().Contain(
            a => a.Contains("submodule update --init") && a.Contains("repos/LmDotnetTools"),
            "the reviewed repo's submodule is initialized in the store");
        commands.Should().Contain(
            a => a.Contains("/workspace/store/repos/LmDotnetTools") && a.Contains("checkout --force") && a.Contains("head-sha"),
            "the PR head is checked out in the reviewed submodule working tree");
        commands.Should().Contain(
            a => a.Contains("/workspace/store/repos/LmDotnetTools") && a.Contains("diff"),
            "the diff is taken from the reviewed submodule, not the store root");

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var payload = JsonDocument.Parse(artifact.Payload).RootElement;
        payload.GetProperty("CheckoutRoot").GetString().Should().Be("/workspace/store/repos/LmDotnetTools");
        payload.GetProperty("StoreRoot").GetString().Should().Be("/workspace/store");
    }

    [Fact]
    public async Task ContextReady_falls_back_to_single_repo_when_the_store_lacks_the_reviewed_submodule()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        // The store declares a DIFFERENT repo — the reviewed repo is not in it, so the review falls back to
        // the single-repo /workspace/target checkout.
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"other\"]\n\tpath = repos/other\n\turl = https://github.com/achieveai/other.git\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("clone") && a.Contains("github.com/achieveai/LmDotnetTools") && a.Contains("/workspace/target"),
            "the reviewed repo is cloned directly when it is not a store submodule");
        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        JsonDocument.Parse(artifact.Payload).RootElement.GetProperty("StoreRoot").ValueKind
            .Should().Be(JsonValueKind.Null, "the single-repo fallback records no store root");
    }

    [Fact]
    public async Task Reviewed_input_reflects_the_store_layout_and_contracts()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The store layout (submodule checkout root + shared Contracts/) is now templated into the review
        // agent's SYSTEM PROMPT via DaemonAgentFactory.CreateReviewProfile's workspace-layout variables,
        // rather than duplicated into the input.
        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().Contain("/workspace/store/repos/LmDotnetTools", "the reviewed repo's submodule path is given");
        profile.SystemPrompt.Should().Contain("/workspace/store/Contracts", "the shared Contracts/ layer is pointed out for cross-repo grounding");
    }

    [Fact]
    public async Task Reviewed_prepends_the_knowledge_base_toc_when_the_store_has_one()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        // The store carries prior knowledge distilled from past PRs; the review must start with its table
        // of contents so the reviewer factors it in (design §3).
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\n## system\n- [Null-guard boundaries](system/null-guard.md)\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("Prior knowledge (KnowledgeBase/_toc.md)", "the ToC is prepended as a labelled block");
        text.Should().Contain("Null-guard boundaries", "the seeded ToC entries are surfaced to the reviewer");
    }

    [Fact]
    public async Task Reviewed_degrades_and_does_not_fail_when_the_knowledge_base_read_throws()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        // The KB read (and the re-review notes listing) go through the boot-lifetime sandbox session, which
        // can 404 ("Session not found") or hiccup. That transport failure must DEGRADE, never fail the
        // review (design §6). Fault only the KB/notes paths so ContextReady's own reads are unaffected.
        fixture.FileSystem.ReadFault = path =>
            path.Contains("KnowledgeBase", StringComparison.Ordinal)
                ? new InvalidOperationException("Session not found: deadbeef")
                : null;
        fixture.FileSystem.ListFault = dir =>
            dir.Contains("/PRs/", StringComparison.Ordinal)
                ? new InvalidOperationException("Session not found: deadbeef")
                : null;

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().NotThrowAsync("a KB/notes read failure must degrade, not kill the review (design §6)");
        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain("Prior knowledge (KnowledgeBase/_toc.md)", "the failed KB read is skipped, not prepended");
    }

    [Fact]
    public async Task Reviewed_leaves_the_input_unchanged_when_the_store_has_no_knowledge_base()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        // No KnowledgeBase/_toc.md seeded — the common case before any knowledge has been extracted. The
        // best-effort read must skip silently and leave the input untouched (design §6).
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain("Prior knowledge", "a missing _toc.md leaves the review input unchanged");
    }

    [Fact]
    public async Task Reviewed_persists_a_review_artifact_and_skips_optional_arms_by_default()
    {
        // What is persisted MID-lifecycle is the load-bearing half. At the barrier the review has already
        // produced a provisional answer, written while its children were still running: it must be visible
        // ONLY as a checkpoint under a kind nothing downstream reads. Persisting it as the authoritative
        // `review` — or letting the judge or the publisher observe it — would publish a half-review that the
        // synthesis turn can no longer retract.
        Fixture? observed = null;
        ReviewRun? observedRun = null;
        var artifactKindsPerPoll = new List<string[]>();
        var outboxRowsPerPoll = new List<int>();
        var source = new ObservingCompletionSource(() =>
        {
            artifactKindsPerPoll.Add([.. observed!.Store.GetArtifacts(observedRun!.Id).Select(a => a.ArtifactKind)]);
            outboxRowsPerPoll.Add(observed.Store.GetOutboxForRun(observedRun.Id).Count);
        });
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: source);
        observed = fixture;
        var run = fixture.SeedRun();
        observedRun = run;

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        artifactKindsPerPoll.Should().NotBeEmpty("the barrier ran");
        artifactKindsPerPoll.Should().AllSatisfy(kindsAtBarrier =>
        {
            kindsAtBarrier.Should().Contain(
                DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
                "the provisional answer is checkpointed before the wait it has to survive");
            kindsAtBarrier.Should().NotContain(
                DaemonReviewStageExecutor.ReviewArtifactKind,
                "only a synthesis turn may write the authoritative review");
            kindsAtBarrier.Should().NotContain(JudgeAgent.JudgeArtifactKind);
        });
        outboxRowsPerPoll.Should().AllBeEquivalentTo(0, "nothing is delivered while the review is still provisional");

        var kinds = fixture.Store.GetArtifacts(run.Id).Select(a => a.ArtifactKind).ToList();
        kinds.Should().Contain(DaemonReviewStageExecutor.ReviewArtifactKind);
        kinds.Should().NotContain(VariantReviewer.VariantReviewArtifactKind, "EnableABVariants is off by default");
        kinds.Should().NotContain(JudgeAgent.JudgeArtifactKind, "the judge runs in the Judged stage when enabled");

        fixture.Factory.CreatedProfileIds.Should().Contain(DaemonAgentFactory.ReviewProfileId);
        fixture.Factory.CreatedProfileIds.Should().NotContain($"{DaemonAgentFactory.ReviewProfileId}-b");
    }

    [Fact]
    public async Task Reviewed_persists_the_provisional_and_the_authoritative_answers_as_separate_artifacts()
    {
        // The two turns produce genuinely different answers, so the checkpoint and the review must not be
        // confusable: the artifact everything downstream reads has to carry the SYNTHESIS text.
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true });
        fixture.Factory.DecorateCreatedAgent = agent => agent.ThenReplies(
            new TextMessage { Text = SynthesisText, Role = Role.Assistant, RunId = "run-synthesis" });
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        var artifacts = fixture.Store.GetArtifacts(run.Id);
        ReviewTextOf(artifacts, DaemonReviewStageExecutor.ProvisionalReviewArtifactKind)
            .Should().Be(fixture.Factory.DefaultText, "the checkpoint keeps the provisional answer verbatim");
        ReviewTextOf(artifacts, DaemonReviewStageExecutor.ReviewArtifactKind).Should().Be(SynthesisText);
        fixture.GitHubPublisher.PostedBodies.Should().ContainSingle().Which
            .Should().Contain(SynthesisText).And.NotContain(
                fixture.Factory.DefaultText, "the provisional answer must never reach the PR");
    }

    // ── Restart semantics: a Reviewed lifecycle interrupted by a daemon restart ──────────────────────
    // The review stage can run for its whole 30-minute budget, so a deploy or crash lands inside it routinely.
    // Only the S2S path is resumable, and that is a property of where the turn RUNS: its conversation lives on
    // a host that outlives the daemon process. These pin what a restart may pick up (the conversation, the
    // accepted synthesis input, the ORIGINAL deadline) and what it must not (a provisional promoted to
    // authoritative, a second fan-out, a rebased budget).

    [Fact]
    public async Task Reviewed_resumes_the_persisted_hosted_conversation_instead_of_re_running_the_provisional_turn()
    {
        var barrierPolls = 0;
        using var fixture = Fixture.GitHub(
            LoggerFactory, S2SResumeOptions(), completionSource: new ObservingCompletionSource(() => barrierPolls++));
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle(
            "one turn was created").Which.Should().Be(
            "thread-persisted", "the review rejoins the conversation its provisional turn ran on");
        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().ContainSingle(
            "the provisional turn ran before the restart; re-running it would fan out a second sub-agent tree");
        barrierPolls.Should().BeGreaterThan(
            0, "no snapshot is checkpointed — a resumed lifecycle re-queries the barrier and re-proves stability");
        fixture.Store.GetArtifacts(run.Id).Should().ContainSingle(
            a => a.ArtifactKind == DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            "the existing checkpoint is resumed, not rewritten");
        fixture.Store.GetArtifacts(run.Id).Should().Contain(
            a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind, "the resumed lifecycle still completes");
    }

    [Fact]
    public async Task Reviewed_polls_an_already_accepted_synthesis_input_instead_of_queueing_a_second_one()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        // The restart landed AFTER the host accepted the synthesis turn: it is (or was) already producing an
        // answer for that input, so re-sending it would run the same synthesis twice on one conversation.
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));
        SeedSynthesisRequest(fixture, run, "input-inflight", "thread-persisted");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var loop = fixture.Factory.ResumableLoops.Should().ContainSingle().Subject;
        loop.ArmedResumeInputIds.Should().Equal("input-inflight");
        loop.RejoinedInputIds.Should().Equal("input-inflight");
        loop.AcceptedInputIds.Should().BeEmpty("nothing new was queued — the accepted input was polled");
    }

    [Fact]
    public async Task Reviewed_ignores_a_synthesis_input_accepted_on_a_different_conversation()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        // A synthesis request left over from an EARLIER lifecycle, on a conversation this one is not resuming.
        // Rejoining it would poll an answer to a review that was never asked on this thread.
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));
        SeedSynthesisRequest(fixture, run, "input-stale", "thread-from-a-previous-attempt");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var loop = fixture.Factory.ResumableLoops.Should().ContainSingle().Subject;
        loop.ArmedResumeInputIds.Should().ContainSingle().Which.Should().BeNull();
        loop.RejoinedInputIds.Should().BeEmpty();
        loop.AcceptedInputIds.Should().ContainSingle("the synthesis turn is sent normally and re-checkpointed");
    }

    [Fact]
    public async Task Reviewed_keeps_the_original_absolute_deadline_across_a_restart()
    {
        // The budget covers provisional + barrier + synthesis. Recomputing it as now + N on every restart would
        // let a run that restarts repeatedly wait forever, which is the exact hang the one budget exists to bound.
        var originalDeadline = DateTimeOffset.UtcNow.AddMinutes(4);
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", originalDeadline);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.Deadlines.Should().ContainSingle(
            "the resumed lifecycle runs exactly one turn — the synthesis").Which.Should().Be(
            originalDeadline, "a resumed turn inherits what is LEFT of the original budget, never a fresh one");
    }

    [Fact]
    public async Task Reviewed_discards_a_checkpoint_whose_budget_already_expired_and_starts_a_fresh_lifecycle()
    {
        // Every turn refuses to start once the deadline has passed, so resuming a spent budget could only fail
        // the same way every round, forever. Starting over is the only way the run can still produce a review —
        // and the self-healing path for a checkpoint the host no longer recognises.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-stale", DateTimeOffset.UtcNow.AddMinutes(-1));

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().BeNull();
        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().HaveCount(2, "the fresh lifecycle runs provisional and synthesis again");
        agent.Deadlines.Should().AllSatisfy(d => d.Should().BeAfter(DateTimeOffset.UtcNow, "a fresh budget was started"));
    }

    [Fact]
    public async Task Reviewed_restarts_an_interrupted_in_process_review_collect_only_and_never_promotes_the_provisional()
    {
        // An in-process turn died with the loop that produced it: there is no conversation to re-enter and no
        // accepted input to poll. Fabricating resumability here would either re-post a stale provisional as the
        // review or wait on a sub-agent tree that no longer exists.
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(
            fixture, run, "thread-that-died", DateTimeOffset.UtcNow.AddMinutes(20), text: StaleProvisionalText);
        SeedSynthesisRequest(fixture, run, "input-that-died", "thread-that-died");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().BeNull();
        fixture.Factory.ResumableLoops.Should().BeEmpty("an in-process loop is not resumable, so none is wrapped");
        var agent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().HaveCount(2, "the attempt restarts collect-only");
        ArtifactsOf(fixture, run, DaemonReviewStageExecutor.SynthesisRequestArtifactKind).Should().ContainSingle(
            "the seeded row is the only one — an in-process turn cannot be rejoined, so none is checkpointed");
        ReviewTextOf(fixture.Store.GetArtifacts(run.Id), DaemonReviewStageExecutor.ReviewArtifactKind)
            .Should().Be(fixture.Factory.DefaultText).And.NotBe(
                StaleProvisionalText, "a provisional is never promoted to the authoritative review");
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("modality")]
    [InlineData("model")]
    [InlineData("rung")]
    [InlineData("tool-mode")]
    [InlineData("context-generation")]
    public async Task Reviewed_discards_a_checkpoint_whose_lifecycle_identity_no_longer_matches(string mismatch)
    {
        // A hosted conversation is bound to a checkout, a model and a context this process may no longer be
        // holding. Resuming a mismatched one is unbounded: the synthesis reviews whatever the conversation is
        // actually attached to and posts those findings to THIS PR, with nothing anywhere reporting an error.
        // Starting over costs exactly one duplicate review. Each case below isolates ONE discriminator; the
        // all-fields-match control is Reviewed_resumes_the_persisted_hosted_conversation_… above.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var current = LifecycleOf(fixture, run);
        var persisted = mismatch switch
        {
            // The pooled slot is re-leased per process, so after a restart the same run can hold a slot whose
            // checkout is a DIFFERENT PR while its checkpointed conversation is bound to the old slot.
            "workspace" => current with { WorkspaceId = "review-slot-3" },
            // Configuration flipped to hosted: an in-process checkpoint's thread id is a daemon-local
            // review-run-* string that no host would recognise.
            "modality" => current with { Modality = DaemonReviewStageExecutor.InProcessModality },
            "model" => current with { ModelId = "gpt-5.6-terra" },
            // An escalation rung runs on its own thread with the overflowing history shed; resuming it as the
            // base attempt would review under a model and window this attempt never chose.
            "rung" => current with
            {
                LocalThreadId = DaemonReviewStageExecutor.ThreadId(run, run.VariantId + "-esc"),
            },
            "tool-mode" => current with { ToolAssisted = true },
            // ContextReady was re-entered: the diff the checkpointed conversation reviewed is superseded.
            _ => current with { ContextGeneration = current.ContextGeneration + 1 },
        };
        SeedProvisionalCheckpoint(
            fixture, run, "thread-from-another-lifecycle", DateTimeOffset.UtcNow.AddMinutes(20),
            lifecycle: persisted);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().BeNull(
            "a checkpoint this process cannot prove it wrote is not a conversation it may rejoin");
        fixture.Factory.ResumableLoops.Should().ContainSingle().Which.MintedThreadIds.Should().ContainSingle(
            "a discarded checkpoint starts over on a NEW conversation");
        fixture.Factory.CreatedAgents.Should().ContainSingle().Which.ReceivedInputs.Should().HaveCount(
            2, "the fresh lifecycle runs provisional and synthesis again");
    }

    [Fact]
    public async Task Reviewed_starts_a_fresh_lifecycle_when_the_context_stage_was_re_entered_after_the_checkpoint()
    {
        // The documented rollback path: a run is reset to ContextReady and its context rebuilt. The checkpoint
        // is keyed to the context generation it was built from — a new 'review-context' row — so re-entering
        // the stage invalidates it without needing a tombstone to be written by whoever did the rollback.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().BeNull(
            "the conversation reviewed a diff this run is no longer about");
        fixture.Factory.ResumableLoops.Should().ContainSingle().Which.MintedThreadIds.Should().ContainSingle();
    }

    [Fact]
    public async Task Reviewed_checkpoints_the_hosted_conversation_the_instant_it_is_minted()
    {
        // The mint window is the one genuinely unrecoverable gap: between the host creating the conversation
        // and the daemon recording it, a crash leaves a fan-out running that nothing can find. Checkpointing
        // at mint time — before the provisional turn is even sent — closes it. The row carries no review text,
        // which is precisely what marks it a LIFECYCLE record rather than an answer.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        fixture.Factory.DecorateCreatedAgent = agent => agent.FailsFirstTurn(new InvalidOperationException("host 503"));
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*host 503*");
        var checkpoint = PayloadOf<ReviewArtifactPayload>(
            fixture, run, DaemonReviewStageExecutor.ProvisionalReviewArtifactKind);
        checkpoint.ThreadId.Should().Be(
            $"hosted-{DaemonReviewStageExecutor.ThreadId(run, run.VariantId)}",
            "the minted conversation is recorded before the turn that fans out on it");
        checkpoint.ProvisionalComplete.Should().BeFalse("the provisional turn never returned");
        checkpoint.ReviewText.Should().BeEmpty();
        checkpoint.Lifecycle.Should().Be(
            LifecycleOf(fixture, run), "a checkpoint only a matching process may resume");
        fixture.Store.GetArtifacts(run.Id).Should().NotContain(
            a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task Reviewed_re_drives_an_unfinished_provisional_turn_on_the_conversation_it_already_minted()
    {
        // A restart INSIDE the provisional turn. The conversation exists, so minting a second one would fan
        // out a second sub-agent tree against the same PR; the turn is re-sent on the SAME conversation under
        // the same idempotency key instead, which the host reconciles to the turn it already accepted.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(
            fixture, run, "thread-minted", DateTimeOffset.UtcNow.AddMinutes(20),
            text: string.Empty, provisionalComplete: false);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().Be("thread-minted");
        var localThreadId = DaemonReviewStageExecutor.ThreadId(run, run.VariantId);
        var loop = fixture.Factory.ResumableLoops.Should().ContainSingle().Subject;
        loop.MintedThreadIds.Should().BeEmpty("the conversation already exists");
        loop.ArmedIdempotencyKeys.Should().Equal(
            DaemonReviewStageExecutor.TurnIdempotencyKey(localThreadId, DaemonReviewStageExecutor.ProvisionalTurn),
            DaemonReviewStageExecutor.TurnIdempotencyKey(localThreadId, DaemonReviewStageExecutor.SynthesisTurn));
        ReviewTextOf(fixture.Store.GetArtifacts(run.Id), DaemonReviewStageExecutor.ReviewArtifactKind)
            .Should().Be(
                fixture.Factory.DefaultText,
                "the empty mint-time payload is a lifecycle record, never an authoritative answer");
    }

    [Fact]
    public async Task Reviewed_checkpoints_the_accepted_synthesis_input_against_the_conversation_it_ran_on()
    {
        // The last re-sendable moment of the lifecycle: once the host has accepted the synthesis turn, a
        // restart must POLL it rather than send a second one. That is only decidable if the row records which
        // conversation the input belongs to — an id alone would be rejoined on a conversation that never saw
        // it. The run id is carried for the same reason artifacts carry one: to be readable in isolation.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var loop = fixture.Factory.ResumableLoops.Should().ContainSingle().Subject;
        loop.ArmedResumeInputIds.Should().ContainSingle().Which.Should().BeNull(
            "no accepted-input row survived, so the turn is sent rather than polled");
        PayloadOf<SynthesisRequestPayload>(
            fixture, run, DaemonReviewStageExecutor.SynthesisRequestArtifactKind).Should().Be(
            new SynthesisRequestPayload(
                loop.NextInputId, run.Id.ToString(CultureInfo.InvariantCulture), "thread-persisted"));
    }

    [Fact]
    public async Task Reviewed_clamps_a_resumed_deadline_to_the_configured_maximum()
    {
        // A checkpoint written by a process configured with a longer window — or by a clock that had run
        // ahead — must not hold this stage open for longer than the configuration in force allows. The
        // persisted deadline is the other ceiling: a restart continues a budget, it never extends one.
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                ReviewSubAgentBarrierQuietSeconds = 1,
                ReviewStageDeadlineMinutes = 1,
            });
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var ceiling = DateTimeOffset.UtcNow.AddMinutes(1);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(90));

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().Be(
            "thread-persisted", "the checkpoint is still resumed — only its budget is clamped");
        fixture.Factory.CreatedAgents.Should().ContainSingle().Which.Deadlines.Should().ContainSingle()
            .Which.Should().BeOnOrBefore(ceiling.AddSeconds(5)).And.BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Reviewed_refuses_a_hosted_review_whose_loop_cannot_checkpoint_its_turns()
    {
        // Resumability is REQUIRED on the hosted path, resolved through any decorator. A wrapper that hides
        // the capability, or a factory wired to the wrong loop, would otherwise mint a second conversation and
        // fan out a second sub-agent tree on every restart — silently, and for as long as restarts happen.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        fixture.Factory.Resumable = false;
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*IResumableReviewTurn*");
        fixture.Store.GetArtifacts(run.Id).Should().NotContain(
            a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task Reviewed_surfaces_an_unreadable_checkpoint_instead_of_starting_a_second_review_on_top_of_it()
    {
        // An unreadable checkpoint is indistinguishable from one whose sub-agent tree is still running on the
        // host. Treating it as "no checkpoint" would fan out a second tree on top of the first EVERY round,
        // forever. Surfacing it lets the retry governor charge the failure and park the run after a bounded
        // number of attempts (PrOrchestratorRetryTests), with the artifact intact for diagnosis.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        _ = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            Provider = "github",
            Payload = "{ truncated by a half-written",
        });

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().ThrowAsync<ReviewCheckpointCorruptException>();
        fixture.Factory.CreatedAgents.Should().BeEmpty("no review is started while an existing lifecycle is unreadable");
    }

    [Fact]
    public async Task Reviewed_blames_the_payload_and_not_the_store_when_a_checkpoint_reads_as_nothing()
    {
        // "Corrupt" is a verdict on the ARTIFACT, and it costs the run: it is charged to the retry governor
        // and parks the PR with the checkpoint left in place for a human. So it may only be reached from a
        // fault the payload itself proves — here, a row that deserializes to no object at all. The store the
        // row is read through raises InvalidOperationException for ordinary transient trouble (a connection
        // closed under a shutdown, say), which says nothing about the artifact; the mapper must not be able
        // to reach its verdict through that shape, so a payload fault is raised as one.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        _ = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            Provider = "github",
            Payload = "null",
        });

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ReviewCheckpointCorruptException>();
        thrown.And.InnerException.Should().BeOfType<JsonException>(
            "only a serialization fault may be answered with a corruption verdict");
        fixture.Factory.CreatedAgents.Should().BeEmpty("no review is started while an existing lifecycle is unreadable");
    }

    [Fact]
    public async Task Reviewed_persists_no_authoritative_review_when_the_synthesis_turn_fails()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        fixture.Factory.DecorateCreatedAgent = agent => agent.ThenThrows(new InvalidOperationException("host 503"));
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*host 503*");
        var kinds = fixture.Store.GetArtifacts(run.Id).Select(a => a.ArtifactKind).ToList();
        kinds.Should().Contain(
            DaemonReviewStageExecutor.ProvisionalReviewArtifactKind, "the interrupted lifecycle's checkpoint survives");
        kinds.Should().NotContain(
            DaemonReviewStageExecutor.ReviewArtifactKind, "a failed synthesis leaves no authoritative review behind");
    }

    [Fact]
    public async Task Reviewed_fails_with_a_barrier_deadline_and_persists_no_review_when_the_resumed_budget_runs_out()
    {
        // The dedicated exception type matters beyond this stage: it is what tells the orchestrator that the
        // tree never settled inside a whole budget — a stuck review to be parked, not a transient to retry.
        using var fixture = Fixture.GitHub(
            LoggerFactory, S2SResumeOptions(), completionSource: new NeverSettlingCompletionSource());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMilliseconds(300));

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        fixture.Store.GetArtifacts(run.Id).Should().NotContain(
            a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
        fixture.Factory.CreatedAgents.Should().ContainSingle().Which
            .ReceivedInputs.Should().BeEmpty("the barrier never opened, so the synthesis turn never ran");
    }

    /// <summary>A source whose roster never reaches a terminal status, so the barrier can only end at the
    /// caller's deadline — the stuck-tree case <see cref="ReviewBarrierDeadlineException"/> exists for.</summary>
    private sealed class NeverSettlingCompletionSource : IReviewSubAgentCompletionSource
    {
        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run, string parentThreadId, CancellationToken ct) =>
            Task.FromResult(new ReviewSubAgentTreeSnapshot(
            [
                new ReviewSubAgentNode
                {
                    AgentId = "child-1",
                    ThreadId = "child-thread",
                    ParentThreadId = parentThreadId,
                    Depth = 1,
                    Status = ReviewSubAgentStatus.Running,
                    Template = "code-reviewer",
                },
            ]));
    }

    [Fact]
    public async Task EnableABVariants_also_persists_a_b_variant_review_artifact()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableABVariants = true });
        fixture.Factory.TextByProfileId[$"{DaemonAgentFactory.ReviewProfileId}-b"] = "## Review (B)\nConsider: extract.";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var bVariant = fixture.Store
            .GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == VariantReviewer.VariantReviewArtifactKind).Subject;
        JsonDocument.Parse(bVariant.Payload).RootElement.GetProperty("ReviewText").GetString()
            .Should().Contain("Review (B)");
        fixture.Factory.CreatedProfileIds.Should().Contain($"{DaemonAgentFactory.ReviewProfileId}-b");
    }

    [Fact]
    public async Task Judged_skips_the_judge_artifact_when_the_flag_is_off()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        fixture.Store.GetArtifacts(run.Id).Should().NotContain(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind);
    }

    [Fact]
    public async Task Judged_persists_a_judge_artifact_when_enabled()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableJudgeAgent = true });
        fixture.Factory.TextByProfileId[DaemonAgentFactory.JudgeProfileId] = "{\"score\": 8, \"rationale\": \"Solid.\"}";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        var judge = fixture.Store
            .GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind).Subject;
        JsonDocument.Parse(judge.Payload).RootElement.GetProperty("Score").GetInt32().Should().Be(8);
    }

    [Fact]
    public async Task An_azure_devops_run_maps_to_the_ado_provider_and_publisher()
    {
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true });
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        fixture.Store.GetArtifacts(run.Id)
            .Should().OnlyContain(a => a.Provider == "ado", "azure-devops is mapped to the 'ado' provider string");
        fixture.AdoPublisher!.PostCount.Should().Be(1);
        fixture.GitHubPublisher.PostCount.Should().Be(0, "the ado run must not select the github publisher");
    }

    [Fact]
    public async Task Posted_publishes_the_review_artifacts_to_the_reviewbot_repo_when_configured()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
        // This is the first review of the PR: the review branch does not exist yet.
        fixture.Runner.OnArgvContains(
            "rev-parse --verify review/lmdotnettools-118",
            new SandboxCommandResult(1, string.Empty, "unknown revision"));
        // The push must succeed so the retention sequence reaches the reviewbot_push record.
        fixture.Runner.OnArgvContains(
            "rev-parse review/lmdotnettools-118",
            new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // CommitNotesAsync commits onto the review branch and pushes the BRANCH (not the default branch) —
        // the branch persists so later re-reviews can keep appending notes to it.
        commands.Should().Contain(a => a.Contains("checkout -B review/lmdotnettools-118"));
        commands.Should().Contain(a => a.Contains("commit -m"));
        commands.Should().Contain(a => a.Contains("push origin review/lmdotnettools-118"));

        // The branch is kept: nothing merges or deletes it as part of a single review pass.
        commands.Should().NotContain(a => a.Contains("branch -D review/lmdotnettools-118"));
        commands.Should().NotContain(a => a.Contains("push origin --delete review/lmdotnettools-118"));
        commands.Should().NotContain(a => a.Contains("push origin main"), "a per-review commit must never fast-forward the default branch");

        // The PRs/... review artifact was written into the checkout before the commit.
        fixture.FileSystem.Writes.Should().Contain(p => p.Contains("/PRs/") && p.EndsWith("review.md"));
        // The retained artifact carries the raw review text — the "[BotName]" prefix is only added to the
        // POSTED comment (see Posted_prefixes_the_posted_comment_with_the_configured_bot_name), never to
        // what's committed to the ReviewBot repo.
        var reviewFilePath = fixture.FileSystem.Writes.Single(p => p.Contains("/PRs/") && p.EndsWith("review.md"));
        fixture.FileSystem.Files[reviewFilePath].Should().NotStartWith("[");

        // The reviewbot_push outcome is persisted in SQLite (outbox row, terminal Posted with the pushed SHA).
        var push = fixture.Store
            .GetOutboxForRun(run.Id)
            .Should().ContainSingle(o => o.Operation == DaemonReviewStageExecutor.PushReviewBotOperation).Subject;
        push.Status.Should().Be(OutboxStatus.Posted);
        push.ProviderResponseId.Should().Be("f00dcafef00dcafe");
    }

    [Fact]
    public async Task Posted_clones_and_validates_the_reviewbot_checkout_before_pushing()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
        // The ReviewBot checkout does not exist yet (rev-parse probe fails everywhere via Default scripting
        // for /workspace/reviewbot), so the executor must clone it from ReviewBotRepoUrl before publishing (H3).
        fixture.Runner.On(
            c => string.Join(' ', c.Argv).Contains("rev-parse --is-inside-work-tree")
                && c.WorkingDirectory == "/workspace/reviewbot",
            new SandboxCommandResult(1, string.Empty, "not a git repo"));
        // A well-formed (already-seeded) ReviewBot checkout: all required skeleton files are present.
        SeedReviewBotSkeleton(fixture);
        fixture.Runner.OnArgvContains("rev-parse main", new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().Contain(
            a => a.Contains("clone") && a.Contains("CodeReviewBot-Workspace"),
            "the ReviewBot remote is cloned into its checkout before pushing (H3)");
    }

    [Fact]
    public async Task Posted_fails_fast_when_the_reviewbot_checkout_is_malformed()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
        // The checkout exists but the skeleton is malformed: only README.md present (PRs/, KnowledgeBase/
        // missing). The executor must surface this rather than pushing into a corrupt repo (H3).
        fixture.FileSystem.Seed("/workspace/reviewbot/README.md", "# ReviewBot");
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*malformed*");
    }

    [Fact]
    public async Task Posted_skips_reviewbot_retention_when_no_repo_is_configured()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains("checkout -B review/"), "retention is off without a ReviewBotRepoUrl");
        fixture.Store.GetOutboxForRun(run.Id)
            .Should().NotContain(o => o.Operation == DaemonReviewStageExecutor.PushReviewBotOperation);
    }

    [Fact]
    public async Task Posted_records_GitSyncFailed_and_keeps_the_review_branch_when_the_push_fails()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
        // The push never succeeds → GitSyncFailed: nothing is deleted, the outbox row is left for reconcile.
        fixture.Runner.OnArgvContains(
            "push origin review/lmdotnettools-118",
            new SandboxCommandResult(1, string.Empty, "rejected"));
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(a => a.Contains("branch -D review/"), "a failed push must keep the review branch");

        var push = fixture.Store
            .GetOutboxForRun(run.Id)
            .Should().ContainSingle(o => o.Operation == DaemonReviewStageExecutor.PushReviewBotOperation).Subject;
        push.Status.Should().Be(OutboxStatus.Pending, "a GitSyncFailed push is left non-terminal so reconcile retries");
        push.ProviderResponseId.Should().BeNull();
    }

    [Fact]
    public async Task ContextReady_throws_and_persists_nothing_when_the_diff_fetch_fails()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, diffResult: new SandboxCommandResult(1, string.Empty, "fatal: bad revision"));
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("a failed diff must surface so the stage retries");
        fixture.Store.GetArtifacts(run.Id).Should().BeEmpty("no partial context artifact is persisted on failure");
    }

    [Fact]
    public async Task Posted_throws_when_no_publisher_matches_the_provider()
    {
        // An ado run but only a 'github' publisher registered → the provider lookup must fail fast.
        using var fixture = Fixture.Ado(
            LoggerFactory,
            new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true },
            publishersOverride: [new FakeReviewCommentPublisher("github")]);
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ado*");
    }

    [Fact]
    public async Task Posted_does_not_post_a_comment_when_the_review_is_empty()
    {
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true });
        // The persisted review carries no prose. Posting a "_No review content was produced._" placeholder would
        // claim the head_sha's idempotency slot on the provider — the backstop scan would then adopt that
        // placeholder and permanently suppress a later REAL review of the same commit (e.g. a re-run on a
        // model that actually produces content). So an empty review must post NOTHING.
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run, SeedEmptyReviewArtifact);

        fixture.AdoPublisher!.PostedBodies.Should().BeEmpty("an empty review must not claim the head's dedup slot");
    }

    [Fact]
    public async Task Posted_does_not_host_post_the_no_new_findings_sentinel()
    {
        // The prompt's "nothing new to post" decision surfaces as the non-empty sentinel text
        // "No new findings since the last review." The host summary fallback must treat that as a no-post (not
        // publish it as a PR comment) — otherwise the post-nothing contract is violated and re-review noise
        // reappears via the host path even when the agent correctly posted nothing.
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true });
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] = "No new findings since the last review.";
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        fixture.AdoPublisher!.PostedBodies.Should().BeEmpty(
            "the no-new-findings sentinel is a deliberate no-post, so the host fallback must not publish it");
    }

    [Fact]
    public async Task Posted_prefixes_the_posted_comment_with_the_configured_bot_name()
    {
        using var fixture = Fixture.Ado(
            LoggerFactory,
            new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true, BotName = "GB's Revobot" });
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        fixture.AdoPublisher!.PostedBodies.Should().ContainSingle()
            .Which.Should().StartWith("[GB's Revobot]\n\n");
    }

    // ── Regression guards: "reviewed but not delivered" ──────────────────────────────────────────────
    // Both live outages (mcqdb/ADO and achieveai/GitHub, 2026-07-14→15) were the SAME silent failure: a run
    // reached Posted/Completed with a real review, on an EnableCommentPosting=true profile, yet posted NOTHING
    // to the PR (only push-reviewbot retention). The causes differed — for ADO the agent's code-reviewer:
    // post-pr-review skill is GitHub-only, and for GitHub the agent loaded the skill but completed without ever
    // running it — but the observable defect was identical: no comment on the PR. These tests pin the invariant
    // that would have caught BOTH: whatever the mechanism, an authorized non-empty review is delivered exactly
    // once via the run's provider publisher.

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_delivers_the_review_to_the_pr_for_every_provider_when_authorized(string provider)
    {
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true })
            : Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true });
        var expectedPublisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        expectedPublisher.PostCount.Should().Be(
            1, $"a completed, authorized, non-empty review must be delivered to the {provider} PR — never silently dropped");
    }

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_does_not_post_when_comment_posting_is_disabled(string provider)
    {
        // The dual of the delivery guard: a collect-only profile (EnableCommentPosting=false, the safe default)
        // still produces + retains a review, but must NEVER post it to the PR.
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = false, EnableHostSummaryFallback = true })
            : Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = false, EnableHostSummaryFallback = true });
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        publisher.PostCount.Should().Be(0, "a collect-only profile must not post to the PR");
    }

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_does_not_post_an_empty_review_for_any_provider(string provider)
    {
        // An empty review must post NOTHING for EITHER provider — posting a placeholder would claim the
        // head_sha's idempotency slot and permanently suppress a later REAL review of the same commit.
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true })
            : Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true, EnableHostSummaryFallback = true });
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run, SeedEmptyReviewArtifact);

        publisher.PostedBodies.Should().BeEmpty("an empty review must not claim the head's dedup slot");
    }

    // ── S2S deep-link: the posted review links back to the LmStreaming review conversation ───────────
    // When the review runs over the LmStreaming S2S API (UseS2SReviewAgent), the hosted conversation is the
    // deliverable a human judge opens. The daemon still owns posting, so it is the single provider-uniform
    // place that appends the deep-link — {LmStreamingBaseUrl}/?threadId={mintedThreadId}&focus=1 — under the
    // [{BotName}] prefix, exactly once, for BOTH providers. (The S2S fixture's loop mints "hosted-{threadId}",
    // standing in for the id LmStreaming assigns at provision — deliberately NOT the daemon's own thread id; no
    // workspace preparer is wired on this ctor, so Create receives workspaceId: null and the loop still surfaces
    // its conversation.) UseS2SReviewAgent alone opens the host-side post path (postHostSide) — the host summary
    // fallback is not required — while shouldPost stays false, so no would-be enforcement turn runs.
    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_appends_the_s2s_deep_link_once_under_the_bot_prefix_for_every_provider(string provider)
    {
        static CodeReviewDaemonOptions S2SOptions() =>
            new()
            {
                UseS2SReviewAgent = true,
                LmStreamingBaseUrl = "http://localhost:5051",
                EnableCommentPosting = true,
            };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, S2SOptions())
            : Fixture.GitHub(LoggerFactory, S2SOptions());
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        var hostedThreadId = $"hosted-{DaemonReviewStageExecutor.ThreadId(run, run.VariantId)}";
        var expectedLink = $"http://localhost:5051/?threadId={hostedThreadId}&focus=1";
        var body = publisher.PostedBodies.Should().ContainSingle().Subject;
        body.Should().StartWith("[Revobot]\n\n", "the deep-link is appended under the existing bot prefix");
        body.Should().Contain($"🔎 Full review conversation: {expectedLink}");
        // Appended exactly once — splitting on the link yields exactly two segments.
        body.Split(expectedLink).Length.Should().Be(2, "the deep-link must be appended exactly once");
    }

    // ── Delivery truthfulness: a post-mode run may not COMPLETE without provider-visible evidence ────
    // Run 27's shape: the Posted stage failed once on retention, the retry re-ran the stage, produced no
    // provider comment at all, and the run still went Completed. The stage must instead stay retryable
    // whenever the review was supposed to reach the PR and demonstrably did not — while the deliberate
    // "no new findings" no-post stays a success (forcing a comment there is the re-review noise bug).

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_stays_retryable_when_a_post_mode_review_reaches_no_provider_comment(string provider)
    {
        // The run was DISCOVERED in post mode (EnableCommentPosting was on), so this review is supposed to
        // land on the PR. The host-side poster only collects it — no provider comment exists — so the stage
        // must fail and leave the run retryable rather than completing and reporting a delivery it never made.
        var options = new CodeReviewDaemonOptions
        {
            EnableCommentPosting = false,
            EnableHostSummaryFallback = true,
        };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, options)
            : Fixture.GitHub(LoggerFactory, options);
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*post*", "the failure has to name the undelivered post so the operator can act");
        publisher.PostCount.Should().Be(0);
        // The notes retention still ran to completion before the stage reported the undelivered post, so the
        // review is not lost — only the delivery is outstanding.
        fixture.Store.GetOutboxForRun(run.Id)
            .Should().Contain(o => o.Operation == ReviewPoster.PostReviewCommentOperation);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_completes_a_post_mode_review_that_actually_reached_the_pr(string provider)
    {
        // The positive half of the guard: a post-mode review whose comment DID land completes normally, with
        // durable Posted+provider-response evidence in the outbox for the delivery classifier to read.
        var options = new CodeReviewDaemonOptions
        {
            EnableCommentPosting = true,
            EnableHostSummaryFallback = true,
        };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, options)
            : Fixture.GitHub(LoggerFactory, options);
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await RunAllStagesAsync(fixture, run);

        publisher.PostCount.Should().Be(1);
        var delivery = fixture.Store.GetOutboxForRun(run.Id)
            .Should().ContainSingle(o => o.Operation == ReviewPoster.PostReviewCommentOperation).Subject;
        delivery.Status.Should().Be(OutboxStatus.Posted);
        delivery.ProviderResponseId.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_completes_a_post_mode_no_new_findings_review_without_posting_anything(string provider)
    {
        // The sentinel is a DELIBERATE no-comment, so it must stay a success in post mode too — the delivery
        // guard above may never be widened into "always force a comment", which is exactly the re-review noise
        // the sentinel exists to stop.
        var options = new CodeReviewDaemonOptions
        {
            EnableCommentPosting = true,
            EnableHostSummaryFallback = true,
        };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, options)
            : Fixture.GitHub(LoggerFactory, options);
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] = "No new findings since the last review.";
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await RunAllStagesAsync(fixture, run);

        publisher.PostCount.Should().Be(0, "the sentinel is an intentional no-post, not a failed delivery");
        fixture.Store.GetOutboxForRun(run.Id)
            .Should().NotContain(
                o => o.Operation == ReviewPoster.PostReviewCommentOperation,
                "no delivery was attempted, so there is no comment outbox row to misread as evidence");
    }

    /// <summary>Seeds a well-formed (already-seeded) ReviewBot skeleton into the checkout.</summary>
    private static void SeedReviewBotSkeleton(Fixture fixture)
    {
        fixture.FileSystem.Seed("/workspace/reviewbot/README.md", "# ReviewBot");
        fixture.FileSystem.Seed("/workspace/reviewbot/PRs/.gitkeep", string.Empty);
        fixture.FileSystem.Seed("/workspace/reviewbot/KnowledgeBase/.gitkeep", string.Empty);
        fixture.FileSystem.Seed("/workspace/reviewbot/KnowledgeBase/_toc.md", "# Knowledge Base");
    }

    private static async Task RunAllStagesAsync(
        Fixture fixture, ReviewRun run, Action<Fixture, ReviewRun>? afterReviewed = null)
    {
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        afterReviewed?.Invoke(fixture, run);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
    }

    /// <summary>
    /// Appends an EMPTY <c>review</c> artifact, which the later stages then read (artifacts are read
    /// last-wins). The Reviewed stage can no longer emit one itself — a blank synthesis turn is now a hard
    /// failure — so this is how the publisher's empty-review guard is reached, and it is the layer that guard
    /// actually protects: what was PERSISTED, whatever produced it (including artifacts written by older builds).
    /// </summary>
    private static void SeedEmptyReviewArtifact(Fixture fixture, ReviewRun run) =>
        _ = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new ReviewArtifactPayload(string.Empty, "run-review", run.VariantId)),
        });

    /// <summary>Options for the restart tests: the resumable (hosted) review path, with the settlement barrier's
    /// quiet period shortened so an already-settled tree costs one second rather than the production default.</summary>
    private static CodeReviewDaemonOptions S2SResumeOptions() =>
        new() { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 };

    /// <summary>
    /// The lifecycle identity the fixture's own Reviewed stage would build for the BASE attempt — the one a
    /// checkpoint has to still match to be resumable. Each parameter overrides exactly one field, so a test
    /// that seeds a mismatch isolates the single discriminator it is about. (No workspace preparer is wired on
    /// this ctor, so the hosted workspace id is null here, as it is in every other test on this fixture.)
    /// </summary>
    private static ReviewLifecycleIdentity LifecycleOf(
        Fixture fixture,
        ReviewRun run,
        string modality = DaemonReviewStageExecutor.S2SModality,
        string? localThreadId = null,
        string? workspaceId = null,
        string? modelId = null,
        bool toolAssisted = false,
        long? contextGeneration = null) =>
        new(
            modality,
            localThreadId ?? DaemonReviewStageExecutor.ThreadId(run, run.VariantId),
            workspaceId,
            modelId ?? run.ModelId,
            toolAssisted,
            contextGeneration
                ?? fixture.Store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ContextArtifactKind)?.Id
                ?? 0);

    /// <summary>Writes the checkpoint a Reviewed stage leaves behind when it is interrupted between the
    /// provisional turn and the synthesis turn — the state every restart test starts from. Defaults to the
    /// identity this fixture would rebuild (i.e. resumable) so a test that wants a discarded checkpoint has to
    /// say which field diverged. <paramref name="provisionalComplete"/> false is the narrower mint-time
    /// checkpoint: the conversation exists, but the provisional turn never returned.</summary>
    private static void SeedProvisionalCheckpoint(
        Fixture fixture,
        ReviewRun run,
        string hostedThreadId,
        DateTimeOffset deadlineUtc,
        string text = StaleProvisionalText,
        ReviewLifecycleIdentity? lifecycle = null,
        bool provisionalComplete = true) =>
        _ = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new ReviewArtifactPayload(
                text, "run-provisional", run.VariantId, hostedThreadId, deadlineUtc.AddMinutes(-10), deadlineUtc,
                lifecycle ?? LifecycleOf(fixture, run), provisionalComplete)),
        });

    /// <summary>Records that the host accepted a synthesis input, i.e. the restart landed AFTER the last
    /// re-sendable moment of the lifecycle.</summary>
    private static void SeedSynthesisRequest(Fixture fixture, ReviewRun run, string inputId, string parentThreadId) =>
        _ = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.SynthesisRequestArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new SynthesisRequestPayload(
                inputId, run.Id.ToString(CultureInfo.InvariantCulture), parentThreadId)),
        });

    /// <summary>The review text of the newest artifact of <paramref name="kind"/> (artifacts are append-only).</summary>
    private static string ReviewTextOf(IReadOnlyList<ReviewArtifact> artifacts, string kind) =>
        JsonSerializer.Deserialize<ReviewArtifactPayload>(
            artifacts.Last(a => string.Equals(a.ArtifactKind, kind, StringComparison.Ordinal)).Payload)!.ReviewText;

    /// <summary>The newest checkpoint artifact of <paramref name="kind"/>, deserialized — the row a restarting
    /// process would read back.</summary>
    private static T PayloadOf<T>(Fixture fixture, ReviewRun run, string kind) =>
        JsonSerializer.Deserialize<T>(fixture.Store
            .GetArtifacts(run.Id)
            .Last(a => string.Equals(a.ArtifactKind, kind, StringComparison.Ordinal)).Payload)!;

    /// <summary>The artifacts of one <paramref name="kind"/>, in append order.</summary>
    private static IEnumerable<ReviewArtifact> ArtifactsOf(Fixture fixture, ReviewRun run, string kind) =>
        fixture.Store.GetArtifacts(run.Id).Where(a => string.Equals(a.ArtifactKind, kind, StringComparison.Ordinal));

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _db;
        private readonly string _repoProvider;

        private Fixture(
            ILoggerFactory loggerFactory,
            string repoProvider,
            CodeReviewDaemonOptions? options,
            SandboxCommandResult? diffResult,
            IReviewCommentPublisher[]? publishersOverride,
            IReviewSubAgentCompletionSource? completionSource)
        {
            _db = new TempSqliteDatabase();
            _repoProvider = repoProvider;
            Store = new ReviewStore(_db.ConnectionString);
            Runner = new FakeSandboxCommandRunner()
                // A fresh sandbox: the target checkout does not exist yet, so the rev-parse probe fails
                // and the executor clones the target repo (PR #121 H1).
                .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
                .OnArgvContains("diff", diffResult ?? new SandboxCommandResult(0, DiffText, string.Empty));
            FileSystem = new FakeSandboxFileSystem();
            GitHubPublisher = new FakeReviewCommentPublisher("github");
            AdoPublisher = repoProvider == "azure-devops" ? new FakeReviewCommentPublisher("ado") : null;

            options ??= new CodeReviewDaemonOptions();
            // Resumability is a property of WHERE the turn runs, so the double is resumable on exactly the
            // path production's is: hosted (S2S) turns survive this process, in-process ones do not.
            Factory.Resumable = options.UseS2SReviewAgent;
            var publishers = publishersOverride
                ?? (AdoPublisher is null
                    ? [GitHubPublisher]
                    : [GitHubPublisher, AdoPublisher]);

            Executor = new DaemonReviewStageExecutor(
                Store,
                Factory,
                Runner,
                FileSystem,
                options,
                publishers,
                loggerFactory,
                completionSource: completionSource);
        }

        public ReviewStore Store { get; }
        public FakeReviewAgentLoopFactory Factory { get; } = new();
        public FakeSandboxCommandRunner Runner { get; }
        public FakeSandboxFileSystem FileSystem { get; }
        public FakeReviewCommentPublisher GitHubPublisher { get; }
        public FakeReviewCommentPublisher? AdoPublisher { get; }
        public DaemonReviewStageExecutor Executor { get; }

        public static Fixture GitHub(
            ILoggerFactory loggerFactory,
            CodeReviewDaemonOptions? options = null,
            SandboxCommandResult? diffResult = null,
            IReviewCommentPublisher[]? publishersOverride = null,
            IReviewSubAgentCompletionSource? completionSource = null) =>
            new(loggerFactory, "github", options, diffResult, publishersOverride, completionSource);

        public static Fixture Ado(
            ILoggerFactory loggerFactory,
            CodeReviewDaemonOptions? options = null,
            IReviewCommentPublisher[]? publishersOverride = null) =>
            new(loggerFactory, "azure-devops", options, diffResult: null, publishersOverride, completionSource: null);

        public ReviewRun SeedRun(string watermark = "wm-1", string mode = "collect-only")
        {
            var repoId = Store.EnsureRepo(new RepoIdentity
            {
                Provider = _repoProvider,
                OrgOrOwner = "achieveai",
                Project = _repoProvider == "azure-devops" ? "Platform" : null,
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            });
            return Store.CreateOrGetReviewRun(new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = "head-sha",
                BaseSha = "base-sha",
                TriggerWatermark = watermark,
                ReviewKind = "full",
                VariantId = "primary",
                Mode = mode,
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
}
