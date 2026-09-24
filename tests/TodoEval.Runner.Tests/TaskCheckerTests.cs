using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The J1 layer's one rule: a checker may report a verdict, and may report that it could not judge,
/// but must never be able to FAIL a run by failing itself. Every mapping below is that rule.
/// </summary>
public class TaskCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"todo-eval-j1-{Guid.NewGuid():N}");

    public TaskCheckerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteScore(string json)
    {
        var path = Path.Combine(_dir, "score.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void CleanExitWithAPassingScore_IsAPass()
    {
        var path = WriteScore(
            """
            { "task": "c1", "outcome": "pass", "score": 1.0,
              "checks": [ { "name": "test_one", "pass": true }, { "name": "test_two", "pass": true } ] }
            """
        );

        var result = J1Result.From(exitCode: 0, path);

        result.Outcome.Should().Be("pass");
        result.Score.Should().Be(1.0);
        result.FailedChecks.Should().BeEmpty();
        result.Judged.Should().BeTrue();
    }

    [Fact]
    public void APartialScore_NamesTheChecksThatDidNotPass()
    {
        var path = WriteScore(
            """
            { "task": "c1", "outcome": "partial", "score": 0.5,
              "checks": [ { "name": "test_one", "pass": true },
                          { "name": "test_two", "pass": false, "detail": "AssertionError" } ] }
            """
        );

        var result = J1Result.From(exitCode: 0, path);

        result.Outcome.Should().Be("partial");
        result.Score.Should().Be(0.5);
        result.FailedChecks.Should().Equal("test_two");
    }

    [Fact]
    public void ExitTwo_IsUnjudgedAndSaysSo()
    {
        // The contract's "I could not judge this". Never a fail: the run may have been perfect.
        var path = WriteScore("""{ "outcome": "pass", "score": 1.0 }""");

        var result = J1Result.From(J1Result.CouldNotJudgeExitCode, path);

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Score.Should().BeNull();
        result.Judged.Should().BeFalse();
        result.Error.Should().Contain("could not judge");
    }

    [Fact]
    public void AnyOtherNonZeroExit_IsAlsoUnjudged()
    {
        var result = J1Result.From(exitCode: 1, Path.Combine(_dir, "score.json"), "NullReferenceException");

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("exited 1").And.Contain("NullReferenceException");
    }

    [Fact]
    public void CleanExitButNoScoreFile_IsUnjudged()
    {
        var result = J1Result.From(exitCode: 0, Path.Combine(_dir, "absent.json"));

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("no score file");
    }

    [Fact]
    public void AnUnparseableScoreFile_IsUnjudged()
    {
        var result = J1Result.From(exitCode: 0, WriteScore("this is not json {{{"));

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("not a readable score file");
    }

    [Fact]
    public void AScoreFileWithoutAnOutcome_IsUnjudged()
    {
        // A score with no verdict cannot be turned into one; a 0.0 here would read as total failure.
        var result = J1Result.From(exitCode: 0, WriteScore("""{ "task": "c1", "score": 0.75 }"""));

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Score.Should().BeNull();
    }

    [Fact]
    public void AnOutcomeWithoutAScore_KeepsTheOutcomeAndReportsNoNumber()
    {
        var result = J1Result.From(exitCode: 0, WriteScore("""{ "outcome": "fail", "checks": [] }"""));

        result.Outcome.Should().Be("fail");
        result.Score.Should().BeNull("an absent number is not a zero");
    }

    [Fact]
    public async Task ATaskWithNoCheckerHasNoJ1AtAll()
    {
        // Distinct from unjudged: there is nothing to judge with, so the run claims no J1 either way.
        var task = new EvalTaskAsset
        {
            Id = "no-checker",
            Dir = _dir,
            Template = "Do {TOPIC}.",
            ExpectedBoard = null,
        };

        var result = await new PwshTaskChecker(TextWriter.Null).JudgeAsync(task, _dir, _dir, CancellationToken.None);

        result.Should().BeNull();
    }

    /// <summary>
    /// Writes a <c>check.ps1</c> that starts a second pwsh, records both pids, and then blocks for
    /// long enough that only a kill ends it. The child is the point: killing the checker alone leaves
    /// it running, so a test that watched one pid could not tell a process kill from a tree kill.
    /// </summary>
    private void WriteBlockingChecker(string pidFile)
    {
        File.WriteAllText(
            Path.Combine(_dir, "check.ps1"),
            $$"""
            param($Workspace, $Out)
            $child = Start-Process pwsh -PassThru -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 45'
            Set-Content -Path '{{pidFile}}' -Value @($PID, $child.Id)
            Start-Sleep -Seconds 45
            """
        );
    }

    private EvalTaskAsset BlockingTask() =>
        new()
        {
            Id = "slow-checker",
            Dir = _dir,
            Template = "Do {TOPIC}.",
            ExpectedBoard = null,
        };

    [Fact]
    public async Task CancellingTheSweep_KillsTheCheckerAndEverythingItStarted()
    {
        // The outer token used to reach the wait only through the linked source, so a sweep abort
        // threw straight out of JudgeAsync with the checker still running. The checker holds the run's
        // workspace, which the sweep deletes next, so the orphan outlives the sweep that started it.
        var pidFile = Path.Combine(_dir, "pids.txt").Replace("\\", "/");
        WriteBlockingChecker(pidFile);

        using var cts = new CancellationTokenSource();
        var judging = new PwshTaskChecker(TextWriter.Null).JudgeAsync(BlockingTask(), _dir, _dir, cts.Token);

        var pids = await WaitForPids(pidFile, judging);
        await cts.CancelAsync();

        var act = async () => await judging;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();

        await AssertReaped(pids);
    }

    [Fact]
    public async Task TheTimeout_AlsoKillsTheCheckerAndEverythingItStarted_AndStillReportsUnjudged()
    {
        // The other branch into the same kill. It is reachable here only because the timeout is a
        // TimeSpan rather than a count of minutes; asserting the tree contract on one branch and not
        // the other would leave half of it untested. A hung checker must never FAIL a run either --
        // the run itself may have been perfect -- so the verdict is still "could not judge".
        var pidFile = Path.Combine(_dir, "timeout-pids.txt").Replace("\\", "/");
        WriteBlockingChecker(pidFile);

        // The budget runs on a clock this test advances, and only once the checker has written its pids:
        // on a wall clock, a loaded machine spends three seconds starting pwsh, the checker is killed
        // before it exists, and the test reports "finished before it wrote its pids" for a contract that
        // held. The budget is still the checker's own timeout branch; only WHEN it elapses is ours.
        var clock = new FakeTimeProvider();
        var judging = new PwshTaskChecker(TextWriter.Null, TimeSpan.FromSeconds(3), clock).JudgeAsync(
            BlockingTask(),
            _dir,
            _dir,
            CancellationToken.None
        );

        var pids = await WaitForPids(pidFile, judging);
        clock.Advance(TimeSpan.FromSeconds(3));
        var result = await judging;

        result!.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("did not finish within");
        await AssertReaped(pids);
    }

    private static async Task AssertReaped(IReadOnlyList<int> pids)
    {
        foreach (var pid in pids)
        {
            (await WaitForExit(pid)).Should().BeTrue($"process {pid} in the checker's tree must be reaped");
        }
    }

    [Fact]
    public async Task WaitForPids_RetriesWhileTheCheckerHasItsPidFileOpen()
    {
        var pidFile = Path.Combine(_dir, "locked-pids.txt");
        Task<int[]> waiting;
        using (var lockedFile = new FileStream(pidFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(lockedFile, System.Text.Encoding.UTF8, 1024, leaveOpen: true))
            {
                await writer.WriteLineAsync("123");
                await writer.WriteLineAsync("456");
                await writer.FlushAsync();
            }

            waiting = WaitForPids(pidFile, new TaskCompletionSource<J1Result?>().Task);
            await Task.Delay(200);
            waiting.IsCompleted.Should().BeFalse("the checker is still writing its pid file");
        }

        (await waiting).Should().Equal(123, 456);
    }

    /// <summary>The checker's own pid and its child's, once the script has written both.</summary>
    private static async Task<int[]> WaitForPids(string pidFile, Task<J1Result?> judging)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (judging.IsCompleted)
            {
                // Surfaces a launch failure ("could not launch the checker") as itself rather than as
                // a timeout, and rethrows anything the judge faulted with.
                var early = await judging;
                throw new InvalidOperationException($"the checker finished before it wrote its pids: {early?.Error}");
            }

            if (File.Exists(pidFile))
            {
                try
                {
                    var lines = File.ReadAllLines(pidFile).Where(l => l.Trim().Length > 0).ToArray();
                    if (lines.Length == 2 && lines.All(l => int.TryParse(l.Trim(), out _)))
                    {
                        return [.. lines.Select(l => int.Parse(l.Trim()))];
                    }
                }
                catch (IOException)
                {
                    // The checker may still hold an exclusive lock during its first write.
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"the checker never wrote both pids to '{pidFile}'.");
    }

    private static async Task<bool> WaitForExit(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // Gone entirely.
            }

            await Task.Delay(100);
        }

        return false;
    }
}
