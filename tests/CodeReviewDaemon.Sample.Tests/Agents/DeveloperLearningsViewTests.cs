using CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The projection every rendered file reads from.
/// <para>
/// This layer exists so that <c>profile.md</c>, <c>checklist.md</c> and <c>_index.md</c> cannot disagree
/// about which patterns are active or how many there are. Two renderers each doing their own counting would
/// eventually diverge, and the divergence would be silent — so the agreement is made structural here and
/// these tests guard the structure.
/// </para>
/// </summary>
public sealed class DeveloperLearningsViewTests
{
    private const string ExceptionDim = "code-reviewer:exception-handling-review";
    private const string TestDim = "code-reviewer:test-coverage-review";
    private const string NullGuard = "missing-null-guard-on-dto";
    private const string Retry = "unbounded-retry-on-transient-failure";
    private const string Slug = "jane-doe-contoso-com-a1b2c3d4e5f6";

    private static readonly DateTimeOffset Now = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> NoSuppression = new HashSet<string>(StringComparer.Ordinal);

    private static DeveloperObservationHit Hit(string patternId, string dimension = ExceptionDim) =>
        new(patternId, dimension, "MEDIUM", "kept", "src/Foo.cs:42", "…verbatim…");

    /// <summary>
    /// One PR. Exposure is explicit on every call so no fixture can accidentally grant full exposure and
    /// make the exposure-denominator assertions vacuous.
    /// </summary>
    private static DeveloperObservation Pr(
        int day,
        IReadOnlyList<string> exposure,
        params DeveloperObservationHit[] hits) =>
        new(
            DeveloperObservation.CurrentSchemaVersion,
            $"azure-devops/o365exchange/weve_da/nova/{5000 + day}",
            "azure-devops",
            "o365exchange/Weve_DA/Nova",
            (5000 + day).ToString(System.Globalization.CultureInfo.InvariantCulture),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day).ToString("O"),
            [1],
            exposure,
            DeveloperObservation.DedupeByPattern(hits));

    private static DeveloperLearningsView Build(
        IReadOnlyList<DeveloperObservation> observations,
        IReadOnlyDictionary<string, string>? dimensions = null,
        IReadOnlyDictionary<string, PatternProse>? prose = null,
        LearningsThresholds? thresholds = null,
        IReadOnlySet<string>? suppressed = null)
    {
        var effective = thresholds ?? LearningsThresholds.Defaults;
        var map =
            dimensions ?? new Dictionary<string, string>(StringComparer.Ordinal) { [NullGuard] = ExceptionDim };
        var standings = DeveloperLearningsLedger.Compute(
            observations, map, effective, suppressed ?? NoSuppression, Now);
        return DeveloperLearningsView.Build(
            Slug,
            observations,
            standings,
            prose ?? new Dictionary<string, PatternProse>(StringComparer.Ordinal),
            effective,
            Now);
    }

    [Fact]
    public void A_developer_with_no_observations_still_projects_a_view()
    {
        // §11 requires all four files to be written for a developer with nothing recorded, stating so. That
        // is only possible if the projection itself survives an empty ledger rather than being skipped.
        var view = Build([]);

        _ = view.Observations.Should().Be(0);
        _ = view.FirstObservedUtc.Should().BeNull();
        _ = view.LastObservedUtc.Should().BeNull();
        _ = view.Patterns.Should().BeEmpty();
        _ = view.DimensionTrends.Should().BeEmpty();
        view.ConfirmedResolvedCount.Should().Be(0);
    }

    [Fact]
    public void Sightings_name_the_PRs_that_hit_and_only_those()
    {
        // "Seen in" is what makes a count auditable back to specific PRs. A count whose corpus cannot be
        // named cannot be checked by the person it is about.
        var view = Build(
            [
                Pr(1, [ExceptionDim], Hit(NullGuard)),
                Pr(2, [ExceptionDim]),
                Pr(3, [ExceptionDim], Hit(NullGuard)),
            ]);

        var sightings = view.Patterns.Should().ContainSingle().Subject.Sightings;
        _ = sightings.Should().HaveCount(2);
        sightings
            .Select(s => s.SourcePr)
            .Should()
            .ContainInOrder(
                "azure-devops/o365exchange/weve_da/nova/5001",
                "azure-devops/o365exchange/weve_da/nova/5003");
    }

    [Fact]
    public void A_missing_pattern_file_keeps_the_count_and_names_the_gap()
    {
        // The counts came from observation files and are real whether or not the prose file survived.
        // Dropping the pattern would understate a developer's record because of a missing markdown file.
        var view = Build([Pr(1, [ExceptionDim], Hit(NullGuard))]);

        var pattern = view.Patterns.Should().ContainSingle().Subject;
        _ = pattern.Standing.Occurrences.Should().Be(1);
        _ = pattern.Prose.Title.Should().Be(NullGuard);
        pattern.Prose.HowToAvoid.Should().Be(PatternProse.MissingText);
    }

    [Fact]
    public void Trend_reports_nothing_until_both_windows_hold_half_a_window()
    {
        // Twelve exposed PRs at the default window: the recent ten are full, the prior two are not. Printing
        // a movement off two PRs would be noise rendered as a finding about a person.
        var observations = new List<DeveloperObservation> { Pr(1, [ExceptionDim], Hit(NullGuard)) };
        for (var day = 2; day <= 12; day++)
        {
            observations.Add(Pr(day, [ExceptionDim]));
        }

        var trend = Build(observations).Patterns.Should().ContainSingle().Subject.Trend;

        _ = trend.Direction.Should().Be(TrendDirection.InsufficientData);
        _ = trend.PriorExposed.Should().Be(2);
        // Null rather than 0.00, which would read as "never happens" instead of "cannot be told".
        _ = trend.RecentRate.Should().BeNull();
        trend.PriorRate.Should().BeNull();
    }

    [Fact]
    public void Trend_is_computed_with_the_ledgers_own_rate_and_not_a_second_definition()
    {
        // Two things called "rate" in one file, computed differently, is the failure this pins against. The
        // assertion is equality with the ledger's own function, not with a literal.
        var observations = new List<DeveloperObservation>();
        for (var day = 1; day <= 10; day++)
        {
            observations.Add(
                day % 2 == 1 ? Pr(day, [ExceptionDim], Hit(NullGuard)) : Pr(day, [ExceptionDim]));
        }

        for (var day = 11; day <= 20; day++)
        {
            observations.Add(Pr(day, [ExceptionDim]));
        }

        var trend = Build(observations).Patterns.Should().ContainSingle().Subject.Trend;

        _ = trend.Direction.Should().Be(TrendDirection.Improving);
        _ = trend.RecentRate.Should().Be(DeveloperLearningsLedger.SmoothedRate(0, 10));
        _ = trend.PriorRate.Should().Be(DeveloperLearningsLedger.SmoothedRate(5, 10));
        trend.RecentExposed.Should().Be(10);
    }

    [Fact]
    public void The_trend_window_follows_the_configured_active_window()
    {
        // The spec writes 10 and 5 as literals. They are ActiveWindowPrs and half of it — derived here, so a
        // reconfigured window does not leave the trend measuring a different span from every other number in
        // the same file while still being labelled the same way.
        var observations = new List<DeveloperObservation>();
        for (var day = 1; day <= 8; day++)
        {
            observations.Add(day <= 4 ? Pr(day, [ExceptionDim], Hit(NullGuard)) : Pr(day, [ExceptionDim]));
        }

        var atDefault = Build(observations).Patterns.Single().Trend;
        var atFour = Build(observations, thresholds: LearningsThresholds.Defaults with { ActiveWindowPrs = 4 })
            .Patterns.Single()
            .Trend;

        _ = atDefault.Direction.Should().Be(TrendDirection.InsufficientData);
        _ = atFour.Direction.Should().Be(TrendDirection.Improving);
        atFour.RecentExposed.Should().Be(4);
    }

    [Fact]
    public void Findings_per_exposed_PR_is_a_count_and_not_a_probability()
    {
        // One PR carrying two distinct mistakes in one dimension gives 2.0. The per-pattern Rate is bounded
        // by 1 and this is not, which is exactly why they do not share a name.
        var view = Build(
            [Pr(1, [ExceptionDim], Hit(NullGuard), Hit(Retry))],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NullGuard] = ExceptionDim,
                [Retry] = ExceptionDim,
            });

        var window = view.DimensionTrends.Should().ContainSingle().Subject.Windows.Should().ContainSingle().Subject;
        _ = window.Findings.Should().Be(2);
        _ = window.ExposedPrs.Should().Be(1);
        window.FindingsPerExposedPr.Should().Be(2.0);
    }

    [Fact]
    public void A_dimension_that_ran_and_found_nothing_still_appears()
    {
        // A specialist that ran sixty times and found nothing is a fact about the developer. Listing only
        // dimensions that produced findings would leave the table showing only where they were caught.
        var view = Build([Pr(1, [ExceptionDim, TestDim], Hit(NullGuard))]);

        var testDimension = view.DimensionTrends.Should()
            .Contain(t => t.Dimension == TestDim)
            .Subject;
        testDimension.Windows.Should().ContainSingle().Subject.Findings.Should().Be(0);
    }

    [Fact]
    public void The_newest_window_is_the_full_one()
    {
        // Chunking forwards would leave the newest window partial — and the newest window is the one the
        // reader compares against, so a short one there reads as an improvement that never happened.
        var observations = new List<DeveloperObservation>();
        for (var day = 1; day <= 15; day++)
        {
            observations.Add(Pr(day, [ExceptionDim]));
        }

        var windows = Build(observations).DimensionTrends.Single().Windows;

        _ = windows.Should().HaveCount(2);
        _ = windows[0].Partial.Should().BeTrue();
        _ = windows[0].ExposedPrs.Should().Be(5);
        _ = windows[1].Partial.Should().BeFalse();
        windows[1].ExposedPrs.Should().Be(10);
    }

    [Fact]
    public void Only_the_six_most_recent_windows_are_kept()
    {
        var observations = new List<DeveloperObservation>();
        for (var day = 1; day <= 70; day++)
        {
            observations.Add(Pr(day, [ExceptionDim]));
        }

        Build(observations)
            .DimensionTrends.Single()
            .Windows.Should()
            .HaveCount(DeveloperLearningsView.ProgressTrendWindows);
    }

    [Fact]
    public void A_quieter_pattern_at_the_same_rate_ranks_lower_on_the_checklist()
    {
        // "rate × recency" made explicit: rate / (1 + clean streak). A ranking nobody can recompute is a
        // ranking nobody can check.
        var recent = ActivePattern(rate: 0.4, cleanStreak: 0);
        var quiet = ActivePattern(rate: 0.4, cleanStreak: 9);

        DeveloperLearningsView
            .ChecklistScore(quiet)
            .Should()
            .BeLessThan(DeveloperLearningsView.ChecklistScore(recent));
    }

    [Fact]
    public void A_provisional_resolution_is_kept_out_of_the_headline_count()
    {
        // A cohort-wide fall in a dimension makes every developer look improved on the same day. Counting
        // those as resolved is how a systemic reviewer change becomes a personal achievement.
        var observations = new List<DeveloperObservation> { Pr(1, [ExceptionDim], Hit(NullGuard)) };
        for (var day = 2; day <= 12; day++)
        {
            observations.Add(Pr(day, [ExceptionDim]));
        }

        var view = Build(
            observations,
            suppressed: new HashSet<string>(StringComparer.Ordinal) { ExceptionDim });

        _ = view.Resolved.Should().ContainSingle();
        _ = view.ProvisionalResolutions.Should().ContainSingle();
        view.ConfirmedResolvedCount.Should().Be(0);
    }

    [Fact]
    public void The_view_does_not_depend_on_the_order_the_observations_arrive_in()
    {
        // §4's conflict policy is "regenerate, never merge", and regeneration only converges if the view is
        // a function of the ledger alone. A directory listing arrives in whatever order the filesystem chose.
        var observations = new List<DeveloperObservation>
        {
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [ExceptionDim, TestDim]),
            Pr(3, [ExceptionDim], Hit(NullGuard)),
            Pr(4, [TestDim]),
        };

        var forwards = Build(observations);
        var backwards = Build([.. Enumerable.Reverse(observations)]);

        backwards.Should().BeEquivalentTo(forwards);
    }

    private static PatternView ActivePattern(double rate, int cleanStreak) =>
        new(
            new PatternStanding(
                PatternId: NullGuard,
                Dimension: ExceptionDim,
                Occurrences: 3,
                ExposedInWindow: 8,
                Rate: rate,
                CleanStreak: cleanStreak,
                LuckProbability: 0.5,
                State: PatternState.Active,
                Regressed: false,
                Provisional: false,
                FirstSeenUtc: "2026-01-01T00:00:00Z",
                LastSeenUtc: "2026-02-01T00:00:00Z",
                RegressedAtUtc: null,
                StreakBrokenAt: null),
            PatternProse.Missing(NullGuard),
            new PatternTrend(TrendDirection.InsufficientData, null, null, 0, 0),
            []);
}
