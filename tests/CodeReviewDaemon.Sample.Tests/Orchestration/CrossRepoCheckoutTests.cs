using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
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
/// <remarks>
/// Every options object here now sets <c>UseS2SReviewAgent: true</c> (#102) — the default is the config
/// <c>Program.cs:278</c> throws on at startup. As in <see cref="ConfidentialityGateTests"/>, <b>the flip is
/// hygiene rather than a fix</b>: this file executes no stage (zero <c>ExecuteStageAsync</c> calls) and the
/// allow-list builder it does call reads <c>CrossRepoSiblings</c> and <c>ReviewedRepoSubmodules</c>, never
/// the modality. Nothing it asserts could have changed. The value is only that the tree no longer carries an
/// options object the daemon would refuse to boot on.
/// </remarks>
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

    /// <summary>A real submodule of the NOVA_reviews store — the store spans two ADO projects, so this one
    /// exercises the cross-project sibling path.</summary>
    private static readonly RepoIdentity NovaRepo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "o365exchange",
        Project = "Weve_DA",
        RepoName = "Nova",
        RepoStableId = "ado-guid-nova",
    };

    [Fact]
    public void StoreSubmoduleAllowList_PermitsTargetRepoAndContracts_DeniesUnrelatedRepos()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableToolAssistedReview = true });
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
            UseS2SReviewAgent = true,
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
    public void StoreSubmoduleAllowList_ReviewedRepoSubmodules_AreAllowed_RegardlessOfConfidentialityGate()
    {
        using var db = new TempSqliteDatabase();
        var options = new CodeReviewDaemonOptions
        {
            UseS2SReviewAgent = true,
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
            allowedSubmodules: rules);

        // The target's OWN submodules are allow-listed even with the gate shut (unlike CrossRepoSiblings).
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/LibProfiler.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue("reviewed-repo submodules are the target's own dependencies, not gated");
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/Microsoft%20Orleans.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue("the URL-encoded name matches its allow rule verbatim");

        // A store-level sibling stays gated (denied) under the same shut gate...
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/some-sibling.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeFalse("CrossRepoSiblings remain gated by AllowsCrossRepoCoLocation");
        // ...and an unlisted same-org name is denied — explicit allow-list, never a same-org wildcard.
        FetchAdo(policy, "/mcqdbdev/MCQdb_Development/_git/UnlistedLib.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeFalse("only explicitly listed submodules are allowed");
    }

    /// <summary>
    /// The closed-gate warning must not claim the PROVIDER reported the target repo public. Both trust
    /// flags collapse to <c>true</c> when the provider could not establish them, so by the time this line
    /// formats, "observed public" and "never established" are the same value — and only one of them is a
    /// daemon bug.
    /// <para>
    /// This matters because the Debug line that records which half was unestablished is off in the console
    /// sink by default, so this Warning is usually the ONLY line an operator sees. Read as an observation,
    /// it sends them to the ADO project's visibility setting, where they find the project is private and
    /// conclude the daemon is lying about the repo — while the actual fault sits in the provider's parser,
    /// which is the one place that reading never takes them. That is the same "confidently wrong about
    /// WHERE the problem is" failure this whole investigation was about, so the line has to disclose the
    /// ambiguity and say where the real answer is recorded.
    /// </para>
    /// </summary>
    [Fact]
    public void StoreSubmoduleAllowList_ClosedGateWarning_DoesNotPassOffTheDefaultAsAProviderObservation()
    {
        using var db = new TempSqliteDatabase();
        using var logs = new CapturingLoggerFactory();
        var options = new CodeReviewDaemonOptions
        {
            UseS2SReviewAgent = true,
            EnableToolAssistedReview = true,
            CrossRepoSiblings = ["Weve_DA/Nova", "O365 Core/WeveNova"],
        };
        var executor = BuildExecutor(db, options, logs);
        // The live shape of run 139: the fork half WAS established (false), the visibility half was not and
        // collapsed to the fail-closed true. The gate is shut, and the true is a default, not a reading.
        var run = SeedRun(isForkPr: false, isTargetRepoPublic: true);

        _ = executor.BuildStoreSubmoduleAllowList(run, NovaRepo);

        var warning = logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Should().Contain(
            "never established",
            "true here means 'public OR unknown', and a line that does not say so is asserting an "
                + "observation the provider never made");
        warning.Should().Contain(
            "PrPollingService",
            "naming the ambiguity is only half of it — the line must also say where the unambiguous "
                + "answer is recorded, or the operator still has nowhere to go");
    }

    [Fact]
    public void StoreSubmoduleAllowList_NotToolAssisted_IsEmpty()
    {
        using var db = new TempSqliteDatabase();
        var executor = BuildExecutor(db, new CodeReviewDaemonOptions { UseS2SReviewAgent = true, EnableToolAssistedReview = false });
        var run = SeedRun();

        executor.BuildStoreSubmoduleAllowList(run, AcmeWidgets).Should().BeEmpty(
            "the diff-only path never grants any submodule fetch");
    }

    [Fact]
    public void StoreSubmoduleAllowList_AdoSibling_MayNameItsOwnProject()
    {
        // A store can span PROJECTS: NOVA_reviews holds Nova/NovaClient/Astra in Weve_DA and
        // WeveNova/MODISService in "O365 Core". Deriving every sibling's project from whichever repo is under
        // review builds the wrong path for the ones that live elsewhere, so a sibling may be written
        // '{project}/{repo}'. The org stays the reviewed repo's — a store's submodules are same-org.
        using var db = new TempSqliteDatabase();
        var options = new CodeReviewDaemonOptions
        {
            UseS2SReviewAgent = true,
            EnableToolAssistedReview = true,
            CrossRepoSiblings = ["NovaClient", "O365 Core/WeveNova"],
        };
        var executor = BuildExecutor(db, options);
        var run = SeedRun(isForkPr: false, isTargetRepoPublic: false);

        var rules = executor.BuildStoreSubmoduleAllowList(run, NovaRepo);
        var policy = DaemonOperationPolicy.BuildForRun(
            NovaRepo,
            "https://github.com/gautamb_microsoft/NOVA_reviews",
            allowWriteOperations: false,
            allowedSubmodules: rules);

        FetchAdo(policy, "/o365exchange/Weve_DA/_git/NovaClient.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue("an unqualified sibling resolves under the reviewed repo's project");
        FetchAdo(policy, "/o365exchange/O365%20Core/_git/WeveNova.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeTrue(
                "a project-qualified sibling resolves under ITS project, and %20 matches the configured space");
        FetchAdo(policy, "/o365exchange/O365%20Core/_git/MODISService.git/info/refs?service=git-upload-pack")
            .IsAllowed.Should().BeFalse("an unconfigured repo in that project is still denied");
    }

    [Theory]
    // Legacy {org}.visualstudio.com host — how the store declares Astra.
    [InlineData("https://o365exchange.visualstudio.com/Weve_DA/_git/Astra",
        "https://dev.azure.com/o365exchange/Weve_DA/_git/Astra")]
    // Legacy host + the implicit DefaultCollection — how the store declares MODISService.
    [InlineData("https://o365exchange.visualstudio.com/DefaultCollection/O365%20Core/_git/MODISService",
        "https://dev.azure.com/o365exchange/O365 Core/_git/MODISService")]
    // A percent-escaped project against the real space the daemon builds its target URL from.
    [InlineData("https://dev.azure.com/o365exchange/O365%20Core/_git/WeveNova",
        "https://dev.azure.com/o365exchange/O365 Core/_git/WeveNova")]
    // A trailing .git and a casing difference are both spelling, not identity.
    [InlineData("https://dev.azure.com/o365exchange/Weve_DA/_git/nova.git",
        "https://dev.azure.com/o365exchange/Weve_DA/_git/Nova")]
    public void SubmoduleTargetsRepo_pairs_every_url_spelling_of_the_same_repo(
        string declaredInGitmodules, string targetUrl)
    {
        // Without this, the run logs "… is not a submodule of the pooled store" and silently degrades to a
        // bare per-PR checkout — losing KnowledgeBase grounding and per-PR notes.
        DaemonReviewStageExecutor
            .StoreSubmoduleTargetsRepo(declaredInGitmodules, targetUrl)
            .Should()
            .BeTrue();
    }

    [Theory]
    // A different repo in the same project.
    [InlineData("https://dev.azure.com/o365exchange/Weve_DA/_git/NovaClient")]
    // The same repo NAME in a different project — the project is part of the identity.
    [InlineData("https://dev.azure.com/o365exchange/O365%20Core/_git/Nova")]
    // The same path on a different host.
    [InlineData("https://github.com/o365exchange/Weve_DA/_git/Nova")]
    // Double-encoding must not decode its way into a match.
    [InlineData("https://dev.azure.com/o365exchange/Weve%255FDA/_git/Nova")]
    public void SubmoduleTargetsRepo_does_not_pair_a_different_repo(string declaredInGitmodules)
    {
        DaemonReviewStageExecutor
            .StoreSubmoduleTargetsRepo(
                declaredInGitmodules, "https://dev.azure.com/o365exchange/Weve_DA/_git/Nova")
            .Should()
            .BeFalse();
    }

    private static PolicyDecision Fetch(OperationPolicy policy, string host, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.FetchSubmodule, "github", host, "GET", path));

    private static PolicyDecision FetchAdo(OperationPolicy policy, string path) =>
        policy.Decide(new OperationRequest(SandboxOperation.FetchSubmodule, "ado", "dev.azure.com", "GET", path));

    private static DaemonReviewStageExecutor BuildExecutor(TempSqliteDatabase db, CodeReviewDaemonOptions options) =>
        BuildExecutor(db, options, NullLoggerFactory.Instance);

    /// <summary>Overload for the tests where the log line IS the deliverable and has to be asserted on.</summary>
    private static DaemonReviewStageExecutor BuildExecutor(
        TempSqliteDatabase db, CodeReviewDaemonOptions options, ILoggerFactory loggerFactory) =>
        new(
            new ReviewStore(db.ConnectionString),
            new FakeReviewAgentLoopFactory(),
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            [new FakeReviewCommentPublisher("github")],
            loggerFactory);

    private static ReviewRun SeedRun(bool isForkPr = true, bool isTargetRepoPublic = true) => new()
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
        // Default to the fail-closed values ReviewRun itself defaults to, so a caller that says nothing gets
        // the shut confidentiality gate.
        IsForkPr = isForkPr,
        IsTargetRepoPublic = isTargetRepoPublic,
    };
}
