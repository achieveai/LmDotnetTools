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
    public async Task ContextReady_persists_changed_paths_with_filename_whitespace_intact()
    {
        // git allows a filename to begin or end with a space, and `diff --name-only` does not quote for
        // plain spaces — quoting triggers on non-ASCII, control, quote and backslash bytes only. Trimming
        // the listing therefore silently renames the first and last records into paths git never reported,
        // and the ranking they feed can no longer match them.
        using var fixture = Fixture.GitHub(LoggerFactory);
        fixture.Runner.OnArgvContainsFirst(
            "diff --name-only",
            new SandboxCommandResult(0, "\n lead.cs\ntrail.cs \n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var changedPaths = JsonDocument.Parse(artifact.Payload).RootElement
            .GetProperty("ChangedPaths").GetString();

        KnowledgeDigest.ParseChangedPaths(changedPaths).Should().Equal(" lead.cs", "trail.cs ");
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
                UseS2SReviewAgent = true,
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
        // The working tree must be moved to the PR head so Read/Grep/Glob and the changed-path listing reflect
        // the code the PR proposes, not the clone's default branch (which would ground findings in the wrong code).
        commands.Should().Contain(
            a => a.Contains("checkout --force") && a.Contains("head-sha"),
            "the PR head is checked out into the target working tree");
    }

    /// <summary>
    /// The context artifact must NOT carry a listing of the whole checkout. It used to: every run ran
    /// <c>git ls-files</c> and persisted up to 2 MiB of it. Measured on the NOVA store before removal, that
    /// manifest was 423,447,790 of the database's 446,353,408 bytes — 95% of everything the daemon had ever
    /// stored — across 207 context artifacts, every copy truncated at the same 2 MiB ceiling, and read by
    /// nothing. The reviewer was never shown it (the brief points at the checkout instead), the Knowledge Base
    /// ranking reads <c>Diff</c> and <c>ChangedPaths</c>, and a tree listing cut off mid-way is not a faithful
    /// audit record either. This test pins all three halves of the removal: the command is not run, the
    /// property is not written, and the artifact still carries what actually is read.
    /// </summary>
    [Fact]
    public async Task ContextReady_does_not_list_or_persist_the_whole_checkout()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        // If anything re-introduces the manifest, this stub is what it would capture — and the assertions below
        // would find README.md in the payload.
        fixture.Runner.OnArgvContains(
            "ls-files", new SandboxCommandResult(0, "src/Foo/Bar.cs\nsrc/Foo/Baz.cs\nREADME.md\n", string.Empty));
        fixture.Runner.OnArgvContainsFirst(
            "diff --name-only", new SandboxCommandResult(0, "src/Foo/Bar.cs\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        commands.Should().NotContain(
            a => a.Contains("ls-files"),
            "listing the tree costs a sandbox round-trip per run and nothing consumes the result");

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var payload = JsonDocument.Parse(artifact.Payload).RootElement;
        payload.TryGetProperty("FileManifest", out _).Should().BeFalse(
            "the manifest was 95% of the store's bytes and had no reader");
        artifact.Payload.Should().NotContain("README.md", "no tracked-but-unchanged file belongs in the artifact");

        // What IS read stays: the range's changed paths (the brief) and the diff (the KB ranking).
        payload.GetProperty("ChangedPaths").GetString().Should().Contain("src/Foo/Bar.cs");
        payload.GetProperty("Diff").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reviewed_sends_the_changed_paths_not_the_patch_or_the_tracked_file_manifest()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        fixture.Runner.OnArgvContains(
            "ls-files", new SandboxCommandResult(0, "src/Foo/Bar.cs\nsrc/Foo/Baz.cs\n", string.Empty));
        // Registered FIRST: the fixture's broad "diff" rule would otherwise answer the listing with a patch.
        fixture.Runner.OnArgvContainsFirst(
            "diff --name-only", new SandboxCommandResult(0, "src/Foo/Bar.cs\n", string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var input = reviewAgent.ReceivedInputs[0];
        var text = input.Messages.OfType<TextMessage>().Single().Text;

        // Changed files, yes — the reviewer needs the blast radius up front and cannot derive it from the
        // checkout alone without knowing the range.
        text.Should().Contain("src/Foo/Bar.cs");

        // The two payloads this brief used to carry. Both are things the reviewer holds a checkout of and can
        // fetch itself, and inlining them cost most of the input budget (measured on run 226: 117k of patch and
        // 15.6k of manifest in a 173,567-char brief).
        text.Should().NotContain(
            "src/Foo/Baz.cs",
            "Baz.cs is tracked but UNCHANGED - listing the whole tree is the manifest coming back in");
        text.Should().NotContain(
            "\n\nDiff:\n", "the patch is read from git now, not copied into the brief");
        text.Should().Contain(
            $"diff {run.BaseSha}...{run.HeadSha}",
            "the fetch instruction must carry the range, the one thing the reviewer cannot derive");

        // The checkout root is templated into the review agent's SYSTEM PROMPT via
        // DaemonAgentFactory.CreateReviewProfile's workspace-layout variables, and stays load-bearing now that
        // the brief tells the reviewer to go read from it.
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
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

    /// <summary>A roster of <paramref name="count"/> already-terminal children — the healthy fan-out, against
    /// which the zero case is the anomaly.</summary>
    private sealed class SettledCompletionSource(int count) : IReviewSubAgentCompletionSource
    {
        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run, string parentThreadId, CancellationToken ct) =>
            Task.FromResult(new ReviewSubAgentTreeSnapshot(
                [.. Enumerable.Range(0, count).Select(i => new ReviewSubAgentNode
                {
                    AgentId = $"agent-{i}",
                    ThreadId = $"thread-child-{i}",
                    ParentThreadId = parentThreadId,
                    Depth = 1,
                    Status = ReviewSubAgentStatus.Completed,
                    Template = "reviewer",
                })]));
    }

    /// <summary>
    /// Live run 145 posted a 1,401-char review to a real PR having dispatched ZERO dimension agents — no
    /// performance, security, test-coverage or simplification pass had looked at anything — and it went out
    /// indistinguishable from run 140's six-agent review. The daemon is entitled to review thinly; it is not
    /// entitled to do so silently, because the rate is then unmeasurable: reconstructing the six runs that
    /// exposed this needed three separate log lines joined by hand. One line, carrying the numbers that
    /// correlate, is what turns "did that happen again?" into a grep.
    /// </summary>
    [Fact]
    public async Task Reviewed_warns_when_the_review_dispatched_no_sub_agents_at_all()
    {
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: new ObservingCompletionSource(() => { }));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var line = logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Where(m => m.Contains($"Run {run.Id}:", StringComparison.Ordinal)
                && m.Contains("sub-agent", StringComparison.Ordinal))
            .Should().ContainSingle("the zero fan-out is reported exactly once, not once per poll")
            .Subject;

        line.Should().Contain(
            "0 sub-agent",
            "the count is the fact — a reader must not have to infer it from the absence of another line");
        line.Should().MatchRegex(
            "(?i)first[_ ]?review|delta",
            "framing decides whether a thin review is even plausible; a delta round with nothing new is not "
                + "the same event as a first review that examined nothing");
    }

    /// <summary>
    /// The partner pin, and the one that decides whether this line survives contact with an operator. A
    /// warning that also fires on legitimate small reviews gets tuned out within a week, at which point it
    /// protects nothing — live run 146 dispatched a single child against a one-file diff and was very likely
    /// correct to. So the threshold is ZERO, not "low", until data justifies a bound.
    /// </summary>
    [Fact]
    public async Task Reviewed_does_not_warn_when_at_least_one_sub_agent_ran()
    {
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: new SettledCompletionSource(1));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.CountAtLevel(LogLevel.Warning, "0 sub-agent").Should().Be(
            0, "one child on a small diff is an ordinary review, and an alarm that cries on it is soon ignored");
    }

    /// <summary>
    /// The same fact, recorded where a HUMAN reading the review later will find it. The log line tells an
    /// operator the rate; the artifact tells whoever opens this particular review which coverage it never
    /// had. Run 145's review was posted looking exactly like a six-agent one, and nothing persisted said
    /// otherwise — so the artifact carries the count, and zero is the degraded case.
    /// </summary>
    [Fact]
    public async Task Reviewed_records_the_sub_agent_count_on_the_review_artifact()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: new ObservingCompletionSource(() => { }));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var artifact = fixture.Store.GetArtifacts(run.Id)
            .Should().ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind).Subject;
        var payload = JsonSerializer.Deserialize<ReviewArtifactPayload>(artifact.Payload);

        payload!.SubAgentCount.Should().Be(
            0,
            "a persisted null would mean 'not recorded' and read as an old artifact; an explicit 0 is the "
                + "claim that this review genuinely had no dimension agent behind it");
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
                UseS2SReviewAgent = true,
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
                UseS2SReviewAgent = true,
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
                UseS2SReviewAgent = true,
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
    public async Task Reviewed_prepends_a_ranked_knowledge_digest_with_absolute_paths_when_the_store_has_an_index()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        // _index.jsonl carries tags/scope per entry, so it — not the title-only _toc.md — is what lets the
        // reviewer (and the sub-agents it dispatches) match a lesson to the files this PR changes.
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            """{"file":"system/null-guard.md","title":"Null-guard boundaries","tags":["null","boundaries"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}"""
                + "\n"
                + """{"file":"system/pagination.md","title":"Filter before paging","tags":["pagination"],"scope":"system","sourcePrs":[],"updated":"2026-07-04"}"""
                + "\n");
        fixture.FileSystem.Seed("/workspace/store/KnowledgeBase/_toc.md", "# Knowledge Base\n\nTOC-ONLY-MARKER\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("## Prior knowledge (Knowledge Base)");
        text.Should().Contain(
            "/workspace/store/KnowledgeBase/system/null-guard.md",
            "the agent cannot Grep the KB open, so it must be handed the exact absolute path");
        text.Should().Contain("tags: null, boundaries", "metadata is what lets a lesson be matched to a dimension");
        text.Should().Contain("sub-agent", "the parent is told to copy matching paths into each sub-agent's brief");
        text.Should().NotContain(
            "TOC-ONLY-MARKER",
            "with an index present the weaker title-only ToC block must not be used");
    }

    [Fact]
    public async Task Reviewed_ranks_knowledge_against_files_the_bounded_diff_truncated_away()
    {
        // The persisted diff is CAPPED, so on a large PR every `diff --git` header past the cap is gone.
        // Ranking off that text makes the files changed late in a PR invisible to retrieval — exactly the
        // files a big PR is most likely to need a lesson about. The changed-path list has to come from a
        // lossless source (`git diff --name-only`), which stays tiny even when the patch does not.
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
                Limits = new CodeReviewDaemon.Sample.Configuration.SandboxLimits { MaxArtifactPayloadChars = 320 },
            },
            diffResult: new SandboxCommandResult(
                0,
                "diff --git a/src/Alpha/Widget.cs b/src/Alpha/Widget.cs\n"
                    + string.Join('\n', Enumerable.Repeat("+ a line of widget body text", 20))
                    + "\ndiff --git a/src/Zeta/KrakenTentacle.cs b/src/Zeta/KrakenTentacle.cs\n+ late\n",
                string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/Alpha/Widget.cs\n", string.Empty));
        // Registered FIRST: the fixture's broad "diff" rule would otherwise swallow this narrower one.
        fixture.Runner.OnArgvContainsFirst(
            "diff --name-only",
            new SandboxCommandResult(0, "src/Alpha/Widget.cs\nsrc/Zeta/KrakenTentacle.cs\n", string.Empty));

        // The kraken entry matches two tokens of a path that only the lossless list still carries, so it
        // must outscore the widget entry. Its file sorts LAST ordinally and both entries share an Updated
        // date, so neither tie-break can produce this order — only the score can.
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            """{"file":"system/a-widget.md","title":"Widget lifecycle","tags":["widget"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}"""
                + "\n"
                + """{"file":"system/z-kraken.md","title":"Kraken tentacle retries","tags":["kraken","tentacle"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}"""
                + "\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain(
            "KrakenTentacle.cs\nb/src",
            "the fixture's cap must really have truncated the diff, or this test proves nothing");
        text.IndexOf("system/z-kraken.md", StringComparison.Ordinal).Should().BeGreaterThan(-1);
        text.IndexOf("system/z-kraken.md", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("system/a-widget.md", StringComparison.Ordinal),
            "the entry matching the truncated-away path must still be ranked on it");
    }

    [Fact]
    public async Task Reviewed_refuses_a_knowledge_entry_whose_path_escapes_the_knowledge_base()
    {
        // _index.jsonl is written into the store by the knowledge agent — an LLM with file-write tools — and
        // is read back here unvalidated. A '..' in a "file" value would hand the reviewer an absolute path
        // outside KnowledgeBase/, i.e. something that is not knowledge presented as though it were, which
        // the reviewer has no way to detect. The refusal has to be LOGGED, not silent: an entry that simply
        // vanishes from the digest is indistinguishable from a Knowledge Base that never had it.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            """{"file":"../../../workspace/target/src/LmCore/Foo.cs","title":"Poisoned","tags":["null"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}"""
                + "\n"
                + """{"file":"system/null-guard.md","title":"Null-guard boundaries","tags":["null"],"scope":"system","sourcePrs":[],"updated":"2026-07-04"}"""
                + "\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain(
            "/workspace/store/KnowledgeBase/system/null-guard.md",
            "the well-formed entry alongside it must still reach the reviewer");
        text.Should().NotContain("Poisoned");
        text.Should().NotContain(
            "/workspace/target/src/LmCore/Foo.cs",
            "the escaping entry must never be rendered as a path the agent is told to Read");

        logs.Capturing.CountAtLevel(LogLevel.Warning, "../../../workspace/target/src/LmCore/Foo.cs")
            .Should().Be(1, "a refused entry has to be visible in the log the way the surfaced ones are");
    }

    [Fact]
    public async Task Reviewed_counts_records_as_records_and_entries_as_entries_over_a_doubled_index()
    {
        // "surfaced 2 of 4 Knowledge Base entries" is defensible arithmetic and misleading English: 4 is the
        // raw record count parsed out of _index.jsonl, not four entries, so an operator reading the line
        // concludes half the store was withheld from the reviewer. Both numbers stay — swapping 4 for the
        // deduplicated count would read cleanly and delete the only number that says the index was doubled —
        // and each names what it counts, with the collapse warning alongside to explain the gap between them.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));

        // Two distinct entries, each recorded twice — the merged-index shape, four records for two lessons.
        const string NullGuard =
            """{"file":"system/null-guard.md","title":"Null-guard boundaries","tags":["null"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}""";
        const string RetryPolicy =
            """{"file":"system/retry-policy.md","title":"Retry policy","tags":["retry"],"scope":"system","sourcePrs":[],"updated":"2026-07-04"}""";
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            NullGuard + "\n" + RetryPolicy + "\n" + NullGuard + "\n" + RetryPolicy + "\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain(
            "/workspace/store/KnowledgeBase/system/null-guard.md",
            "collapsing the repeats must not cost a distinct entry");
        text.Should().Contain("/workspace/store/KnowledgeBase/system/retry-policy.md");

        logs.Capturing.CountAtLevel(LogLevel.Information, "surfaced 2 Knowledge Base entries")
            .Should().Be(1, "two entries reached the reviewer, and that is what the entry count must count");
        logs.Capturing.CountAtLevel(LogLevel.Information, "from 4 _index.jsonl records")
            .Should().Be(1, "the raw record count is the only signal that the index was doubled — it stays");
        logs.Capturing.CountAtLevel(LogLevel.Warning, "collapsed 2 duplicate _index.jsonl records")
            .Should().Be(1, "the gap between 2 and 4 is only honest if something explains it");
    }

    [Fact]
    public async Task Reviewed_keeps_a_knowledge_entry_whose_title_links_outside_the_knowledge_base()
    {
        // End to end on the primary route: an extraction agent writes a title pointing at the repo's own
        // docs, which live outside KnowledgeBase/ because repo docs do. The link must not reach the
        // reviewer, and the entry must - refusing it would delete a sound lesson over a decoration, which
        // is the knowledge-blindness this whole feature exists to remove. The scrub is logged for the same
        // reason the refusal above is: the entry arrives looking healthy, so nothing else would ever say
        // that the knowledge agent had written an escaping link into it.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            """{"file":"system/ado-onboarding.md","title":"Follow the [ADO guide](../../docs/ado.md) first","tags":["ado"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}"""
                + "\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain("../../docs/ado.md", "the escaping link must not reach the reviewer");
        text.Should().Contain(
            "/workspace/store/KnowledgeBase/system/ado-onboarding.md",
            "the entry itself is sound and must still be handed over");

        logs.Capturing.CountAtLevel(LogLevel.Warning, "system/ado-onboarding.md")
            .Should().Be(1, "a cleared field has to be as visible as a refusal, and countable");
    }

    [Fact]
    public async Task Reviewed_warns_when_an_oversized_index_parses_to_nothing_at_all()
    {
        // The record ceiling made an oversized _index.jsonl stop being read; this is about what the operator
        // is TOLD when it does. If every examined record is junk, the parse yields zero entries AND reports
        // truncation, and an empty digest is exactly what a store with no Knowledge Base yet produces. The
        // caller cannot tell those apart on its own — it falls through to the _toc.md fallback under a
        // comment reading "never extracted, or a torn file" — so the review is quietly downgraded to titles
        // and links, with no tags, no scope and no ranking, and reads as clean. The flag that separates the
        // two cases exists one frame down; dropping it there is the failure this whole feature exists to end.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            string.Join('\n', Enumerable.Repeat("not json at all", KnowledgeIndex.MaxIndexRecords + 1)));
        // A usable fallback, so the run takes the downgrade rather than the no-knowledge-at-all path: the
        // point is that a review which LOOKS well-supplied still says the index was torn.
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\n## system\n- [Null-guard boundaries](system/null-guard.md)\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.CountAtLevel(LogLevel.Warning, "_index.jsonl exceeds")
            .Should().Be(1, "an index too big AND too broken to read must not look like an absent one");
    }

    [Fact]
    public async Task Reviewed_warns_about_an_oversized_index_even_with_no_toc_to_fall_back_on()
    {
        // Same defect one step further along: with no _toc.md either, the caller's own line reads "No usable
        // Knowledge Base ...; reviewing without prior knowledge" — which is exactly what a store that has
        // never been extracted logs. This is the case where the operator has the LEAST to go on, so the
        // warning has to be attached to the reading rather than to whichever route the digest ends up taking.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            string.Join('\n', Enumerable.Repeat("not json at all", KnowledgeIndex.MaxIndexRecords + 1)));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.CountAtLevel(LogLevel.Warning, "_index.jsonl exceeds")
            .Should().Be(1, "the torn index is a fact about the read, not about which fallback followed it");
    }

    [Fact]
    public async Task Reviewed_does_not_claim_truncation_for_a_small_torn_index()
    {
        // The partner pin: the fix above must not degrade into "warn about truncation whenever the digest
        // comes out empty". A short unparseable index is the ordinary torn-file case the fallback already
        // handles, and telling the operator it exceeded a 5,000-record ceiling would send them hunting a
        // runaway extraction that is not there.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_index.jsonl",
            string.Join('\n', Enumerable.Repeat("not json at all", 3)));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\n## system\n- [Null-guard boundaries](system/null-guard.md)\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.CountAtLevel(LogLevel.Warning, "_index.jsonl exceeds")
            .Should().Be(0, "nothing was left behind, so nothing was truncated");
    }

    /// <summary>
    /// The fourth zero, and the only one that is a daemon defect rather than a fact about the reviewed repo:
    /// a run with no leased checkout never probes for <c>CLAUDE.md</c>/<c>AGENTS.md</c> at all. It reaches
    /// the brief inventory as the same <c>repo-guidance=0</c> a well-behaved probe of a repo with no
    /// conventions produces, and the two want opposite responses — accept the first, go and find out why the
    /// lease is missing on the second. A resumed pooled run that lost its in-memory lease lands here, which
    /// is precisely the case that must not read as "this repository has no house rules".
    /// </summary>
    [Fact]
    public async Task Reviewed_says_the_root_guidance_was_never_probed_when_the_run_has_no_leased_checkout()
    {
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(logs);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var line = logs.Capturing.MessagesAtLevel(LogLevel.Information)
            .Where(m => m.Contains($"Run {run.Id}:", StringComparison.Ordinal)
                && m.Contains("root guidance", StringComparison.Ordinal))
            .Should().ContainSingle("the un-probed run is accounted for like the probed ones, not by silence")
            .Subject;

        line.Should().Contain(
            "not probed",
            "the line has to say the lookup never happened; anything softer reads as a lookup that came "
                + "back empty, which is the opposite diagnosis");
        line.Should().NotContain(
            "absent",
            "no file was examined, so claiming one is absent asserts something this run never established");
    }

    [Fact]
    public async Task Reviewed_prepends_the_knowledge_base_toc_when_the_store_has_one()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        // The store carries prior knowledge distilled from past PRs; with no _index.jsonl to rank (a KB
        // written before the index existed) the review must still start with its table of contents (design §3).
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\n## system\n- [Null-guard boundaries](system/null-guard.md)\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        // The prompt teaches ONE canonical heading and teaches that its absence means "no Knowledge Base
        // exists, don't go looking". The fallback must therefore arrive under that same heading — a
        // separately-labelled block is one the agent has been told to ignore — and must still carry an exact
        // absolute path, since a bare "_toc.md" is not something the agent can open.
        text.Should().Contain("## Prior knowledge (Knowledge Base)", "the ToC is prepended as a labelled block");
        text.Should().Contain("Null-guard boundaries", "the seeded ToC entries are surfaced to the reviewer");
        text.Should().Contain(
            "/workspace/store/KnowledgeBase/_toc.md",
            "the fallback must hand over the ToC's exact absolute path");
    }

    [Fact]
    public async Task Reviewed_degrades_and_does_not_fail_when_the_knowledge_base_read_throws()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
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
        text.Should().NotContain("Prior knowledge", "the failed KB read is skipped, not prepended");
    }

    [Fact]
    public async Task Reviewed_leaves_the_input_unchanged_when_the_store_has_no_knowledge_base()
    {
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
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
    public async Task Reviewed_leaves_the_input_unchanged_when_the_knowledge_base_is_seeded_but_still_empty()
    {
        // The state every store is in until the first PR closes, and the one the nova daemon was actually in:
        // a _toc.md that exists and reads cleanly but lists no entries, because at-close knowledge extraction
        // runs only on PrLifecycleSweeper's merged path and nothing had merged yet. Measured over two days,
        // all 120 review briefs carried a "Prior knowledge (Knowledge Base)" block of exactly the 741-char
        // header with no link under it — Listed=0, Dropped=0, Truncated=false, no refusals — and the honest
        // "No usable Knowledge Base" branch, whose only test is an empty block, never ran.
        //
        // Asserted here and not only on the renderer because the damage is done at THIS seam: the review
        // prompt teaches that heading as the sole place prior knowledge appears and teaches its absence as
        // "there is no Knowledge Base", so a header standing alone tells the reviewer the opposite of the
        // truth and then instructs it to go joining links that do not exist.
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        fixture.FileSystem.Seed(
            "/workspace/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\nDurable lessons captured from closed pull requests.\n");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain(
            "Prior knowledge",
            "an empty Knowledge Base says the same thing as no Knowledge Base, and must say it the same way");
    }

    /// <summary>
    /// A fixture whose store carries curated knowledge AND a per-developer feedback record, with the
    /// feedback agent enabled. The record path is the one <see cref="ReviewFeedbackAgent"/> writes to.
    /// </summary>
    private static Fixture FeedbackFixture(ILoggerFactory loggerFactory, bool enableReviewFeedbackAgent = true)
    {
        var fixture = Fixture.GitHub(
            loggerFactory,
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                EnableToolAssistedReview = true,
                CrossRepoStoreUrl = "https://github.com/achieveai/AchieveAiReviews.git",
                EnableReviewFeedbackAgent = enableReviewFeedbackAgent,
            });
        fixture.FileSystem.Seed(
            "/workspace/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n");
        fixture.Runner.OnArgvContains("ls-files", new SandboxCommandResult(0, "src/LmCore/Foo.cs\n", string.Empty));
        return fixture;
    }

    /// <summary>
    /// The stem the review-feedback writer files "octocat" under. Derived, not typed: these tests are about
    /// the RETRIEVAL side finding what the writer wrote, and a literal would pin the two together only by
    /// coincidence.
    /// </summary>
    private static readonly string OctocatSlug = ReviewFeedbackAgent.SlugifyAuthor("octocat")!;

    private static void SeedFeedbackRecord(Fixture fixture, string slug, string body) =>
        fixture.FileSystem.Seed(
            $"/workspace/store/KnowledgeBase/developers/{slug}.reviewfeedbacks.md",
            $"---\ndeveloper: {slug}\nsourcePrs: [\"github/a-r/1\"]\nupdated: 2026-08-04\n---\n\n## PATTERNS\n\n{body}\n");

    private static async Task<string> RunAndReadReviewInputAsync(Fixture fixture, ReviewRun run)
    {
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        return reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
    }

    /// <summary>
    /// The point of the whole feature: the author's own recurring mistakes reach the reviewer of THEIR PR.
    /// The record is looked up under the slug the extraction wrote it under, so a display name that is not
    /// already a slug ("Jane.Doe@contoso.com") must still find its file — reader and writer deriving the path
    /// differently would silently inject nothing forever. That is why the seed goes through the writer's own
    /// <see cref="ReviewFeedbackAgent.SlugifyAuthor"/> rather than a literal: the retrieval side is the code
    /// under test, and a literal would only pin whatever the test author happened to type.
    /// </summary>
    [Theory]
    [InlineData("octocat")]
    [InlineData("Jane.Doe@contoso.com")]
    [InlineData("AchieveAI\\gautam")]
    public async Task Reviewed_prepends_the_pr_authors_own_feedback_record(string author)
    {
        var slug = ReviewFeedbackAgent.SlugifyAuthor(author)!;
        using var fixture = FeedbackFixture(LoggerFactory);
        SeedFeedbackRecord(fixture, slug, "- Leaves `ConfigureAwait(false)` off awaits in library code.");
        var run = fixture.SeedRun(prAuthor: author);

        var text = await RunAndReadReviewInputAsync(fixture, run);

        text.Should().Contain(
            $"KnowledgeBase/developers/{slug}.reviewfeedbacks.md",
            "the record is prepended as a labelled block naming the exact file it came from");
        text.Should().Contain("ConfigureAwait(false)", "the seeded patterns are surfaced to the reviewer");
        text.Should().NotContain(
            "sourcePrs:", "the daemon-owned frontmatter is bookkeeping, not something the reviewer should read");
    }

    /// <summary>
    /// No author, a bot author, or an author that slugs to nothing addresses no record — the reader must not
    /// guess a file, and must certainly not fall back to a shared one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dependabot[bot]")]
    [InlineData("???")]
    public async Task Reviewed_injects_no_feedback_when_no_developer_is_addressable(string? author)
    {
        using var fixture = FeedbackFixture(LoggerFactory);
        SeedFeedbackRecord(fixture, OctocatSlug, "- Leaves `ConfigureAwait(false)` off awaits in library code.");
        var run = fixture.SeedRun(prAuthor: author);

        var text = await RunAndReadReviewInputAsync(fixture, run);

        text.Should().NotContain("Recurring feedback", "an unaddressable author injects nothing");
        text.Should().NotContain("ConfigureAwait(false)", "no other developer's record may be substituted");
    }

    [Fact]
    public async Task Reviewed_injects_no_feedback_for_a_first_time_author()
    {
        using var fixture = FeedbackFixture(LoggerFactory);
        // No record seeded — the normal case the first time someone opens a PR.
        var run = fixture.SeedRun(prAuthor: "octocat");

        var text = await RunAndReadReviewInputAsync(fixture, run);

        text.Should().NotContain("Recurring feedback", "a missing record leaves the review input unchanged");
    }

    [Fact]
    public async Task Reviewed_injects_no_feedback_when_the_feature_is_disabled()
    {
        using var fixture = FeedbackFixture(LoggerFactory, enableReviewFeedbackAgent: false);
        SeedFeedbackRecord(fixture, OctocatSlug, "- Leaves `ConfigureAwait(false)` off awaits in library code.");
        var run = fixture.SeedRun(prAuthor: "octocat");

        var text = await RunAndReadReviewInputAsync(fixture, run);

        text.Should().NotContain(
            "Recurring feedback",
            "a store that carries records from another daemon must not leak them into a review here");
    }

    /// <summary>Design §6: reading the record must never fail the review.</summary>
    [Fact]
    public async Task Reviewed_degrades_and_does_not_fail_when_the_feedback_record_read_throws()
    {
        using var fixture = FeedbackFixture(LoggerFactory);
        SeedFeedbackRecord(fixture, OctocatSlug, "- Leaves `ConfigureAwait(false)` off awaits in library code.");
        var run = fixture.SeedRun(prAuthor: "octocat");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.FileSystem.ReadFault = path =>
            path.Contains("reviewfeedbacks.md", StringComparison.Ordinal)
                ? new InvalidOperationException("Session not found: deadbeef")
                : null;

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().NotThrowAsync("a feedback read failure must degrade, not kill the review (design §6)");
        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain("Recurring feedback", "the failed read is skipped, not prepended");
    }

    /// <summary>
    /// The record accumulates over every PR its author opens, so an unbounded one would crowd the diff out of
    /// the reviewer's context. Truncation is marked so the model knows it is reading a partial record.
    /// </summary>
    [Fact]
    public async Task Reviewed_truncates_an_oversized_feedback_record_and_marks_it()
    {
        using var fixture = FeedbackFixture(LoggerFactory);
        SeedFeedbackRecord(fixture, OctocatSlug, new string('x', 40_000) + "\n- TAIL PATTERN");
        var run = fixture.SeedRun(prAuthor: "octocat");

        var text = await RunAndReadReviewInputAsync(fixture, run);

        text.Should().Contain("Recurring feedback", "an oversized record is still injected, just bounded");
        text.Should().Contain("[record truncated]", "the model must know the record it read is partial");
        text.Should().NotContain("TAIL PATTERN", "content past the cap is dropped, not smuggled in");
    }

    /// <summary>
    /// A sub-agent sees only what the parent copies into its brief, so a block the parent never forwards is
    /// one the dimension reviewers never get — they would review this author's PR blind to exactly the
    /// mistakes the record exists to catch. The ranked prior-knowledge digest makes this promise; the two
    /// blocks carry the same kind of payload into the same prompt, and a guarantee that holds on only one of
    /// them is the recurring defect on this path.
    /// </summary>
    [Fact]
    public async Task Reviewed_tells_the_reviewer_to_hand_the_feedback_record_to_its_sub_agents()
    {
        using var fixture = FeedbackFixture(LoggerFactory);
        SeedFeedbackRecord(fixture, OctocatSlug, "- Leaves `ConfigureAwait(false)` off awaits in library code.");
        var run = fixture.SeedRun(prAuthor: "octocat");

        var text = await RunAndReadReviewInputAsync(fixture, run);

        var block = text[text.IndexOf("## Recurring feedback", StringComparison.Ordinal)..];
        block.Should().Contain(
            "sub-agent", "the parent must be told to forward this, exactly as the prior-knowledge digest is");
        block.Should().Contain(
            "copy that path into its brief",
            "forwarding the PATH is what a sub-agent can act on; it has no other route to the record");
    }

    /// <summary>
    /// The path the block names must be the one the AGENT's tools resolve. A store-relative path — or, in
    /// pooled mode, the daemon's own host path — is one the agent can never open, and a Grep for it can come
    /// back empty even though the file is right there, so the reviewer would conclude the record is missing.
    /// </summary>
    [Fact]
    public async Task Reviewed_names_the_feedback_record_by_the_path_the_agent_can_open()
    {
        using var fixture = FeedbackFixture(LoggerFactory);
        SeedFeedbackRecord(fixture, OctocatSlug, "- Leaves `ConfigureAwait(false)` off awaits in library code.");
        var run = fixture.SeedRun(prAuthor: "octocat");

        var text = await RunAndReadReviewInputAsync(fixture, run);

        var block = text[text.IndexOf("## Recurring feedback", StringComparison.Ordinal)..];
        block.Should().Contain(
            $"/workspace/store/KnowledgeBase/developers/{OctocatSlug}.reviewfeedbacks.md",
            "the heading names the record's exact ABSOLUTE path as the agent sees it, not a relative one");
        block.Should().Contain(
            "do NOT ", "the agent is steered off Grep/Glob, which can miss the file even when it exists");
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
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

    // DELETED (#102): Reviewed_restarts_an_interrupted_in_process_review_collect_only_and_never_promotes_the_provisional.
    //
    // Its whole premise was that the interrupted turn was NOT resumable — "an in-process loop is not
    // resumable, so none is wrapped". That is true only of the in-process path, which Program.cs:278 has
    // refused to boot since 2026-08-01. It was the single test in this file that the fixture default flip
    // reddened, out of 134 cases, and its NAME is the finding.
    //
    // Measured rather than argued: under UseS2SReviewAgent: true it fails with
    //   Expected fixture.Factory.ResumeHostedThreadIds to be <null>, but found "thread-that-died".
    // On S2S the hosted conversation IS rejoined — the exact opposite of what this asserted. There is no
    // version of it that both boots and passes.
    //
    // Nothing is owed. Its one property worth keeping — that a provisional is never promoted to the
    // authoritative review — is pinned more strongly by
    // Reviewed_synthesizes_over_the_provisional_and_posts_only_the_synthesis (search ReviewTextOf +
    // ProvisionalReviewArtifactKind above), which asserts not merely that the review text differs from the
    // provisional but that the provisional "must never reach the PR". That test asserts against the posted
    // body, so it covers the consequence rather than the intermediate.
    //
    // The live counterpart of the restart behaviour is
    // Reviewed_discards_a_checkpoint_whose_budget_already_expired_and_starts_a_fresh_lifecycle, which starts
    // over for a reason that still exists on S2S.

    [Theory]
    [InlineData("modality")]
    [InlineData("model")]
    [InlineData("rung")]
    [InlineData("tool-mode")]
    [InlineData("context-generation")]
    public async Task Reviewed_discards_a_checkpoint_whose_lifecycle_identity_no_longer_matches(string mismatch)
    {
        // A hosted conversation is bound to a model and a context this process may no longer be holding.
        // Resuming a mismatched one is unbounded: the synthesis reviews whatever the conversation is
        // actually attached to and posts those findings to THIS PR, with nothing anywhere reporting an error.
        // Starting over costs exactly one duplicate review. Each case below isolates ONE discriminator; the
        // all-fields-match control is Reviewed_resumes_the_persisted_hosted_conversation_… above.
        //
        // There used to be a "workspace" case here, and it went when WorkspaceId left the identity — the
        // coverage was not dropped quietly, the field was. Its concern (a re-leased slot whose checkout is a
        // different PR) is answered by ReviewSlotPreparer throwing on a checkout that is not at the run's
        // head, which is where it can protect a FRESH review as well as a resumed one. The cross-slot resume
        // this unblocked is pinned by
        // DaemonReviewStageExecutorPooledTests.Reviewed_resumes_its_checkpoint_when_the_restart_re_leases_a_different_slot.
        //
        // This theory is also the mutation target for the identity itself: delete any field from
        // BuildLifecycleIdentity and the matching case here must go red. If one does not, the identity has a
        // field nothing tests.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var current = LifecycleOf(fixture, run);
        var persisted = mismatch switch
        {
            // Configuration flipped to hosted: an in-process checkpoint's thread id is a daemon-local
            // review-run-* string that no host would recognise.
            //
            // Spelled out rather than referencing a constant, because the production constant is gone (#84).
            // "in-process" is not an arbitrary placeholder: it is the value BuildLifecycleIdentity actually
            // wrote between 2026-07-30 (when the lifecycle identity landed) and 2026-08-01 (when Program.cs
            // began throwing on UseS2SReviewAgent: false), so it is the realistic persisted mismatch rather
            // than an invented one. The discriminator is a record comparison and never special-cases the
            // modality, so ANY non-matching string exercises the same branch — this one documents which
            // string a real checkpoint could carry.
            "modality" => current with { Modality = "in-process" },
            "model" => current with { ModelId = "gpt-5.6-terra" },
            // An escalation rung runs on its own thread with the overflowing history shed; resuming it as the
            // base attempt would review under a model and window this attempt never chose.
            "rung" => current with
            {
                LocalThreadId = DaemonReviewStageExecutor.ThreadId(run, run.VariantId + "-esc"),
            },
            "tool-mode" => current with { ToolAssisted = true },
            // The rebuilt context is about a different PR/base/head/diff: the checkpointed conversation
            // reviewed a subject this run is no longer about.
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
        // The documented rollback path: a run is reset to ContextReady and its context REBUILT — and the
        // rebuild comes back different, because that is what a rollback is for. The checkpoint is keyed to the
        // context generation it was built from (the 'review-context' row id), so a rebuild that changes the
        // context invalidates it without needing a tombstone from whoever did the rollback.
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));

        // First-match-wins, so this overrides the fixture's standing diff rule for every call from here on.
        _ = fixture.Runner.OnArgvContainsFirst(
            "diff", new SandboxCommandResult(0, "diff --git a/Bar.cs b/Bar.cs\n+ rebuilt", string.Empty));
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().BeNull(
            "the conversation reviewed a diff this run is no longer about");
        fixture.Factory.ResumableLoops.Should().ContainSingle().Which.MintedThreadIds.Should().ContainSingle();
    }

    /// <summary>
    /// The same rollback path when the rebuild changes NOTHING. Re-entering ContextReady recomputes the
    /// context, but an identical context is not a new generation: no row is appended, so the checkpoint still
    /// matches and the hosted conversation — with its whole sub-agent fan-out already paid for — is rejoined
    /// instead of thrown away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test does not establish that the resume re-lease is safe in production, and its doc used to say
    /// it did.</b> The claim was false for as long as it stood: resume has fired 0 times in 4 attempts, and
    /// every discard was a context rebuild that produced a payload differing in ONE field this fixture cannot
    /// vary — <c>SiblingRepos</c>, which appears nowhere in this file. This fixture is not store-backed, so its
    /// sibling list is empty on every call and the two payloads come back byte-identical. The test passed
    /// because it could not observe the field that breaks it.
    /// </para>
    /// <para>
    /// What the test does establish is narrower and still worth having: that a byte-identical rebuild does not
    /// append a row, and that a checkpoint survives one. The production guarantee rests on the lifecycle
    /// identity being keyed to what the review is ABOUT rather than to which row last recorded it — see
    /// <c>DaemonReviewStageExecutorPooledTests.Reviewed_resumes_when_another_review_populates_a_sibling_between_the_two_context_passes</c>,
    /// which is store-backed and does vary the sibling set.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Reviewed_resumes_the_checkpoint_when_the_context_stage_re_ran_and_produced_the_same_context()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, S2SResumeOptions());
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        SeedProvisionalCheckpoint(fixture, run, "thread-persisted", DateTimeOffset.UtcNow.AddMinutes(20));

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.ResumeHostedThreadIds.Should().ContainSingle().Which.Should().Be(
            "thread-persisted", "the checkpointed conversation reviewed exactly the diff this run is about");
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
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableABVariants = true });
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
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableJudgeAgent = true });
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
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewBotRepoUrl = "https://github.com/achieveai/CodeReviewBot-Workspace.git" });
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true },
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
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
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
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
        // A DELIVERED earlier round, because the sentinel is only a sentence this run is entitled to say when
        // one exists — the Reviewed stage refuses it otherwise. Delivered, so nothing is carried forward and
        // the body that reaches the host fallback is the sentinel alone, which is this test's subject.
        _ = fixture.SeedPriorRound("[BLOCKER] round 01 found this", delivered: true);
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
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true, BotName = "GB's Revobot" });
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
            ? Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true })
            : Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
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
            ? Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = false, EnableHostSummaryFallback = true })
            : Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = false, EnableHostSummaryFallback = true });
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
            ? Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true })
            : Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
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

    /// <summary>
    /// NOVA PR 5503135's shape: round 01 raised two BLOCKERs and was never delivered (collect-only, so its
    /// outbox row stayed Collected); round 02 answered a reviewer's question and reported "No new findings
    /// since the last review." The comment a reader would see therefore contained NONE of the findings the
    /// bot had — the recommendations existed only in the review store. A round is entitled to say nothing is
    /// new; it is not entitled to let undelivered findings fall off the record.
    /// </summary>
    [Fact]
    public async Task Posted_carries_an_earlier_round_that_was_never_delivered_into_this_comment()
    {
        const string PriorFinding = "[BLOCKER] HIGH — view-backed tables can throw on a null Source";
        static CodeReviewDaemonOptions S2SOptions() =>
            new()
            {
                UseS2SReviewAgent = true,
                LmStreamingBaseUrl = "http://localhost:5051",
                EnableCommentPosting = true,
            };
        using var fixture = Fixture.GitHub(LoggerFactory, S2SOptions());
        var prior = fixture.SeedPriorRound(PriorFinding, delivered: false);
        var run = fixture.SeedRun(watermark: "wm-2");

        await RunAllStagesAsync(fixture, run);

        var body = fixture.GitHubPublisher.PostedBodies.Should().ContainSingle().Subject;
        body.Should().Contain(
            PriorFinding,
            "an undelivered round's findings are part of what this PR still has outstanding, and this comment "
                + "is the only place a reader will look for them");
        body.Should().Contain(
            $"run {prior.Id}",
            "the carried text is attributed to the round that wrote it, not presented as this round's work");
        body.Should().Contain("old-head-sha", "and to the commit it reviewed, which is not this one");
    }

    [Fact]
    public async Task Posted_leaves_the_comment_alone_when_the_earlier_round_already_reached_the_pr()
    {
        // The other side, and the reason the carry-forward keys on the outbox rather than on a config flag:
        // when round 01 genuinely posted, repeating it turns every later round into a growing wall of text
        // the PR already carries. "Nothing new" is then exactly the right comment.
        const string PriorFinding = "[BLOCKER] HIGH — view-backed tables can throw on a null Source";
        static CodeReviewDaemonOptions S2SOptions() =>
            new()
            {
                UseS2SReviewAgent = true,
                LmStreamingBaseUrl = "http://localhost:5051",
                EnableCommentPosting = true,
            };
        using var fixture = Fixture.GitHub(LoggerFactory, S2SOptions());
        _ = fixture.SeedPriorRound(PriorFinding, delivered: true);
        var run = fixture.SeedRun(watermark: "wm-2");

        await RunAllStagesAsync(fixture, run);

        var body = fixture.GitHubPublisher.PostedBodies.Should().ContainSingle().Subject;
        body.Should().NotContain(PriorFinding, "the PR already carries that round's comment");
        body.Should().NotContain("never delivered");
    }

    // ── The sentinel is a WHOLE-BODY decision, and it decides only about THIS round ──────────────────
    // The prompt mandates one exact sentence for the no-op exit, so the classifier's question is "did the
    // model emit that sentence and nothing else?" A prefix test asks a strictly wider question, and every
    // body it wrongly answers yes to is discarded in silence: no comment, no retained pr_comment.md, and no
    // carry-forward of earlier rounds. The tests below pin both halves — what counts as the sentinel, and
    // what the sentinel is still not allowed to suppress.

    [Theory]
    [InlineData("No new findings since the last review.")]
    [InlineData("no new findings since the last review.")]
    [InlineData("  No new findings since the last review.\n\n")]
    [InlineData("No new findings since the last review")]
    [InlineData("No new findings\nsince the last review.")]
    [InlineData("No new findings.")]
    [InlineData("No new findings — nothing to post.")]
    public void A_body_that_is_only_the_sentinel_is_the_sentinel(string reviewText) =>
        DaemonReviewStageExecutor.IsNoNewFindingsSentinel(reviewText).Should().BeTrue(
            "the prompt mandates this sentence for the no-op exit, and a round that writes it and nothing "
                + "else has deliberately decided there is nothing to put on the PR");

    /// <summary>
    /// The failure this whole change exists for. Each of these opens with the mandated phrase and then keeps
    /// going — and what follows is the review. Classified as a no-post, none of it is written anywhere a
    /// reader will look: the run finishes reporting success with its findings left in the review store.
    /// </summary>
    [Theory]
    [InlineData("No new findings in the auth module, but three BLOCKERs elsewhere:\n\n- [BLOCKER] token TTL")]
    [InlineData("No new findings since the last review.\n\nHowever, one thing I did not raise last round:")]
    [InlineData("No new findings on the diff itself; the migration still has no backfill.")]
    [InlineData("No new findings — see below for the two I raised last round that are still open.")]
    public void A_body_that_merely_opens_with_the_sentinel_is_not_the_sentinel(string reviewText) =>
        DaemonReviewStageExecutor.IsNoNewFindingsSentinel(reviewText).Should().BeFalse(
            "everything after the opening phrase is the review, and classifying this as a no-post discards it");

    [Fact]
    public async Task Posted_delivers_a_review_that_merely_opens_with_the_sentinel_phrase()
    {
        // The end-to-end shape of the same defect: the reviewer qualified its "no new findings" and then
        // raised a BLOCKER. The qualification is not the verdict.
        const string Body =
            "No new findings in the files I flagged last round.\n\n"
            + "The new migration is a different matter:\n\n"
            + "- [BLOCKER] `AddTenantId` adds a NOT NULL column with no default against a populated table.";
        using var fixture = Fixture.Ado(
            LoggerFactory,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] = Body;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await RunAllStagesAsync(fixture, run);

        fixture.AdoPublisher!.PostedBodies.Should().ContainSingle()
            .Which.Should().Contain(
                "[BLOCKER]",
                "the body carries a blocker, so it is a review — the words it happens to open with do not "
                    + "make it a decision to post nothing");
    }

    /// <summary>
    /// NOVA PR 5503135's actual round 02, which the existing carry-forward test describes but does not
    /// reproduce: round 01 raised two BLOCKERs and was never delivered, and round 02 answered "No new
    /// findings since the last review." Round 02 is entitled to that verdict — but the sentence is only true
    /// for a reader if round 01's findings are already on the PR, and they are not. The sentinel decides
    /// whether THIS round has anything to add; it does not decide whether earlier rounds stay withheld.
    /// </summary>
    [Fact]
    public async Task Posted_carries_an_undelivered_earlier_round_even_when_this_round_says_nothing_is_new()
    {
        const string PriorFinding = "[BLOCKER] HIGH — view-backed tables can throw on a null Source";
        static CodeReviewDaemonOptions S2SOptions() =>
            new()
            {
                UseS2SReviewAgent = true,
                LmStreamingBaseUrl = "http://localhost:5051",
                EnableCommentPosting = true,
            };
        using var fixture = Fixture.GitHub(LoggerFactory, S2SOptions());
        var prior = fixture.SeedPriorRound(PriorFinding, delivered: false);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] =
            "No new findings since the last review.";
        var run = fixture.SeedRun(watermark: "wm-2");

        await RunAllStagesAsync(fixture, run);

        var body = fixture.GitHubPublisher.PostedBodies.Should().ContainSingle(
                "a round with nothing of its own to say still owes the PR what was withheld from it")
            .Subject;
        body.Should().Contain(PriorFinding);
        body.Should().Contain($"run {prior.Id}", "the carried text is attributed to the round that wrote it");
    }

    [Fact]
    public async Task Posted_still_posts_nothing_when_a_no_new_findings_round_has_nothing_withheld()
    {
        // The dual, and the guard against over-correcting: with every earlier round already on the PR, the
        // sentinel round genuinely has nothing to deliver, and forcing a comment there is the re-review
        // noise the sentinel exists to stop.
        static CodeReviewDaemonOptions S2SOptions() =>
            new()
            {
                UseS2SReviewAgent = true,
                LmStreamingBaseUrl = "http://localhost:5051",
                EnableCommentPosting = true,
            };
        using var fixture = Fixture.GitHub(LoggerFactory, S2SOptions());
        _ = fixture.SeedPriorRound("[BLOCKER] HIGH — null Source", delivered: true);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] =
            "No new findings since the last review.";
        var run = fixture.SeedRun(watermark: "wm-2");

        await RunAllStagesAsync(fixture, run);

        fixture.GitHubPublisher.PostCount.Should().Be(0, "there is nothing new and nothing withheld");
        fixture.Store.GetOutboxForRun(run.Id).Should().NotContain(
            o => o.Operation == ReviewPoster.PostReviewCommentOperation,
            "no delivery was attempted, so there is no comment outbox row to misread as evidence");
    }

    // ── #113: daemon-infrastructure narration is filtered at the one place the posted comment is ────────
    // composed. This is the end-to-end proof that InfraNarrationFilterTests exercises in isolation: real
    // sandbox/tooling narration (run 41) is rewritten generically on the posted body, real posting-state
    // narration (run 17) is held back entirely and reaches only the operator-facing log line, and the
    // author's own finding is untouched by either disposition.

    /// <summary>
    /// Live runs 41 and 17, combined into one review, posted end to end. The reader-facing assertions pin
    /// what the AUTHOR sees: the blocker survives, the internal "sandbox does not have dotnet" detail is
    /// gone, and the posting-state sentence is gone with nothing substituted for it (it carries zero value
    /// to the author). The operator-facing assertion pins the other half of the contract: the withheld
    /// posting-state text is not simply discarded — it is logged, with the run id and sub-tag a human
    /// grepping the operator log needs to find it.
    /// </summary>
    [Fact]
    public async Task Posted_rewrites_sandbox_tooling_narration_and_moves_posting_state_narration_to_the_operator_log()
    {
        const string Body =
            "No new findings in the files I flagged last round.\n\n"
            + "The new migration is a different matter:\n\n"
            + "- [BLOCKER] `AddTenantId` adds a NOT NULL column with no default against a populated table.\n\n"
            + "### Verification\n\n"
            + "Focused tests could not be run because the sandbox does not have `dotnet` installed.\n\n"
            + "### Posting status\n\n"
            + "No comments were posted.";
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.Ado(
            logs,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableCommentPosting = true, EnableHostSummaryFallback = true });
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] = Body;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await RunAllStagesAsync(fixture, run);

        var posted = fixture.AdoPublisher!.PostedBodies.Should().ContainSingle().Subject;
        posted.Should().Contain(
            "[BLOCKER]", "the author's own finding is never touched by this filter, whatever else it removes");
        posted.Should().NotContain("dotnet", "the internal sandbox/tooling cause is stripped from what the author sees");
        posted.Should().NotContain(
            "No comments were posted", "posting-state narration carries zero value to the author and is not substituted");
        posted.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the code "
                + "are reflected in this assessment.",
            "sandbox/tooling narration is REWRITTEN, never deleted outright — the author still learns tests "
                + "could not be run, just not why in daemon-internal terms");

        var line = logs.Capturing.MessagesAtLevel(LogLevel.Information)
            .Should().ContainSingle(
                m => m.Contains("posting_state", StringComparison.Ordinal),
                "the withheld text must actually reach the operator channel, not just vanish from the posted body")
            .Subject;
        line.Should().Contain($"Run {run.Id}:", "an operator grepping this log needs the run id, not just the text");
        line.Should().Contain(
            "No comments were posted.", "the operator channel receives the exact text the PR author never saw");
    }

    /// <summary>
    /// The reconciliation the daemon has never done: what the fan-out produced against what the review body
    /// carries. Live run nova-5500188 settled a full roster of specialists and wrote a 38-byte review.md —
    /// the sentinel, exactly. Nothing logged the discrepancy, so a review that threw away every specialist's
    /// work is indistinguishable in the record from one that had nothing to throw away.
    /// <para>
    /// That run is also refused outright now: it had no prior review for its findings to be new since. The
    /// two belong in one test because the ORDER between them is a property. The reconciliation line has to be
    /// in the log by the time the refusal lands, or the failure an operator sees says only "the sentinel was
    /// not authorized" and omits the part that says four specialists reported back and were discarded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reviewed_reconciles_a_settled_fan_out_against_a_review_body_that_reports_nothing()
    {
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: new SettledCompletionSource(4));
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] =
            "No new findings since the last review.";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>(
            "nothing precedes this run on the PR, so the sentinel is not a claim it can make");

        var line = logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Where(m => m.Contains($"Run {run.Id}:", StringComparison.Ordinal)
                && m.Contains("sub-agent(s) completed", StringComparison.Ordinal))
            .Should().ContainSingle("the discrepancy is reported once, as its own line, and before the refusal")
            .Subject;

        line.Should().Contain(
            "4 of 4 sub-agent(s) completed", "how many specialists reported back is half the reconciliation");
        line.Should().Contain(
            "38 chars", "and the size of the body that reached a reader is the other half");
    }

    [Fact]
    public async Task Reviewed_does_not_reconcile_a_fan_out_against_a_body_that_carries_findings()
    {
        // The threshold is the sentinel, not "short": a settled roster and a real verdict is the ordinary
        // outcome, and an alarm that fires on it is tuned out within a week.
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(
            logs,
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, ReviewSubAgentBarrierQuietSeconds = 1 },
            completionSource: new SettledCompletionSource(4));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.CountAtLevel(LogLevel.Warning, "sub-agent(s) completed").Should().Be(
            0, "specialists reporting into a review that then states findings is the healthy path");
    }

    /// <summary>
    /// What a whole-body match costs, made measurable. A reviewer that meant to take the no-op exit but
    /// phrased it its own way is now treated as findings and posts a comment saying nothing — the safe
    /// direction to be wrong in, but not a free one. Nothing distinguishes that from a genuine review that
    /// opens with the same words, so the daemon reports the case rather than judging it, and the sentinel
    /// set gets widened on what this line shows rather than on a guess about model phrasing.
    /// </summary>
    [Fact]
    public async Task Reviewed_reports_a_body_that_opens_with_the_exit_phrase_without_being_it()
    {
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(logs);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] =
            "No new findings worth raising this round, I think.";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.MessagesAtLevel(LogLevel.Information)
            .Should().ContainSingle(
                m => m.Contains($"Run {run.Id}:", StringComparison.Ordinal)
                    && m.Contains("is not only that sentence", StringComparison.Ordinal),
                "the near miss is the tuning signal for the sentinel set, and it only exists if it is logged");
    }

    [Fact]
    public async Task Reviewed_says_nothing_about_a_body_that_never_goes_near_the_exit_phrase()
    {
        using var logs = new CapturingLoggerFactory();
        using var fixture = Fixture.GitHub(logs);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        logs.Capturing.CountAtLevel(LogLevel.Information, "is not only that sentence").Should().Be(
            0, "an ordinary review is not a near miss, and a line that fires on every run measures nothing");
    }

    [Fact]
    public void The_store_and_the_poster_agree_on_which_outbox_operation_delivers_a_review()
    {
        // GetUndeliveredPriorReviews decides "was this round delivered" by matching an outbox operation
        // string it holds privately, so the persistence layer need not depend on orchestration. If the two
        // ever drift, the query silently matches nothing, every prior round looks undelivered, and each
        // comment grows a duplicate copy of every earlier one — a failure that reads as a formatting bug.
        typeof(ReviewStore)
            .GetField(
                "PostReviewCommentOperation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            .Should().Be(ReviewPoster.PostReviewCommentOperation);
    }

    /// <summary>
    /// NOVA runs 32 and 62: the ContextReady stage captured a diff of ZERO characters and zero changed paths,
    /// and the Reviewed stage handed that to the reviewer anyway. Both failure modes it produces are worse
    /// than an error. Run 62 answered "No new findings since the last review." — a review of nothing,
    /// recorded as nothing-new. Run 32 went looking: it diffed a local commit against its parent
    /// (b531b302 vs 0d11c184, NOT the PR's base...head) and reviewed that, opening "Review basis: Local
    /// commit …" — a confident review of a range nobody asked about, with nothing marking it as such.
    /// An empty capture is a fact about the DAEMON's capture, and only the daemon can report it honestly.
    /// </summary>
    [Fact]
    public async Task Reviewed_reports_no_reviewable_changes_instead_of_reviewing_an_empty_capture()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        // The shape runs 32/62 recorded: `git diff base...head` came back empty, so no path was named either.
        fixture.Runner.OnArgvContainsFirst("diff", new SandboxCommandResult(0, string.Empty, string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Factory.CreatedProfiles.Should().BeEmpty(
            "with nothing captured there is nothing for a reviewer to read — and an agent given no diff goes "
                + "and finds its own range to review");

        var review = PayloadOf<ReviewArtifactPayload>(
            fixture, run, DaemonReviewStageExecutor.ReviewArtifactKind);
        review.ReviewText.Should().MatchRegex(
            "(?i)no reviewable",
            "the run still owes the PR a verdict — silence is indistinguishable from never having run");
        review.ReviewText.Should().NotContain(
            "No new findings since the last review.",
            "that sentence claims a comparison against an earlier round; no comparison happened");
        review.ReviewText.Should().MatchRegex(
            "(?i)(base-sha|head-sha)",
            "naming the range that came back empty is what lets a reader tell a binary-only PR from a fetch "
                + "that never had the commits");
    }

    // ── Delivery truthfulness: a post-mode run may not COMPLETE without provider-visible evidence ────    // Run 27's shape: the Posted stage failed once on retention, the retry re-ran the stage, produced no
    // provider comment at all, and the run still went Completed. The stage must instead stay retryable
    // whenever the review was supposed to reach the PR and demonstrably did not — while the deliberate
    // "no new findings" no-post stays a success (forcing a comment there is the re-review noise bug).

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_stays_retryable_when_an_authorized_post_replays_a_row_that_proves_no_comment(string provider)
    {
        // The run was DISCOVERED in post mode and posting IS authorized, so this review is supposed to land on
        // the PR. Its outbox row already claims Posted but carries no provider response id — nothing about it
        // proves a comment exists, and the poster reads it as a terminal replay and touches no provider. That
        // ambiguity is exactly what let run 27 complete undelivered, so the stage must stay retryable.
        var options = new CodeReviewDaemonOptions
        {
            UseS2SReviewAgent = true,
            EnableCommentPosting = true,
            EnableHostSummaryFallback = true,
        };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, options)
            : Fixture.GitHub(LoggerFactory, options);
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");
        SeedEvidenceFreePostedOutbox(fixture, run, provider);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*post*", "the failure has to name the undelivered post so the operator can act");
        publisher.PostCount.Should().Be(0, "the poster treated the seeded row as a terminal replay");
        // One row, the seeded one — a second row would mean the key drifted and this test proved nothing.
        fixture.Store.GetOutboxForRun(run.Id)
            .Should().ContainSingle(o => o.Operation == ReviewPoster.PostReviewCommentOperation);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public async Task Posted_completes_a_post_mode_run_as_collect_only_when_posting_is_switched_off(string provider)
    {
        // `run.Mode` is frozen at discovery, so a run discovered while posting was enabled stays "post" even
        // after an operator turns posting off. Every attempt is then authorized only to COLLECT and can never
        // produce delivery evidence — and Posted is not a governed stage, so failing here would spin the run in
        // an unbounded retry hot-loop over a config change it cannot influence. Collecting is the truthful
        // outcome for an unauthorized attempt, so the stage completes and records exactly that.
        var options = new CodeReviewDaemonOptions
        {
            UseS2SReviewAgent = true,
            EnableCommentPosting = false,
            EnableHostSummaryFallback = true,
        };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, options)
            : Fixture.GitHub(LoggerFactory, options);
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await RunAllStagesAsync(fixture, run);

        publisher.PostCount.Should().Be(0, "no live posting was authorized");
        var delivery = fixture.Store.GetOutboxForRun(run.Id)
            .Should().ContainSingle(o => o.Operation == ReviewPoster.PostReviewCommentOperation).Subject;
        delivery.Status.Should().Be(
            OutboxStatus.Collected,
            "the run is recorded as having deliberately collected rather than posted");

        // No hot-loop: re-running the terminal stage is still not a failure.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
        publisher.PostCount.Should().Be(0);
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
            UseS2SReviewAgent = true,
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
            UseS2SReviewAgent = true,
            EnableCommentPosting = true,
            EnableHostSummaryFallback = true,
        };
        using var fixture = provider == "azure-devops"
            ? Fixture.Ado(LoggerFactory, options)
            : Fixture.GitHub(LoggerFactory, options);
        var publisher = provider == "azure-devops" ? fixture.AdoPublisher! : fixture.GitHubPublisher;
        // See the host-fallback case above: the sentinel is a claim about an earlier round, so this run needs
        // one to be entitled to it. Delivered, so this round genuinely owes the PR nothing.
        _ = fixture.SeedPriorRound("[BLOCKER] round 01 found this", delivered: true);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] = "No new findings since the last review.";
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z", mode: "post");

        await RunAllStagesAsync(fixture, run);

        publisher.PostCount.Should().Be(0, "the sentinel is an intentional no-post, not a failed delivery");
        fixture.Store.GetOutboxForRun(run.Id)
            .Should().NotContain(
                o => o.Operation == ReviewPoster.PostReviewCommentOperation,
                "no delivery was attempted, so there is no comment outbox row to misread as evidence");
    }

    /// <summary>
    /// Seeds the review-comment outbox row the executor would find on replay, already terminal
    /// <see cref="OutboxStatus.Posted"/> but WITHOUT a provider response id — a row that claims closure and
    /// proves nothing. The key must match what <c>PostReviewCommentHostSideAsync</c> builds for this run, or
    /// the poster enqueues a second row and posts normally (which the caller asserts against).
    /// </summary>
    private static void SeedEvidenceFreePostedOutbox(Fixture fixture, ReviewRun run, string provider)
    {
        // The executor keys off the PUBLISHER provider ("ado"), not the repo provider ("azure-devops").
        var postProvider = provider == "azure-devops" ? "ado" : provider;
        var key = IdempotencyKey.Build(new IdempotencyKeyComponents(
            Provider: postProvider,
            OrgOrOwner: "achieveai",
            Project: provider == "azure-devops" ? "Platform" : null,
            RepoStableId: "repo-stable-1",
            PrId: run.PrId,
            Operation: ReviewPoster.PostReviewCommentOperation,
            ArtifactKind: DaemonReviewStageExecutor.ReviewArtifactKind,
            ArtifactSubject: "summary",
            HeadSha: run.HeadSha,
            VariantId: run.VariantId));
        _ = fixture.Store.EnqueueOutbox(new OutboxEntry
        {
            IdempotencyKey = key,
            Provider = postProvider,
            ReviewRunId = run.Id,
            Operation = ReviewPoster.PostReviewCommentOperation,
            ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
            Status = OutboxStatus.Posted,
        });
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
    /// <remarks>
    /// The context generation is obtained by CALLING the production method rather than re-deriving it, and the
    /// distinction is deliberate. These tests assert a RELATIONSHIP — "a matching checkpoint resumes" — that
    /// merely happens to be keyed by a value; re-deriving the digest here would copy any bug in it faithfully
    /// into all thirteen of them and they would all still pass. The digest's own properties are pinned in ONE
    /// place instead, by <c>StableSubjectHash</c>'s dedicated tests, which construct payloads directly.
    /// (Contrast the family-pattern table in <c>ModelConfigGeneratorServiceTests</c>, where an independent
    /// transcription IS the asset because the test's purpose is to pin the value itself.)
    /// <para>
    /// A direct call rather than reflection, for the same reason: reflection binds by string name, so a rename
    /// leaves the build green and fails at runtime. This binds at compile time and breaks loudly.
    /// </para>
    /// </remarks>
    private static ReviewLifecycleIdentity LifecycleOf(
        Fixture fixture,
        ReviewRun run,
        string modality = DaemonReviewStageExecutor.S2SModality,
        string? localThreadId = null,
        string? modelId = null,
        bool toolAssisted = false,
        long? contextGeneration = null) =>
        new(
            modality,
            localThreadId ?? DaemonReviewStageExecutor.ThreadId(run, run.VariantId),
            modelId ?? run.ModelId,
            toolAssisted,
            contextGeneration ?? fixture.Executor.ContextSubjectGeneration(run));

    /// <summary>
    ///     Every SUBJECT field must move the digest. This is the discard direction — the one that guards the
    ///     failure the fix creates rather than the one it removes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Synthetic, and deliberately so: production has no example of any of these.</b> Across 175
    ///         runs, <c>PrId</c>, <c>BaseSha</c> and <c>HeadSha</c> have never varied between two context rows
    ///         of the same run — a new head sha starts a new run — and <c>Diff</c> never varied either in 76
    ///         consecutive pairs. These cases are constructed. A reader who assumes they mirror observed
    ///         behaviour will badly overestimate how often this path runs.
    ///     </para>
    ///     <para>
    ///         Why they matter anyway: today's identity is too INCLUSIVE, so it discards checkpoints it should
    ///         keep — wasteful, loud, and safe, because every review is recomputed against correct inputs. A
    ///         digest that excluded too much would fail the other way, resuming onto a conversation whose
    ///         reasoning was formed against a diff the PR no longer has, and reading as an ordinary review.
    ///         The fix converts an expensive-and-safe failure into a cheap-and-wrong one, and this is the
    ///         guard on that conversion.
    ///     </para>
    ///     <para>
    ///         These pin that the digest DISCRIMINATES. They do not pin that the stage consults it — that is
    ///         <c>DaemonReviewStageExecutorPooledTests.Reviewed_discards_its_checkpoint_when_the_rebuilt_context_carries_a_different_diff</c>,
    ///         which drives a differing subject through the real restart-and-re-lease path. Neither half is
    ///         sufficient alone: delete that stage test as "redundant with these" and the wiring becomes
    ///         unpinned with nothing going red.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("PrId")]
    [InlineData("BaseSha")]
    [InlineData("HeadSha")]
    [InlineData("Diff")]
    public void The_subject_digest_changes_when_any_field_the_review_is_about_changes(string field)
    {
        var baseline = SubjectPayload();
        var altered = field switch
        {
            "PrId" => baseline with { PrId = "999" },
            "BaseSha" => baseline with { BaseSha = new string('b', 40) },
            "HeadSha" => baseline with { HeadSha = new string('h', 40) },
            "Diff" => baseline with { Diff = "diff --git a/Other.cs b/Other.cs\n+ different" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unhandled subject field"),
        };

        DaemonReviewStageExecutor.StableSubjectHash(altered).Should().NotBe(
            DaemonReviewStageExecutor.StableSubjectHash(baseline),
            "{0} is part of what the review is ABOUT, so a checkpoint built before it changed must not be "
                + "resumed onto",
            field);
    }

    /// <summary>
    ///     Every EXCLUDED field must leave the digest alone. Without this, a field could be left out of the
    ///     hash and still reach the discard decision through some other route, reintroducing the defect by a
    ///     side door.
    /// </summary>
    /// <remarks>
    ///     <c>SiblingRepos</c> is the live one — it is the sole cause of both production discards. The other
    ///     three are excluded on their own grounds (the roots record a sandbox path with exactly one distinct
    ///     value across all 245 context rows; changed paths are derived from the diff; the reason string is a
    ///     diagnostic), and are pinned here so nobody quietly folds one back in.
    /// </remarks>
    [Theory]
    [InlineData("SiblingRepos")]
    [InlineData("CheckoutRoot")]
    [InlineData("StoreRoot")]
    [InlineData("ChangedPaths")]
    [InlineData("UncomparableReason")]
    public void The_subject_digest_ignores_every_field_that_is_not_what_the_review_is_about(string field)
    {
        var baseline = SubjectPayload();
        var altered = field switch
        {
            "SiblingRepos" => baseline with
            {
                SiblingRepos = [new SiblingRepoPointer("Contracts", "/workspace/store/repos/Contracts", "url")],
            },
            "CheckoutRoot" => baseline with { CheckoutRoot = "/workspace/slot-7/repo" },
            "StoreRoot" => baseline with { StoreRoot = "/workspace/slot-7/notes" },
            "ChangedPaths" => baseline with { ChangedPaths = "Foo.cs\nBar.cs" },
            "UncomparableReason" => baseline with { UncomparableReason = "merge base unreachable" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unhandled excluded field"),
        };

        DaemonReviewStageExecutor.StableSubjectHash(altered).Should().Be(
            DaemonReviewStageExecutor.StableSubjectHash(baseline),
            "{0} describes where the review is mounted or what sits beside it, not what it is reviewing — a "
                + "checkpoint whose fan-out is already paid for must survive it changing",
            field);
    }

    /// <summary>
    ///     The digest must not depend on where one subject field ends and the next begins, which is why the
    ///     fields are length-prefixed rather than concatenated.
    /// </summary>
    [Fact]
    public void The_subject_digest_distinguishes_subjects_that_differ_only_in_where_a_field_boundary_falls()
    {
        var left = SubjectPayload() with { BaseSha = "aabb", HeadSha = "cc" };
        var right = SubjectPayload() with { BaseSha = "aa", HeadSha = "bbcc" };

        DaemonReviewStageExecutor.StableSubjectHash(left).Should().NotBe(
            DaemonReviewStageExecutor.StableSubjectHash(right),
            "plain concatenation would render both as 'aabbcc' and call two different subjects the same one");
    }

    /// <summary>A context payload with every subject field set, as the baseline for digest comparisons.</summary>
    private static ContextArtifactPayload SubjectPayload() =>
        new("118", new string('a', 40), new string('c', 40), "diff --git a/Foo.cs b/Foo.cs\n+ added");

    /// <summary>
    ///     A subject field that reads back blank must fail CLOSED to the row id, not be hashed as if the
    ///     subject were genuinely empty.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is not hypothetical and it is not a null check.</b> A schema change can blank a field
    ///         with no deserialization failure at all: a payload written while the record declared a field the
    ///         current record no longer declares parses cleanly, and the dropped content reads as blank.
    ///         <c>FileManifest</c> did exactly this — present in <c>review-context</c> payloads through run
    ///         138, gone from 139 — and three runs in the nova store (32, 62, 154) already carry a blank
    ///         <c>Diff</c>, two of which were genuinely reviewed.
    ///     </para>
    ///     <para>
    ///         The direction matters. Drift that blanks a field on ONE side makes the digests differ and
    ///         discards — wasteful, loud, safe. Drift that blanks the same field on BOTH sides makes the
    ///         digests MATCH, and the review resumes onto a checkpoint whose subject was never compared —
    ///         silent, cheap, wrong. The deserialization guard cannot catch the second case, because nothing
    ///         failed to deserialize. Hence: blank means "could not read the subject", never "the subject is
    ///         empty".
    ///     </para>
    ///     <para>
    ///         Asserting equality with the ROW ID rather than merely "not the hash" is what makes this a test
    ///         of the fail-closed direction: the row id is the pre-fix behaviour, i.e. the conservative one.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("PrId")]
    [InlineData("BaseSha")]
    [InlineData("HeadSha")]
    [InlineData("Diff")]
    public void A_subject_field_blanked_by_schema_drift_falls_back_to_the_row_id_instead_of_hashing_a_blank(
        string blankedField)
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();
        var full = SubjectPayload();
        var drifted = blankedField switch
        {
            "PrId" => full with { PrId = string.Empty },
            "BaseSha" => full with { BaseSha = string.Empty },
            "HeadSha" => full with { HeadSha = string.Empty },
            "Diff" => full with { Diff = string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(blankedField)),
        };
        var row = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ContextArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(drifted),
        });

        fixture.Executor.ContextSubjectGeneration(run).Should().Be(
            row.Id,
            "a blank {0} means the subject could not be READ, and hashing it would assert the subject is "
                + "empty — a claim that is not true and that makes two unreadable subjects compare equal",
            blankedField);
    }

    /// <summary>
    ///     The control for the test above: with every subject field present the digest is used, so the
    ///     fail-closed branch is reached by blankness and not by something incidental to the fixture.
    /// </summary>
    [Fact]
    public void A_context_whose_subject_is_fully_readable_is_hashed_rather_than_falling_back()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();
        var full = SubjectPayload();
        var row = fixture.Store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ContextArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(full),
        });

        var generation = fixture.Executor.ContextSubjectGeneration(run);
        generation.Should().Be(
            DaemonReviewStageExecutor.StableSubjectHash(full),
            "a readable subject is what the digest exists to summarise");
        generation.Should().NotBe(row.Id, "otherwise the assertion above could hold by coincidence");
    }

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

            // s2s: true because it is the only modality Program.cs will boot — it throws at startup on
            // UseS2SReviewAgent: false (Program.cs:278) — so a fixture defaulting the other way runs every
            // test that supplies no options against a configuration that cannot ship (#102). It defaulted to
            // false until then. Call sites that pass their OWN options object are unaffected by this line and
            // were flipped individually.
            options ??= new CodeReviewDaemonOptions { UseS2SReviewAgent = true };
            // Resumability is a property of WHERE the turn runs, so the double is resumable on exactly the
            // path production's is: hosted (S2S) turns survive this process, in-process ones do not.
            //
            // Measured, so the flip above is not taken on faith: pinning this to `false` (leaving every other
            // line alone) reddens 71 of the 133 tests in this class, and 13 of the 16 whose own options object
            // was flipped to S2S. Those 13 therefore reach a branch that only the S2S path takes.
            //
            // The 3 survivors are all ContextReady_*, and their survival is NOT a defect in them: they run
            // ExecuteStageAsync(ContextReady) only, and the resumability throw lives in the review stage, so
            // this mutant cannot discriminate them. Their flip removes the "cannot boot" objection without
            // changing what they exercise, because this fixture injects no S2SReviewWorkspacePreparer and the
            // two fail-closed S2S guards in FetchContextAsync are skipped when it is null. Same "necessary but
            // not sufficient" caveat as DaemonReviewStageExecutorSessionTests; #103 decides that path's fate.
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

        /// <summary>
        /// Seeds a COMPLETED earlier round on the same PR (an older head), with its review artifact and the
        /// outbox row that records whether that round's comment actually reached the provider. The pair is
        /// the point: a round that produced findings and a row that says they were never delivered is the
        /// state a later round must not paper over.
        /// </summary>
        public ReviewRun SeedPriorRound(string reviewText, bool delivered)
        {
            var repoId = Store.EnsureRepo(new RepoIdentity
            {
                Provider = _repoProvider,
                OrgOrOwner = "achieveai",
                Project = _repoProvider == "azure-devops" ? "Platform" : null,
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            });
            var prior = Store.CreateOrGetReviewRun(new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = "old-head-sha",
                BaseSha = "base-sha",
                TriggerWatermark = "wm-0",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "collect-only",
                Stage = ReviewStage.Posted,
                WorkflowStatus = WorkflowStatus.Completed,
                PrLifecycleState = PrLifecycleState.Open,
            });
            _ = Store.AddArtifact(new ReviewArtifact
            {
                ReviewRunId = prior.Id,
                ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = _repoProvider,
                Payload = JsonSerializer.Serialize(
                    new ReviewArtifactPayload(reviewText, "prior-run", "primary")),
            });
            _ = Store.EnqueueOutbox(new OutboxEntry
            {
                IdempotencyKey = $"prior:{prior.Id}:post-review-comment",
                Provider = _repoProvider,
                ReviewRunId = prior.Id,
                Operation = ReviewPoster.PostReviewCommentOperation,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Status = delivered ? OutboxStatus.Posted : OutboxStatus.Collected,
            });
            return prior;
        }

        public ReviewRun SeedRun(string watermark = "wm-1", string mode = "collect-only", string? prAuthor = null)
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
                PrAuthor = prAuthor,
            });
        }

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
        }
    }
}
