using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 16 — the tool-assisted review clones the cross-repo <c>AchieveAiReviews</c> store and needs a
/// per-run submodule allow-list to read across it. <see cref="DaemonReviewStageExecutor.BuildStoreSubmoduleAllowList"/>
/// always permits the reviewed repo itself and the shared, low-sensitivity <c>Contracts/</c> layer, and
/// denies everything else by default — sibling private submodules are added only when the confidentiality
/// gate (Task 17, see <c>ConfidentialityGateTests</c>) permits co-location for the run.
/// </summary>
public sealed class CrossRepoCheckoutTests
{
    private static readonly RepoIdentity AcmeWidgets = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_1",
    };

    [Fact]
    public void StoreSubmoduleAllowList_PermitsTargetRepoAndContracts_DeniesUnrelatedRepos()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });
        var run = SeedRun();

        var rules = executor.BuildStoreSubmoduleAllowList(run, AcmeWidgets);
        var policy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgets,
            "https://github.com/acme/AchieveAiReviews.git",
            allowWriteOperations: false,
            allowedSubmodules: rules);

        Fetch(policy, "github.com", "/acme/widgets.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue("the reviewed repo itself is always allow-listed");
        Fetch(policy, "github.com", "/acme/Contracts.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue("Contracts/ is the shared low-sensitivity layer, always allowed");
        Fetch(policy, "github.com", "/evil/secret.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeFalse("an unrelated repo is never on the allow-list");
    }

    [Fact]
    public void StoreSubmoduleAllowList_ConfiguredSibling_NotYetGrantedWithoutTheConfidentialityGate()
    {
        // The confidentiality gate (Task 17) decides whether a configured sibling is added; until it
        // positively confirms same-trust-domain, no sibling is added — proven here for a run carrying no
        // trust signal at all (fail closed, design §6 Risk B).
        using var db = new TempSqliteDatabase();
        var options = new CodeReviewDaemonOptions
        {
            EnableToolAssistedReview = true,
            CrossRepoSiblings = ["acme/other-service"],
        };
        var executor = BuildExecutor(db, options);
        var run = SeedRun();

        var rules = executor.BuildStoreSubmoduleAllowList(run, AcmeWidgets);
        var policy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgets,
            "https://github.com/acme/AchieveAiReviews.git",
            allowWriteOperations: false,
            allowedSubmodules: rules);

        Fetch(policy, "github.com", "/acme/other-service.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeFalse("co-location is gated by trust (Task 17); an unconfirmed run gets none");
    }

    [Fact]
    public void StoreSubmoduleAllowList_NotToolAssisted_IsEmpty()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = false });
        var run = SeedRun();

        executor.BuildStoreSubmoduleAllowList(run, AcmeWidgets).Should().BeEmpty(
            "the diff-only path never grants any submodule fetch");
    }

    private static PolicyDecision Fetch(OperationPolicy policy, string host, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.FetchSubmodule, "github", host, "GET", path));

    private static DaemonReviewStageExecutor BuildExecutor(TempSqliteDatabase db, CodeReviewDaemonOptions options) =>
        new(
            new ReviewStore(db.ConnectionString),
            new FakeReviewAgentLoopFactory(),
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            NullLoggerFactory.Instance);

    private static ReviewRun SeedRun() => new()
    {
        RepoId = 1,
        PrId = "42",
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
}
