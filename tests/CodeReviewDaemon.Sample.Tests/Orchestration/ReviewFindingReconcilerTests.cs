using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Two things not covered by <see cref="ReviewNotesArtifactBuilderTests"/>, which drives the reconciler
/// through the builder end to end: the parser bug where a plain cited bullet under a Questions heading was
/// invisible, and the match-tracing fields (<see cref="ReconciledFinding.MatchScore"/>,
/// <see cref="ReconciledFinding.MatchTiedCandidates"/>) and Parser-health section that make the JOIN itself
/// auditable, as distinct from the outcome it produced.
/// </summary>
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

    // ── Match tracing: MatchScore / MatchTiedCandidates ──────────────────────────────────────────────

    [Fact]
    public void An_unambiguous_join_records_its_score_and_a_tied_count_of_one()
    {
        var sources = new[]
        {
            new ReviewFindingSource("reviewer-1", "template-1", "#### [MEDIUM] widget\nsrc/Foo.cs:10\n"),
        };
        var shippedBody = "#### [MEDIUM] widget\nsrc/Foo.cs:10\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);

        rows.Should().ContainSingle();
        rows[0].MatchScore.Should().Be(1);
        rows[0].MatchTiedCandidates.Should().Be(1);
    }

    [Fact]
    public void A_dropped_finding_carries_a_zero_score_and_zero_tied_candidates()
    {
        // Zero here means "no candidate was scored", not "a candidate scored zero" — that's why the
        // rendered Match cell (below) shows `—` rather than `0` for these rows.
        var sources = new[]
        {
            new ReviewFindingSource("reviewer-1", "template-1", "#### [MEDIUM] widget\nsrc/Foo.cs:10\n"),
        };
        var shippedBody = "#### [MEDIUM] other\nsrc/Bar.cs:99\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);

        rows.Should().ContainSingle();
        rows[0].Outcome.Should().Be(ReviewFindingOutcome.Dropped);
        rows[0].MatchScore.Should().Be(0);
        rows[0].MatchTiedCandidates.Should().Be(0);
    }

    [Fact]
    public void Two_shipped_items_scoring_equally_are_recorded_as_a_tie_and_the_first_seen_one_wins()
    {
        var sources = new[]
        {
            new ReviewFindingSource("reviewer-1", "template-1", "#### [MEDIUM] widget\nsrc/Foo.cs:10\n"),
        };
        var shippedBody = "#### [MEDIUM] alpha\nsrc/Foo.cs:10\n\n#### [MEDIUM] beta\nsrc/Foo.cs:10\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);

        rows.Should().ContainSingle();
        rows[0].MatchScore.Should().Be(1);
        rows[0].MatchTiedCandidates.Should().Be(2);
        rows[0].ShippedTitle.Should().Be("[MEDIUM] alpha");
    }

    // ── The rendered Match column ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_rendered_Match_column_shows_a_dash_for_a_dropped_row_and_the_score_for_a_kept_one()
    {
        var sources = new[]
        {
            new ReviewFindingSource(
                "reviewer-1",
                "template-1",
                "#### [MEDIUM] first\nsrc/Foo.cs:10\n\n"
                    + "#### [MEDIUM] second\nsrc/Foo.cs:20\nsrc/Foo.cs:21\nsrc/Foo.cs:22\n"
            ),
        };
        var shippedBody = "#### [MEDIUM] second\nsrc/Foo.cs:20\nsrc/Foo.cs:21\nsrc/Foo.cs:22\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);
        var rendered = ReviewFindingReconciler.Render("1", sources, rows, shippedBody);

        // "first" has no candidate at all: dash, not a zero that would read as a weak match.
        rendered.Should().Contain("`dropped` | — | — | — | —");
        // "second" shares all three citations with the one shipped item, unambiguously.
        rendered.Should().Contain("| Medium | [MEDIUM] second | — | 3 |");
    }

    [Fact]
    public void The_rendered_Match_column_flags_a_tie_with_its_candidate_count()
    {
        var sources = new[]
        {
            new ReviewFindingSource("reviewer-1", "template-1", "#### [MEDIUM] widget\nsrc/Foo.cs:10\n"),
        };
        var shippedBody = "#### [MEDIUM] alpha\nsrc/Foo.cs:10\n\n#### [MEDIUM] beta\nsrc/Foo.cs:10\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);
        var rendered = ReviewFindingReconciler.Render("1", sources, rows, shippedBody);

        rendered.Should().Contain("| 1 (tied ×2) |");
    }

    // ── Parser health ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parser_health_reports_uncited_orphaned_and_ambiguous_counts_separately_from_the_outcomes()
    {
        var sources = new[]
        {
            new ReviewFindingSource(
                "reviewer-1",
                "template-1",
                "#### [MEDIUM] uncited\nno citation here\n\n#### [MEDIUM] tied\nsrc/Foo.cs:10\n"
            ),
        };
        var shippedBody =
            "#### [MEDIUM] alpha\nsrc/Foo.cs:10\n\n"
            + "#### [MEDIUM] beta\nsrc/Foo.cs:10\n\n"
            + "#### [LOW] orphan\nsrc/Zeta.cs:1\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);
        var rendered = ReviewFindingReconciler.Render("1", sources, rows, shippedBody);

        rendered.Should().Contain("## Parser health");
        rendered.Should().Contain("| Specialist findings parsed | 2 |");
        rendered.Should().Contain("| ...citing no `path:line` (cannot be matched by construction) | 1 |");
        rendered.Should().Contain("| Shipped findings parsed | 3 |");
        rendered.Should().Contain("| ...never cited by any specialist finding above | 2 |");
        rendered.Should().Contain("| Matches decided by a tie (see the `Match` column) | 1 |");
    }

    [Fact]
    public void Duplicate_shipped_titles_are_counted_as_distinct_identities_not_collapsed()
    {
        // F-006: two DISTINCT shipped items share a byte-identical title but cite different lines. Only
        // the first is ever cited by a specialist finding, so exactly one of the two is a real orphan.
        // A title-keyed HashSet would see "widget" already marked cited by the first match and read the
        // second, uncited item as cited too — undercounting the orphan total.
        var sources = new[]
        {
            new ReviewFindingSource("reviewer-1", "template-1", "#### [LOW] widget\nsrc/Foo.cs:10\n"),
        };
        var shippedBody = "#### [LOW] widget\nsrc/Foo.cs:10\n\n#### [LOW] widget\nsrc/Zeta.cs:99\n";

        var rows = ReviewFindingReconciler.Reconcile(sources, shippedBody);
        var rendered = ReviewFindingReconciler.Render("1", sources, rows, shippedBody);

        rows.Should().ContainSingle();
        rows[0].ShippedTitle.Should().Be("[LOW] widget");
        rows[0].ShippedIndex.Should().Be(0, "the first shipped item is the one that shares the citation");

        rendered.Should().Contain("| Shipped findings parsed | 2 |");
        rendered
            .Should()
            .Contain(
                "| ...never cited by any specialist finding above | 1 |",
                "the second same-titled shipped item was never cited by anything"
            );
    }
}
