using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class ReviewFindingReconcilerTests
{
    // ── Plain bullets under a Questions heading ──────────────────────────────────────────────────────

    [Fact]
    public void A_plain_bullet_under_a_questions_heading_is_its_own_finding()
    {
        // Before the fix: the heading never opens a block by itself, and a bullet with no severity word
        // and no [QUESTION]/`Question:` marker of its own didn't either — so with nothing open, the
        // bullet's text and its citation were dropped with no trace at all.
        var findings = ReviewFindingReconciler.ParseFindings(
            "## Questions\n"
                + "- Does the retry budget reset per attempt? src/Foo.cs:10\n"
                + "- What owns cleanup on cancel? src/Bar.cs:20\n"
        );

        findings.Should().HaveCount(2);
        findings[0].IsQuestion.Should().BeTrue();
        findings[0].Citations.Should().ContainSingle(c => c.Path == "src/Foo.cs" && c.StartLine == 10);
        findings[1].IsQuestion.Should().BeTrue();
        findings[1].Citations.Should().ContainSingle(c => c.Path == "src/Bar.cs" && c.StartLine == 20);
    }

    [Fact]
    public void A_plain_bullet_outside_a_questions_heading_still_opens_nothing()
    {
        // The fix only broadens what counts as an opener under a Questions heading. Elsewhere, a bullet
        // with no severity word and no question marker of its own must stay invisible exactly as before.
        ReviewFindingReconciler
            .ParseFindings("## Notes\n- Saw this at src/Foo.cs:10, looks fine.\n")
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_finding_tally_under_a_questions_heading_is_still_not_a_finding()
    {
        // The heading broadens what OPENS a block, but IsNotAFinding is still checked first — a tally
        // line does not become a question just because it sits under a Questions heading.
        ReviewFindingReconciler
            .ParseFindings("## Questions\n- 2 HIGH/BLOCKER findings: tracked separately.\n")
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_nested_bullet_under_a_questions_heading_stays_folded_into_its_parent()
    {
        // ListItemLine only matches 0-3 leading spaces; the fix touches the top-level branch only, so an
        // indented sub-bullet must still fold into whatever block is open rather than starting its own.
        var findings = ReviewFindingReconciler.ParseFindings(
            "## Questions\n" + "- Does retry reset per attempt? src/Foo.cs:10\n" + "    - see also src/Bar.cs:99\n"
        );

        findings.Should().ContainSingle();
        findings[0].Citations.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_one_to_three_space_nested_bullet_under_a_questions_heading_stays_folded_into_its_parent(
        int indentSpaces
    )
    {
        var indent = new string(' ', indentSpaces);
        var findings = ReviewFindingReconciler.ParseFindings(
            "## Questions\n" + "- Does retry reset per attempt? src/Foo.cs:10\n" + $"{indent}- see also src/Bar.cs:99\n"
        );

        findings.Should().ContainSingle("the nested bullet is content of the open question, not a sibling");
        findings[0].Citations.Should().HaveCount(2, "both the parent's and the nested bullet's citations survive");
    }

    [Fact]
    public void Genuine_sibling_top_level_question_bullets_stay_separate_even_after_a_nested_bullet()
    {
        // The indentation guard must not over-fold: a later bullet back at the same (top) indentation as
        // the question list is a real sibling question and must still open its own block.
        var findings = ReviewFindingReconciler.ParseFindings(
            "## Questions\n"
                + "- Does retry reset per attempt? src/Foo.cs:10\n"
                + "  - see also src/Bar.cs:99\n"
                + "- What owns cleanup on cancel? src/Baz.cs:30\n"
        );

        findings.Should().HaveCount(2);
        findings[0].Citations.Should().HaveCount(2, "the nested bullet folds into the first question");
        findings[1].Citations.Should().ContainSingle(c => c.Path == "src/Baz.cs" && c.StartLine == 30);
    }
}
