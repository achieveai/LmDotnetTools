using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using PersistedReviewActionKind = CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The policy half of a <c>DiscussionFollowUp</c> round (design §5.3, §5.4). The executor correlates
/// candidates MECHANICALLY, hands the frozen inputs to the discussion agent, then VALIDATES what came
/// back: an interpretation may not cite a comment the correlator never linked, a new finding may not
/// exist without a cause in the frozen window and code evidence, and a broad-review need is recorded
/// rather than dispatched. Every test drives a fake interpreter, so nothing here can reach a provider.
/// </summary>
public sealed class DiscussionRoundExecutorTests : LoggingTestBase
{
    private const long RoundId = 9;

    public DiscussionRoundExecutorTests(ITestOutputHelper output)
        : base(output) { }

    private static DateTimeOffset Later => DateTimeOffset.UtcNow.AddMinutes(10);

    private static readonly AuditSourceReference Evidence = new("11", "sha-11");

    private static DiscussionComment Comment(string id, string body, string? threadId = null, string? parent = null) =>
        new(id, "octocat", body, Evidence, ParentCommentId: parent, ThreadId: threadId);

    /// <summary>A question published as <c>c-question</c> in thread <c>t-1</c>.</summary>
    private static OpenClarificationQuestion Question(string id = "q-7") =>
        new(id, "Per-attempt or per-run?", "whether the retry loop is unbounded", "c-question", "t-1");

    private static DiscussionRoundContext Context(
        IReadOnlyList<DiscussionComment>? comments = null,
        IReadOnlyList<OpenClarificationQuestion>? questions = null
    ) =>
        new(
            RoundId,
            "abc1234",
            comments ?? [Comment("c-5", "It is per-run.", threadId: "t-1")],
            questions ?? [Question()]
        );

    private static DiscussionDecision Decision(
        bool relevant = true,
        IReadOnlyList<QuestionInterpretation>? questions = null,
        IReadOnlyList<PlannedReviewAction>? actions = null,
        IReadOnlyList<RoundObservationDraft>? observations = null,
        bool broadReview = false,
        string? broadReviewReason = null
    ) =>
        new(
            relevant,
            questions ?? [],
            actions ?? [],
            observations ?? [],
            broadReview,
            broadReviewReason,
            Prose: "prose"
        );

    private (DiscussionRoundExecutor Sut, FakeInterpreter Interpreter, RecordingSink Sink) Build(
        DiscussionDecision decision
    )
    {
        var interpreter = new FakeInterpreter(decision);
        var sink = new RecordingSink();
        return (
            new DiscussionRoundExecutor(interpreter, sink, LoggerFactory.CreateLogger<DiscussionRoundExecutor>()),
            interpreter,
            sink
        );
    }

    // ---------------------------------------------------------------- mechanical correlation

    [Fact]
    public async Task The_agent_receives_mechanically_correlated_candidates_it_did_not_produce()
    {
        var (sut, interpreter, _) = Build(Decision(relevant: false));

        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        var input = interpreter.Received.Should().ContainSingle().Subject;
        input.Candidates.Should().ContainSingle().Which.CommentId.Should().Be("c-5");
        input.RoundId.Should().Be(RoundId);
        input.HeadSha.Should().Be("abc1234");
    }

    // ---------------------------------------------------------------- question interpretation gate

    [Fact]
    public async Task A_supported_answer_is_accepted_with_its_candidate_and_evidence()
    {
        var (sut, _, _) = Build(
            Decision(
                questions: [new QuestionInterpretation("q-7", ClarificationQuestionState.Answered, ["c-5"], [Evidence])]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        var accepted = outcome.QuestionInterpretations.Should().ContainSingle().Subject;
        accepted.State.Should().Be(ClarificationQuestionState.Answered);
        outcome.Observations.Should().Contain(o => o.Kind == ObservationKind.Answer);
    }

    [Fact]
    public async Task Conflicting_sourced_candidates_stay_contested_and_are_not_collapsed_into_answered()
    {
        var (sut, _, _) = Build(
            Decision(
                questions:
                [
                    new QuestionInterpretation(
                        "q-7",
                        ClarificationQuestionState.Contested,
                        ["c-5", "c-6"],
                        [Evidence, new AuditSourceReference("12", "sha-12")]
                    ),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(
            Context(
                comments:
                [
                    Comment("c-5", "per-run", threadId: "t-1"),
                    Comment("c-6", "no, per-attempt", threadId: "t-1"),
                ]
            ),
            Later,
            CancellationToken.None
        );

        outcome
            .QuestionInterpretations.Should()
            .ContainSingle()
            .Which.State.Should()
            .Be(ClarificationQuestionState.Contested);
    }

    [Fact]
    public async Task A_contested_verdict_resting_on_one_candidate_is_refused()
    {
        // "Contested" asserts that sourced answers DISAGREE. One candidate cannot disagree with itself,
        // so a single-candidate contest is an unsourced verdict wearing a sourced label.
        var (sut, _, _) = Build(
            Decision(
                questions:
                [
                    new QuestionInterpretation("q-7", ClarificationQuestionState.Contested, ["c-5"], [Evidence]),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.QuestionInterpretations.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("at least two");
    }

    [Fact]
    public async Task An_answer_citing_a_comment_the_correlator_never_linked_is_refused()
    {
        // The mechanical set is the allow-list. Without this, the agent could close a question on any
        // comment it liked the look of — semantics manufacturing a correlation.
        var (sut, _, _) = Build(
            Decision(
                questions:
                [
                    new QuestionInterpretation("q-7", ClarificationQuestionState.Answered, ["c-99"], [Evidence]),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(
            Context(comments: [Comment("c-5", "per-run", threadId: "t-1"), Comment("c-99", "unrelated")]),
            Later,
            CancellationToken.None
        );

        outcome.QuestionInterpretations.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("c-99");
        outcome.Observations.Should().Contain(o => o.Kind == ObservationKind.Gap);
    }

    [Fact]
    public async Task An_answer_with_no_candidate_at_all_is_refused()
    {
        // This is the "timing / thread closure / generic thanks" case §5.3 names: nothing mechanical
        // links a comment to the question, so nothing may close it.
        var (sut, _, _) = Build(
            Decision(
                questions: [new QuestionInterpretation("q-7", ClarificationQuestionState.Answered, [], [Evidence])]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.QuestionInterpretations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_answer_with_no_interpretation_evidence_is_refused()
    {
        var (sut, _, _) = Build(
            Decision(questions: [new QuestionInterpretation("q-7", ClarificationQuestionState.Answered, ["c-5"], [])])
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.QuestionInterpretations.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("evidence");
    }

    [Fact]
    public async Task An_interpretation_of_a_question_this_round_was_not_given_is_refused()
    {
        var (sut, _, _) = Build(
            Decision(
                questions:
                [
                    new QuestionInterpretation("q-404", ClarificationQuestionState.Answered, ["c-5"], [Evidence]),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.QuestionInterpretations.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("404");
    }

    [Fact]
    public async Task Only_answered_or_contested_may_be_concluded_by_a_discussion_round()
    {
        // Table-driven rather than a [Theory]: the state is an internal enum, which cannot appear in the
        // signature of the public method xUnit needs for a data-driven case.
        ClarificationQuestionState[] notConclusions =
        [
            ClarificationQuestionState.Open,
            ClarificationQuestionState.Superseded,
            ClarificationQuestionState.UnansweredAtMerge,
        ];

        foreach (var state in notConclusions)
        {
            var (sut, _, _) = Build(
                Decision(questions: [new QuestionInterpretation("q-7", state, ["c-5"], [Evidence])])
            );

            var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

            outcome.QuestionInterpretations.Should().BeEmpty($"{state} is not a conclusion this round may reach");
        }
    }

    // ---------------------------------------------------------------- action gate

    [Fact]
    public async Task A_new_finding_tied_to_new_discussion_and_backed_by_evidence_is_accepted()
    {
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(
                        PersistedReviewActionKind.SubmitInlineFindings,
                        "the budget is rebuilt on resume",
                        Path: "src/Retry.cs",
                        Line: 42,
                        CausalCommentIds: ["c-5"],
                        Evidence: [Evidence]
                    ),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome
            .PlannedPublicationActions.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(PersistedReviewActionKind.SubmitInlineFindings);
        outcome.Participated.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_finding_with_no_causal_link_to_the_new_discussion_is_refused()
    {
        // §5.4: a new finding is permitted ONLY when causally tied to the new discussion. Without this
        // the round quietly becomes the second code review it is forbidden to be.
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(
                        PersistedReviewActionKind.SubmitInlineFindings,
                        "unrelated smell I noticed",
                        Path: "src/Other.cs",
                        Line: 1,
                        Evidence: [Evidence]
                    ),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("causally");
    }

    [Fact]
    public async Task A_new_finding_whose_cause_is_outside_the_frozen_window_is_refused()
    {
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(
                        PersistedReviewActionKind.SubmitInlineFindings,
                        "b",
                        Path: "src/Retry.cs",
                        Line: 42,
                        CausalCommentIds: ["c-from-last-week"],
                        Evidence: [Evidence]
                    ),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("c-from-last-week");
    }

    [Fact]
    public async Task A_new_finding_with_no_code_evidence_is_refused()
    {
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(
                        PersistedReviewActionKind.SubmitInlineFindings,
                        "b",
                        Path: "src/Retry.cs",
                        Line: 42,
                        CausalCommentIds: ["c-5"]
                    ),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("evidence");
    }

    [Fact]
    public async Task A_reply_must_target_a_ref_this_round_was_actually_shown()
    {
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(PersistedReviewActionKind.ReplyToDiscussion, "sure", TargetRef: "c-nope"),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("c-nope");
    }

    [Fact]
    public async Task A_reply_into_the_frozen_window_is_accepted()
    {
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(PersistedReviewActionKind.ReplyToDiscussion, "per-run", TargetRef: "c-5"),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().ContainSingle();
    }

    // ------------------------------------------------- publication state comes only from a typed receipt

    [Fact]
    public async Task An_action_the_gate_accepted_is_not_yet_recorded_as_something_the_round_said()
    {
        // The gate decides what the round MAY publish. Nothing has reached the provider yet, and the
        // publication coordinator may still reject it, collect it without sending, or crash. Recording a
        // DiscussionContribution here would make "we planned to say this" read exactly like "we said it".
        var (sut, _, sink) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(PersistedReviewActionKind.ReplyToDiscussion, "per-run", TargetRef: "c-5"),
                ]
            )
        );

        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        sink.Recorded.Should().NotContain(o => o.Kind == ObservationKind.DiscussionContribution);
    }

    [Fact]
    public async Task A_question_counts_as_asked_only_once_its_provider_receipt_exists()
    {
        // Design §5.1, verbatim: "A question counts as asked only after a provider receipt exists." A
        // Question observation written at plan time is the daemon telling itself it asked something it
        // has not asked, and the close report counts those.
        var question = new PlannedReviewAction(
            PersistedReviewActionKind.PostClarificationQuestion,
            "is the budget per-run?"
        );
        var (sut, _, sink) = Build(Decision(actions: [question]));

        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);
        sink.Recorded.Should().NotContain(o => o.Kind == ObservationKind.Question);

        _ = await sut.RecordPublicationAsync(
            RoundId,
            [new DiscussionPublicationOutcome(question, ReviewActionStatus.Accepted, "https://example.test/c/1")],
            CancellationToken.None
        );

        sink.Recorded.Should()
            .ContainSingle(o => o.Kind == ObservationKind.Question)
            .Which.Summary.Should()
            .Contain("https://example.test/c/1");
    }

    [Fact]
    public async Task An_accepted_receipt_records_the_contribution_the_action_actually_made()
    {
        var reply = new PlannedReviewAction(
            PersistedReviewActionKind.ReplyToDiscussion,
            "per-run",
            TargetRef: "c-5",
            Evidence: [Evidence]
        );
        var finding = new PlannedReviewAction(
            PersistedReviewActionKind.SubmitInlineFindings,
            "rebuilt on resume",
            CausalCommentIds: ["c-5"],
            Evidence: [Evidence]
        );
        var (sut, _, sink) = Build(Decision(actions: [reply, finding]));
        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        var participated = await sut.RecordPublicationAsync(
            RoundId,
            [
                new DiscussionPublicationOutcome(reply, ReviewActionStatus.Accepted),
                new DiscussionPublicationOutcome(finding, ReviewActionStatus.Accepted),
            ],
            CancellationToken.None
        );

        participated.Should().BeTrue();
        sink.Recorded.Should().Contain(o => o.Kind == ObservationKind.DiscussionContribution);
        sink.Recorded.Should()
            .ContainSingle(o => o.Kind == ObservationKind.Finding)
            .Which.Evidence.Should()
            .Equal([Evidence], "the observation carries the action's own citations");
    }

    [Fact]
    public async Task A_rejected_receipt_records_a_gap_rather_than_a_contribution()
    {
        var reply = new PlannedReviewAction(PersistedReviewActionKind.ReplyToDiscussion, "per-run", TargetRef: "c-5");
        var (sut, _, sink) = Build(Decision(actions: [reply]));
        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        var participated = await sut.RecordPublicationAsync(
            RoundId,
            [new DiscussionPublicationOutcome(reply, ReviewActionStatus.Rejected, RejectionCode: "stale-head")],
            CancellationToken.None
        );

        participated.Should().BeFalse();
        sink.Recorded.Should().NotContain(o => o.Kind == ObservationKind.DiscussionContribution);
        sink.Recorded.Should()
            .ContainSingle(o => o.Kind == ObservationKind.Gap)
            .Which.Summary.Should()
            .Contain("stale-head");
    }

    [Fact]
    public async Task A_collect_only_action_is_recorded_as_unsent_not_as_a_contribution()
    {
        // Collect-only is the shipped default. If it recorded a contribution, every unposted round would
        // claim to have participated in a discussion nobody outside the daemon ever saw.
        var reply = new PlannedReviewAction(PersistedReviewActionKind.ReplyToDiscussion, "per-run", TargetRef: "c-5");
        var (sut, _, sink) = Build(Decision(actions: [reply]));
        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        var participated = await sut.RecordPublicationAsync(
            RoundId,
            [new DiscussionPublicationOutcome(reply, ReviewActionStatus.CollectedOnly)],
            CancellationToken.None
        );

        participated.Should().BeFalse();
        sink.Recorded.Should().NotContain(o => o.Kind == ObservationKind.DiscussionContribution);
        sink.Recorded.Should().ContainSingle(o => o.Kind == ObservationKind.Gap);
    }

    [Fact]
    public async Task Recording_no_publication_outcomes_appends_nothing()
    {
        var (sut, _, sink) = Build(Decision(relevant: false));
        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);
        var callsAfterExecute = sink.Calls;

        var participated = await sut.RecordPublicationAsync(RoundId, [], CancellationToken.None);

        participated.Should().BeFalse();
        sink.Calls.Should().Be(callsAfterExecute, "an empty batch is not an append");
    }

    [Fact]
    public async Task A_discussion_round_may_not_create_the_root_summary()
    {
        var (sut, _, _) = Build(
            Decision(actions: [new PlannedReviewAction(PersistedReviewActionKind.CreateRootSummary, "# Review")])
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("root summary");
    }

    [Fact]
    public async Task A_material_round_keeps_one_root_summary_delta_and_refuses_a_second()
    {
        var (sut, _, _) = Build(
            Decision(
                actions:
                [
                    new PlannedReviewAction(PersistedReviewActionKind.AppendSummaryDelta, "answered q-7"),
                    new PlannedReviewAction(PersistedReviewActionKind.AppendSummaryDelta, "and also this"),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome
            .PlannedPublicationActions.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(PersistedReviewActionKind.AppendSummaryDelta);
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("one root-summary delta");
    }

    [Fact]
    public async Task An_action_the_executor_alone_owns_cannot_be_planned_by_the_agent()
    {
        var (sut, _, _) = Build(
            Decision(actions: [new PlannedReviewAction(PersistedReviewActionKind.FinalizeRound, "done")])
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("finaliz");
    }

    // ---------------------------------------------------------------- broad review is recorded, never dispatched

    [Fact]
    public async Task A_need_for_broader_review_becomes_an_observation_and_never_an_action()
    {
        var (sut, interpreter, sink) = Build(
            Decision(broadReview: true, broadReviewReason: "the comment names an unreviewed module")
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.BroadReviewRequested.Should().BeTrue();
        outcome.PlannedPublicationActions.Should().BeEmpty("an unchanged head must not silently become a code review");
        interpreter
            .Received.Should()
            .ContainSingle("the round runs exactly one turn; it does not re-enter as a review");
        sink.Recorded.Should().Contain(o => o.Kind == ObservationKind.Gap && o.Summary.Contains("unreviewed module"));
    }

    [Fact]
    public async Task An_irrelevant_round_cannot_demand_a_broader_review()
    {
        // Relevance is the agent's own answer to "have I anything to add". A round that answers no and
        // then demands a wider review is contradicting itself in the same breath, exactly as one that
        // answers no and plans an action — and the demand is not free: the coordinator acts on it.
        var (sut, _, sink) = Build(
            Decision(relevant: false, broadReview: true, broadReviewReason: "the comment names an unreviewed module")
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.BroadReviewRequested.Should().BeFalse();
        outcome.Rejections.Should().ContainSingle().Which.Reason.Should().Contain("irrelevant");
        sink.Recorded.Should().NotContain(o => o.Summary.Contains("unreviewed module"));
    }

    // ---------------------------------------------------------------- the genuine no-op

    [Fact]
    public async Task A_genuine_no_op_plans_no_publication_and_records_a_deliberate_no_action()
    {
        var (sut, _, sink) = Build(Decision(relevant: false));

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.Participated.Should().BeFalse();
        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Actions.Should().ContainSingle().Which.Kind.Should().Be(PersistedReviewActionKind.FinalizeRound);
        sink.Recorded.Should().ContainSingle(o => o.Kind == ObservationKind.DeliberateNoAction);
    }

    [Fact]
    public async Task An_irrelevant_round_does_not_publish_actions_the_agent_planned_anyway()
    {
        var (sut, _, _) = Build(
            Decision(
                relevant: false,
                actions: [new PlannedReviewAction(PersistedReviewActionKind.ReplyToDiscussion, "hi", TargetRef: "c-5")]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.PlannedPublicationActions.Should().BeEmpty();
        outcome.Participated.Should().BeFalse();
    }

    [Fact]
    public async Task A_round_whose_every_action_was_refused_is_a_no_op_not_a_participation()
    {
        var (sut, _, sink) = Build(
            Decision(actions: [new PlannedReviewAction(PersistedReviewActionKind.CreateRootSummary, "# Review")])
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        outcome.Participated.Should().BeFalse();
        sink.Recorded.Should().Contain(o => o.Kind == ObservationKind.DeliberateNoAction);
    }

    // ---------------------------------------------------------------- observations

    [Fact]
    public async Task Observations_are_appended_once_and_include_what_the_agent_recorded()
    {
        var (sut, _, sink) = Build(
            Decision(
                observations: [new RoundObservationDraft(ObservationKind.Correction, "my earlier claim was wrong")],
                actions:
                [
                    new PlannedReviewAction(
                        PersistedReviewActionKind.ReplyToDiscussion,
                        "correcting",
                        TargetRef: "c-5"
                    ),
                ]
            )
        );

        var outcome = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        sink.Calls.Should().Be(1, "one round appends one batch");
        sink.RoundIds.Should().Equal([RoundId]);
        sink.Recorded.Should().Contain(o => o.Kind == ObservationKind.Correction);
        outcome.Observations.Should().BeEquivalentTo(sink.Recorded);
    }

    [Fact]
    public async Task A_blank_observation_summary_is_dropped_rather_than_stored_as_an_empty_row()
    {
        var (sut, _, sink) = Build(Decision(observations: [new RoundObservationDraft(ObservationKind.Finding, "   ")]));

        _ = await sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        sink.Recorded.Should().NotContain(o => o.Kind == ObservationKind.Finding);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task Cancellation_before_the_round_starts_reaches_no_agent_and_writes_nothing()
    {
        var (sut, interpreter, sink) = Build(Decision());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.ExecuteAsync(Context(), Later, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        interpreter.Received.Should().BeEmpty();
        sink.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_raised_by_the_agent_propagates_and_writes_nothing()
    {
        var interpreter = new FakeInterpreter(new OperationCanceledException());
        var sink = new RecordingSink();
        var sut = new DiscussionRoundExecutor(interpreter, sink, LoggerFactory.CreateLogger<DiscussionRoundExecutor>());

        var act = () => sut.ExecuteAsync(Context(), Later, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Calls.Should().Be(0);
    }

    [Fact]
    public async Task The_cancellation_token_is_handed_to_both_the_agent_and_the_sink()
    {
        var (sut, interpreter, sink) = Build(Decision(relevant: false));
        using var cts = new CancellationTokenSource();

        _ = await sut.ExecuteAsync(Context(), Later, cts.Token);

        interpreter.Tokens.Should().Equal([cts.Token]);
        sink.Tokens.Should().Equal([cts.Token]);
    }

    // ---------------------------------------------------------------- doubles

    private sealed class FakeInterpreter : IDiscussionInterpreter
    {
        private readonly DiscussionDecision? _decision;
        private readonly Exception? _throw;

        public FakeInterpreter(DiscussionDecision decision) => _decision = decision;

        public FakeInterpreter(Exception toThrow) => _throw = toThrow;

        public List<DiscussionRoundInput> Received { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<DiscussionDecision> InterpretAsync(
            DiscussionRoundInput input,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken
        )
        {
            Received.Add(input);
            Tokens.Add(cancellationToken);
            return _throw is not null ? Task.FromException<DiscussionDecision>(_throw) : Task.FromResult(_decision!);
        }
    }

    private sealed class RecordingSink : IRoundObservationSink
    {
        public int Calls { get; private set; }

        public List<long> RoundIds { get; } = [];

        public List<RoundObservationDraft> Recorded { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task RecordAsync(
            long roundId,
            IReadOnlyList<RoundObservationDraft> observations,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            RoundIds.Add(roundId);
            Recorded.AddRange(observations);
            Tokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }
}
