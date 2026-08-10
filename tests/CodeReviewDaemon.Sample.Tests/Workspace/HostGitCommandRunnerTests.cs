using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
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

        (await fs.ReadFileAsync(path, SandboxReadLimits.RepositoryFileBytes, default))
            .Content.Should().Be("hello");
        (await fs.ReadFileAsync(Path.Combine(_dir, "missing.txt"), SandboxReadLimits.RepositoryFileBytes, default))
            .Should().Be(SandboxFileRead.Missing);
    }

    [Fact]
    public async Task HostFileSystem_RefusesAFileLargerThanTheCallersCeiling()
    {
        var fs = new HostFileSystem();
        var path = Path.Combine(_dir, "big.txt");
        await fs.WriteFileAsync(path, new string('x', 4096), default);

        var read = await fs.ReadFileAsync(path, 1024, default);

        // Refused, and distinguishable from absent: a caller that cannot tell them apart re-seeds a store,
        // or tells a reviewer the Knowledge Base is empty, over the top of a file that is right there.
        read.Should().Be(SandboxFileRead.Refused);
        read.Content.Should().BeNull();
        read.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task HostFileSystem_ReadsAFileExactlyAtTheCeiling()
    {
        // The boundary is inclusive: maxBytes is what the caller agreed to read, not one less. An
        // off-by-one here refuses legitimate files and is invisible until a store reaches a round number.
        var fs = new HostFileSystem();
        var path = Path.Combine(_dir, "exact.txt");
        await fs.WriteFileAsync(path, new string('x', 1024), default);

        var read = await fs.ReadFileAsync(path, 1024, default);

        read.Content.Should().HaveLength(1024);
    }

    // ── Bounding a host git command (task 28) ────────────────────────────────────────────────────
    //
    // The daemon ran a 969,911-object fetch into a 2.3 GB store with NO timeout of any kind and no output
    // whatsoever, single-threaded through the whole process. From the log it was indistinguishable from
    // dead, and it was twice diagnosed as hung when it was working. Meanwhile the SANDBOX path has bounded
    // every command at 5 minutes since it was written; only the host path was unbounded.

    private static HostGitCommandRunner Runner(
        ILogger<HostGitCommandRunner>? logger = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? maxDuration = null) =>
        new(
            GithubOnly("t"),
            logger ?? NullLogger<HostGitCommandRunner>.Instance,
            adoOrgs: null,
            idleTimeout: idleTimeout,
            maxDuration: maxDuration);

    /// <summary>
    /// A command that goes SILENT past the idle timeout is killed and reported as a failure — not left to
    /// hold the daemon forever. The failure is returned as a non-zero result rather than thrown, so it
    /// travels the same path a real network failure already takes (<c>RunGitOrThrowAsync</c> →
    /// <c>InvalidOperationException</c> → <c>RetryPending</c>) and needs no new handling anywhere.
    /// </summary>
    [Fact]
    public async Task RunAsync_kills_and_fails_a_command_that_goes_silent_past_the_idle_timeout()
    {
        var runner = Runner(idleTimeout: TimeSpan.FromMilliseconds(300));
        var started = DateTimeOffset.UtcNow;

        var result = await runner.RunAsync(new SandboxCommand(["sleep", "30"], _dir), default);

        result.Succeeded.Should().BeFalse("a command nothing can be learned from must not run unbounded");
        result.Stderr.Should().Contain("idle", "the operator has to be able to tell a timeout from a git error");
        (DateTimeOffset.UtcNow - started).Should().BeLessThan(
            TimeSpan.FromSeconds(15), "it must be killed near the timeout, not waited out");
    }

    /// <summary>
    /// The design call this whole fix rests on: the bound is INACTIVITY, not elapsed time. The live fetch was
    /// perfectly healthy eleven minutes in with <c>index-pack</c> burning CPU, so any total ceiling loose
    /// enough to allow that is useless as a stuck-detector, and any ceiling tight enough to catch stuck kills
    /// honest work on a 2.3 GB store. A command that keeps talking is working, however long it takes.
    /// <para>
    /// This is the test that fails if someone later "simplifies" the idle timer into a total one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_leaves_a_chatty_long_command_alone_because_the_bound_is_inactivity()
    {
        var runner = Runner(idleTimeout: TimeSpan.FromMilliseconds(600));

        // Runs ~1.2s — twice the idle timeout — but never silent for more than ~120ms.
        var result = await runner.RunAsync(
            new SandboxCommand(["sh", "-c", "for i in $(seq 1 10); do echo working; sleep 0.12; done"], _dir),
            default);

        result.Succeeded.Should().BeTrue(
            "the command outlived the idle timeout several times over while never once going quiet");
        result.Stdout.Should().Contain("working");
    }

    /// <summary>The absolute ceiling is the backstop for the case inactivity cannot catch: a remote that
    /// dribbles bytes forever, staying "active" indefinitely without ever finishing.</summary>
    [Fact]
    public async Task RunAsync_enforces_the_absolute_ceiling_even_while_output_keeps_arriving()
    {
        var runner = Runner(
            idleTimeout: TimeSpan.FromSeconds(30), maxDuration: TimeSpan.FromMilliseconds(500));

        var result = await runner.RunAsync(
            new SandboxCommand(["sh", "-c", "while true; do echo drip; sleep 0.05; done"], _dir),
            default);

        result.Succeeded.Should().BeFalse();
        result.Stderr.Should().Contain(
            "ceiling", "a ceiling stop and an idle stop have different causes and must read differently");
    }

    /// <summary>
    /// Caller cancellation is the daemon shutting down, which is NOT a run failure — it must stay
    /// distinguishable from a timeout. A timeout returns a failed result (the run retries); a shutdown
    /// throws (nobody is waiting for the answer).
    /// </summary>
    [Fact]
    public async Task RunAsync_rethrows_on_caller_cancellation_rather_than_reporting_a_failed_run()
    {
        var runner = Runner(idleTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => runner.RunAsync(new SandboxCommand(["sleep", "30"], _dir), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a shutdown is not a failed review — conflating them would mark runs RetryPending on every stop");
    }

    /// <summary>
    /// The child process must actually DIE on both exit paths. Today a cancelled <c>WaitForExitAsync</c>
    /// abandons the process: a shutdown mid-fetch leaves a 969,911-object fetch running detached, still
    /// holding the store. That is rare today because only shutdown cancels — but adding a timeout makes it
    /// reachable on every stuck operation, so shipping the bound without the kill would turn a rare leak
    /// into a routine one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_kills_the_child_rather_than_abandoning_it(bool byCallerCancellation)
    {
        var marker = Path.Combine(_dir, "still-alive.txt");
        // Writes the marker only if it survives well past the point it should have been killed (~300ms).
        var script = $"sleep 4; echo alive > {marker}";
        var runner = Runner(idleTimeout: TimeSpan.FromMilliseconds(300));

        if (byCallerCancellation)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                _ = await runner.RunAsync(new SandboxCommand(["sh", "-c", script], _dir), cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on this path; the point of the test is what happens to the child.
            }
        }
        else
        {
            _ = await runner.RunAsync(new SandboxCommand(["sh", "-c", script], _dir), default);
        }

        // Well past when the abandoned child would have written it.
        await Task.Delay(TimeSpan.FromSeconds(5));
        File.Exists(marker).Should().BeFalse(
            "the child outlived the call, so it was abandoned rather than killed — that is the orphaned "
                + "fetch that keeps holding the store after the daemon has moved on");
    }

    /// <summary>
    /// Legibility, and scoped so it does not become noise. Remote-talking verbs announce themselves at
    /// Information — the whole defect was a daemon that looked dead for eleven minutes to anyone reading the
    /// log, and Debug is off in the console sink, so Debug would have fixed nothing that actually went
    /// wrong. Local plumbing (<c>rev-parse</c>, <c>checkout</c>) stays silent: it is the overwhelming
    /// majority of git traffic and none of it is ever the thing you are waiting on.
    /// </summary>
    [Fact]
    public async Task RunAsync_announces_remote_operations_and_stays_quiet_for_local_plumbing()
    {
        var remoteLog = new CapturingLogger<HostGitCommandRunner>();
        var localLog = new CapturingLogger<HostGitCommandRunner>();
        _ = await Runner(remoteLog).RunAsync(new SandboxCommand(["git", "init"], _dir), default);

        // No 'origin' remote configured, so this fails fast — but it is still a remote-talking verb.
        _ = await Runner(remoteLog).RunAsync(new SandboxCommand(["git", "fetch", "origin"], _dir), default);
        _ = await Runner(localLog).RunAsync(
            new SandboxCommand(["git", "rev-parse", "--is-inside-work-tree"], _dir), default);

        remoteLog.MessagesAtLevel(LogLevel.Information).Should().Contain(
            m => m.Contains("fetch", StringComparison.Ordinal),
            "a long fetch that announces nothing is indistinguishable from a hung daemon");
        localLog.MessagesAtLevel(LogLevel.Information).Should().BeEmpty(
            "every rev-parse announcing itself would bury the one line that matters");
    }

    /// <summary>
    /// <c>--progress</c> is what makes a long fetch legible, but it writes thousands of lines to stderr —
    /// and stderr is what <c>RunGitOrThrowAsync</c> interpolates into the exception message a failure is
    /// diagnosed from. Unfiltered, a genuine "fatal: could not read from remote" would arrive buried in a
    /// wall of percentages, which would trade one illegible failure for another. Progress lines still stamp
    /// the activity clock and still feed the throttled log; they just do not accumulate.
    /// </summary>
    [Theory]
    [InlineData("Receiving objects:  12% (1000/8000), 1.5 MiB | 500 KiB/s", true)]
    [InlineData("remote: Counting objects: 45% (123/456)", true)]
    [InlineData("Resolving deltas: 100% (500/500), done.", true)]
    [InlineData("fatal: could not read from remote repository", false)]
    [InlineData("error: RPC failed; curl 56 Recv failure: Connection timed out", false)]
    [InlineData("", false)]
    public void ProgressLines_are_recognised_without_swallowing_real_errors(string line, bool isProgress)
    {
        HostGitCommandRunner.IsProgressLine(line).Should().Be(
            isProgress,
            "a filter that also ate 'fatal:' or 'curl 56' would hide exactly the lines run 123 was "
                + "diagnosed from");
    }

    /// <summary>
    /// Where <c>--progress</c> goes, for the verb that caused the outage.
    ///
    /// <c>git submodule update</c> writes nothing to a redirected stderr unless asked, so the idle-timeout
    /// watchdog read a healthy multi-gigabyte fetch as a hang and killed it at exactly 300s — twice in
    /// fifteen minutes, each kill abandoning a pack (4.68 GB, 5.85 GB, 0.70 GB). Asking for progress is what
    /// stops that.
    ///
    /// The placement is the whole test. <c>git submodule --progress update --init</c> — which is what
    /// inserting at the verb produces — is REJECTED by git 2.53.0 with a usage dump, so the naive version of
    /// this change would have broken every submodule command the daemon runs while reading like a logging
    /// improvement. git's own usage string does not document <c>--progress</c> on <c>update</c> at all, even
    /// though it is accepted; that was verified directly, against a positive control.
    /// </summary>
    [Fact]
    public void Submodule_update_takes_progress_after_the_SUBCOMMAND_not_after_the_verb()
    {
        string[] argv = ["git", "-c", "core.hooksPath=/dev/null", "submodule", "update", "--init", "--", "repos/Nova"];

        var result = HostGitCommandRunner.WithProgress(argv, "submodule");

        var progressIndex = result.ToList().IndexOf("--progress");
        var updateIndex = result.ToList().IndexOf("update");
        progressIndex.Should().Be(
            updateIndex + 1,
            "git rejects 'submodule --progress update' with a usage dump; the flag belongs to the subcommand");
        result.Should().ContainInOrder("submodule", "update", "--progress", "--init");
    }

    /// <summary>Only <c>update</c> fetches. Adding the flag to a subcommand that neither talks to a remote
    /// nor accepts it would turn a working command into a usage error.</summary>
    [Theory]
    [InlineData("sync")]
    [InlineData("status")]
    [InlineData("init")]
    public void Submodule_subcommands_that_do_not_fetch_get_no_progress_flag(string subcommand)
    {
        string[] argv = ["git", "submodule", subcommand];

        var result = HostGitCommandRunner.WithProgress(argv, "submodule");

        result.Should().NotContain("--progress");
        result.Should().Equal(argv);
    }

    /// <summary>The ordinary case still inserts at the verb — the submodule special case must not have
    /// moved it for everything else.</summary>
    [Fact]
    public void Fetch_still_takes_progress_immediately_after_the_verb()
    {
        string[] argv = ["git", "fetch", "origin", "main"];

        var result = HostGitCommandRunner.WithProgress(argv, "fetch");

        result.Should().Equal("git", "fetch", "--progress", "origin", "main");
    }

    /// <summary>An explicit choice by the caller wins. Appending a second <c>--progress</c>, or overriding a
    /// deliberate <c>--no-progress</c>, would both be the tool disagreeing with its own call site.</summary>
    [Theory]
    [InlineData("--progress")]
    [InlineData("--no-progress")]
    public void An_explicit_progress_choice_is_left_untouched(string flag)
    {
        string[] argv = ["git", "submodule", "update", flag, "--init"];

        var result = HostGitCommandRunner.WithProgress(argv, "submodule");

        result.Should().Equal(argv);
    }
}
