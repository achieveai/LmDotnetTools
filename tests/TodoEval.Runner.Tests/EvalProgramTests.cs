using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Exit-code policy pins (F-004): the archived baseline sweep gates the #620 merge through this
/// value, so a sweep in which nothing completed must never exit 0.
/// </summary>
public class EvalProgramTests
{
    private static RunManifestEntry Entry(string status, int seed = 0) =>
        new()
        {
            RunKey = $"m1/seed{seed}",
            Model = "m1",
            SeedIndex = seed,
            Topic = "t",
            Status = status,
        };

    [Fact]
    public void ComputeExitCode_AllRunsTimedOut_IsNonZero()
    {
        EvalProgram
            .ComputeExitCode([Entry(RunOutcomes.TimedOut, 0), Entry(RunOutcomes.TimedOut, 1)])
            .Should()
            .Be(3, "a sweep with zero completed runs is not a usable baseline");
    }

    [Fact]
    public void ComputeExitCode_AllRunsErroredOrInterrupted_IsNonZero()
    {
        EvalProgram.ComputeExitCode([Entry(RunOutcomes.Errored, 0), Entry(RunOutcomes.Interrupted, 1)]).Should().Be(3);
    }

    [Fact]
    public void ComputeExitCode_HarnessError_IsOne_EvenWithCompletedRuns()
    {
        EvalProgram
            .ComputeExitCode([Entry(RunOutcomes.Completed, 0), Entry(RunOutcomes.HarnessError, 1)])
            .Should()
            .Be(1);
    }

    [Fact]
    public void ComputeExitCode_AtLeastOneCompleted_NoHarnessError_IsZero()
    {
        EvalProgram.ComputeExitCode([Entry(RunOutcomes.Completed, 0), Entry(RunOutcomes.TimedOut, 1)]).Should().Be(0);
    }

    [Fact]
    public void HelpText_DocumentsTheExitCodes()
    {
        TodoEval.Runner.CliOptions.HelpText.Should().Contain("Exit codes");
    }

    // --- #677: the comparison's own exit codes --------------------------------------------------

    private static ComparisonReport Report(ComparisonRefusal refusal, params GateOutcome[] outcomes) =>
        new()
        {
            Refusal = refusal,
            Reason = refusal == ComparisonRefusal.None ? "" : "test",
            BaselineDirectory = "b",
            CandidateDirectory = "c",
            Deltas = refusal == ComparisonRefusal.None ? [] : null,
            Gates =
                refusal != ComparisonRefusal.None
                    ? null
                    :
                    [
                        .. outcomes.Select(
                            (outcome, i) =>
                                new GateResult
                                {
                                    GateId = $"gate-{i}",
                                    Description = "d",
                                    Outcome = outcome,
                                    Direction = GateDirection.AtMost,
                                }
                        ),
                    ],
        };

    [Fact]
    public void ComparisonExitCode_ARefusal_IsFive_NeverZero()
    {
        EvalProgram.ComparisonExitCode(Report(ComparisonRefusal.CorpusHashDiffers)).Should().Be(5);
    }

    [Fact]
    public void ComparisonExitCode_AFailedGate_IsFour()
    {
        EvalProgram
            .ComparisonExitCode(Report(ComparisonRefusal.None, GateOutcome.Passed, GateOutcome.Failed))
            .Should()
            .Be(4);
    }

    [Fact]
    public void ComparisonExitCode_EveryGatePassed_IsZero()
    {
        EvalProgram.ComparisonExitCode(Report(ComparisonRefusal.None, GateOutcome.Passed)).Should().Be(0);
    }

    /// <summary>
    /// A gate nothing could measure has not failed, so it does not turn the run red — but it is not a
    /// pass either, and the summary says so. Exit codes carry failures; "unproven" is carried by the
    /// report the reader actually reads.
    /// </summary>
    [Fact]
    public void ComparisonExitCode_AnUnmeasurableGate_DoesNotFailTheRun()
    {
        var report = Report(ComparisonRefusal.None, GateOutcome.Passed, GateOutcome.NotMeasurable);

        EvalProgram.ComparisonExitCode(report).Should().Be(0);
        report.AllGatesPassed.Should().BeFalse();
    }

    [Fact]
    public void ComparisonExitCode_NoComparisonRequested_IsZero()
    {
        EvalProgram.ComparisonExitCode(null).Should().Be(0);
    }

    /// <summary>A broken sweep's comparison means nothing, so the sweep's own outcome is reported first.</summary>
    [Fact]
    public void ComputeExitCode_AHarnessError_OutranksACleanComparison()
    {
        EvalProgram
            .ComputeExitCode(
                [Entry(RunOutcomes.Completed, 0), Entry(RunOutcomes.HarnessError, 1)],
                Report(ComparisonRefusal.None, GateOutcome.Passed)
            )
            .Should()
            .Be(1);
    }

    [Fact]
    public void ComputeExitCode_AGoodSweepWithARefusedComparison_IsFive()
    {
        EvalProgram
            .ComputeExitCode([Entry(RunOutcomes.Completed, 0)], Report(ComparisonRefusal.SpecVersionDiffers))
            .Should()
            .Be(5);
    }

    [Fact]
    public void HelpText_DocumentsTheComparisonExitCodes()
    {
        TodoEval.Runner.CliOptions.HelpText.Should().Contain("--compare").And.Contain("4").And.Contain("5");
    }

    [Fact]
    public void CliOptions_ParsesTheCompareBaselineDirectory()
    {
        TodoEval
            .Runner.CliOptions.Parse(["--compare", "evals/todo-eval/results/team-baseline"])
            .CompareBaselineDir.Should()
            .Be("evals/todo-eval/results/team-baseline");
    }

    // --- per-variant host diagnostics ----------------------------------------------------------

    /// <summary>
    /// Every variant launches its own host, so the host's stdout/stderr/publish logs must live in a
    /// per-variant directory: written straight into the sweep dir, each variant overwrote the last.
    /// </summary>
    [Fact]
    public void HostLogDir_IsPerVariant_UnderTheSweepsHostsFolder()
    {
        var sweepDir = Path.Combine("results", "20260916-000000-abcd1234");

        var off = EvalProgram.HostLogDir(sweepDir, "off");
        var compact = EvalProgram.HostLogDir(sweepDir, "compact-v0");

        off.Should().Be(Path.Combine(sweepDir, "hosts", "off"));
        compact.Should().Be(Path.Combine(sweepDir, "hosts", "compact-v0"));
        off.Should().NotBe(compact, "two variants must never share a host log file");
    }

    [Fact]
    public void HelpText_DocumentsThePerVariantHostLogs()
    {
        TodoEval.Runner.CliOptions.HelpText.Should().Contain("hosts/<variant>/");
    }

    /// <summary>
    /// The host writes its own Serilog files under <c>{instanceDir}/logs</c>; the instance dir is
    /// deleted after the sweep, so those logs must be copied out FIRST or an errored run's only
    /// server-side trace is gone with it.
    /// </summary>
    [Fact]
    public void RetireInstance_CopiesTheHostsOwnLogsOut_BeforeDeletingTheInstanceDir()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"todo-eval-retire-{Guid.NewGuid():N}");
        var instanceDir = Path.Combine(scratch, "instance");
        var hostLogDir = Path.Combine(scratch, "sweep", "hosts", "off");
        Directory.CreateDirectory(Path.Combine(instanceDir, "logs", "nested"));
        File.WriteAllText(Path.Combine(instanceDir, "logs", "lmstreaming-20260916.jsonl"), "{\"@l\":\"Warning\"}");
        File.WriteAllText(Path.Combine(instanceDir, "logs", "nested", "codex-rpc.jsonl"), "{}");
        File.WriteAllText(Path.Combine(instanceDir, "LmStreaming.Sample.dll"), "binary, not a log");

        try
        {
            EvalProgram.RetireInstance(instanceDir, hostLogDir, TextWriter.Null);

            Directory.Exists(instanceDir).Should().BeFalse("the temp host instance is still cleaned up");
            var archived = Path.Combine(hostLogDir, "instance-logs");
            File.ReadAllText(Path.Combine(archived, "lmstreaming-20260916.jsonl")).Should().Be("{\"@l\":\"Warning\"}");
            File.Exists(Path.Combine(archived, "nested", "codex-rpc.jsonl")).Should().BeTrue();
            File.Exists(Path.Combine(archived, "LmStreaming.Sample.dll")).Should().BeFalse("only logs/ is archived");
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>A host that never wrote a logs/ folder is retired the same way, with nothing archived.</summary>
    [Fact]
    public void RetireInstance_NoLogsFolder_StillDeletesTheInstanceDir()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"todo-eval-retire-{Guid.NewGuid():N}");
        var instanceDir = Path.Combine(scratch, "instance");
        var hostLogDir = Path.Combine(scratch, "sweep", "hosts", "off");
        Directory.CreateDirectory(instanceDir);

        try
        {
            EvalProgram.RetireInstance(instanceDir, hostLogDir, TextWriter.Null);

            Directory.Exists(instanceDir).Should().BeFalse();
            Directory.Exists(Path.Combine(hostLogDir, "instance-logs")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }
}
