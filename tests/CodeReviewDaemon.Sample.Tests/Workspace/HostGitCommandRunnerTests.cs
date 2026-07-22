using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class HostGitCommandRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crd-hostgit-" + Guid.NewGuid().ToString("N"));

    public HostGitCommandRunnerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Func<CancellationToken, Task<IReadOnlyList<GitProviderToken>>> GithubOnly(string token) =>
        _ => Task.FromResult<IReadOnlyList<GitProviderToken>>([new GitProviderToken("github", token)]);

    [Fact]
    public async Task RunAsync_GitInit_CreatesRepo()
    {
        var runner = new HostGitCommandRunner(GithubOnly("t"), NullLogger<HostGitCommandRunner>.Instance);

        var result = await runner.RunAsync(new SandboxCommand(["git", "init"], _dir), default);

        result.Succeeded.Should().BeTrue();
        Directory.Exists(Path.Combine(_dir, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WorkingDirectoryMissing_FailsGracefullyInsteadOfThrowing()
    {
        // Reproduces the sweeper's first-run probe: the checkout dir doesn't exist yet, so
        // Process.Start (which requires an existing WorkingDirectory) must never be reached.
        var missingDir = Path.Combine(_dir, "not-yet-cloned");
        var runner = new HostGitCommandRunner(GithubOnly("t"), NullLogger<HostGitCommandRunner>.Instance);

        var result = await runner.RunAsync(
            new SandboxCommand(["git", "rev-parse", "--is-inside-work-tree"], missingDir),
            default);

        result.Succeeded.Should().BeFalse();
        result.Stderr.Should().Contain(missingDir);
    }

    [Fact]
    public async Task RunAsync_InjectsProviderExtraHeaders_ForEachSignedInProvider()
    {
        // Both GitHub and ADO signed in ⇒ git sees an extraHeader for each host (the ad-hoc GIT_CONFIG_*
        // env the runner injects), so a private clone on either host can authenticate.
        var runner = new HostGitCommandRunner(
            _ => Task.FromResult<IReadOnlyList<GitProviderToken>>(
                [new GitProviderToken("github", "gh"), new GitProviderToken("ado", "ado-tok")]),
            NullLogger<HostGitCommandRunner>.Instance);

        (await runner.RunAsync(new SandboxCommand(["git", "init"], _dir), default)).Succeeded.Should().BeTrue();

        var listed = await runner.RunAsync(
            new SandboxCommand(["git", "config", "--get-regexp", "extraheader"], _dir), default);

        listed.Succeeded.Should().BeTrue();
        listed.Stdout.Should().Contain("github.com");
        listed.Stdout.Should().Contain("dev.azure.com");
    }

    [Fact]
    public async Task HostFileSystem_WriteThenRead_RoundTrips()
    {
        var fs = new HostFileSystem();
        var path = Path.Combine(_dir, "sub", "a.txt");

        await fs.WriteFileAsync(path, "hello", default);

        (await fs.ReadFileAsync(path, default)).Should().Be("hello");
        (await fs.ReadFileAsync(Path.Combine(_dir, "missing.txt"), default)).Should().BeNull();
    }
}
