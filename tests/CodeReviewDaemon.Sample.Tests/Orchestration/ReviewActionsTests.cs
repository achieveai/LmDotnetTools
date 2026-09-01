using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Issue #649 — the tolerant `review-actions` fenced-block parser. Tests are grouped by outcome
/// under the section headers below: 8a per-action rejection (a bad action never takes the block
/// down), 8b whole-block structural failure, and 8c valid/neutral outcomes.
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
        result
            .Actions.Should()
            .ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good" && a.Index == 1);
    }

    [Fact]
    public void Reply_missing_body_is_rejected()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: reply\n" + "  ref: gh-review:123\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result
            .Rejections.Should()
            .ContainSingle(r => r.Index == 0 && r.Reason == "reply requires body" && r.Ref == "gh-review:123");
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
    public void Finding_with_range_like_line_value_is_rejected_but_sibling_summary_survives()
    {
        // RawReviewAction.Line is string?, not int?: a per-action line value that isn't a plain
        // integer (e.g. a copied range like "42-58") must reject only this one finding. Before the
        // fix-round-1 correction, Line was typed int? directly, so YamlDotNet's type converter threw
        // while deserializing the *whole* list the moment this action's line failed to parse as an
        // int — turning a should-be-per-action rejection into an incorrect WholeBlockFailed=true.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "- kind: finding\n"
                + "  path: src/Foo.cs\n"
                + "  line: 42-58\n"
                + "  body: \"oops\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result
            .Actions.Should()
            .ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good" && a.Index == 0);
        result.Rejections.Should().ContainSingle(r => r.Index == 1 && r.Reason == "finding requires a positive line");
    }

    [Fact]
    public void Finding_with_nonnumeric_line_value_is_rejected_but_sibling_summary_survives()
    {
        // Same regression guard as above, for a non-numeric value ("L42") rather than a range.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "- kind: finding\n"
                + "  path: src/Foo.cs\n"
                + "  line: L42\n"
                + "  body: \"oops\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result
            .Actions.Should()
            .ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good" && a.Index == 0);
        result.Rejections.Should().ContainSingle(r => r.Index == 1 && r.Reason == "finding requires a positive line");
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
    public void Missing_kind_is_rejected_with_missing_placeholder_but_sibling_survives()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- body: \"no kind field\"\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "unknown kind '(missing)'");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Index == 1);
    }

    [Fact]
    public void Blank_kind_is_rejected_with_empty_reason_but_sibling_survives()
    {
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: \"\"\n"
                + "  body: \"blank kind\"\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "unknown kind ''");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Index == 1);
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
        // Pins the one-newline Markdown join semantics exactly: both fenced spans (including their
        // own trailing newlines) are removed, leaving the prose before the first fence joined to the
        // prose between the two fences by a single '\n' each — not zero, not two.
        result.Markdown.Should().Be("Intro\nMore text");
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
    public void Reply_and_finding_both_round_trip_their_type_specific_fields()
    {
        // Round-trip guard: both non-summary kinds must carry their type-specific fields (Ref for
        // reply; Path/Line for finding) through onto ReviewAction unchanged. This is NOT itself the
        // enum-direct-deserialization regression guard — neither action here has an invalid `kind`,
        // so this test would pass even if Kind were (incorrectly) typed as the ReviewActionKind enum
        // directly. That regression is instead caught by
        // Unknown_kind_is_rejected_but_sibling_valid_action_survives above: with a direct-enum Kind,
        // YamlDotNet would throw while deserializing the whole list the moment it hit "bogus",
        // turning that test's expected per-action rejection into an incorrect WholeBlockFailed=true.
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

    // ---- Fix-round-1 (#649 blocker 2): Markdown-preservation and fence-scan mutation coverage ----

    [Fact]
    public void Single_valid_fence_with_prose_before_and_after_preserves_both_exactly()
    {
        // Pins the one-newline Markdown join semantics exactly for the common case: prose before and
        // after a single valid fence must survive verbatim, joined by exactly one '\n' each — proving
        // the span-removal only removes the fence's own lines, not a prefix- or suffix-only slice of
        // the surrounding text.
        var result = ReviewActionsParser.Parse(
            "Intro text\n"
                + "```review-actions\n"
                + "- kind: summary\n"
                + "  body: \"all good\"\n"
                + "```\n"
                + "Outro text"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Markdown.Should().Be("Intro text\nOutro text");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good");
    }

    [Fact]
    public void Crlf_line_endings_around_a_valid_fence_still_parse_and_preserve_markdown()
    {
        // Fence-line matching uses TrimEnd() (which strips a trailing '\r' along with other trailing
        // whitespace), so CRLF documents must be tolerated for free. Removing that TrimEnd() call (or
        // narrowing it to only '\n') would make this fence go undetected under CRLF, and this test
        // would fail — proving the CRLF tolerance is load-bearing, not accidental.
        var result = ReviewActionsParser.Parse(
            "Intro text\r\n"
                + "```review-actions\r\n"
                + "- kind: summary\r\n"
                + "  body: \"all good\"\r\n"
                + "```\r\n"
                + "Outro text"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Markdown.Should().Be("Intro text\r\nOutro text");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Body == "all good");
    }

    [Fact]
    public void Multiline_block_scalar_body_containing_an_indented_fence_line_is_not_truncated()
    {
        // The closing-fence scan matches only an exact `TrimEnd() == "```"` line (no leading
        // whitespace stripped), so an indented ``` inside a YAML block-scalar body must NOT be
        // mistaken for the real closing fence. Changing that comparison to Trim() (stripping leading
        // whitespace too) would make the indented ``` below match first, truncating the fence body
        // before the real close and losing "more text" from the parsed action entirely.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n"
                + "- kind: summary\n"
                + "  body: |\n"
                + "    Some text\n"
                + "    ```\n"
                + "    more text\n"
                + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().BeEmpty();
        var action = result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary).Subject;
        action.Body.Should().Contain("```");
        action.Body.Should().Contain("more text");
    }

    // ---- Fix-round-2 (PR #659 review 5486805528): null-item and top-level-null structural coverage ----

    [Fact]
    public void Null_sequence_item_is_rejected_but_sibling_summary_survives()
    {
        // A bare `-` with nothing after it deserializes as a null list element (not a
        // RawReviewAction), even though the surrounding sequence is otherwise well-formed YAML.
        // Dereferencing it directly (e.g. `raw.Kind`) would throw a NullReferenceException instead
        // of the per-action rejection the "never throws for malformed content" contract promises.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "-\n" + "- kind: summary\n" + "  body: \"all good\"\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "action must be a mapping");
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Index == 1);
    }

    [Fact]
    public void Null_sequence_item_after_a_valid_summary_preserves_its_own_index()
    {
        // Same defect class as above, with the null item second — proves the rejection's Index
        // reflects its own position in the raw list (1), not a renumbered/derived position, and that
        // the valid sibling ahead of it is unaffected.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: summary\n" + "  body: \"all good\"\n" + "-\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Actions.Should().ContainSingle(a => a.Kind == ReviewActionKind.Summary && a.Index == 0);
        result.Rejections.Should().ContainSingle(r => r.Index == 1 && r.Reason == "action must be a mapping");
    }

    [Fact]
    public void Top_level_null_fence_body_fails_the_whole_block()
    {
        // A fence body whose only content is an explicit YAML null (the literal token `null`) still
        // deserializes to a null list — same as a genuinely empty body — but it is not "no actions
        // offered": the document never resolved to a sequence at all, so this must be a structural
        // whole-block failure, distinct from the empty-fence-body case below.
        var result = ReviewActionsParser.Parse("```review-actions\n" + "null\n" + "```");

        result.WholeBlockFailed.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().ContainSingle(r => r.Index == -1 && r.Reason == WholeBlockFailureReason);
    }

    [Fact]
    public void Explicit_empty_sequence_bracket_is_valid_with_zero_actions()
    {
        // `[]` deserializes to a non-null, zero-count list, so it must be treated the same as a
        // genuinely empty fence body (valid, zero actions) — not conflated with the top-level-null
        // case above, which never produces a list at all.
        var result = ReviewActionsParser.Parse("```review-actions\n" + "[]\n" + "```");

        result.WholeBlockFailed.Should().BeFalse();
        result.Actions.Should().BeEmpty();
        result.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Explicit_null_kind_value_is_treated_as_missing_kind()
    {
        // A different null edge than the two above: here the *item* is a real mapping (not a null
        // list element), but its `kind` field is an explicit YAML null rather than simply absent.
        // Both must land on the same "(missing)" placeholder reason, since RawReviewAction.Kind is
        // string? either way.
        var result = ReviewActionsParser.Parse(
            "```review-actions\n" + "- kind: null\n" + "  body: \"no real kind\"\n" + "```"
        );

        result.WholeBlockFailed.Should().BeFalse();
        result.Rejections.Should().ContainSingle(r => r.Index == 0 && r.Reason == "unknown kind '(missing)'");
    }

    [Fact]
    public void Null_response_argument_still_throws_argument_null_exception()
    {
        // The "never throws" contract is scoped to malformed *content*; a null response argument
        // remains a caller error per the Parse XML doc, and must still throw rather than being
        // silently treated as empty input.
        var act = () => ReviewActionsParser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
