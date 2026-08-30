using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Unit pins for the spec's storm-walk semantics, one rule per test, so a mutation of the
/// detector names the exact rule it broke.
/// </summary>
public class RetryStormDetectorTests
{
    private static StormWalkItem Error(string tool, string args = "{}") =>
        new(RetryStormDetector.MakeIdentity(tool, args), IsError: true, HasResult: true);

    private static StormWalkItem Success(string tool, string args = "{}") =>
        new(RetryStormDetector.MakeIdentity(tool, args), IsError: false, HasResult: true);

    private static StormWalkItem Unpaired(string tool, string args = "{}") =>
        new(RetryStormDetector.MakeIdentity(tool, args), IsError: false, HasResult: false);

    [Fact]
    public void ThreeConsecutiveFailures_ClosedBySuccess_IsOneStormOfThree()
    {
        var storms = RetryStormDetector.Walk(
            "t",
            [Error("add-note"), Error("add-note"), Error("add-note"), Success("add-note")]
        );

        var storm = storms.Should().ContainSingle().Subject;
        storm.Count.Should().Be(3);
        storm.Tool.Should().Be("add-note");
        storm.Args.Should().Be("{}");
        storm.ThreadId.Should().Be("t");
    }

    [Fact]
    public void TwoFailures_IsBelowThreshold()
    {
        RetryStormDetector.Walk("t", [Error("add-note"), Error("add-note"), Success("add-note")]).Should().BeEmpty();
    }

    [Fact]
    public void OpenRunAtThreadEnd_IsStillCounted()
    {
        var storms = RetryStormDetector.Walk("t", [Error("add-note"), Error("add-note"), Error("add-note")]);
        storms.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    [Fact]
    public void SuccessOfSameIdentity_ResetsTheRun_SoLaterFailuresStartFresh()
    {
        // 3 errors -> success (storm of 3) -> 2 errors -> end. NOT one storm of 5, NOT two storms.
        var storms = RetryStormDetector.Walk(
            "t",
            [
                Error("add-note"),
                Error("add-note"),
                Error("add-note"),
                Success("add-note"),
                Error("add-note"),
                Error("add-note"),
            ]
        );

        storms.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    [Fact]
    public void InterleavedOtherIdentity_DoesNotBreakTheRun()
    {
        var storms = RetryStormDetector.Walk(
            "t",
            [Error("add-note"), Success("list-tasks"), Error("add-note"), Success("get-task"), Error("add-note")]
        );

        storms.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    [Fact]
    public void DifferentArgs_AreDifferentIdentities()
    {
        // 2 failures each of two argument sets: no identity reaches 3.
        var storms = RetryStormDetector.Walk(
            "t",
            [
                Error("add-note", """{"taskId":"1"}"""),
                Error("add-note", """{"taskId":"2"}"""),
                Error("add-note", """{"taskId":"1"}"""),
                Error("add-note", """{"taskId":"2"}"""),
            ]
        );

        storms.Should().BeEmpty();
    }

    [Fact]
    public void DifferentTool_SameArgs_AreDifferentIdentities()
    {
        var storms = RetryStormDetector.Walk(
            "t",
            [Error("add-note"), Error("edit-note"), Error("add-note"), Error("edit-note")]
        );

        storms.Should().BeEmpty();
    }

    [Fact]
    public void UnpairedCall_LeavesTheStreakUntouched()
    {
        // 2 errors + 1 unpaired = still a streak of 2: an unpaired call is neither failure
        // nor success.
        RetryStormDetector.Walk("t", [Error("add-note"), Error("add-note"), Unpaired("add-note")]).Should().BeEmpty();
    }

    [Fact]
    public void UnpairedSameIdentity_DoesNotCloseAnOpenRun()
    {
        // 3 errors, then an unpaired occurrence, then end: the run is still open at 3.
        var storms = RetryStormDetector.Walk(
            "t",
            [Error("add-note"), Error("add-note"), Error("add-note"), Unpaired("add-note")]
        );

        storms.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    [Fact]
    public void TwoIndependentStorms_AreBothReported()
    {
        var storms = RetryStormDetector.Walk(
            "t",
            [
                Error("add-note"),
                Error("add-note"),
                Error("add-note"),
                Success("add-note"),
                Error("get-task"),
                Error("get-task"),
                Error("get-task"),
                Error("get-task"),
            ]
        );

        storms.Should().HaveCount(2);
        storms.Should().ContainSingle(s => s.Tool == "add-note").Which.Count.Should().Be(3);
        storms.Should().ContainSingle(s => s.Tool == "get-task").Which.Count.Should().Be(4);
    }

    [Fact]
    public void MakeIdentity_JoinsToolAndArgsWithNewline()
    {
        RetryStormDetector.MakeIdentity("add-note", """{"a":1}""").Should().Be("add-note\n{\"a\":1}");
    }
}
