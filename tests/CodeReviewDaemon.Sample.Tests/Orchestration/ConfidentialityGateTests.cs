using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 17 — the confidentiality gate (design §6 Risk B): co-locating the AchieveAiReviews store's
/// sibling private submodule beside a review checkout is safe only when the PR is positively established
/// as same-trust-domain (head not from a fork, target repo private). Anything else — a fork PR, a public
/// target, or a run whose trust signal was never populated — must deny co-location, because a
/// prompt-injected agent reviewing untrusted diff content could otherwise <c>Read</c> the sibling and
/// surface it verbatim in the posted review. <see cref="DaemonReviewStageExecutor.AllowsCrossRepoCoLocation"/>
/// is the single decision point <see cref="DaemonReviewStageExecutor.BuildStoreSubmoduleAllowList"/> (Task
/// 16, <see cref="CrossRepoCheckoutTests"/>) consults before adding any configured sibling.
/// </summary>
public sealed class ConfidentialityGateTests
{
    private static readonly RepoIdentity AcmeWidgets = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_1",
    };

    private static readonly RepoIdentity OssRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "oss-widget",
        RepoStableId = "R_node_2",
    };

    private static readonly RepoIdentity AdoRepo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "mcqdbdev",
        Project = "MCQdb_Development",
        RepoName = "MCQdbDEV",
    };

    [Fact]
    public void CoLocation_SameOrgNonFork_Allowed()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });

        executor
            .AllowsCrossRepoCoLocation(SeedRun(isForkPr: false, isTargetRepoPublic: false), AcmeWidgets)
            .Should()
            .BeTrue("the PR head is not from a fork and the target repo is private — same trust domain");
    }

    [Fact]
    public void CoLocation_ForkPr_Denied()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });

        executor
            .AllowsCrossRepoCoLocation(SeedRun(isForkPr: true, isTargetRepoPublic: false), AcmeWidgets)
            .Should()
            .BeFalse("a fork PR's diff is untrusted and must not see the sibling private submodule");
    }

    [Fact]
    public void CoLocation_PublicRepo_Denied()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });

        executor
            .AllowsCrossRepoCoLocation(SeedRun(isForkPr: false, isTargetRepoPublic: true), OssRepo)
            .Should()
            .BeFalse("a public target repo review must not co-locate a private sibling");
    }

    [Fact]
    public void CoLocation_UnknownTrustSignal_DeniedByDefault()
    {
        // A run built without positively setting either trust field must deny co-location — the
        // fail-closed default (design §6 Risk B: never permissive when trust cannot be confirmed).
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });
        var run = new ReviewRun
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

        run.IsForkPr.Should().BeTrue("the default must be fail-closed when nothing populated it");
        run.IsTargetRepoPublic.Should().BeTrue("the default must be fail-closed when nothing populated it");
        executor.AllowsCrossRepoCoLocation(run, AcmeWidgets).Should().BeFalse();
    }

    [Fact]
    public void StoreSubmoduleAllowList_TrustedRun_GrantsConfiguredSibling()
    {
        // End-to-end wiring: once the gate confirms same-trust-domain, BuildStoreSubmoduleAllowList (Task
        // 16) actually adds the configured sibling to the run's allow-list.
        using var db = new TempSqliteDatabase();
        var options = new CodeReviewDaemonOptions
        {
            EnableToolAssistedReview = true,
            CrossRepoSiblings = ["acme/other-service"],
        };
        var executor = BuildExecutor(db, options);
        var run = SeedRun(isForkPr: false, isTargetRepoPublic: false);

        var rules = executor.BuildStoreSubmoduleAllowList(run, AcmeWidgets);
        var policy = DaemonOperationPolicy.BuildForRun(
            AcmeWidgets,
            "https://github.com/acme/AchieveAiReviews.git",
            allowWriteOperations: false,
            allowedSubmodules: rules);

        policy
            .Decide(new OperationRequest(
                SandboxOperation.FetchSubmodule,
                "github",
                "github.com",
                "GET",
                "/acme/other-service.git/info/refs?service=git-upload-pack"))
            .IsAllowed.Should()
            .BeTrue("the gate confirmed same-trust-domain, so the configured sibling is granted");
    }

    [Fact]
    public void StoreSubmoduleAllowList_AdoRepo_AllowsReviewedRepoSubmodule()
    {
        // Regression: the allow-list host/path must be provider-aware. An ADO submodule lives at
        // dev.azure.com/{org}/{project}/_git/{repo}; a github.com rule denies it (live symptom:
        // "submodule '…MCQdbDEV.git/info/refs' is not on the allow-list"). Assert the reviewed ADO repo's
        // own submodule is granted with its dev.azure.com host + path.
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });
        var run = SeedRun(isForkPr: false, isTargetRepoPublic: false);

        var rules = executor.BuildStoreSubmoduleAllowList(run, AdoRepo);
        var policy = DaemonOperationPolicy.BuildForRun(
            AdoRepo,
            "https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbReview",
            allowWriteOperations: false,
            allowedSubmodules: rules);

        policy
            .Decide(new OperationRequest(
                SandboxOperation.FetchSubmodule,
                "ado",
                "dev.azure.com",
                "GET",
                "/mcqdbdev/MCQdb_Development/_git/MCQdbDEV.git/info/refs?service=git-upload-pack"))
            .IsAllowed.Should()
            .BeTrue("the reviewed ADO repo's own submodule must be allow-listed with its dev.azure.com host/path");
    }

    private static DaemonReviewStageExecutor BuildExecutor(TempSqliteDatabase db, CodeReviewDaemonOptions options) =>
        new(
            new ReviewStore(db.ConnectionString),
            new FakeReviewAgentLoopFactory(),
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            NullLoggerFactory.Instance);

    private static ReviewRun SeedRun(bool isForkPr, bool isTargetRepoPublic) => new()
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
        IsForkPr = isForkPr,
        IsTargetRepoPublic = isTargetRepoPublic,
    };
}
