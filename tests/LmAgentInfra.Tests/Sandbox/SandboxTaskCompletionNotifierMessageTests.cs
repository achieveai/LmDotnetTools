using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// The notice a background task's end puts in front of the model: what happened, for how long, the output
/// tails, and when to read the rest. A self-ended task's exit code is always 0 from the gateway, so it must not
/// be presented as success.
/// </summary>
public class SandboxTaskCompletionNotifierMessageTests
{
    [Fact]
    public void Completed_SaysTheExitCodeIsNotReal_AndCarriesBothTails()
    {
        var message = SandboxTaskCompletionNotifier.BuildMessage(
            Completion("completed", exitCode: 0, stdout: "built\n", stderr: "warning: x\n")
        );

        message.NotifyKind.Should().Be(SandboxTaskCompletionNotifier.NotifyKind);
        message.Label.Should().Be("Task 3 completed");
        message.Text.Should().Contain("Background task 3 has ended: it exited.");
        message.Text.Should().Contain("judge success from the output");
        message.Text.Should().NotContain("exit code 0");
        message.Text.Should().Contain("It ran for 1.5 s.");
        message.Text.Should().Contain("stdout (last part):\nbuilt");
        message.Text.Should().Contain("stderr (last part):\nwarning: x");
        message.Text.Should().NotContain("TaskOutput");
    }

    [Fact]
    public void Terminated_SaysItWasKilled()
    {
        var message = SandboxTaskCompletionNotifier.BuildMessage(Completion("terminated", exitCode: null));

        message.Text.Should().Contain("Background task 3 has ended: it was killed.");
        message.Label.Should().Be("Task 3 terminated");
    }

    [Fact]
    public void Failed_NamesTheExitCode_OrSaysItIsUnknown()
    {
        SandboxTaskCompletionNotifier
            .BuildMessage(Completion("failed", exitCode: 2))
            .Text.Should()
            .Contain("has ended: failed (exit code 2).");
        SandboxTaskCompletionNotifier
            .BuildMessage(Completion("failed", exitCode: null))
            .Text.Should()
            .Contain("has ended: failed (exit code unknown).");
    }

    [Fact]
    public void TruncatedOutput_PointsTheModelAtTaskOutput()
    {
        var message = SandboxTaskCompletionNotifier.BuildMessage(
            Completion("completed", exitCode: 0, stdout: "tail", truncated: true)
        );

        message.Detail.Should().EndWith("The output above is only the end; read the rest with TaskOutput.");
    }

    private static SandboxTaskCompletion Completion(
        string status,
        int? exitCode,
        string? stdout = null,
        string? stderr = null,
        bool truncated = false
    ) => new("sess-1", "", "3", status, exitCode, 1500, stdout, stderr, truncated);
}
