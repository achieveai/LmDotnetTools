using System.Diagnostics;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// <see cref="HostGitCommandRunner"/> must never report a command as having SUCCEEDED with output it did not
/// capture.
/// <para>
/// This is a race, so an ordinary test proves nothing about it: the pass and the defect look identical on
/// any single invocation. What makes this a control is that it REPRODUCED before the fix. Measured against
/// the production rate of 1 in 72 prepare cycles (run 200, 2026-08-08T02:13:18Z — <c>git rev-parse HEAD</c>
/// returning exit 0 with empty stdout AND empty stderr, which no git invocation produces), the arm is sized
/// for power rather than for a round number: at p = 1/72, P(at least one) = 1 − (1 − p)^n, so n = 300 gives
/// ≈ 98.5% and n = 100 only ≈ 75%. Report the arm size and the observed count, never just "green".
/// </para>
/// <para>
/// <c>git --version</c> is the probe on purpose: it is the fastest always-output git command there is, it
/// needs no repository, and a fast command is what maximises the window — the process can exit before the
/// reader task has been scheduled at all. Concurrency is the second half: the race needs the drain delayed,
/// and thread-pool pressure is what delays it.
/// </para>
/// </summary>
public sealed class HostGitCommandRunnerStressTests : IDisposable
{
    /// <summary>1 − (1 − 1/72)^300 ≈ 0.985. See the class remarks before lowering this.</summary>
    private const int Attempts = 300;

    /// <summary>
    /// How long the grandchild holds the pipe. Comfortably longer than the runner's 5s drain deadline, so
    /// "returned on the deadline" and "waited for the grandchild" are separated by 15s and cannot be
    /// confused under load.
    /// </summary>
    private static readonly TimeSpan S_lingeringGrandchild = TimeSpan.FromSeconds(20);

    private readonly ProcessLab _lab = new();

    public void Dispose() => _lab.Dispose();

    private static HostGitCommandRunner CreateRunner() =>
        new(_ => Task.FromResult<IReadOnlyList<GitProviderToken>>([]), NullLogger<HostGitCommandRunner>.Instance);

    [Fact]
    public async Task RunAsync_UnderConcurrency_NeverReportsExitZeroWithNoOutput()
    {
        var runner = CreateRunner();
        var results = new SandboxCommandResult[Attempts];

        await Parallel.ForAsync(
            0,
            Attempts,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
            async (i, ct) =>
            {
                results[i] = await runner.RunAsync(new SandboxCommand(["git", "--version"]), ct);
            }
        );

        // The positive control: `git --version` always prints. If NOTHING succeeded, the harness is
        // measuring a broken environment rather than the race, and a green below would be vacuous.
        var succeeded = results.Count(r => r.ExitCode == 0);
        succeeded
            .Should()
            .BeGreaterThan(0, "the probe must actually run, or a 'no empty captures' result means nothing");

        var lostCaptures = results
            .Select((r, i) => (Index: i, r.ExitCode, Stdout: r.Stdout ?? string.Empty))
            .Where(x => x.ExitCode == 0 && x.Stdout.Trim().Length == 0)
            .ToList();

        lostCaptures
            .Should()
            .BeEmpty(
                "a command that exits 0 has produced its output, so the runner must have captured it "
                    + $"({succeeded}/{Attempts} exited 0; {lostCaptures.Count} of those returned nothing)"
            );
    }

    /// <summary>
    /// The other half of the defect, and the hazard the first fix INTRODUCED: draining to EOF after the
    /// child exits is only safe if it is bounded.
    /// <para>
    /// "The process has exited so both pipes are at EOF" is false. A pipe reaches EOF when the LAST write
    /// handle closes, so anything that inherited the handle keeps it open after the child is gone.
    /// <see cref="ProcessLab.EchoLeavingAGrandchildHoldingThePipe"/> reproduces exactly that shape on both
    /// platforms: the shell exits at once, the backgrounded child inherits stdout and holds it. Without a
    /// deadline the runner would block for the grandchild's whole lifetime, turning a silent truncation into
    /// a silent hang on a path the daemon runs constantly. (<c>gc --auto</c> looks like the obvious
    /// real-world case and is NOT one — it detaches with its stdio redirected to .git/gc.log, so it never
    /// inherits ours.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenAGrandchildHoldsThePipeOpen_FailsLoudlyRatherThanWaitingForIt()
    {
        var runner = CreateRunner();
        var started = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            new SandboxCommand(_lab.EchoLeavingAGrandchildHoldingThePipe("hello", S_lingeringGrandchild)),
            CancellationToken.None
        );

        started.Stop();

        started
            .Elapsed.Should()
            .BeLessThan(
                S_lingeringGrandchild,
                "the drain is bounded, so it must return while the grandchild is still holding the pipe — "
                    + "waiting it out is the hang this deadline exists to prevent"
            );
        result
            .Succeeded.Should()
            .BeFalse(
                "once a write handle outlives the child there is no way to know the capture is complete, and "
                    + "'possibly truncated' reported as success is the defect this exists to remove"
            );
        result
            .Stderr.Should()
            .Contain("incomplete", "the condition has to name itself, or it is just a different silence");
        result
            .Stdout.Should()
            .Contain("hello", "what was read is still returned as evidence — it is the completeness that is in doubt");
    }

    /// <summary>
    /// The control for the test above: an ordinary command whose children all exit must NOT trip the
    /// deadline. Without this, a drain that simply always failed would satisfy the grandchild test.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenNothingHoldsThePipe_DrainsImmediatelyAndSucceeds()
    {
        var runner = CreateRunner();
        var started = Stopwatch.StartNew();

        var result = await runner.RunAsync(new SandboxCommand(_lab.Echo("hello")), CancellationToken.None);

        started.Stop();

        result.Succeeded.Should().BeTrue("nothing held the pipe, so the drain reached EOF naturally");
        result.Stdout.Trim().Should().Be("hello");
        started
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(5),
                "a normal command must not pay the drain deadline — if it does, the drain is not detecting EOF"
            );
    }
}
