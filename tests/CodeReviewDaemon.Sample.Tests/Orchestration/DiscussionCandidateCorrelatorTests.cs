using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The MECHANICAL half of candidate-answer correlation (design §5.3). Everything asserted here is a
/// string or graph comparison a machine can settle on its own; nothing here decides whether a comment
/// ANSWERS anything. That separation is the point: semantics can never manufacture a correlation, and
/// correlation can never manufacture an answer.
/// </summary>
public sealed class DiscussionCandidateCorrelatorTests
{
    private const string QuestionId = "q-7";

    private static readonly AuditSourceReference Source = new("1", "hash-1");

    private static OpenClarificationQuestion Question(
        string? targetRef = "c-question",
        string? threadId = "t-1",
        string? permalink = null,
        string? actionId = null
    ) =>
        new(
            QuestionId,
            "Is the retry budget meant to be per-attempt or per-run?",
            "whether the retry loop is unbounded",
            ProviderTargetRef: targetRef,
            ProviderThreadId: threadId,
            ProviderPermalink: permalink,
            ActionId: actionId
        );

    private static DiscussionComment Comment(string id, string body, string? parent = null, string? threadId = null) =>
        new(id, "octocat", body, Source, ParentCommentId: parent, ThreadId: threadId);

    [Fact]
    public void A_comment_in_the_questions_thread_is_a_candidate_by_ancestry()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question()],
            [Comment("c-2", "Per-run.", threadId: "t-1")]
        );

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.QuestionId.Should().Be(QuestionId);
        candidate.CommentId.Should().Be("c-2");
        candidate.Signals.Should().Contain(CandidateSignal.ThreadAncestry);
    }

    [Fact]
    public void A_direct_reply_to_the_published_question_is_a_candidate()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(threadId: null)],
            [Comment("c-2", "Per-run.", parent: "c-question")]
        );

        candidates.Should().ContainSingle().Which.Signals.Should().Contain(CandidateSignal.DirectReply);
    }

    [Fact]
    public void A_comment_quoting_the_stable_question_id_is_a_candidate()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(targetRef: null, threadId: null)],
            [Comment("c-9", "Re q-7: it is per-run.")]
        );

        candidates.Should().ContainSingle().Which.Signals.Should().Contain(CandidateSignal.QuestionIdReference);
    }

    [Fact]
    public void The_question_id_token_does_not_match_a_longer_id_that_merely_starts_with_it()
    {
        // "q-7" must not fire on "q-71". A substring match here would silently attach one question's
        // answers to another's, and the executor trusts this set as its allow-list.
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(targetRef: null, threadId: null)],
            [Comment("c-9", "Answered in q-71 already.")]
        );

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void A_comment_quoting_the_publishing_action_id_is_a_candidate()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(targetRef: null, threadId: null, actionId: "act-55")],
            [Comment("c-9", "See act-55 — per-run.")]
        );

        candidates.Should().ContainSingle().Which.Signals.Should().Contain(CandidateSignal.ActionIdReference);
    }

    [Fact]
    public void A_comment_quoting_the_permalink_is_a_candidate()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(targetRef: null, threadId: null, permalink: "https://github.com/o/r/pull/1#discussion_r9")],
            [Comment("c-9", "answering https://github.com/o/r/pull/1#discussion_r9 — per-run")]
        );

        candidates.Should().ContainSingle().Which.Signals.Should().Contain(CandidateSignal.Permalink);
    }

    [Fact]
    public void A_comment_mentioning_the_questions_provider_ref_is_a_candidate()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(threadId: null)],
            [Comment("c-9", "Following up on c-question: per-run.")]
        );

        candidates.Should().ContainSingle().Which.Signals.Should().Contain(CandidateSignal.Mention);
    }

    [Fact]
    public void An_unrelated_comment_that_merely_arrived_after_the_question_is_not_a_candidate()
    {
        // §5.3: the daemon does not infer an answer from timing. The correlator is given the comments in
        // arrival order and must still produce nothing, because no correlation signal fired.
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question()],
            [Comment("c-2", "Nice work on the docs!", threadId: "t-other")]
        );

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void A_generic_acknowledgement_in_the_thread_is_still_only_a_candidate_not_an_answer()
    {
        // Ancestry makes "thanks!" a CANDIDATE — that is correct and deliberate. What the correlator must
        // not do is grade it. It reports the signal and stops; the executor's gate is what refuses to let
        // a bare acknowledgement close a question.
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question()],
            [Comment("c-2", "thanks!", threadId: "t-1")]
        );

        candidates.Should().ContainSingle().Which.Signals.Should().Equal([CandidateSignal.ThreadAncestry]);
    }

    [Fact]
    public void Every_signal_that_fired_is_reported_in_enum_order_without_duplicates()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [Question(permalink: "https://example.test/q/7", actionId: "act-55")],
            [
                Comment(
                    "c-2",
                    "re c-question / q-7 / act-55 / https://example.test/q/7",
                    parent: "c-question",
                    threadId: "t-1"
                ),
            ]
        );

        candidates
            .Should()
            .ContainSingle()
            .Which.Signals.Should()
            .Equal([
                CandidateSignal.ThreadAncestry,
                CandidateSignal.DirectReply,
                CandidateSignal.QuestionIdReference,
                CandidateSignal.ActionIdReference,
                CandidateSignal.Permalink,
                CandidateSignal.Mention,
            ]);
    }

    [Fact]
    public void One_comment_can_be_a_candidate_for_several_questions()
    {
        var first = new OpenClarificationQuestion("q-1", "a?", "x", ProviderThreadId: "t-1");
        var second = new OpenClarificationQuestion("q-2", "b?", "y", ProviderThreadId: "t-1");

        var candidates = DiscussionCandidateCorrelator.Correlate(
            [first, second],
            [Comment("c-2", "both are per-run", threadId: "t-1")]
        );

        candidates.Select(c => c.QuestionId).Should().Equal(["q-1", "q-2"]);
    }

    [Fact]
    public void A_question_with_no_provider_identity_at_all_correlates_only_by_its_stable_id()
    {
        // A question whose publication receipt is missing has no ref, thread or permalink to match on.
        // It must not fall back to "everything in the window", which would hand the agent an allow-list
        // that permits any comment to close it.
        var orphan = new OpenClarificationQuestion("q-3", "c?", "z");

        var candidates = DiscussionCandidateCorrelator.Correlate(
            [orphan],
            [Comment("c-2", "unrelated chatter", threadId: "t-1"), Comment("c-3", "see q-3", threadId: "t-9")]
        );

        candidates.Should().ContainSingle().Which.CommentId.Should().Be("c-3");
    }

    [Fact]
    public void No_open_questions_means_no_candidates_however_busy_the_window_is()
    {
        var candidates = DiscussionCandidateCorrelator.Correlate(
            [],
            [Comment("c-2", "q-7 act-55", threadId: "t-1", parent: "c-question")]
        );

        candidates.Should().BeEmpty();
    }
}
