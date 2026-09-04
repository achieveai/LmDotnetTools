using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.ReviewBot;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 15 — the ReviewBot retention push (and, by construction, the KB-entry write into the same
/// checkout) must run against a HOST-side workspace when one is supplied (design §6 Risk A), never the
/// sandbox the untrusted review agent shares. The sandbox runner injected as the boot-lifetime
/// <c>ISandboxCommandRunner</c> is scripted to fail on anything ReviewBot-shaped, so this test would fail
/// loudly (not silently pass) if retention regressed back onto the sandbox path.
/// </summary>
public sealed class HostRetentionTests
{
    private const string ReviewBotRepoUrl = "https://github.com/acme/AchieveAiReviews.git";
    private const string CrossRepoStoreUrl = "https://github.com/acme/CrossRepoReviews.git";

    [Fact]
    public async Task Post_Retention_RunsOnHostRunner_NotSandbox()
    {
        var sandbox = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree",
                new SandboxCommandResult(1, string.Empty, "not a git repo")
            )
            .OnArgvContains(
                "diff",
                new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty)
            )
            // Any ReviewBot-shaped command reaching the sandbox is a hard failure, not a silent success —
            // this is what would catch a regression back to the sandbox path.
            .OnArgvContains(
                "AchieveAiReviews",
                new SandboxCommandResult(1, string.Empty, "must not reach the sandbox")
            );
        var sandboxFileSystem = new FakeSandboxFileSystem();

        var host = new FakeSandboxCommandRunner()
            .OnArgvContains("remote get-url origin", new SandboxCommandResult(0, ReviewBotRepoUrl + "\n", string.Empty))
            .OnArgvContains(
                "rev-parse review/lmdotnettools-118",
                new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty)
            );
        var hostFileSystem = new FakeSandboxFileSystem()
            .Seed("/host/reviewbot/README.md", "# ReviewBot")
            .Seed("/host/reviewbot/PRs/.gitkeep", string.Empty)
            .Seed("/host/reviewbot/KnowledgeBase/.gitkeep", string.Empty)
            .Seed("/host/reviewbot/KnowledgeBase/_toc.md", "# Knowledge Base");
        var hostRetention = new HostRetentionWorkspace(host, hostFileSystem, "/host/reviewbot", ReviewBotRepoUrl);

        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var executor = new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            sandbox,
            sandboxFileSystem,
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = ReviewBotRepoUrl },
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            hostRetention: hostRetention
        );
        var run = SeedRun(store);

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        var hostCommands = host.Commands.Select(c => string.Join(' ', c.Argv)).ToList();
        var sandboxCommands = sandbox.Commands.Select(c => string.Join(' ', c.Argv)).ToList();

        hostCommands
            .Should()
            .Contain(
                c => c.Contains("push origin review/lmdotnettools-118"),
                "the retention push must run on the host runner"
            );
        hostCommands
            .Should()
            .NotContain(
                c => c.Contains("branch -D review/") || c.Contains("push origin --delete review/"),
                "a per-review commit keeps the review branch; only a PR-close op merges or deletes it"
            );
        sandboxCommands
            .Should()
            .NotContain(
                c => c.Contains("AchieveAiReviews") || c.Contains("checkout -B review/"),
                "the sandbox runner (shared with the untrusted review agent) must never see ReviewBot git traffic"
            );

        hostFileSystem
            .Writes.Should()
            .Contain(
                p => p.Contains("/PRs/") && p.EndsWith("review.md"),
                "the review artifact lands in the host checkout"
            );
        sandboxFileSystem
            .Writes.Should()
            .NotContain(
                p => p.Contains("/PRs/") && p.EndsWith("review.md"),
                "the review artifact must not be written into the sandbox checkout"
            );
    }

    [Fact]
    public async Task Post_Retention_ClonesTheResolvedCrossRepoStore()
    {
        var sandbox = new FakeSandboxCommandRunner().OnArgvContains(
            "diff",
            new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty)
        );
        var host = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree",
                new SandboxCommandResult(1, string.Empty, "not a git repo")
            )
            .OnArgvContains(
                "rev-parse review/lmdotnettools-118",
                new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty)
            );
        var hostFileSystem = new FakeSandboxFileSystem()
            .Seed("/host/reviewbot/README.md", "# ReviewBot")
            .Seed("/host/reviewbot/PRs/.gitkeep", string.Empty)
            .Seed("/host/reviewbot/KnowledgeBase/.gitkeep", string.Empty)
            .Seed("/host/reviewbot/KnowledgeBase/_toc.md", "# Knowledge Base");
        var hostRetention = new HostRetentionWorkspace(host, hostFileSystem, "/host/reviewbot", CrossRepoStoreUrl);

        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var executor = new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            sandbox,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = ReviewBotRepoUrl, CrossRepoStoreUrl = CrossRepoStoreUrl },
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            hostRetention: hostRetention
        );
        var run = SeedRun(store);

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        var clone = host.Commands.Should().ContainSingle(c => c.Argv.Contains("clone")).Subject;
        clone.Argv.Should().ContainInOrder("clone", CrossRepoStoreUrl, "/host/reviewbot");
        clone.Argv.Should().NotContain(ReviewBotRepoUrl);
    }

    [Fact]
    public async Task EnsureCheckout_RejectsAMismatchedExistingOrigin_WithoutLeakingCredentials()
    {
        const string expectedUrl = "https://expected-user:expected-secret@example.test/reviews.git";
        const string actualUrl = "https://other-user:other-secret@example.test/other.git";
        var host = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(0, "true\n", string.Empty))
            .OnArgvContains("remote get-url origin", new SandboxCommandResult(0, actualUrl + "\n", string.Empty));
        var logger = new CapturingLogger<HostRetentionTests>();

        var result = await ReviewBotCheckout.EnsureCheckoutAsync(
            new GitRunner(host),
            expectedUrl,
            "/host/reviewbot",
            logger,
            CancellationToken.None
        );

        result.Should().NotBeNull();
        result!.Message.Should().NotContain("expected-secret").And.NotContain("other-secret");
        logger
            .MessagesAtLevel(LogLevel.Error)
            .Should()
            .OnlyContain(message => !message.Contains("expected-secret") && !message.Contains("other-secret"));
        host.Commands.Should().NotContain(c => c.Argv.Contains("clone"));
    }

    private static ReviewRun SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );
        return store.CreateOrGetReviewRun(
            new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = "head-sha",
                BaseSha = "base-sha",
                TriggerWatermark = "2026-06-29T12:34:56Z",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "collect-only",
                Stage = ReviewStage.Discovered,
                WorkflowStatus = WorkflowStatus.Running,
                PrLifecycleState = PrLifecycleState.Open,
            }
        );
    }
}
