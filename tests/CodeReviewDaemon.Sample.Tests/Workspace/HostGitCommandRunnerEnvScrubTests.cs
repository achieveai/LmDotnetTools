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
/// The first tests set one inherited variable to a DECOY repository and assert the command landed on
/// its working directory instead — the incident in miniature. The rest inherit a remote rewrite plus a
/// helper (ssh command, askpass) or extra config, and assert git used the named remote, ran no helper, and
/// kept only the runner's own auth config. All of them fail without the scrub selected by
/// <see cref="HostGitCommandRunner.IsInheritedGitControl"/>.
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

    [Fact]
    public async Task RunAsync_WithInheritedRemoteRewriteAndSshCommand_UsesTheNamedRemoteAndNeverRunsTheHelper()
    {
        var runner = Runner();
        var (remoteUrl, head) = await NewLocalRemoteAsync(runner);
        var marker = Path.Combine(_root, "ssh-helper-ran");

        // The review finding's repro, made offline: an inherited insteadOf turns the command's remote into an
        // ssh URL, and an inherited GIT_SSH_COMMAND is then the program git runs to reach it.
        using var inherited = Inherit(
            ("GIT_CONFIG_PARAMETERS", "'url.ssh://git@invalid.local/.insteadOf=file:///'"),
            ("GIT_SSH_COMMAND", $"echo ran >> '{ShellPath(marker)}'; false")
        );

        var result = await runner.RunAsync(new SandboxCommand(["git", "ls-remote", remoteUrl], _root), default);

        File.Exists(marker).Should().BeFalse("an inherited GIT_SSH_COMMAND must never be executed");
        result.ExitCode.Should().Be(0, "git must reach the remote it was given: {0}", result.Stderr);
        result.Stdout.Should().Contain(head, "the answer must come from the named remote, not a rewritten one");
    }

    [Fact]
    public async Task RunAsync_WithInheritedRemoteRewriteAndAskpass_UsesTheNamedRemoteAndNeverRunsTheHelper()
    {
        var runner = Runner();
        var (remoteUrl, head) = await NewLocalRemoteAsync(runner);
        var marker = Path.Combine(_root, "askpass-helper-ran");
        var askpass = Path.Combine(_root, "askpass.sh");
        await File.WriteAllTextAsync(askpass, $"#!/bin/sh\necho ran >> '{ShellPath(marker)}'\necho stolen\n");

        // git asks for a credential only after a server answers 401, so a loopback listener that answers
        // nothing else stands in for one. The inherited `credential.helper=` reset keeps a machine-level helper
        // (Git Credential Manager) from answering first and hiding the askpass call. The WHOLE URL is
        // rewritten rather than a prefix: appending a temp path that holds a space to an http URL makes curl
        // reject it before any request is made, which would leave this test unable to fail.
        using var server = new UnauthorizedHttpServer();
        using var inherited = Inherit(
            ("GIT_CONFIG_PARAMETERS", $"'url.{server.Url}repo.insteadOf={remoteUrl}' 'credential.helper='"),
            ("GIT_ASKPASS", askpass),
            ("SSH_ASKPASS", askpass)
        );

        var result = await runner.RunAsync(new SandboxCommand(["git", "ls-remote", remoteUrl], _root), default);

        File.Exists(marker).Should().BeFalse("an inherited GIT_ASKPASS/SSH_ASKPASS must never be executed");
        result.ExitCode.Should().Be(0, "git must reach the remote it was given: {0}", result.Stderr);
        result.Stdout.Should().Contain(head, "the answer must come from the named remote, not a rewritten one");
    }

    [Fact]
    public async Task RunAsync_WithInheritedGitConfig_KeepsOnlyTheRunnersOwnAuthHeader()
    {
        var runner = new HostGitCommandRunner(
            _ => Task.FromResult<IReadOnlyList<GitProviderToken>>([new GitProviderToken("github", "t")]),
            NullLogger<HostGitCommandRunner>.Instance
        );
        var expectedHeader = HostGitCredentialEnv.Build("t")["GIT_CONFIG_VALUE_0"];

        // extraHeader is multi-valued, so an inherited one is not overridden by the runner's — it would be SENT
        // alongside it, to the host the runner authenticates.
        using var inherited = Inherit(
            ("GIT_CONFIG_PARAMETERS", "'http.https://github.com/.extraHeader=X-Inherited: parameters'")
        );

        var result = await runner.RunAsync(
            new SandboxCommand(["git", "config", "--get-all", "http.https://github.com/.extraheader"], _root),
            default
        );

        result.ExitCode.Should().Be(0, "the runner's own header must still be configured: {0}", result.Stderr);
        result.Stdout.Should().Contain(expectedHeader);
        result.Stdout.Should().NotContain("X-Inherited");
    }

    /// <summary>
    /// A committed repository addressed by <c>file://</c> URL, which is the remote a test names and the prefix
    /// an inherited rewrite hijacks. Built before any inherited variable is set, so setup is never affected.
    /// </summary>
    private async Task<(string RemoteUrl, string Head)> NewLocalRemoteAsync(HostGitCommandRunner runner)
    {
        var repo = NewDirectory("remote");
        (await runner.RunAsync(new SandboxCommand(["git", "init"], repo), default)).ExitCode.Should().Be(0);
        (
            await runner.RunAsync(
                new SandboxCommand(
                    ["git", "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-m", "init"],
                    repo
                ),
                default
            )
        )
            .ExitCode.Should()
            .Be(0);
        var head = await runner.RunAsync(new SandboxCommand(["git", "rev-parse", "HEAD"], repo), default);
        head.ExitCode.Should().Be(0);
        return ("file:///" + ShellPath(repo), head.Stdout.Trim());
    }

    private static string ShellPath(string path) => path.Replace('\\', '/');

    /// <summary>Sets process environment variables for one test and restores their previous values.</summary>
    private static InheritedEnvironment Inherit(params (string Name, string Value)[] variables) => new(variables);

    /// <summary>
    /// A loopback HTTP endpoint that answers every request with a Basic-auth challenge — the one response that
    /// makes git ask for a credential. Raw TCP rather than <c>HttpListener</c>, which needs a URL reservation.
    /// </summary>
    private sealed class UnauthorizedHttpServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public UnauthorizedHttpServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}/";
            _loop = ServeAsync(_stop.Token);
        }

        public string Url { get; }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            var challenge = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"test\"\r\n"
                    + "Content-Length: 0\r\nConnection: close\r\n\r\n"
            );
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var request = new System.Text.StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        _ = request.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await stream.WriteAsync(challenge, cancellationToken);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) { }

            _stop.Dispose();
        }
    }

    private sealed class InheritedEnvironment : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _previous = [];

        public InheritedEnvironment((string Name, string Value)[] variables)
        {
            foreach (var (name, value) in variables)
            {
                _previous.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
