using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Pins how the two at-close extraction passes combine into the single outcome the committer commits on.
/// The rules that matter are asymmetric on purpose: a write is never held hostage to the other pass, and a
/// failure is never reported as a decline.
/// </summary>
public class AtCloseExtractionSeamTests
{
    private static KnowledgeExtractionResult Wrote(string entry) =>
        KnowledgeExtractionResult.Wrote(entry, runId: "run-1");

    private static KnowledgeExtractionResult Failed() => KnowledgeExtractionResult.Failed("run-1");

    private static KnowledgeExtractionResult Declined() => KnowledgeExtractionResult.Declined("run-1");

    [Fact]
    public void Combine_reports_declined_only_when_both_passes_declined()
    {
        var combined = AtCloseExtractionSeam.Combine(Declined(), Declined());

        combined.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Declined);
        combined.DroppedPass.Should().BeNull();
    }

    [Fact]
    public void Combine_reports_the_knowledge_write()
    {
        var combined = AtCloseExtractionSeam.Combine(Wrote("system/a-lesson.md"), Declined());

        combined.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        combined.Result.EntryFileName.Should().Be("system/a-lesson.md");
        combined.DroppedPass.Should().BeNull();
    }

    [Fact]
    public void Combine_reports_the_feedback_write_when_knowledge_declined()
    {
        var combined = AtCloseExtractionSeam.Combine(Declined(), Wrote("developers/octocat.reviewfeedbacks.md"));

        combined.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        combined.Result.EntryFileName.Should().Be("developers/octocat.reviewfeedbacks.md");
        combined.DroppedPass.Should().BeNull();
    }

    /// <summary>
    /// The wedge guard. A developer record grown past ReviewFeedbackAgent's size ceiling fails every sweep and
    /// no retry can clear it. If that failure suppressed the commit, curated knowledge would stop landing for
    /// good — so the write is committed and the failing pass is NAMED for the caller's warning.
    /// </summary>
    [Fact]
    public void Combine_commits_a_write_even_when_the_other_pass_failed_and_names_the_dropped_pass()
    {
        var knowledgeWroteFeedbackFailed = AtCloseExtractionSeam.Combine(Wrote("system/a-lesson.md"), Failed());

        knowledgeWroteFeedbackFailed.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        knowledgeWroteFeedbackFailed.Result.EntryFileName.Should().Be("system/a-lesson.md");
        knowledgeWroteFeedbackFailed.DroppedPass.Should().Be(AtCloseExtractionSeam.ReviewFeedbackPass);

        var feedbackWroteKnowledgeFailed = AtCloseExtractionSeam.Combine(
            Failed(), Wrote("developers/octocat.reviewfeedbacks.md"));

        feedbackWroteKnowledgeFailed.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        feedbackWroteKnowledgeFailed.Result.EntryFileName.Should().Be("developers/octocat.reviewfeedbacks.md");
        feedbackWroteKnowledgeFailed.DroppedPass.Should().Be(AtCloseExtractionSeam.KnowledgePass);
    }

    [Fact]
    public void Combine_reports_both_writes_as_one_write_without_dropping_anything()
    {
        var combined = AtCloseExtractionSeam.Combine(
            Wrote("system/a-lesson.md"), Wrote("developers/octocat.reviewfeedbacks.md"));

        combined.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        combined.DroppedPass.Should().BeNull();
    }

    /// <summary>
    /// Reporting a failure as a decline would let the sweeper merge and DELETE the notes branch, making the
    /// lost extraction permanent — the Declined-vs-Failed defect, at the combination step.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Combine_reports_failed_when_neither_wrote_and_either_failed(
        bool knowledgeFailed,
        bool feedbackFailed
    )
    {
        var combined = AtCloseExtractionSeam.Combine(
            knowledgeFailed ? Failed() : Declined(),
            feedbackFailed ? Failed() : Declined());

        combined.Result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed);
        combined.DroppedPass.Should().BeNull();
    }
}
