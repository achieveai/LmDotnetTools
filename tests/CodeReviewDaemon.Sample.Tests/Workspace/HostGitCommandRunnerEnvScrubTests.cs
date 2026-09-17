using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// These tests set PROCESS-wide environment variables, which every other test in this assembly would
/// otherwise inherit mid-run — the same inheritance the code under test exists to stop. The collection
/// keeps them off the parallel schedule so the blast radius stays inside this class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitEnvironmentMutatingCollection
{
    public const string Name = "git-environment-mutating";
}

/// <summary>
/// A git hook exports <c>GIT_DIR</c> and friends into every process it starts, and those processes pass
/// them on to their own children. On 2026-09-15 that chain — pre-commit hook, <c>dotnet test</c>, this
/// project's real-git fixtures — pointed the fixtures' git at the developer's live repository: one
/// <c>git init</c> re-initialised it as bare (breaking every worktree in it) and one <c>push</c> put
/// five fixture commits on the shared default branch.
/// <para>
/// Each test below sets one inherited variable to a DECOY repository and asserts the command landed on
/// its working directory instead. Without the scrub in
/// <see cref="HostGitCommandRunner.InheritedRepositoryScope"/> both fail on the decoy's state, which is
/// exactly the incident in miniature.
/// </para>
/// </summary>
[Collection(GitEnvironmentMutatingCollection.Name)]
public sealed class HostGitCommandRunnerEnvScrubTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crd-gitenv-" + Guid.NewGuid().ToString("N"));

    public HostGitCommandRunnerEnvScrubTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    private static HostGitCommandRunner Runner() =>
        new(_ => Task.FromResult<IReadOnlyList<GitProviderToken>>([]), NullLogger<HostGitCommandRunner>.Instance);

    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        _ = Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task RunAsync_WithInheritedGitDir_InitialisesTheWorkingDirectoryNotTheInheritedRepository()
    {
        var runner = Runner();
        var decoy = NewDirectory("decoy");
        (await runner.RunAsync(new SandboxCommand(["git", "init"], decoy), default)).ExitCode.Should().Be(0);
        var decoyConfigBefore = await File.ReadAllTextAsync(Path.Combine(decoy, ".git", "config"));

        var target = NewDirectory("target");
        var restore = Environment.GetEnvironmentVariable("GIT_DIR");
        Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(decoy, ".git"));
        try
        {
            // `--bare` is the incident's exact argv: re-initialising an existing repository this way is
            // what wrote core.bare=true into a live checkout, so asserting on the decoy's config below
            // pins the damage, not merely the redirection.
            var result = await runner.RunAsync(new SandboxCommand(["git", "init", "--bare"], target), default);

            result.ExitCode.Should().Be(0);
            Directory
                .Exists(Path.Combine(target, "objects"))
                .Should()
                .BeTrue("the repository must be created in the command's working directory");
            var decoyConfigAfter = await File.ReadAllTextAsync(Path.Combine(decoy, ".git", "config"));
            decoyConfigAfter.Should().Be(decoyConfigBefore, "the inherited repository must not be touched at all");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DIR", restore);
        }
    }

    [Fact]
    public async Task RunAsync_WithInheritedGitIndexFile_ReadsTheWorkingDirectorysOwnIndex()
    {
        var runner = Runner();
        var repo = NewDirectory("repo");
        (await runner.RunAsync(new SandboxCommand(["git", "init"], repo), default)).ExitCode.Should().Be(0);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "content\n");
        (await runner.RunAsync(new SandboxCommand(["git", "add", "tracked.txt"], repo), default))
            .ExitCode.Should()
            .Be(0);

        var restore = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(_root, "someone-elses-index"));
        try
        {
            var result = await runner.RunAsync(new SandboxCommand(["git", "status", "--porcelain"], repo), default);

            result.ExitCode.Should().Be(0);
            // An inherited index file is empty here, so git would report the staged file as untracked
            // ("?? tracked.txt") — and a command that stages or commits would write into someone else's
            // index rather than this repository's.
            result.Stdout.Should().Contain("A  tracked.txt");
            result.Stdout.Should().NotContain("??");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", restore);
        }
    }
}
