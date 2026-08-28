using System.Globalization;
using CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The four files a developer, and later a coding agent, actually read.
/// <para>
/// The failure mode here is silent and personal: a section that quietly disappears when it is empty, a
/// count printed without the corpus it came from, or a decimal comma that makes every regeneration a diff.
/// None of those throw and none fail a build, so the guard is these tests or there is no guard.
/// </para>
/// </summary>
public sealed class DeveloperLearningsRendererTests
{
    private const string ExceptionDim = "code-reviewer:exception-handling-review";
    private const string Slug = "jane-doe-contoso-com-a1b2c3d4e5f6";

    private static readonly DateTimeOffset Now = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    private static DeveloperLearningsView Empty(string slug = Slug) =>
        DeveloperLearningsView.Build(
            slug,
            [],
            [],
            new Dictionary<string, PatternProse>(StringComparer.Ordinal),
            LearningsThresholds.Defaults,
            Now);

    private static PatternStanding Standing(
        string patternId,
        PatternState state,
        bool regressed = false,
        bool provisional = false,
        double luck = 0.01,
        int cleanStreak = 4,
        int occurrences = 3,
        int exposedInWindow = 8) =>
        new(
            PatternId: patternId,
            Dimension: ExceptionDim,
            Occurrences: occurrences,
            ExposedInWindow: exposedInWindow,
            Rate: 0.4,
            CleanStreak: cleanStreak,
            LuckProbability: luck,
            State: state,
            Regressed: regressed,
            Provisional: provisional,
            FirstSeenUtc: "2026-01-05T00:00:00Z",
            LastSeenUtc: "2026-02-09T00:00:00Z",
            RegressedAtUtc: regressed ? "2026-02-09T00:00:00Z" : null,
            StreakBrokenAt: regressed ? 7 : null);

    private static PatternView Pattern(
        string patternId,
        PatternState state,
        bool regressed = false,
        bool provisional = false,
        double luck = 0.01,
        int cleanStreak = 4,
        string? howToAvoid = null,
        TrendDirection trend = TrendDirection.InsufficientData) =>
        new(
            Standing(patternId, state, regressed, provisional, luck, cleanStreak),
            new PatternProse(
                "Title for " + patternId,
                "What " + patternId + " is.",
                "Why " + patternId + " matters.",
                howToAvoid ?? ("Avoid " + patternId + ".")),
            trend == TrendDirection.InsufficientData
                ? new PatternTrend(trend, null, null, 3, 1)
                : new PatternTrend(trend, 0.25, 0.5, 10, 10),
            [new PatternSighting("azure-devops/o/r/p/5001", "2026-01-05T00:00:00Z", "MEDIUM", "src/Foo.cs:42")]);

    private static DeveloperLearningsView View(
        IReadOnlyList<PatternView> patterns,
        string slug = Slug,
        string? lastObserved = "2026-02-09T00:00:00Z",
        int observations = 12) =>
        new(
            slug,
            DeveloperLearningsView.Iso(Now),
            observations,
            "2026-01-05T00:00:00Z",
            lastObserved,
            patterns,
            [new DimensionTrend(ExceptionDim, [new DimensionWindow(ExceptionDim, 10, 4, 0.4, false)])],
            LearningsThresholds.Defaults);

    [Fact]
    public void Every_profile_section_prints_for_a_developer_with_nothing_recorded()
    {
        // §9: an empty section prints and states it is empty. A missing section and an empty one are
        // different facts, and only one of them means the daemon failed to write something.
        var profile = DeveloperLearningsRenderer.RenderProfile(Empty());

        _ = profile.Should()
            .Contain("## Snapshot")
            .And.Contain("## Regressions")
            .And.Contain("## Active patterns")
            .And.Contain("## Watch")
            .And.Contain("## Resolved")
            .And.Contain("## Unjudgeable")
            .And.Contain("## Provenance");
        _ = profile.Should()
            .Contain("_No pattern has come back after resolving._")
            .And.Contain("_No active patterns._")
            .And.Contain("_No patterns are on watch._")
            .And.Contain("_No pattern has resolved yet._")
            .And.Contain("_No patterns are unjudgeable._");
        profile.Should().Contain("| First observed | never |").And.Contain("| Last observed | never |");
    }

    [Fact]
    public void Every_progress_section_prints_for_a_developer_with_nothing_recorded()
    {
        var progress = DeveloperLearningsRenderer.RenderProgress(Empty());

        _ = progress.Should()
            .Contain("## 1. Resolved")
            .And.Contain("## 2. Regressed after resolution")
            .And.Contain("## 3. Trend")
            .And.Contain("## 4. Not enough exposure to judge");
        progress.Should()
            .Contain("_No pattern has resolved yet._")
            .And.Contain("_No resolved pattern has come back._")
            .And.Contain("_No specialist has completed on any of this developer's PRs._")
            .And.Contain("_Every pattern has enough exposure to judge._");
    }

    [Fact]
    public void The_checklist_says_so_rather_than_rendering_an_empty_list()
    {
        DeveloperLearningsRenderer
            .RenderChecklist(Empty())
            .Should()
            .Contain("_No active patterns. There is nothing to watch for on this developer's PRs._");
    }

    [Fact]
    public void The_index_says_so_when_no_developer_has_a_ledger()
    {
        DeveloperLearningsRenderer
            .RenderIndex([], Now)
            .Should()
            .Contain("_No developer has an observation ledger yet._");
    }

    [Fact]
    public void Rendered_files_carry_no_carriage_returns()
    {
        // These files are committed and diffed. Taking the platform's newline would make the same ledger
        // render differently on a different host, and every regeneration would be noise.
        var view = View([Pattern("missing-null-guard", PatternState.Active)]);

        _ = DeveloperLearningsRenderer.RenderProfile(view).Should().NotContain("\r");
        _ = DeveloperLearningsRenderer.RenderProgress(view).Should().NotContain("\r");
        _ = DeveloperLearningsRenderer.RenderChecklist(view).Should().NotContain("\r");
        DeveloperLearningsRenderer.RenderIndex([view], Now).Should().NotContain("\r");
    }

    [SkippableFact]
    public void Numbers_are_written_with_an_invariant_decimal_point()
    {
        // This test CANNOT FAIL on a runtime with invariant globalization, because no locale there can
        // produce a decimal comma however the renderer is written. Reporting that as a pass would put it in
        // every coverage count we quote while guarding nothing, so it reports SKIPPED instead.
        //
        // The condition probes the capability rather than the environment variable: if de-DE cannot produce
        // a separator different from the invariant one, this machine cannot tell the two apart.
        Skip.If(
            CommaLocaleUnavailable,
            "invariant globalization: no locale on this runtime can produce a decimal comma, so this "
                + "assertion could not fail and a pass would mean nothing");

        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Rendering UNDER a comma locale, not merely asserting the shape of the output. If any number
            // in the renderer reached for CurrentCulture, "0.95" would come back as "0,95" here — and a
            // committed file would then differ on every machine that regenerated it.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var profile = DeveloperLearningsRenderer.RenderProfile(View([Pattern("p", PatternState.Active)]));

            _ = profile.Should().Contain("| ResolutionConfidence | 0.95 |");
            profile.Should().NotContain("0,95");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Whether this runtime can produce a number format that differs from the invariant one at all. Under
    /// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> every culture behaves as invariant, and on some
    /// configurations constructing one throws outright; both mean the same thing here.
    /// </summary>
    private static bool CommaLocaleUnavailable
    {
        get
        {
            try
            {
                return string.Equals(
                    CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator,
                    CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator,
                    StringComparison.Ordinal);
            }
            catch (CultureNotFoundException)
            {
                return true;
            }
        }
    }

    [Fact]
    public void The_renderer_buckets_by_the_recorded_state_and_never_re_derives_it()
    {
        // Deliberately impossible input: a Resolved standing whose luck probability is 0.90, which no
        // classifier would ever produce. It renders under Resolved anyway.
        //
        // That is the property that makes these renderers safe to write while the state-precedence question
        // is still open with the owner: if the precedence changes, only the ledger moves. A renderer that
        // re-derived a state from the arithmetic would have to change too, and would disagree with the
        // ledger in the meantime.
        var view = View([Pattern("p", PatternState.Resolved, luck: 0.90)]);

        var profile = DeveloperLearningsRenderer.RenderProfile(view);

        var resolvedAt = profile.IndexOf("## Resolved", StringComparison.Ordinal);
        var unjudgeableAt = profile.IndexOf("## Unjudgeable", StringComparison.Ordinal);
        _ = profile[resolvedAt..unjudgeableAt].Should().Contain("Title for p");
        profile.Should().Contain("_No active patterns._");
    }

    [Fact]
    public void A_regression_is_rendered_first_and_again_in_its_state_section()
    {
        // §8 calls a returning pattern the highest-signal event in the system, and it is Active again rather
        // than a state of its own. So it legitimately appears twice, and the profile says so rather than
        // leaving a reader to think the count was doubled.
        var view = View([Pattern("came-back", PatternState.Active, regressed: true)]);

        var profile = DeveloperLearningsRenderer.RenderProfile(view);

        var regressionsAt = profile.IndexOf("## Regressions", StringComparison.Ordinal);
        var activeAt = profile.IndexOf("## Active patterns", StringComparison.Ordinal);
        _ = regressionsAt.Should().BeLessThan(activeAt);
        _ = profile[regressionsAt..activeAt].Should().Contain("came-back").And.Contain("breaking a clean streak of 7");
        _ = profile[activeAt..].Should().Contain("came-back");
        profile.Should().Contain("these also appear in their state section below");
    }

    [Fact]
    public void A_provisional_resolution_is_labelled_and_excluded_from_the_headline_count()
    {
        // A cohort-wide fall in a dimension improves every developer's numbers on the same day. Counting
        // those as achievements is the trap the guard exists for, and hiding the label would restore it.
        var view = View([Pattern("p", PatternState.Resolved, provisional: true)]);

        var profile = DeveloperLearningsRenderer.RenderProfile(view);

        _ = profile.Should().Contain("| Resolved (confirmed) | 0 |");
        _ = profile.Should().Contain("| Resolved (provisional) | 1 |");
        DeveloperLearningsRenderer
            .RenderProgress(view)
            .Should()
            .Contain("yes — cohort drop in this dimension");
    }

    [Fact]
    public void The_active_table_states_its_denominator_and_its_window()
    {
        // §9: every table that shows a count must also state its denominator and window. A number with no
        // corpus cannot be checked, and so cannot be trusted by the person it is about.
        var profile = DeveloperLearningsRenderer.RenderProfile(View([Pattern("p", PatternState.Active)]));

        _ = profile.Should().Contain("3 in 8 exposed PRs");
        _ = profile.Should().Contain("(hits + 1) / (exposed + 2)");
        profile.Should().Contain("4 exposed PRs |");
    }

    [Fact]
    public void An_unjudgeable_trend_prints_as_unknown_rather_than_as_a_flat_line()
    {
        // "we cannot tell" and "it did not move" are different facts. Rendering the first as 0.00 turns an
        // absence of evidence into a claim.
        var profile = DeveloperLearningsRenderer.RenderProfile(View([Pattern("p", PatternState.Active)]));

        _ = profile.Should().Contain("insufficient data");
        profile.Should().NotContain("unchanged (0.00)");
    }

    [Fact]
    public void A_pipe_in_a_pattern_id_cannot_break_a_table_row()
    {
        // Model-proposed slugs are validated, but ids reaching this renderer come from pattern-file
        // frontmatter, which phase 3 reads back off disk and does not re-validate. A hand-edited or
        // corrupted file would otherwise silently shift every column in the table.
        var profile = DeveloperLearningsRenderer.RenderProfile(View([Pattern("weird|id", PatternState.Active)]));

        _ = profile.Should().Contain("weird\\|id");
        profile.Should().NotContain("| `weird|id` |");
    }

    [Fact]
    public void The_checklist_stays_short_however_the_model_formatted_its_prose()
    {
        // The under-40-lines property must not depend on how the model chose to wrap its answer. A
        // multi-paragraph body carries its own newlines straight into the file unless something collapses it.
        var sprawling = "First line.\n\nSecond paragraph.\n\n- a bullet\n- another\n\n" + new string('x', 300);
        var patterns = Enumerable
            .Range(0, 12)
            .Select(i => Pattern("pattern-" + i, PatternState.Active, howToAvoid: sprawling))
            .ToArray();

        var checklist = DeveloperLearningsRenderer.RenderChecklist(View(patterns));

        _ = checklist.Split('\n').Should().HaveCountLessThan(40);
        checklist.Should().NotContain("\n- a bullet");
    }

    [Fact]
    public void The_checklist_carries_at_most_five_patterns()
    {
        var patterns = Enumerable
            .Range(0, 12)
            .Select(i => Pattern("pattern-" + i, PatternState.Active))
            .ToArray();

        var checklist = DeveloperLearningsRenderer.RenderChecklist(View(patterns));

        _ = checklist.Split('\n').Count(l => l.StartsWith("- **", StringComparison.Ordinal))
            .Should()
            .Be(DeveloperLearningsRenderer.MaxChecklistPatterns);
        checklist.Should().Contain("Top 5 of 12 active patterns");
    }

    [Fact]
    public void The_checklist_ranks_by_rate_and_recency_and_says_which_rule_it_used()
    {
        var quiet = new PatternView(
            Standing("quiet", PatternState.Active, cleanStreak: 9),
            new PatternProse("Quiet", "…", "…", "Avoid quiet."),
            new PatternTrend(TrendDirection.InsufficientData, null, null, 0, 0),
            []);
        var recent = new PatternView(
            Standing("recent", PatternState.Active, cleanStreak: 0),
            new PatternProse("Recent", "…", "…", "Avoid recent."),
            new PatternTrend(TrendDirection.InsufficientData, null, null, 0, 0),
            []);

        var checklist = DeveloperLearningsRenderer.RenderChecklist(View([quiet, recent]));

        _ = checklist.Should().Contain("ranked by rate ÷ (1 + clean streak in exposed PRs)");
        checklist.IndexOf("Recent", StringComparison.Ordinal)
            .Should()
            .BeLessThan(checklist.IndexOf("Quiet", StringComparison.Ordinal));
    }

    [Fact]
    public void Watch_and_unjudgeable_patterns_are_listed_and_not_merely_counted()
    {
        // Added because a mutation that made the shared pattern-list helper emit nothing killed NOTHING in a
        // 1745-test suite. Every renderer test up to that point had an EMPTY Watch and Unjudgeable list, so
        // the populated path — two profile sections and the whole of progress §4 — was never rendered once.
        // The visible failure would have been a heading with nothing under it on a named person's file.
        var view = View(
            [
                Pattern("on-watch", PatternState.Watch),
                Pattern("stale-dimension", PatternState.Unjudgeable),
            ]);

        var profile = DeveloperLearningsRenderer.RenderProfile(view);
        var watchAt = profile.IndexOf("## Watch", StringComparison.Ordinal);
        var resolvedAt = profile.IndexOf("## Resolved", StringComparison.Ordinal);
        var unjudgeableAt = profile.IndexOf("## Unjudgeable", StringComparison.Ordinal);
        var provenanceAt = profile.IndexOf("## Provenance", StringComparison.Ordinal);

        // Asserted BETWEEN the headings, so a pattern rendered into the wrong section fails rather than
        // passing on a bare "the file mentions it somewhere".
        _ = profile[watchAt..resolvedAt]
            .Should()
            .Contain("Title for on-watch")
            .And.Contain("3 in 8 exposed PRs")
            .And.Contain("4 exposed PRs")
            .And.NotContain("stale-dimension");
        _ = profile[unjudgeableAt..provenanceAt]
            .Should()
            .Contain("Title for stale-dimension")
            .And.NotContain("on-watch");

        var progress = DeveloperLearningsRenderer.RenderProgress(view);
        var honestUnknownAt = progress.IndexOf("## 4. Not enough exposure to judge", StringComparison.Ordinal);
        progress[honestUnknownAt..].Should().Contain("Title for stale-dimension");
    }

    [Fact]
    public void The_index_sorts_by_last_activity_and_not_by_pattern_count()
    {
        // Sorting by "worst" makes this a leaderboard, which changes how everyone treats the whole system.
        var busyButOld = View(
            [.. Enumerable.Range(0, 9).Select(i => Pattern("p" + i, PatternState.Active))],
            slug: "aaa-older-000000000000",
            lastObserved: "2026-01-02T00:00:00Z");
        var quietButRecent = View(
            [Pattern("p", PatternState.Active)],
            slug: "zzz-newer-000000000000",
            lastObserved: "2026-03-02T00:00:00Z");

        var index = DeveloperLearningsRenderer.RenderIndex([busyButOld, quietButRecent], Now);

        index.IndexOf("zzz-newer", StringComparison.Ordinal)
            .Should()
            .BeLessThan(index.IndexOf("aaa-older", StringComparison.Ordinal));
    }

    [Fact]
    public void The_index_is_a_function_of_its_input_and_not_of_the_order_it_arrives_in()
    {
        // §4: rendered-file conflicts are resolved by regeneration, never by merge. Two developers' PRs
        // closing at once must regenerate to the same bytes whichever order the ledgers are handed in.
        var first = View([Pattern("p", PatternState.Active)], slug: "aaa-000000000000");
        var second = View([Pattern("p", PatternState.Active)], slug: "bbb-000000000000");

        DeveloperLearningsRenderer
            .RenderIndex([second, first], Now)
            .Should()
            .Be(DeveloperLearningsRenderer.RenderIndex([first, second], Now));
    }

    [Fact]
    public void The_index_counts_confirmed_resolutions_and_names_the_provisional_ones()
    {
        var view = View([Pattern("p", PatternState.Resolved, provisional: true)]);

        var index = DeveloperLearningsRenderer.RenderIndex([view], Now);

        _ = index.Should().Contain("| `" + Slug + "` | 12 | 0 | 0 | 0 |");
        index.Should().Contain("1 resolution held provisional");
    }

    [Fact]
    public void A_developer_who_has_never_been_observed_reads_as_never_rather_than_as_a_date()
    {
        var index = DeveloperLearningsRenderer.RenderIndex([Empty()], Now);

        index.Should().Contain("| never |");
    }
}
