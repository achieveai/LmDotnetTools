using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Issue #649 — the tolerant `review-actions` fenced-block parser. Test matrix numbers in the
/// `[Fact]` names below (e.g. "8a_1") trace back to the acceptance-criteria matrix in
/// `.claude/scratchpad/issue-649-implementation-brief.md` §8, so a reviewer can check coverage
/// against the brief directly rather than re-deriving it from these names.
/// </summary>
public sealed class ReviewActionsTests
{
    private const string WholeBlockFailureReason = "review-actions block could not be parsed";

    // ---- 8a: individual-action rejection — WholeBlockFailed stays false, only the bad action drops ----

    [Fact]
    public void Reply_missing_ref_is_rejected_but_sibling_summary_survives()
    {
        var result = ReviewActionsParser.Parse(
            "Review text\n"
                + "```review-actions\n"
                + "- kind: reply\n"
                + "  body: \"hello\"\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "reply requires ref");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good");
    }

    [Fact]
    public void Reply_missing_body_is_rejected()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: reply\n" + "  ref: gh-review:123\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "reply requires body");
        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Finding_missing_path_is_rejected()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: finding\n" + "  line: 3\n" + "  body: \"oops\"\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "finding requires path");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Finding_with_non_positive_line_is_rejected(int line)
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: finding\n"
                + "  path: src/Foo.cs\n"
                + $"  line: {line}\n"
                + "  body: \"oops\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "finding requires a positive line");
    }

    [Fact]
    public void Finding_missing_line_entirely_is_rejected()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: finding\n" + "  path: src/Foo.cs\n" + "  body: \"oops\"\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "finding requires a positive line");
    }

    [Fact]
    public void Finding_missing_body_is_rejected()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: finding\n" + "  path: src/Foo.cs\n" + "  line: 42\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "finding requires body");
    }

    [Fact]
    public void Summary_missing_body_is_rejected()
    {
        var result = ReviewActionsParser.Parse("```review-actions\n" + "- kind: summary\n" + "```");

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "summary requires body");
    }

    [Fact]
    public void Unknown_kind_is_rejected_but_sibling_valid_action_survives()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: bogus\n"
                + "  body: \"???\"\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Kind == "bogus");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary);
    }

    [Fact]
    public void Multiple_independently_invalid_actions_do_not_short_circuit_each_other()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: reply\n"
                + "  body: \"missing ref\"\n"
                + "- kind: finding\n"
                + "  line: -1\n"
                + "  body: \"missing path and bad line\"\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().HaveCount(2);
        result.Rejections.Should().Contain(r => r.Index == 0);
        result.Rejections.Should().Contain(r => r.Index == 1);
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary);
    }

    // ---- 8b: whole-block failure — Actions empty, exactly one Index=-1 rejection, Markdown kept ----

    [Fact]
    public void Whole_block_failure_preserves_markdown()
    {
        var result = ReviewActionsParser.Parse("Review text\n```review-actions\n- kind: [\n```");

        result.Markdown.Should().Be("Review text");
        result.WholeBlockFailed.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().ContainSingle(r => r.Index == -1 && r.Reason == WholeBlockFailureReason);
    }

    [Fact]
    public void Multiple_fences_fail_the_whole_block()
    {
        var result = ReviewActionsParser.Parse(
            "Intro\n"
                + "```review-actions\n"
                + "- kind: summary\n"
                + "  body: \"first\"\n"
                + "```\n"
                + "More text\n"
                + "```review-actions\n"
                + "- kind: summary\n"
                + "  body: \"second\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().ContainSingle(r => r.Index == -1 && r.Reason == WholeBlockFailureReason);
    }

    [Fact]
    public void Unterminated_fence_fails_the_whole_block()
    {
        var result = ReviewActionsParser.Parse(
            "Review text\n" + "```review-actions\n" + "- kind: summary\n" + "  body: \"never closed\"\n"
        );

        result.WholeBlockFailed.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().ContainSingle(r => r.Index == -1 && r.Reason == WholeBlockFailureReason);
        result.Markdown.Should().Be("Review text");
    }

    [Fact]
    public void Fence_body_that_is_a_mapping_not_a_sequence_fails_the_whole_block()
    {
        var result = ReviewActionsParser.Parse("```review-actions\n" + "kind: summary\n" + "body: \"oops\"\n" + "```");

        result.WholeBlockFailed.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().ContainSingle(r => r.Index == -1 && r.Reason == WholeBlockFailureReason);
    }

    // ---- 8c: valid / neutral outcomes ----

    [Fact]
    public void No_fence_anywhere_is_valid_with_full_markdown_preserved()
    {
        const string response = "Just a plain Markdown review with no structured block.";
        var result = ReviewActionsParser.Parse(response);

        result.Actions.Should().BeEmpty();
        result.Rejections.Should().BeEmpty();
        result.WholeBlockFailed.Should().BeFalse();
        result.Markdown.Should().Be(response);
    }

    [Fact]
    public void Unknown_yaml_key_on_an_otherwise_valid_action_is_silently_ignored()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: summary\n" + "  body: \"all good\"\n" + "  confidence: 0.9\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().BeEmpty();
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good");
    }

    [Fact]
    public void Odd_but_valid_indentation_still_parses()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "  - kind: summary\n" + "    body: \"indented\"\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().BeEmpty();
        result.Actions.Should().ContainSingle(a => a.Body == "indented");
    }

    [Fact]
    public void Free_text_outside_the_fence_mentioning_action_like_words_is_never_an_action()
    {
        var result = ReviewActionsParser.Parse(
            "Please remember: kind: reply and ref: something are just words here, not YAML.\n"
                + "```review-actions\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary);
    }

    [Fact]
    public void Empty_fence_body_is_valid_with_zero_actions()
    {
        var result = ReviewActionsParser.Parse("```review-actions\n```");

        result.WholeBlockFailed.Should().BeFalse();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().BeEmpty();
    }

    // ---- Mutation-sensitivity spot checks (not in the brief's matrix, but pin behavior that a ----
    // ---- single mutated comparison operator or deleted branch could silently break) ----

    [Fact]
    public void Exactly_two_fences_is_required_to_trigger_multiple_fence_failure_not_one()
    {
        // A single well-formed fence must NOT be treated as "multiple" — guards against a
        // `> 1` -> `>= 1` (or similar) mutation on the fence-count check.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: summary\n" + "  body: \"only one\"\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Actions.Should().ContainSingle(a => a.Body == "only one");
    }

    [Fact]
    public void Four_backtick_fence_is_treated_as_absent_not_as_a_parse_failure()
    {
        // AC requires the literal 3-backtick marker; a 4-backtick line must not match, and the
        // block must be treated as simply absent (zero actions), not as a failure.
        var result = ReviewActionsParser.Parse(
            "````review-actions\n" + "- kind: summary\n" + "  body: \"nope\"\n" + "````"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Reply_with_ref_and_body_and_finding_with_all_required_fields_both_parse()
    {
        // Direct-deserialization / enum-mapping regression guard: both non-summary kinds must
        // round-trip their type-specific fields onto ReviewAction, proving Kind was mapped from the
        // raw string rather than accidentally deserialized straight onto the ReviewActionKind enum
        // (which would throw for "bogus" mid-document instead of scoping to one rejection).
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: reply\n"
                + "  ref: gh-review:123\n"
                + "  body: \"thanks\"\n"
                + "- kind: finding\n"
                + "  path: src/Foo.cs\n"
                + "  line: 42\n"
                + "  body: \"looks off\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().BeEmpty();
        result.Actions.Should().HaveCount(2);

        var reply = result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Reply).Subject;
        reply.Ref.Should().Be("gh-review:123");
        reply.Body.Should().Be("thanks");
        reply.Index.Should().Be(0);

        var finding = result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Finding).Subject;
        finding.Path.Should().Be("src/Foo.cs");
        finding.Line.Should().Be(42);
        finding.Body.Should().Be("looks off");
        finding.Index.Should().Be(1);
    }
}
