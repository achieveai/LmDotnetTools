using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The <c>discussion-decision</c> fence a follow-up turn emits. Tolerant like the synthesis turn's
/// <c>review-actions</c> fence — model output is never allowed to throw at the caller — but a decision
/// that could not be read is reported as a durable <see cref="ObservationKind.Gap"/> and an irrelevant
/// round, never as a silently empty success.
/// </summary>
public sealed class DiscussionDecisionParserTests
{
    private static string Fenced(string yaml) => $"Some prose.\n\n```discussion-decision\n{yaml}\n```\n";

    [Fact]
    public void A_response_with_no_fence_is_a_no_op_decision_that_keeps_the_prose()
    {
        var decision = DiscussionDecisionParser.Parse("Nothing here needs me.");

        decision.IsRelevant.Should().BeFalse();
        decision.Actions.Should().BeEmpty();
        decision.QuestionInterpretations.Should().BeEmpty();
        decision.Prose.Should().Be("Nothing here needs me.");
    }

    [Fact]
    public void An_unparsable_fence_yields_a_gap_observation_rather_than_an_empty_success()
    {
        var decision = DiscussionDecisionParser.Parse(Fenced("relevant: [unterminated"));

        decision.IsRelevant.Should().BeFalse();
        decision
            .Observations.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(ObservationKind.Gap, "a decision nobody could read is a gap, not a decision to do nothing");
    }

    [Fact]
    public void The_prose_outside_the_fence_survives_parsing()
    {
        var decision = DiscussionDecisionParser.Parse(Fenced("relevant: true"));

        decision.Prose.Should().Be("Some prose.");
    }

    [Fact]
    public void An_actions_member_is_refused_without_losing_local_bookkeeping()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced(
                """
                relevant: true
                questions:
                  - questionId: q-7
                    state: answered
                    candidates: [c-5]
                    evidence: ["9:hash-9"]
                actions:
                  - kind: finding
                    body: The retry budget is rebuilt on every resume.
                    path: src/Retry.cs
                    line: 42
                    causes: [c-5]
                    evidence: ["9:hash-9"]
                observations:
                  - kind: answer
                    summary: q-7 is answered
                    evidence: ["9:hash-9"]
                """
            )
        );

        decision.IsRelevant.Should().BeTrue();
        decision.Actions.Should().BeEmpty("provider publication is authorized only by typed parent tool calls");
        decision.QuestionInterpretations.Should().ContainSingle().Which.QuestionId.Should().Be("q-7");
        decision.Observations.Should().Contain(o => o.Kind == ObservationKind.Answer);
        decision
            .Observations.Should()
            .Contain(
                o => o.Kind == ObservationKind.Gap && o.Summary.Contains("typed publication", StringComparison.Ordinal),
                "the forbidden action handoff must leave a durable refusal"
            );
    }

    [Fact]
    public void Action_contents_are_not_interpreted_and_local_observations_survive()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced(
                """
                relevant: true
                actions:
                  - kind: launch-specialists
                    body: fan out
                  - kind: reply
                    body: answered
                    ref: c-1
                    evidence: ["not-a-ref"]
                observations:
                  - kind: correction
                    summary: the earlier explanation was incomplete
                """
            )
        );

        decision.Actions.Should().BeEmpty();
        decision.Observations.Should().ContainSingle(o => o.Kind == ObservationKind.Correction);
        decision
            .Observations.Should()
            .ContainSingle(
                o => o.Kind == ObservationKind.Gap && o.Summary.Contains("typed publication", StringComparison.Ordinal),
                "the host refuses the entire publication member without interpreting model-authored action data"
            );
    }

    [Fact]
    public void Question_interpretations_read_state_candidates_and_evidence()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced(
                """
                relevant: true
                questions:
                  - questionId: q-7
                    state: contested
                    candidates: [c-5, c-6]
                    evidence: ["9:hash-9", "10:hash-10"]
                """
            )
        );

        var interpretation = decision.QuestionInterpretations.Should().ContainSingle().Subject;
        interpretation.QuestionId.Should().Be("q-7");
        interpretation.State.Should().Be(ClarificationQuestionState.Contested);
        interpretation.CandidateCommentIds.Should().Equal(["c-5", "c-6"]);
        interpretation.Evidence.Should().HaveCount(2);
    }

    [Fact]
    public void An_unknown_question_state_is_dropped_with_a_gap()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced("relevant: true\nquestions:\n  - questionId: q-7\n    state: probably\n    candidates: [c-5]")
        );

        decision.QuestionInterpretations.Should().BeEmpty();
        decision.Observations.Should().Contain(o => o.Kind == ObservationKind.Gap);
    }

    [Fact]
    public void A_question_state_that_names_no_member_is_dropped_however_it_is_spelled()
    {
        // Enum.TryParse alone answers TRUE for three things the protocol never offers: an undefined
        // ordinal ("99" becomes (ClarificationQuestionState)99), a defined one nothing writes by number,
        // and a comma list folded bitwise into an unrelated member ("Open,Answered" becomes Answered).
        // Every one of them reads downstream as a state the model never named.
        string[] notNames = ["99", "1", "Open,Answered"];

        foreach (var spelling in notNames)
        {
            var decision = DiscussionDecisionParser.Parse(
                Fenced(
                    $"relevant: true\nquestions:\n  - questionId: q-7\n    state: \"{spelling}\"\n    candidates: [c-5]"
                )
            );

            decision.QuestionInterpretations.Should().BeEmpty($"'{spelling}' names no question state");
            decision.Observations.Should().Contain(o => o.Kind == ObservationKind.Gap);
        }
    }

    [Fact]
    public void An_observation_kind_that_names_no_member_is_dropped_however_it_is_spelled()
    {
        string[] notNames = ["99", "Finding,Gap"];

        foreach (var spelling in notNames)
        {
            var decision = DiscussionDecisionParser.Parse(
                Fenced($"relevant: true\nobservations:\n  - kind: \"{spelling}\"\n    summary: something")
            );

            decision
                .Observations.Should()
                .ContainSingle($"'{spelling}' names no observation kind, so only the gap remains")
                .Which.Kind.Should()
                .Be(ObservationKind.Gap);
        }
    }

    [Fact]
    public void A_null_observation_entry_is_reported_as_not_a_mapping_rather_than_an_unknown_kind()
    {
        // A bare "-" is an entry that is not a mapping at all. Calling it an unknown KIND tells an
        // operator to look for a kind token that was never written, and hides the shape error that was.
        var decision = DiscussionDecisionParser.Parse(Fenced("relevant: true\nobservations:\n  -\n"));

        var gap = decision.Observations.Should().ContainSingle().Subject;
        gap.Kind.Should().Be(ObservationKind.Gap);
        gap.Summary.Should().Contain("not a mapping").And.NotContain("unknown kind");
    }

    [Fact]
    public void An_indented_decision_fence_is_still_read()
    {
        // Models indent a fence when they nest it under a list item or a quote. Matching the marker only
        // at column zero turns that formatting choice into a decision nobody reads — and, because the
        // unread turn degrades to "relevant: false", into a round that looks like a deliberate no-op.
        var decision = DiscussionDecisionParser.Parse(
            "Some prose.\n\n  ```discussion-decision\n  relevant: true\n  observations:\n"
                + "    - kind: answer\n      summary: per-run\n  ```\n"
        );

        decision.IsRelevant.Should().BeTrue();
        decision.Observations.Should().ContainSingle().Which.Kind.Should().Be(ObservationKind.Answer);
    }

    [Fact]
    public void A_broad_review_need_is_read_as_a_flag_and_a_reason_never_as_an_action()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced("relevant: true\nbroadReviewNeeded: true\nbroadReviewReason: the comment names an unreviewed module")
        );

        decision.BroadReviewNeeded.Should().BeTrue();
        decision.BroadReviewReason.Should().Be("the comment names an unreviewed module");
        decision.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Observations_are_read_with_their_kind_and_summary()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced(
                """
                relevant: true
                observations:
                  - kind: correction
                    summary: my earlier claim about Foo.cs:10 was wrong
                    evidence: ["9:hash-9"]
                """
            )
        );

        var observation = decision.Observations.Should().ContainSingle().Subject;
        observation.Kind.Should().Be(ObservationKind.Correction);
        observation.Summary.Should().Be("my earlier claim about Foo.cs:10 was wrong");
        observation.Evidence.Should().Equal([new AuditSourceReference("9", "hash-9")]);
    }

    [Fact]
    public void A_question_interpretation_with_no_questionId_is_dropped_with_a_gap()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced("relevant: true\nquestions:\n  - state: answered\n    candidates: [c-5]")
        );

        decision.QuestionInterpretations.Should().BeEmpty();
        decision.Observations.Should().Contain(o => o.Kind == ObservationKind.Gap);
    }

    [Fact]
    public void A_malformed_evidence_ref_is_dropped_rather_than_invented()
    {
        // "evidence" that cannot name BOTH a source record and its hash is not evidence. The record id is
        // a free-form string, so the only thing distinguishing a ref from prose is that it carries a
        // non-empty half either side of the first colon — a citation missing one half cannot be checked
        // later, and an unverifiable citation is worse than none because it reads as verified.
        string[] malformed = ["not-a-ref", ":hash-only", "id-only:"];

        foreach (var ref_ in malformed)
        {
            var decision = DiscussionDecisionParser.Parse(
                Fenced(
                    $"relevant: true\nquestions:\n  - questionId: q-7\n    state: answered\n    candidates: [c-5]\n    evidence: [\"{ref_}\"]"
                )
            );

            decision
                .QuestionInterpretations.Should()
                .ContainSingle()
                .Which.Evidence.Should()
                .BeEmpty($"'{ref_}' does not carry both an id and a hash");
        }
    }

    [Fact]
    public void A_dropped_evidence_ref_leaves_a_gap_naming_it()
    {
        var decision = DiscussionDecisionParser.Parse(
            Fenced("relevant: true\nobservations:\n  - kind: correction\n    summary: s\n    evidence: [\"not-a-ref\"]")
        );

        decision
            .Observations.Should()
            .Contain(
                o => o.Kind == ObservationKind.Gap && o.Summary.Contains("not-a-ref"),
                "a lost local-bookkeeping citation must remain visible"
            );
    }

    [Fact]
    public void A_dropped_evidence_ref_is_reported_wherever_local_bookkeeping_cited_it()
    {
        (string Yaml, string Expected)[] sites =
        [
            (
                "questions:\n  - questionId: q-7\n    state: answered\n    candidates: [c-5]\n    evidence: [\"bad\"]",
                "q-7"
            ),
            ("observations:\n  - kind: correction\n    summary: s\n    evidence: [\"bad\"]", "correction"),
        ];

        foreach (var (yaml, expected) in sites)
        {
            var decision = DiscussionDecisionParser.Parse(Fenced($"relevant: true\n{yaml}"));

            decision
                .Observations.Should()
                .Contain(
                    o => o.Kind == ObservationKind.Gap && o.Summary.Contains("bad") && o.Summary.Contains(expected),
                    $"the gap must say where '{expected}' cited the unusable ref"
                );
        }
    }

    [Fact]
    public void An_evidence_ref_keeps_a_non_numeric_record_id_verbatim()
    {
        // Audit source record ids are free-form strings (a guid, "src-3"). Requiring a number here would
        // silently discard the evidence behind every real citation the daemon can produce.
        var decision = DiscussionDecisionParser.Parse(
            Fenced(
                "relevant: true\nquestions:\n  - questionId: q-7\n    state: answered\n    candidates: [c-5]\n    evidence: [\"src-3:sha-3\"]"
            )
        );

        decision
            .QuestionInterpretations.Should()
            .ContainSingle()
            .Which.Evidence.Should()
            .Equal([new AuditSourceReference("src-3", "sha-3")]);
    }

    [Fact]
    public void Parsing_a_null_response_is_a_caller_error_not_malformed_content()
    {
        var act = () => DiscussionDecisionParser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
