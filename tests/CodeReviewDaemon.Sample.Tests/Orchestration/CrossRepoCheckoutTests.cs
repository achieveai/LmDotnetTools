using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging;
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

    private static readonly RepoIdentity McqdbDev = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "mcqdbdev",
        Project = "MCQdb_Development",
        RepoName = "MCQdbDEV",
        RepoStableId = "ado-guid-1",
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
            allowedSubmodules: rules
        );

        Fetch(policy, "github.com", "/acme/widgets.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue("the reviewed repo itself is always allow-listed");
        Fetch(policy, "github.com", "/acme/Contracts.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue("Contracts/ is the shared low-sensitivity layer, always allowed");
        Fetch(policy, "github.com", "/evil/secret.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeFalse("an unrelated repo is never on the allow-list");
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
            allowedSubmodules: rules
        );

        Fetch(policy, "github.com", "/acme/other-service.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeFalse("co-location is gated by trust (Task 17); an unconfirmed run gets none");
    }

    [Fact]
    public void StoreSubmoduleAllowList_ReviewedRepoSubmodules_AreAllowed_RegardlessOfConfidentialityGate()
    {
        using var db = new TempSqliteDatabase();
        var options = new CodeReviewDaemonOptions
        {
            EnableToolAssistedReview = true,
            // A store-level sibling (gated) alongside the target's OWN submodules (never gated).
            CrossRepoSiblings = ["some-sibling"],
            ReviewedRepoSubmodules = ["LibProfiler", "Microsoft%20Orleans"],
        };
        var executor = BuildExecutor(db, options);
        // A default run carries NO positive trust signal (IsForkPr/IsTargetRepoPublic default true), so
        // AllowsCrossRepoCoLocation is FALSE — the confidentiality gate is shut.
        var run = SeedRun();
        executor.AllowsCrossRepoCoLocation(run, McqdbDev).Should().BeFalse("the gate is shut for an unconfirmed run");

        var rules = executor.BuildStoreSubmoduleAllowList(run, McqdbDev);
        var policy = DaemonOperationPolicy.BuildForRun(
            McqdbDev,
            "https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbReview",
            allowWriteOperations: false,
            allowedSubmodules: rules
        );

        // The target's OWN submodules are allow-listed even with the gate shut (unlike CrossRepoSiblings).
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/LibProfiler.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue("reviewed-repo submodules are the target's own dependencies, not gated");
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/Microsoft%20Orleans.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue("the URL-encoded name matches its allow rule verbatim");

        // A store-level sibling stays gated (denied) under the same shut gate...
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/some-sibling.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeFalse("CrossRepoSiblings remain gated by AllowsCrossRepoCoLocation");
        // ...and an unlisted same-org name is denied — explicit allow-list, never a same-org wildcard.
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/UnlistedLib.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeFalse("only explicitly listed submodules are allowed");
    }

    /// <summary>
    /// Issue #478 — the clone URL and the allow-list are built from the SAME identity and then compared to
    /// each other: <c>TargetRemoteUrl</c> is re-parsed and its host+path matched against these rules (that is
    /// how the daemon decides which store submodule is the reviewed repo, and which submodule URLs a review
    /// may fetch at all). An Azure DevOps org or project name may legally contain a space, which raw makes
    /// the clone URL malformed — but encoding only the URL would leave this matcher comparing raw segments
    /// against an encoded path and silently stop matching a legitimately allow-listed repo.
    /// <para>
    /// This pins the AGREEMENT at the two production sites, not either spelling: whatever encoding is
    /// canonical, the parsed clone URL must land exactly on the rule, and the run's policy must then permit
    /// fetching it.
    /// </para>
    /// </summary>
    [Fact]
    public void StoreSubmoduleAllowList_AgreesWithTheCloneUrl_ForASpacedAdoIdentity()
    {
        var spaced = new RepoIdentity
        {
            Provider = "azure-devops",
            OrgOrOwner = "contoso org",
            Project = "MCQdb Development",
            RepoName = "My Repo",
            RepoStableId = "ado-guid-2",
        };
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = true });
        var run = SeedRun();

        var cloneUrl = DaemonReviewStageExecutor.TargetRemoteUrl(spaced, "ado");
        var parsed = GitRemoteUrl.Parse(cloneUrl);
        var rules = executor.BuildStoreSubmoduleAllowList(run, spaced);

        cloneUrl.Should().NotContain(" ", "a raw space makes the argv git clone parses a malformed URL");
        rules
            .Should()
            .Contain(
                r => r.Host == parsed.Host && r.RepoPath == parsed.RepoPath,
                "the reviewed repo's allow rule must be the exact path the clone URL addresses"
            );

        var policy = DaemonOperationPolicy.BuildForRun(
            spaced,
            DaemonReviewStageExecutor.TargetRemoteUrl(spaced with { RepoName = "MCQdbReview" }, "ado"),
            allowWriteOperations: false,
            allowedSubmodules: rules
        );

        FetchAdo(policy, $"{parsed.RepoPath}.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeTrue("the spaced-org repo the daemon clones is the one the policy permits");
        FetchAdo(policy, "/contoso%20org/MCQdb%20Development/_git/Unlisted.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should()
            .BeFalse("the allow-list is still explicit — no same-org wildcard");
    }

    /// <summary>
    /// A configured submodule/sibling name goes into the allow rule VERBATIM (its documented contract is the
    /// URL's own spelling). Configure a RAW space and the rule is built from a path the parser — which never
    /// decodes — can never produce, so the submodule is silently never allowed: indistinguishable from having
    /// left it out. The warning is the only thing that tells the two apart, so it is the deliverable here.
    /// </summary>
    [Fact]
    public void StoreSubmoduleAllowList_WarnsWhenAConfiguredNameIsNotInUrlForm()
    {
        using var db = new TempSqliteDatabase();
        using var loggers = new CapturingLoggerFactory();
        var options = new CodeReviewDaemonOptions
        {
            EnableToolAssistedReview = true,
            // The correct spelling and the foot-gun side by side: only the raw-space one may be reported.
            ReviewedRepoSubmodules = ["Microsoft%20Orleans", "Microsoft Orleans", "LibProfiler"],
        };
        var executor = BuildExecutor(db, options, loggers);

        var rules = executor.BuildStoreSubmoduleAllowList(SeedRun(), McqdbDev);

        loggers
            .Capturing.CountAtLevel(LogLevel.Warning, "is not in URL form")
            .Should()
            .Be(1, "the raw-space entry never matches, and only the log can distinguish that from an unconfigured one");
        loggers
            .Capturing.CountAtLevel(LogLevel.Warning, "Microsoft Orleans")
            .Should()
            .Be(1, "the warning must name the offending entry");
        rules
            .Should()
            .Contain(
                r => r.RepoPath.EndsWith("/Microsoft%20Orleans", StringComparison.Ordinal),
                "the already-correct URL-form entry is left alone and must not be reported"
            );
    }

    [Fact]
    public void StoreSubmoduleAllowList_NotToolAssisted_IsEmpty()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { EnableToolAssistedReview = false });
        var run = SeedRun();

        executor
            .BuildStoreSubmoduleAllowList(run, AcmeWidgets)
            .Should()
            .BeEmpty("the diff-only path never grants any submodule fetch");
    }

    private static PolicyDecision Fetch(OperationPolicy policy, string host, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.FetchSubmodule, "github", host, "GET", path));

    private static PolicyDecision FetchAdo(OperationPolicy policy, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.FetchSubmodule, "ado", "dev.azure.com", "GET", path));

    private static DaemonReviewStageExecutor BuildExecutor(
        TempSqliteDatabase db,
        CodeReviewDaemonOptions options,
        ILoggerFactory? loggerFactory = null
    ) =>
        new(
            new ReviewStore(db.ConnectionString),
            new FakeReviewAgentLoopFactory(),
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            [new FakeReviewCommentPublisher("github")],
            loggerFactory ?? NullLoggerFactory.Instance
        );

    private static ReviewRun SeedRun() =>
        new()
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
