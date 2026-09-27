using System.Diagnostics;
using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Black-box proof that a malformed scoped-command prefix (e.g. <c>--run-pr</c> with the wrong number of
/// arguments) can never reach host startup (security review round 2: "Add a test that malformed scoped
/// prefixes cannot reach host startup"). <c>ScopedCommandLineTests</c>
/// (<c>Orchestration.ScopedCommandLine.Parse</c>) already pins the pure parse result; this exercises the
/// REAL entry point as a subprocess, because that is the only way to observe that <c>Program.cs</c> exits
/// before
/// <c>WebApplication.CreateBuilder</c>/<c>app.Build()</c>/<c>app.StartAsync()</c> are ever reached — no
/// in-process host (including <c>WebApplicationFactory&lt;Program&gt;</c>) invokes <c>Main</c> with our
/// actual argv, so it cannot exercise this branch.
/// <para>
/// A process that DID reach <c>app.StartAsync()</c>/<c>app.Run()</c> would keep running (nothing after that
/// point returns for a malformed argv) rather than exit on its own — so a bounded wait that observes a
/// clean, prompt exit is itself part of the proof, not just the exit code.
/// </para>
/// </summary>
public sealed class ProgramArgvCompositionTests
{
    private const int ExpectedUsageExitCode = 64; // EX_USAGE

    [Theory]
    [InlineData("--run-pr", "acme/widgets")]
    [InlineData("--workflow-operation")]
    [InlineData("--list-candidate-prs", "unexpected-extra-arg")]
    [InlineData("--redo-artifact-branch", "acme/widgets")]
    [InlineData("--redo-artifact-branch", "acme/widgets", "7", "unexpected-extra-arg")]
    public async Task Exits_with_a_usage_failure_before_any_host_or_provider_is_built(params string[] args)
    {
        using var process = StartDaemon(args);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // A malformed argv that is still running after 20s did not exit before host startup — the
            // opposite of what this test exists to prove. Kill it so the test run does not hang, then fail
            // with a message that says what actually happened rather than just timing out silently.
            TryKill(process);
            Assert.Fail(
                $"'{string.Join(' ', args)}' did not exit within 20s — a malformed scoped command must fail "
                    + "usage before any host is started, not hang serving one."
            );
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        process.ExitCode.Should().Be(ExpectedUsageExitCode, because: $"stderr was: {stderr}");
        stderr.Should().Contain(args[0], "the usage message must name the mistyped scoped command");
        stdout.Should().BeEmpty("a usage failure must never emit the JSON output a well-formed scoped command would");
        stdout.Should().NotContain("Now listening on", "the host must never have started Kestrel for a malformed argv");
    }

    private static Process StartDaemon(string[] args)
    {
        var dllPath = typeof(CodeReviewDaemonOptions).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dllPath)!,
        };
        startInfo.ArgumentList.Add(dllPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the daemon process.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout firing and the kill attempt — nothing to do.
        }
    }
}
