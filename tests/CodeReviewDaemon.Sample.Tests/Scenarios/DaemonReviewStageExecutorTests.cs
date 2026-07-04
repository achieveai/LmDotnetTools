using System.Text.Json;
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
            new CodeReviewDaemonOptions { Limits = new Configuration.SandboxLimits { MaxArtifactPayloadChars = 256 } },
            diffResult: new SandboxCommandResult(0, hugeDiff, string.Empty));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var diff = JsonDocument.Parse(artifact.Payload).RootElement.GetProperty("Diff").GetString()!;
        diff.Length.Should().BeLessThan(hugeDiff.Length, "an oversized diff must be capped before persisting (H4)");
        diff.Should().Contain("truncated");
    }

    [Fact]
    public async Task Reviewed_persists_a_review_artifact_and_skips_optional_arms_by_default()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var kinds = fixture.Store.GetArtifacts(run.Id).Select(a => a.ArtifactKind).ToList();
        kinds.Should().Contain(DaemonReviewStageExecutor.ReviewArtifactKind);
        kinds.Should().NotContain(VariantReviewer.VariantReviewArtifactKind, "EnableABVariants is off by default");
        kinds.Should().NotContain(JudgeAgent.JudgeArtifactKind, "the judge runs in the Judged stage when enabled");

        fixture.Factory.CreatedProfileIds.Should().Contain(DaemonAgentFactory.ReviewProfileId);
        fixture.Factory.CreatedProfileIds.Should().NotContain($"{DaemonAgentFactory.ReviewProfileId}-b");
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
    public async Task EnableKnowledgeAgent_writes_a_knowledge_base_entry_to_the_sandbox()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableKnowledgeAgent = true });
        fixture.Factory.TextByProfileId[DaemonAgentFactory.KnowledgeProfileId] = "# Null-check lesson\nAlways guard.";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.FileSystem.Writes.Should().Contain(p => p.Contains("KnowledgeBase/") && p.EndsWith("_toc.md"));
        fixture.FileSystem.Writes.Should().Contain(p => p.Contains("KnowledgeBase/") && !p.EndsWith("_toc.md"));
        fixture.Factory.CreatedProfileIds.Should().Contain(DaemonAgentFactory.KnowledgeProfileId);
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
    public async Task Posted_records_collect_only_and_does_not_post_by_default()
    {
        using var fixture = Fixture.GitHub(LoggerFactory);
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        fixture.GitHubPublisher.PostCount.Should().Be(0, "comment posting is off by default (collect-only)");
    }

    [Fact]
    public async Task Posted_posts_exactly_once_when_comment_posting_is_authorized()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true });
        // An ISO trigger watermark carries ':' — the executor must sanitize it before the idempotency
        // key is built (IdempotencyKey.Build rejects ':' in any component), or this throws.
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        fixture.GitHubPublisher.PostCount.Should().Be(1);
        fixture.GitHubPublisher.PostedBodies.Should().ContainSingle().Which.Should().Contain("Foo.cs");
    }

    [Fact]
    public async Task An_azure_devops_run_maps_to_the_ado_provider_and_publisher()
    {
        using var fixture = Fixture.Ado(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true });
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
        // The push must succeed so the retention sequence reaches the reviewbot_push record.
        fixture.Runner.OnArgvContains("rev-parse main", new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.Runner.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        // The §2 durable one-commit retention sequence ran in the ReviewBot checkout.
        commands.Should().Contain(a => a.Contains("checkout -B review/github/achieveai-lmdotnettools/118"));
        commands.Should().Contain(a => a.Contains("commit -m"));
        commands.Should().Contain(a => a.Contains("push origin main"));

        // The PRs/... review artifact was written into the checkout before the commit.
        fixture.FileSystem.Writes.Should().Contain(p => p.Contains("/PRs/github/") && p.EndsWith("review.md"));

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
        fixture.Runner.OnArgvContains("push origin main", new SandboxCommandResult(1, string.Empty, "rejected"));
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
        // A github run but only an 'ado' publisher registered → the provider lookup must fail fast.
        using var fixture = Fixture.GitHub(
            LoggerFactory,
            new CodeReviewDaemonOptions { EnableCommentPosting = true },
            publishersOverride: [new FakeReviewCommentPublisher("ado")]);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*github*");
    }

    [Fact]
    public async Task Posted_substitutes_a_placeholder_body_when_the_review_is_empty()
    {
        using var fixture = Fixture.GitHub(LoggerFactory, new CodeReviewDaemonOptions { EnableCommentPosting = true });
        // The agent produces no review prose → ReadReviewText is empty → the poster needs a non-blank body.
        fixture.Factory.TextByProfileId[DaemonAgentFactory.ReviewProfileId] = string.Empty;
        var run = fixture.SeedRun(watermark: "2026-06-29T12:34:56Z");

        await RunAllStagesAsync(fixture, run);

        fixture.GitHubPublisher.PostedBodies.Should().ContainSingle()
            .Which.Should().Be("_No review content was produced._");
    }

    /// <summary>Seeds a well-formed (already-seeded) ReviewBot skeleton into the checkout.</summary>
    private static void SeedReviewBotSkeleton(Fixture fixture)
    {
        fixture.FileSystem.Seed("/workspace/reviewbot/README.md", "# ReviewBot");
        fixture.FileSystem.Seed("/workspace/reviewbot/PRs/.gitkeep", string.Empty);
        fixture.FileSystem.Seed("/workspace/reviewbot/KnowledgeBase/.gitkeep", string.Empty);
        fixture.FileSystem.Seed("/workspace/reviewbot/KnowledgeBase/_toc.md", "# Knowledge Base");
    }

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
        private readonly string _repoProvider;

        private Fixture(
            ILoggerFactory loggerFactory,
            string repoProvider,
            CodeReviewDaemonOptions? options,
            SandboxCommandResult? diffResult,
            IReviewCommentPublisher[]? publishersOverride)
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
                loggerFactory);
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
            IReviewCommentPublisher[]? publishersOverride = null) =>
            new(loggerFactory, "github", options, diffResult, publishersOverride);

        public static Fixture Ado(ILoggerFactory loggerFactory, CodeReviewDaemonOptions? options = null) =>
            new(loggerFactory, "azure-devops", options, diffResult: null, publishersOverride: null);

        public ReviewRun SeedRun(string watermark = "wm-1")
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
}
