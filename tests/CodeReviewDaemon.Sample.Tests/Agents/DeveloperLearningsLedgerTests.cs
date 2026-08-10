using CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The arithmetic behind every number a developer will read about themselves.
/// <para>
/// These tests exist because the failure mode here is silent and personal. A wrong count under a named
/// person's file does not throw, does not fail a build, and reads exactly like a right one — so the guard
/// has to be the test suite or there is no guard. Each scenario below is one of §11's required cases.
/// </para>
/// </summary>
public sealed class DeveloperLearningsLedgerTests
{
    private const string ExceptionDim = "code-reviewer:exception-handling-review";
    private const string TestDim = "code-reviewer:test-coverage-review";
    private const string NullGuard = "missing-null-guard-on-dto";
    private const string Retry = "unbounded-retry-on-transient-failure";

    /// <summary>Shortly after every fixture ledger below, so staleness is opt-in per test.</summary>
    private static readonly DateTimeOffset Now = new(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Seven months after them, which is what the staleness rule is for.</summary>
    private static readonly DateTimeOffset MuchLater = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> NoSuppression = new HashSet<string>(StringComparer.Ordinal);

    private static DeveloperObservationHit Hit(string patternId, string dimension = ExceptionDim) =>
        new(patternId, dimension, "MEDIUM", "kept", "src/Foo.cs:42", "…verbatim…");

    /// <summary>
    /// One PR. <paramref name="exposure"/> is explicit on every call — a fixture that always grants full
    /// exposure makes every exposure test vacuous, which §11 calls out by name.
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

    private static IReadOnlyList<PatternStanding> Compute(
        IReadOnlyList<DeveloperObservation> observations,
        IReadOnlyDictionary<string, string>? dimensions = null,
        LearningsThresholds? thresholds = null,
        IReadOnlySet<string>? suppressed = null,
        DateTimeOffset? now = null) =>
        DeveloperLearningsLedger.Compute(
            observations,
            dimensions ?? new Dictionary<string, string>(StringComparer.Ordinal) { [NullGuard] = ExceptionDim },
            thresholds ?? LearningsThresholds.Defaults,
            suppressed ?? NoSuppression,
            now ?? Now);

    [Fact]
    public void Two_findings_of_one_pattern_in_one_PR_are_one_occurrence()
    {
        // Five instances of the same mistake in one PR is one occurrence. Per-finding counting lets the
        // per-PR rate exceed 1.0, which makes (1-p)^n meaningless and every resolution downstream a lie.
        var pr = Pr(1, [ExceptionDim], Hit(NullGuard), Hit(NullGuard), Hit(NullGuard));

        pr.Hits.Should().ContainSingle();
        Compute([pr]).Should().ContainSingle().Which.Occurrences.Should().Be(1);
    }

    [Fact]
    public void A_dropped_finding_produces_no_hit()
    {
        // The lead reviewer threw it out. That is evidence about the finding, not about the author.
        DeveloperObservation.Survived("dropped").Should().BeFalse();
        DeveloperObservation.Survived("kept").Should().BeTrue();
        DeveloperObservation.Survived("severity-changed").Should().BeTrue();
        DeveloperObservation.Survived("reframed").Should().BeTrue();
        DeveloperObservation.Survived("merged-into").Should().BeTrue();
    }

    [Fact]
    public void A_single_occurrence_does_not_resolve_immediately()
    {
        // Without Laplace smoothing one hit in one exposed PR gives p = 1.0, so (1-p)^n = 0 for any streak
        // and the very next clean PR declares the pattern fixed on one data point.
        var standing = Compute(
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [ExceptionDim]),
        ]).Should().ContainSingle().Subject;

        standing.Rate.Should().BeApproximately(2.0 / 3.0, 1e-9, "(1+1)/(1+2) with smoothing");
        standing.State.Should().NotBe(PatternState.Resolved);
    }

    [Fact]
    public void Resolution_counts_exposed_PRs_and_not_all_PRs()
    {
        // Four PRs follow the hit but only ONE ran the dimension. The streak is 1, not 4 — otherwise a
        // developer's record improves when the reviewer stops looking, which is the opposite of progress.
        var standing = Compute(
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [TestDim]),
            Pr(3, [TestDim]),
            Pr(4, [TestDim]),
            Pr(5, [ExceptionDim]),
        ]).Should().ContainSingle().Subject;

        standing.CleanStreak.Should().Be(1, "only one of the four later PRs exercised this dimension");
        standing.State.Should().NotBe(PatternState.Resolved);
    }

    [Fact]
    public void A_high_rate_pattern_resolves_after_five_clean_exposed_PRs()
    {
        // §8's worked table: historical rate 0.50 resolves at 5. This pins the precedence choice as much as
        // the arithmetic — five is inside the ten-PR active window, so Resolved must outrank Active or the
        // spec's own table is unreachable.
        List<DeveloperObservation> ledger =
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [ExceptionDim], Hit(NullGuard)),
            Pr(3, [ExceptionDim], Hit(NullGuard)),
            Pr(4, [ExceptionDim], Hit(NullGuard)),
            .. Enumerable.Range(5, 9).Select(d => Pr(d, [ExceptionDim])),
        ];

        var standing = Compute(ledger).Should().ContainSingle().Subject;
        standing.Occurrences.Should().Be(4);
        standing.CleanStreak.Should().Be(9);
        standing.State.Should().Be(PatternState.Resolved);
    }

    [Fact]
    public void Zero_exposure_for_ninety_days_is_Unjudgeable_and_never_Resolved()
    {
        // A long clean streak whose last exposure was months ago is silence from a dimension nobody read.
        // Calling that Resolved credits the developer for the reviewer's absence.
        List<DeveloperObservation> ledger =
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            .. Enumerable.Range(2, 12).Select(d => Pr(d, [ExceptionDim])),
        ];

        Compute(ledger, now: MuchLater).Should().ContainSingle().Which.State
            .Should().Be(PatternState.Unjudgeable, "the newest exposure is over 90 days before now");

        // Positive control: the identical ledger judged close to its own last exposure is NOT Unjudgeable,
        // so the assertion above is about staleness and not about the ledger being unjudgeable anyway.
        Compute(ledger, now: Now)
            .Should().ContainSingle().Which.State.Should().NotBe(PatternState.Unjudgeable);
    }

    [Fact]
    public void A_regression_continues_the_count_and_does_not_reset_to_one()
    {
        // The highest-signal event in the system. A reset would erase the history that makes a returning
        // pattern meaningful, and the returning pattern would read as brand new.
        List<DeveloperObservation> ledger =
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [ExceptionDim], Hit(NullGuard)),
            Pr(3, [ExceptionDim], Hit(NullGuard)),
            Pr(4, [ExceptionDim], Hit(NullGuard)),
            .. Enumerable.Range(5, 9).Select(d => Pr(d, [ExceptionDim])),
            Pr(14, [ExceptionDim], Hit(NullGuard)),
        ];

        var standing = Compute(ledger).Should().ContainSingle().Subject;

        standing.Occurrences.Should().Be(5, "the count continues through the regression");
        standing.Regressed.Should().BeTrue();
        standing.StreakBrokenAt.Should().Be(9);
        standing.State.Should().Be(PatternState.Active);
    }

    [Fact]
    public void A_cohort_wide_dimension_drop_marks_the_resolution_provisional()
    {
        // Three things make a pattern stop appearing and only one of them is improvement. A population-wide
        // collapse in a dimension's finding rate is the systemic one, and it would otherwise make every
        // developer appear to improve on the same day.
        List<DeveloperObservation> ledger =
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [ExceptionDim], Hit(NullGuard)),
            Pr(3, [ExceptionDim], Hit(NullGuard)),
            Pr(4, [ExceptionDim], Hit(NullGuard)),
            .. Enumerable.Range(5, 9).Select(d => Pr(d, [ExceptionDim])),
        ];

        var clean = Compute(ledger).Should().ContainSingle().Subject;
        clean.State.Should().Be(PatternState.Resolved);
        clean.Provisional.Should().BeFalse();

        var suppressed = Compute(
            ledger,
            suppressed: new HashSet<string>(StringComparer.Ordinal) { ExceptionDim })
            .Should().ContainSingle().Subject;
        suppressed.State.Should().Be(PatternState.Resolved, "the resolution is marked, never blocked");
        suppressed.Provisional.Should().BeTrue();
    }

    [Fact]
    public void A_dimension_whose_cohort_rate_collapsed_is_suppressed()
    {
        var suppressed = DeveloperLearningsLedger.SuppressedDimensions(
            new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal)
            {
                [ExceptionDim] = [1.0, 1.2, 0.8, 0.1],
                [TestDim] = [1.0, 1.2, 0.8, 0.9],
            },
            LearningsThresholds.Defaults.CohortDropThreshold);

        suppressed.Should().Contain(ExceptionDim);
        suppressed.Should().NotContain(TestDim, "a normal window must not be flagged");
    }

    [Fact]
    public void A_developer_with_no_hits_for_a_pattern_has_no_standing_at_all()
    {
        // Zero observations must not render as a zero-count pattern — an absent pattern and a pattern seen
        // zero times are different facts, and only the second one is a claim.
        Compute([Pr(1, [ExceptionDim]), Pr(2, [ExceptionDim])]).Should().BeEmpty();
        Compute([]).Should().BeEmpty();
    }

    [Fact]
    public void Patterns_are_counted_independently_within_one_PR()
    {
        // Dedupe is per pattern, not per PR. Two DIFFERENT mistakes in one PR are two occurrences.
        var standings = Compute(
            [Pr(1, [ExceptionDim, TestDim], Hit(NullGuard), Hit(Retry, TestDim))],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NullGuard] = ExceptionDim,
                [Retry] = TestDim,
            });

        standings.Should().HaveCount(2);
        standings.Should().OnlyContain(s => s.Occurrences == 1);
    }

    [Fact]
    public void The_window_denominator_excludes_PRs_that_never_ran_the_dimension()
    {
        // The rate's denominator is exposed PRs inside the first-to-last-hit window. Widening it to all PRs
        // depresses every rate, which makes resolution easier the less the reviewer looked.
        var standing = Compute(
        [
            Pr(1, [ExceptionDim], Hit(NullGuard)),
            Pr(2, [TestDim]),
            Pr(3, [TestDim]),
            Pr(4, [ExceptionDim], Hit(NullGuard)),
        ]).Should().ContainSingle().Subject;

        standing.ExposedInWindow.Should().Be(2, "only two PRs in the window ran this dimension");
        standing.Rate.Should().BeApproximately(3.0 / 4.0, 1e-9, "(2+1)/(2+2)");
    }
}
