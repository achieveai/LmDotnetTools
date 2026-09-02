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
        storm.Args.Should().Be(TranscriptRedactor.ArgsDigest("{}"));
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
    public void ManySuccessfulPollsOfOneIdentity_AreNeverAStorm()
    {
        // The polling exemption. CheckAgents/WaitAgent are POLLS: a supervisor calling the same
        // one twenty times while it waits is doing its job, not thrashing. The walk gets this for
        // free from its IsError guard, and this pin is what stops a future "count repeats" shortcut
        // from turning every healthy supervisor into a storm.
        var polls = Enumerable.Repeat(Success("CheckAgents", """{"agentIds":["agent-1"]}"""), 20);

        RetryStormDetector.Walk("t", polls).Should().BeEmpty();
    }

    [Fact]
    public void SuccessfulPollsInterleavedWithFailures_DoNotExtendTheFailingRun()
    {
        // Same identity, alternating outcomes: every success closes the run below the threshold, so
        // six failures spread across polls are still no storm.
        var items = new List<StormWalkItem>();
        for (var i = 0; i < 6; i++)
        {
            items.Add(Error("WaitForAgents"));
            items.Add(Success("WaitForAgents"));
        }

        RetryStormDetector.Walk("t", items).Should().BeEmpty();
    }

    [Fact]
    public void CoordinationRefusals_FormStormsOnTheSameTermsAsBoardCalls()
    {
        // The detector is family-blind by design: #670 feeds coordination calls into the same walk,
        // so a supervisor retrying an unknown-agent wait is reported exactly like a retried add-note.
        var storms = RetryStormDetector.Walk(
            "t",
            [Error("WaitForAgents"), Error("WaitForAgents"), Error("WaitForAgents")]
        );

        storms.Should().ContainSingle().Which.Tool.Should().Be("WaitForAgents");
    }

    [Fact]
    public void ReportedArgs_AreADigest_NeverTheLiteralArguments()
    {
        // runs.jsonl is committed. A storm that echoed its arguments would publish model-authored
        // text into the repo and make the redacted archive score differently from the raw store.
        var args = """{"noteText":"secret plan","taskId":"2.1"}""";

        var storm = RetryStormDetector
            .Walk("t", [Error("add-note", args), Error("add-note", args), Error("add-note", args)])
            .Should()
            .ContainSingle()
            .Subject;

        storm.Args.Should().NotContain("secret plan");
        storm.Args.Should().Contain(Fingerprints.RedactedArgsKey);
        storm.Args.Should().Be(TranscriptRedactor.ArgsDigest(args));
    }

    [Fact]
    public void AnAlreadyRedactedArgumentDigest_PassesThroughUnchanged()
    {
        // Idempotence is what makes the archive metric-identical: re-digesting a digest would give
        // the archive a different storm identity from the store it was taken from.
        var digest = TranscriptRedactor.ArgsDigest("""{"a":1}""");

        var storm = RetryStormDetector
            .Walk("t", [Error("add-note", digest), Error("add-note", digest), Error("add-note", digest)])
            .Should()
            .ContainSingle()
            .Subject;

        storm.Args.Should().Be(digest);
    }

    [Fact]
    public void MakeIdentity_JoinsToolAndArgsWithNewline()
    {
        RetryStormDetector.MakeIdentity("add-note", """{"a":1}""").Should().Be("add-note\n{\"a\":1}");
    }
}
