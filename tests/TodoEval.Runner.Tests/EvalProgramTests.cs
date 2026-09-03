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
}
