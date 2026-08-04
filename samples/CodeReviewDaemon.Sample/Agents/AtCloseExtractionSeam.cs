namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The combined outcome of one at-close extraction run — the <see cref="KnowledgeExtractionResult"/> reported
/// to <see cref="KnowledgeExtractionCommitter"/>, and the name of the pass whose write was dropped so the
/// caller can warn about it (<c>null</c> when nothing was dropped).
/// </summary>
internal sealed record AtCloseExtraction(KnowledgeExtractionResult Result, string? DroppedPass);

/// <summary>
/// Combines the two at-close extraction passes — curated knowledge and per-developer review feedback — into
/// the single outcome the committer commits on. Kept out of the composition root so it is unit-testable.
/// </summary>
internal static class AtCloseExtractionSeam
{
    /// <summary>Name of the curated-knowledge pass, for the dropped-pass warning.</summary>
    public const string KnowledgePass = "knowledge";

    /// <summary>Name of the per-developer review-feedback pass, for the dropped-pass warning.</summary>
    public const string ReviewFeedbackPass = "review-feedback";

    /// <summary>
    /// Precedence: <c>Wrote</c> &gt; <c>Failed</c> &gt; <c>Declined</c>. Both passes write under
    /// <c>KnowledgeBase/</c> on the same notes branch and the committer stages that directory wholesale, so a
    /// single <c>Wrote</c> carries whatever either pass wrote.
    /// <para>
    /// A write is reported even when the OTHER pass failed. The alternative — holding the commit back until
    /// both succeed — lets one durably failing pass wedge the other indefinitely: a developer record grown
    /// past <see cref="ReviewFeedbackAgent"/>'s size ceiling fails every sweep and no retry can clear it, which
    /// would stop curated knowledge from ever landing again. The failing pass is named in
    /// <see cref="AtCloseExtraction.DroppedPass"/> so it is warned about rather than lost silently; the PR's
    /// notes are not consumed by extraction, so a later PR can still pick that ground up.
    /// </para>
    /// <para>
    /// When NEITHER wrote, a failure outranks a decline: <c>Failed</c> holds the notes branch back for the
    /// sweeper's bounded retry, where <c>Declined</c> would let it merge and delete — making a lost
    /// extraction permanent.
    /// </para>
    /// </summary>
    public static AtCloseExtraction Combine(KnowledgeExtractionResult knowledge, KnowledgeExtractionResult feedback)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(feedback);

        var knowledgeFailed = knowledge.Outcome == KnowledgeExtractionOutcome.Failed;
        var feedbackFailed = feedback.Outcome == KnowledgeExtractionOutcome.Failed;
        var knowledgeWrote = knowledge.Outcome == KnowledgeExtractionOutcome.Wrote;
        var feedbackWrote = feedback.Outcome == KnowledgeExtractionOutcome.Wrote;

        if (knowledgeWrote || feedbackWrote)
        {
            var dropped = knowledgeFailed ? KnowledgePass
                : feedbackFailed ? ReviewFeedbackPass
                : null;
            return new AtCloseExtraction(knowledgeWrote ? knowledge : feedback, dropped);
        }

        // Neither wrote: a failure is retryable and must outrank a decline.
        return new AtCloseExtraction(knowledgeFailed ? knowledge : feedback, DroppedPass: null);
    }
}
