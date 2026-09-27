using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

// Per-slot clone/submodule preparation was removed. These tests preserve the separate retention
// checkout and origin contract; remote slot preparation is covered by ReviewSetupScriptRunnerTests.
public sealed class ReviewSlotPreparerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_root_acquisition_preserves_metadata_and_remote_default_branch(bool metadata)
    {
        using var fixture = await RootFixture.CreateAsync();
        if (metadata)
            fixture.AddMetadata();
        await fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default);
        (await fixture.Git("symbolic-ref", "--short", "HEAD")).Should().Be("outer-baseline");
        (await fixture.Git("remote", "get-url", "origin")).Should().Be(RootFixture.Origin);
        (await fixture.Git("rev-parse", "--is-shallow-repository")).Should().Be("false");
        RealGitFixture.Read(fixture.Root, "README.md").Should().Be("baseline");
        if (metadata)
            RealGitFixture.Read(fixture.Root, ".mcp-gateway/operations/sentinel").Should().Be("gateway-owned");
        (await fixture.Git("status", "--porcelain", "--untracked-files=all")).Should().BeEmpty();
        var before = await fixture.Git("rev-parse", "HEAD");
        await fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default);
        (await fixture.Git("rev-parse", "HEAD")).Should().Be(before);
    }

    [Theory]
    [InlineData("user-file")]
    [InlineData("metadata-file")]
    [InlineData("metadata-link")]
    [InlineData("git-link")]
    public async Task Workspace_root_refuses_foreign_content_without_initializing_git(string entry)
    {
        using var fixture = await RootFixture.CreateAsync();
        switch (entry)
        {
            case "user-file":
                fixture.AddMetadata();
                RealGitFixture.Write(fixture.Root, "user.txt", "keep");
                break;
            case "metadata-file":
                RealGitFixture.Write(fixture.Root, ".mcp-gateway", "keep");
                break;
            case "metadata-link":
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, ".mcp-gateway"), fixture.Remote);
                break;
            case "git-link":
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, ".git"), fixture.Remote);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
        );
        if (entry != "git-link")
            Directory.Exists(Path.Combine(fixture.Root, ".git")).Should().BeFalse();
        if (entry == "user-file")
            RealGitFixture.Read(fixture.Root, "user.txt").Should().Be("keep");
    }

    [Fact]
    public async Task Workspace_root_partial_fetch_failure_requires_reconciliation_without_retry()
    {
        using var fixture = await RootFixture.CreateAsync();
        fixture.AddMetadata();
        fixture.FailFetch = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
        );
        fixture.FailFetch = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
        );
        RealGitFixture.Read(fixture.Root, ".mcp-gateway/operations/sentinel").Should().Be("gateway-owned");
        RealGitFixture.Read(fixture.Root, "README.md").Should().BeNull();
    }

    [Fact]
    public async Task Workspace_root_unknown_fetch_keeps_unknown_outcome()
    {
        using var fixture = await RootFixture.CreateAsync();
        fixture.LoseFetch = true;
        await Assert.ThrowsAsync<RemoteWorkspaceOutcomeUnknownException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
        );
    }

    [Fact]
    public async Task Workspace_root_rejects_tracked_gateway_metadata_before_checkout()
    {
        using var fixture = await RootFixture.CreateAsync(collision: true);
        fixture.AddMetadata();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
        );
        RealGitFixture.Read(fixture.Root, ".mcp-gateway/operations/sentinel").Should().Be("gateway-owned");
        RealGitFixture.Read(fixture.Root, "README.md").Should().BeNull();
    }

    [Fact]
    public async Task Workspace_root_rejects_foreign_origin_without_changing_head()
    {
        using var fixture = await RootFixture.CreateAsync();
        await fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default);
        var head = await fixture.Git("rev-parse", "HEAD");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync("https://github.com/other/reviews", default)
        );
        (await fixture.Git("rev-parse", "HEAD")).Should().Be(head);
        (await fixture.Git("remote", "get-url", "origin")).Should().Be(RootFixture.Origin);
    }

    [Fact]
    public async Task Workspace_root_rejects_incomplete_checkout_without_repairing_files()
    {
        using var fixture = await RootFixture.CreateAsync();
        await fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default);
        File.Delete(Path.Combine(fixture.Root, "README.md"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
        );
        RealGitFixture.Read(fixture.Root, "README.md").Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_root_allows_only_unstaged_declared_gitlink_movement(bool stage)
    {
        using var fixture = await RootFixture.CreateAsync();
        await fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default);
        var source = Path.Combine(fixture.Root, "repos", "Nova");
        Directory.CreateDirectory(source);
        await fixture.GitAt(source, "init");
        RealGitFixture.Write(source, "file.txt", "base");
        await fixture.GitAt(source, "add", ".");
        await fixture.GitAt(source, "commit", "-m", "base");
        RealGitFixture.Write(
            fixture.Root,
            ".gitmodules",
            "[submodule \"repos/Nova\"]\npath = repos/Nova\nurl = https://dev.azure.com/example/project/_git/Nova\n"
        );
        await fixture.Git("add", ".gitmodules", "repos/Nova");
        await fixture.Git("commit", "-m", "declare source");
        RealGitFixture.Write(source, "file.txt", "advanced");
        await fixture.GitAt(source, "commit", "-am", "advance");
        if (stage)
        {
            await fixture.Git("add", "repos/Nova");
            await fixture.Git("config", "submodule.repos/Nova.ignore", "all");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
            );
        }
        else
        {
            await fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default);
            RealGitFixture.Write(fixture.Root, "README.md", "ordinary dirty file");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Preparer.EnsureWorkspaceRootAsync(RootFixture.Origin, default)
            );
        }
    }

    private sealed class RootFixture : ISandboxCommandRunner, ISandboxFileSystem, IDisposable
    {
        public const string Origin = "https://github.com/fixture/outer-reviews.git";
        private readonly RealGitFixture _git;
        private readonly HostFileSystem _files = new();
        public string Root { get; }
        public string Remote => _git.OriginPath;
        public bool FailFetch { get; set; }
        public bool LoseFetch { get; set; }
        public ReviewSlotPreparer Preparer { get; }

        private RootFixture(RealGitFixture git)
        {
            _git = git;
            Root = Path.Combine(git.Root, "workspace");
            Directory.CreateDirectory(Root);
            Preparer = new ReviewSlotPreparer(new GitRunner(this), this);
        }

        public static async Task<RootFixture> CreateAsync(bool collision = false)
        {
            var git = await RealGitFixture.CreateAsync();
            var seed = await git.CloneAsync("seed");
            RealGitFixture.Write(seed, "README.md", "baseline");
            if (collision)
                RealGitFixture.Write(seed, ".mcp-gateway", "remote-collision");
            await git.GitAsync(seed, default, "checkout", "-b", "outer-baseline");
            await git.GitAsync(seed, default, "add", ".");
            await git.GitAsync(seed, default, "commit", "-m", "fixture");
            await git.GitAsync(seed, default, "push", "origin", "outer-baseline");
            await git.GitAsync(git.OriginPath, default, "symbolic-ref", "HEAD", "refs/heads/outer-baseline");
            return new RootFixture(git);
        }

        public void AddMetadata() => RealGitFixture.Write(Root, ".mcp-gateway/operations/sentinel", "gateway-owned");

        public async Task<string> Git(params string[] args) => (await _git.GitAsync(Root, default, args)).Trim();

        public Task<string> GitAt(string path, params string[] args) => _git.GitAsync(path, default, args);

        public async Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken ct)
        {
            if (command.Argv.Contains("fetch"))
            {
                if (LoseFetch)
                    throw new IOException("synthetic unacknowledged fetch");
                if (FailFetch)
                    return new SandboxCommandResult(128, "", "synthetic definite failure");
            }
            var argv = command.Argv.Select(a => a == "/workspace" ? Root : a).ToArray();
            if (argv.Contains("fetch") || argv.Contains("ls-remote"))
                argv =
                [
                    "git",
                    "-c",
                    $"url.{new Uri(Remote + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')}.insteadOf={Origin}",
                    .. argv.Skip(1),
                ];
            var result = await _git.Runner.RunAsync(new SandboxCommand(argv, Root), ct);
            return command.Argv.Contains("--show-toplevel") && result.Succeeded
                ? result with
                {
                    Stdout = "/workspace\n",
                }
                : result;
        }

        private string Local(string path) => Path.Combine(Root, path["/workspace".Length..].TrimStart('/'));

        public Task<SandboxFileRead> ReadFileAsync(string path, long maxBytes, CancellationToken ct) =>
            _files.ReadFileAsync(Local(path), maxBytes, ct);

        public Task WriteFileAsync(string path, string content, CancellationToken ct) =>
            _files.WriteFileAsync(Local(path), content, ct);

        public Task<IReadOnlyList<string>> ListFilesAsync(string path, CancellationToken ct) =>
            _files.ListFilesAsync(Local(path), ct);

        public void Dispose() => _git.Dispose();
    }

    [Theory]
    [InlineData("https://github.com/acme/reviews.git", "https://GITHUB.com/acme/reviews/")]
    [InlineData("https://github.com/acme/reviews", "https://user@github.com:443/ACME/reviews.git")]
    [InlineData("https://dev.azure.com/org/project/_git/reviews", "https://org.visualstudio.com/project/_git/reviews")]
    public async Task Origin_verification_accepts_equivalent_repository_urls(string configured, string actual)
    {
        var runner = new FakeSandboxCommandRunner().OnArgvContains(
            "remote get-url",
            new SandboxCommandResult(0, actual, "")
        );
        await ReviewSlotPreparer.VerifyStoreOriginAsync(new GitRunner(runner), "/store", configured, default);
        runner.Commands.Should().HaveCount(2);
        runner.Commands.Should().Contain(command => command.Argv.Contains("--push"));
    }

    [Theory]
    [InlineData("https://github.com/acme/other")]
    [InlineData("https://github.com:8443/acme/reviews")]
    [InlineData("https://other.example/acme/reviews")]
    [InlineData("https://evil.example/x@github.com/acme/reviews")]
    [InlineData("https://github.com/acme/reviews?alternate=1")]
    [InlineData("https://github.com/acme/reviews\nhttps://github.com/acme/other")]
    [InlineData("")]
    public async Task Origin_verification_rejects_wrong_or_ambiguous_push_destinations(string pushUrl)
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("--push", new SandboxCommandResult(0, pushUrl, ""))
            .OnArgvContains("remote get-url", new SandboxCommandResult(0, "https://github.com/acme/reviews", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReviewSlotPreparer.VerifyStoreOriginAsync(
                new GitRunner(runner),
                "/store",
                "https://github.com/acme/reviews",
                default
            )
        );
        runner.Commands.Should().OnlyContain(command => command.Argv.Contains("get-url"));
    }

    [Fact]
    public async Task Existing_wrong_origin_fails_without_mutating_checkout()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --git-dir", new SandboxCommandResult(0, ".git", ""))
            .OnArgvContains("remote get-url", new SandboxCommandResult(0, "https://github.com/other/reviews", ""));
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), new FakeSandboxFileSystem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            preparer.EnsureStoreAsync("/store", "https://github.com/acme/reviews", default)
        );
        runner
            .Commands.Should()
            .NotContain(c => c.Argv.Contains("clone") || c.Argv.Contains("fetch") || c.Argv.Contains("set-url"));
    }

    [Fact]
    public async Task Empty_retention_directory_is_cloned_without_a_nonexistent_cwd()
    {
        var runner = new FakeSandboxCommandRunner().OnArgvContains(
            "rev-parse --git-dir",
            new SandboxCommandResult(128, "", "not a repository")
        );
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), new FakeSandboxFileSystem());
        await preparer.EnsureStoreAsync("/store", "https://github.com/acme/reviews", default);
        var clone = runner.Commands.Single(c => c.Argv.Contains("clone"));
        clone.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public async Task Nonempty_nonrepository_is_not_deleted_or_recloned()
    {
        var runner = new FakeSandboxCommandRunner().OnArgvContains(
            "rev-parse --git-dir",
            new SandboxCommandResult(128, "", "not a repository")
        );
        var files = new FakeSandboxFileSystem();
        files.Files["/store/operator-notes.txt"] = "keep";
        var preparer = new ReviewSlotPreparer(new GitRunner(runner), files);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            preparer.EnsureStoreAsync("/store", "https://github.com/acme/reviews", default)
        );
        runner.Commands.Should().NotContain(c => c.Argv.Contains("clone"));
        files.Files["/store/operator-notes.txt"].Should().Be("keep");
    }
}
