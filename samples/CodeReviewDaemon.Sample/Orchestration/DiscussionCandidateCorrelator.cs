using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Collects candidate answers for open clarification questions MECHANICALLY (design §5.3).
/// <para>
/// Everything here is a string or graph comparison. It never reads meaning, never looks at arrival
/// order, never looks at whether a thread was resolved, and never looks at reactions — because §5.3
/// forbids inferring agreement from timing, thread closure or a generic acknowledgement, and the surest
/// way not to infer from a signal is not to compute it. A "thanks!" posted in the question's own thread
/// IS a candidate here, and that is correct: correlation says the two are linked, not that one answers
/// the other. Refusing to let a bare acknowledgement close a question is
/// <see cref="DiscussionRoundExecutor"/>'s job, on evidence the agent must supply.
/// </para>
/// <para>
/// The candidate POOL is the round's frozen window — §5.3's "external comments inside the unconsumed
/// activity window" scopes what may be considered, it is not a fifth signal. Treating it as one would
/// make every comment a candidate for every question, which would drown the four real signals and leave
/// the executor with an allow-list that allows everything.
/// </para>
/// <para>
/// Self-authored comments are expected to have been excluded upstream (the coordinator's self-trigger
/// exclusion). This class does not know the daemon's own identity and deliberately does not guess at it.
/// </para>
/// </summary>
internal static class DiscussionCandidateCorrelator
{
    /// <summary>
    /// Links each comment in <paramref name="windowComments"/> to every question in
    /// <paramref name="questions"/> it carries a signal for. Ordered by question, then by the comment's
    /// position in the window, so the same inputs always produce the same list.
    /// </summary>
    public static IReadOnlyList<CandidateAnswer> Correlate(
        IReadOnlyList<OpenClarificationQuestion> questions,
        IReadOnlyList<DiscussionComment> windowComments
    )
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(windowComments);

        var candidates = new List<CandidateAnswer>();
        foreach (var question in questions)
        {
            foreach (var comment in windowComments)
            {
                var signals = SignalsFor(question, comment);
                if (signals.Count > 0)
                {
                    candidates.Add(new CandidateAnswer(question.QuestionId, comment.CommentId, signals));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Every signal that fires for one (question, comment) pair, in <see cref="CandidateSignal"/> order.
    /// A question with no published identity at all still correlates by its stable id, and by nothing
    /// else — it must not fall back to "everything in the window".
    /// </summary>
    private static IReadOnlyList<CandidateSignal> SignalsFor(
        OpenClarificationQuestion question,
        DiscussionComment comment
    )
    {
        var signals = new List<CandidateSignal>();

        if (question.ProviderThreadId is { Length: > 0 } thread && comment.ThreadId == thread)
        {
            signals.Add(CandidateSignal.ThreadAncestry);
        }

        if (question.ProviderTargetRef is { Length: > 0 } target)
        {
            if (comment.ParentCommentId == target)
            {
                signals.Add(CandidateSignal.DirectReply);
            }
        }

        // The id is matched VERBATIM, with no prefix bolted on. The store's ids are already the token a
        // human would quote ("q-7", a guid); synthesising "q-" + id would build a token that appears in
        // no comment for a guid id, and "q-q-7" for one already shaped that way — a signal that can never
        // fire, which reads exactly like a signal nobody triggered.
        if (ContainsToken(comment.Body, question.QuestionId))
        {
            signals.Add(CandidateSignal.QuestionIdReference);
        }

        if (question.ActionId is { Length: > 0 } actionId && ContainsToken(comment.Body, actionId))
        {
            signals.Add(CandidateSignal.ActionIdReference);
        }

        if (
            question.ProviderPermalink is { Length: > 0 } permalink
            && comment.Body.Contains(permalink, StringComparison.OrdinalIgnoreCase)
        )
        {
            signals.Add(CandidateSignal.Permalink);
        }

        if (question.ProviderTargetRef is { Length: > 0 } mention && ContainsToken(comment.Body, mention))
        {
            signals.Add(CandidateSignal.Mention);
        }

        return signals;
    }

    /// <summary>
    /// Whether <paramref name="body"/> contains <paramref name="token"/> as a whole token — neither
    /// neighbour may be a letter or digit.
    /// <para>
    /// A plain substring match would make <c>q-7</c> fire on <c>q-71</c>, silently attaching one
    /// question's answers to another's. The executor treats this set as an allow-list, so a false
    /// positive here becomes permission to close the wrong question.
    /// </para>
    /// </summary>
    private static bool ContainsToken(string body, string token)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var from = 0;
        while (from <= body.Length - token.Length)
        {
            var at = body.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            var beforeOk = at == 0 || !char.IsLetterOrDigit(body[at - 1]);
            var end = at + token.Length;
            var afterOk = end == body.Length || !char.IsLetterOrDigit(body[end]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }
}
