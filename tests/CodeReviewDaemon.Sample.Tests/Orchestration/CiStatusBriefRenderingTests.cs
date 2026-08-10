using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins how a <see cref="AdoCiStatus"/> is rendered into the reviewer's brief.
/// <para>
/// The reader's job is to establish what CI reported; this block's job is to say it without overstating it,
/// and every test here guards a way of overstating it. That matters more than usual because the reviewer has
/// no way to check: its sandbox has no toolchain and no network, so whatever this block says about building
/// and testing is the only thing it will ever know, and a confident wrong number is strictly worse than no
/// number at all — it converts "I could not tell" into "I checked".
/// </para>
/// </summary>
public sealed class CiStatusBriefRenderingTests
{
    private static string? Render(AdoCiStatus status) =>
        DaemonReviewStageExecutor.DescribeCiStatusForTests(status);

    /// <summary>
    /// The rule the reader explicitly asked the brief to honour. <c>null</c> means nobody established a
    /// count; <c>0</c> is what a genuinely test-free (or still-starting) build reports. Printing null as zero
    /// tells the reviewer a pipeline ran and found no tests when in fact nothing looked — the same false
    /// confidence as repeating the PR's own validation claim, only quieter, because a stated number reads as
    /// a measurement.
    /// </summary>
    [Fact]
    public void An_unread_test_count_is_never_rendered_as_zero()
    {
        var text = Render(new AdoCiStatus
        {
            State = AdoCiState.Succeeded,
            BuildId = "39168345",
            BuildStatus = "completed",
            BuildResult = "succeeded",
            TotalTests = null,
            PassedTests = null,
            FailedTests = null,
        });

        text.Should().NotBeNull();
        text.Should().Contain(
            "not reported", "the block has to say the number is absent, in words");
        text.Should().Contain(
            "NOT the same as zero",
            "the reviewer is told the distinction outright, because it cannot check it from inside the sandbox");
        text.Should().NotMatchRegex(
            @"\b0 total\b", "a count nobody established must never appear as a measured zero");
    }

    /// <summary>
    /// A real zero is a different statement and survives intact — otherwise the fix for the null case would
    /// have suppressed the honest one too.
    /// </summary>
    [Fact]
    public void A_genuine_zero_failure_count_is_still_reported_as_a_number()
    {
        var text = Render(new AdoCiStatus
        {
            State = AdoCiState.Succeeded,
            BuildId = "1",
            BuildStatus = "completed",
            BuildResult = "succeeded",
            TotalTests = 45051,
            PassedTests = 45051,
            FailedTests = 0,
        });

        text.Should().Contain("45051 total").And.Contain("0 failed");
        text.Should().NotContain("not reported");
    }

    /// <summary>
    /// The reader's second warning. These lines are the build timeline's error issues: on PR 5505458 that IS
    /// the failing test, but on a compile break the same field carries <c>CS1002 ; expected</c>. Labelling
    /// them "failing tests" would make the brief assert something false on every non-test failure.
    /// </summary>
    [Fact]
    public void Failure_lines_are_labelled_as_ci_failures_not_as_failing_tests()
    {
        var text = Render(new AdoCiStatus
        {
            State = AdoCiState.Failed,
            BuildId = "39168345",
            BuildStatus = "completed",
            BuildResult = "failed",
            TotalTests = 45051,
            PassedTests = 45050,
            FailedTests = 1,
            FailureMessages =
            [
                @"clr\src\Plane0\MetricLibrary\TagService\TagService.UnitTests_Retail_Amd64__TEST Attempt: [2], 1 of 1 tests failed.",
            ],
        });

        text.Should().NotBeNull();
        text.Should().Contain("TagService.UnitTests", "the actual failure reaches the reviewer");
        text.Should().Contain(
            "build timeline", "the lines are named for what they are, since they are not always tests");
        text.Should().NotContain(
            "### Failing tests",
            "a compile break puts a compiler error in this same field, so a 'tests' heading would be false");
        text.Should().Contain(
            "caused by this pull request's changes or are pre-existing",
            "a failing pipeline is a finding, and attributing it is the reviewer's job, not the daemon's");
    }

    /// <summary>
    /// A dropped tail must be announced. A capped list that says nothing about the cap reads as the complete
    /// set, and a reviewer that believes it has seen every failure will happily conclude the rest are fine.
    /// </summary>
    [Fact]
    public void A_capped_failure_list_says_how_many_it_dropped()
    {
        var text = Render(new AdoCiStatus
        {
            State = AdoCiState.Failed,
            BuildId = "1",
            BuildStatus = "completed",
            BuildResult = "failed",
            FailureMessages = ["one", "two"],
            OmittedFailureMessages = 7,
        });

        text.Should().Contain("7 further failure line(s) omitted");
    }

    /// <summary>
    /// The two "nothing ran" states must not read as clean. An empty CI section beside a green-looking brief
    /// is exactly how a reviewer concludes the change was validated when nothing ever compiled it.
    /// </summary>
    [Fact]
    public void A_build_that_never_ran_says_so_rather_than_looking_clean()
    {
        foreach (var state in new[] { AdoCiState.NoBuildPolicy, AdoCiState.NotStarted })
        {
            var text = Render(new AdoCiStatus { State = state });

            text.Should().NotBeNull($"state {state} is a fact worth telling the reviewer");
            text.Should().Contain(
                "not evidence of correctness",
                $"state {state} means nothing compiled or tested the change, and the brief must say so "
                    + "rather than leaving a gap that reads as a pass");
        }
    }

    /// <summary>
    /// A running build's numbers are partial, and saying so is what stops the reviewer treating a
    /// mid-flight "0 failed" as a verdict.
    /// </summary>
    [Fact]
    public void A_running_build_warns_that_its_numbers_are_partial()
    {
        var text = Render(new AdoCiStatus
        {
            State = AdoCiState.Running,
            BuildId = "1",
            BuildStatus = "inProgress",
            BuildResult = null,
            TotalTests = 120,
            PassedTests = 120,
            FailedTests = 0,
        });

        text.Should().Contain("still running").And.Contain("a later failure is still possible");
    }

    /// <summary>
    /// A read that failed is not evidence about the pipeline. Rendering "CI status unknown" spends the
    /// reviewer's limited attention to tell it nothing, so the block is omitted entirely and the reader's own
    /// log carries the cause.
    /// </summary>
    [Fact]
    public void An_unavailable_read_renders_no_block_at_all()
    {
        Render(AdoCiStatus.Unavailable).Should().BeNull();
    }
}
