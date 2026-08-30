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
}
