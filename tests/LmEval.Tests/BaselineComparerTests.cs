using AchieveAi.LmDotnetTools.LmEval.Running;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// A silent incomparable comparison is the most likely way this system produces a confident wrong
/// number, so every precondition here is asserted as a <b>refusal</b> — not a regression, and not a
/// pass.
/// </summary>
public class BaselineComparerTests
{
    private static EvalItemResult Scored(string id, double score, VerdictOutcome outcome) =>
        new()
        {
            CandidateId = id,
            Exclusion = ScoreExclusion.None,
            Verdict = new Verdict
            {
                CandidateId = id,
                Outcome = outcome,
                Score = score,
                GateDecisions = [],
                Ballots = [],
                ExcludedBallots = [],
                RubricId = "test-rubric",
                RubricVersion = "1.0",
                TieBreakRule = TieBreakRules.Consensus,
                Degradation = PanelDegradation.None,
            },
        };

    private static EvalItemResult Undecided(string id) =>
        new()
        {
            CandidateId = id,
            Exclusion = ScoreExclusion.NoDecision,
            Verdict = new Verdict
            {
                CandidateId = id,
                Outcome = VerdictOutcome.NoDecision,
                Score = null,
                GateDecisions = [],
                Ballots = [],
                ExcludedBallots = [],
                RubricId = "test-rubric",
                RubricVersion = "1.0",
                TieBreakRule = TieBreakRules.NoDecision,
                Degradation = PanelDegradation.None,
            },
        };

    /// <summary>An item the run holds no verdict for at all — a judge-provider outage.</summary>
    private static EvalItemResult Faulted(string id) =>
        new()
        {
            CandidateId = id,
            Verdict = null,
            FaultReason = nameof(HttpRequestException),
            Exclusion = ScoreExclusion.Faulted,
        };

    /// <summary>An item the panel could not decide AND could not fully staff.</summary>
    private static EvalItemResult UndecidedAndDegraded(string id) =>
        new()
        {
            CandidateId = id,
            Exclusion = ScoreExclusion.NoDecision,
            Verdict = new Verdict
            {
                CandidateId = id,
                Outcome = VerdictOutcome.NoDecision,
                Score = null,
                GateDecisions = [],
                Ballots = [],
                ExcludedBallots = [],
                RubricId = "test-rubric",
                RubricVersion = "1.0",
                TieBreakRule = TieBreakRules.NoDecision,
                Degradation = PanelDegradation.PanelUnavailable,
                DegradationReason = "judge-faulted:openai:HttpRequestException",
            },
        };

    private static EvalRun Run(
        IReadOnlyList<EvalItemResult> items,
        string corpusHash = "corpus-hash",
        string evaluatorHash = "evaluator-hash",
        string rubricVersion = "1.0",
        string taskType = HarnessFixtures.TaskType
    ) =>
        new()
        {
            RunId = "run-1",
            TaskType = taskType,
            CorpusId = "corpus-1",
            CorpusSnapshotHash = corpusHash,
            EvaluatorConfigHash = evaluatorHash,
            RubricId = "test-rubric",
            RubricVersion = rubricVersion,
            Items = items,
        };

    /// <summary>Ten items, <paramref name="passing"/> of which pass at <paramref name="passScore"/>.</summary>
    private static EvalRun UniformRun(
        int passing,
        double passScore = 8.0,
        double failScore = 4.0,
        int size = 10,
        string corpusHash = "corpus-hash",
        string evaluatorHash = "evaluator-hash",
        string rubricVersion = "1.0"
    ) =>
        Run(
            [
                .. Enumerable
                    .Range(0, size)
                    .Select(i =>
                        i < passing
                            ? Scored($"i{i}", passScore, VerdictOutcome.Pass)
                            : Scored($"i{i}", failScore, VerdictOutcome.Fail)
                    ),
            ],
            corpusHash,
            evaluatorHash,
            rubricVersion
        );

    private static EvalBaseline Baseline(EvalRun run, double minCoverage = 0.8) =>
        EvalBaseline.From("base-1", run, minCoverage);

    [Fact]
    public void An_identical_run_is_neither_a_regression_nor_a_refusal()
    {
        var run = UniformRun(passing: 8);
        var comparison = BaselineComparer.Compare(run, Baseline(run));

        comparison.IsRefused.Should().BeFalse();
        comparison.IsRegression.Should().BeFalse();
        comparison.PassRateDelta.Should().Be(0.0);
        comparison.Coverage.Should().Be(1.0);
        comparison.BaselineCoverage.Should().Be(1.0);
    }

    [Fact]
    public void A_different_rubric_version_is_refused()
    {
        var baseline = Baseline(UniformRun(passing: 8));
        var candidate = UniformRun(passing: 8, rubricVersion: "1.1");

        var comparison = BaselineComparer.Compare(candidate, baseline);

        comparison.Refusal.Should().Be(ComparisonRefusal.RubricVersionDiffers);
        comparison.IsRegression.Should().BeFalse();
        comparison.PassRateDelta.Should().BeNull();
        comparison.RefusalDetail.Should().Contain("never");
    }

    [Fact]
    public void A_different_corpus_snapshot_is_refused()
    {
        var baseline = Baseline(UniformRun(passing: 8));
        var candidate = UniformRun(passing: 8, corpusHash: "another-corpus");

        BaselineComparer
            .Compare(candidate, baseline)
            .Refusal.Should()
            .Be(ComparisonRefusal.CorpusSnapshotDiffers);
    }

    [Fact]
    public void A_moved_evaluator_config_is_refused_and_never_read_as_a_candidate_regression()
    {
        // Same corpus, same rubric, same candidate output — one judge model swapped. The scores
        // will differ and none of that is the candidate's doing.
        var baseline = Baseline(UniformRun(passing: 9));
        var candidate = UniformRun(passing: 3, evaluatorHash: "evaluator-hash-v2");

        var comparison = BaselineComparer.Compare(candidate, baseline);

        comparison.Refusal.Should().Be(ComparisonRefusal.EvaluatorConfigDiffers);
        comparison.IsRegression.Should().BeFalse();
        comparison.Triggers.Should().Be(RegressionTrigger.None);
    }

    [Fact]
    public void A_task_type_mismatch_is_refused()
    {
        var baseline = Baseline(UniformRun(passing: 8));
        var candidate = Run(
            [.. Enumerable.Range(0, 10).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass))],
            taskType: "summarization"
        );

        BaselineComparer
            .Compare(candidate, baseline)
            .Refusal.Should()
            .Be(ComparisonRefusal.TaskTypeDiffers);
    }

    [Fact]
    public void A_run_below_the_baseline_s_minimum_coverage_is_refused_rather_than_reported()
    {
        var baseline = Baseline(UniformRun(passing: 8), minCoverage: 0.8);

        // Only 5 of 10 items scored — a coverage of 0.5, below the floor the baseline imposes.
        var thin = Run(
            [
                .. Enumerable.Range(0, 5).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(5, 5).Select(i => Undecided($"i{i}")),
            ]
        );

        var comparison = BaselineComparer.Compare(thin, baseline);

        comparison.Refusal.Should().Be(ComparisonRefusal.CoverageBelowMinimum);
        comparison.Coverage.Should().Be(0.5);
        comparison.IsRegression.Should().BeFalse();
    }

    [Fact]
    public void The_run_cannot_relax_the_bar_it_is_judged_against()
    {
        // The floor lives on the baseline. A thin run stays refused however it describes itself.
        var strict = Baseline(UniformRun(passing: 8), minCoverage: 0.9);
        var lenient = Baseline(UniformRun(passing: 8), minCoverage: 0.1);

        var thin = Run(
            [
                .. Enumerable.Range(0, 5).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(5, 5).Select(i => Undecided($"i{i}")),
            ]
        );

        BaselineComparer.Compare(thin, strict).IsRefused.Should().BeTrue();
        BaselineComparer.Compare(thin, lenient).IsRefused.Should().BeFalse();
    }

    [Fact]
    public void A_seeded_pass_rate_drop_is_detected()
    {
        var baseline = Baseline(UniformRun(passing: 10, size: 20), minCoverage: 0.5);
        var degraded = UniformRun(passing: 1, size: 20);

        var comparison = BaselineComparer.Compare(degraded, baseline);

        comparison.IsRefused.Should().BeFalse();
        comparison.Triggers.Should().HaveFlag(RegressionTrigger.PassRateDrop);
        comparison.PassRateDelta.Should().BeApproximately(-0.45, 1e-9);
        comparison.PassRateDeltaUpper.Should().BeLessThan(0);
    }

    [Fact]
    public void A_pass_rate_drop_inside_the_confidence_interval_is_not_declared()
    {
        // One item's worth of movement on a 20-item corpus is noise the resampling cannot tell from
        // zero. Declaring it would make this report cry wolf on every run.
        var baseline = Baseline(UniformRun(passing: 10, size: 20), minCoverage: 0.5);
        var jittered = UniformRun(passing: 9, size: 20);

        BaselineComparer
            .Compare(jittered, baseline)
            .Triggers.Should()
            .NotHaveFlag(RegressionTrigger.PassRateDrop);
    }

    [Fact]
    public void A_drop_past_the_margin_is_still_not_declared_while_the_interval_straddles_zero()
    {
        // The case that isolates the bootstrap conjunct from the margin: two items' worth of
        // movement on a SIX-item corpus is a 16.7-point drop, comfortably past the 5-point margin,
        // and still entirely inside what resampling six items produces by chance. Declaring it
        // would mean the interval is decorative — the margin alone would be deciding, and on a
        // corpus this thin the margin alone is nearly always met.
        var baseline = Baseline(UniformRun(passing: 5, size: 6), minCoverage: 0.5);
        var candidate = UniformRun(passing: 4, size: 6);

        var comparison = BaselineComparer.Compare(candidate, baseline);

        comparison.PassRateDelta.Should().BeApproximately(-1.0 / 6.0, 1e-9);
        comparison.PassRateDelta.Should().BeLessThan(-new RegressionMargins().PassRateMargin);
        comparison.PassRateDeltaUpper.Should().BeGreaterThan(0);
        comparison.Triggers.Should().NotHaveFlag(RegressionTrigger.PassRateDrop);
    }

    [Fact]
    public void A_tail_collapse_is_detected_when_the_mean_holds()
    {
        // Ten items at 8.0 becomes nine at 8.222 and one at 6.0: the mean is unmoved and the tail
        // has fallen two points. This is the case a mean hides, and trigger 1 does not fire on it.
        var baseline = Baseline(
            Run([.. Enumerable.Range(0, 10).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass))])
        );

        var collapsed = Run(
            [
                .. Enumerable
                    .Range(0, 9)
                    .Select(i => Scored($"i{i}", 8.2222222222, VerdictOutcome.Pass)),
                Scored("i9", 6.0, VerdictOutcome.Pass),
            ]
        );

        var comparison = BaselineComparer.Compare(collapsed, baseline);

        comparison.MeanScoreDelta.Should().BeApproximately(0.0, 0.05);
        comparison.P10ScoreDelta.Should().BeApproximately(-2.0, 1e-6);
        comparison.Triggers.Should().HaveFlag(RegressionTrigger.TailCollapse);
        comparison.Triggers.Should().NotHaveFlag(RegressionTrigger.PassRateDrop);
    }

    [Fact]
    public void A_whole_distribution_drop_is_not_reported_as_a_tail_collapse()
    {
        // The mean-hold conjunct is what makes trigger 2 a DISTINCT finding — "the mean hid this"
        // — rather than a second name for a drop that is already visible. Here every item fell by
        // three points, so P10 fell past its margin and the mean fell with it. Reporting that as a
        // tail collapse would mean the flag no longer identifies the case it exists to surface.
        var baseline = Baseline(
            Run([.. Enumerable.Range(0, 10).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass))])
        );

        var lowered = Run(
            [.. Enumerable.Range(0, 10).Select(i => Scored($"i{i}", 5.0, VerdictOutcome.Pass))]
        );

        var comparison = BaselineComparer.Compare(lowered, baseline);

        comparison.P10ScoreDelta.Should().BeApproximately(-3.0, 1e-6);
        comparison.MeanScoreDelta.Should().BeApproximately(-3.0, 1e-6);
        comparison.Triggers.Should().NotHaveFlag(RegressionTrigger.TailCollapse);
    }

    [Fact]
    public void A_no_decision_rise_is_detected()
    {
        var baseline = Baseline(UniformRun(passing: 10, size: 10), minCoverage: 0.4);

        // Half the corpus now yields no decision. The panel has stopped being able to judge, which
        // invalidates the comparison rather than passing it.
        var undecided = Run(
            [
                .. Enumerable.Range(0, 5).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(5, 5).Select(i => Undecided($"i{i}")),
            ]
        );

        var comparison = BaselineComparer.Compare(undecided, baseline);

        comparison.IsRefused.Should().BeFalse();
        comparison.NoDecisionRateDelta.Should().BeApproximately(0.5, 1e-9);
        comparison.Triggers.Should().HaveFlag(RegressionTrigger.NoDecisionRise);
    }

    [Fact]
    public void The_bootstrap_is_reproducible_across_calls()
    {
        // A regression verdict that changed between two runs over identical inputs would be
        // indistinguishable from a real one.
        var baseline = Baseline(UniformRun(passing: 10, size: 20), minCoverage: 0.5);
        var candidate = UniformRun(passing: 4, size: 20);

        var first = BaselineComparer.Compare(candidate, baseline);
        var second = BaselineComparer.Compare(candidate, baseline);

        second.PassRateDeltaLower.Should().Be(first.PassRateDeltaLower);
        second.PassRateDeltaUpper.Should().Be(first.PassRateDeltaUpper);
    }

    [Fact]
    public void A_baseline_freezes_the_coverage_its_conditional_metrics_belong_to()
    {
        var run = Run(
            [
                .. Enumerable.Range(0, 8).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(8, 2).Select(i => Undecided($"i{i}")),
            ]
        );

        var baseline = Baseline(run, minCoverage: 0.5);

        baseline.CorpusSize.Should().Be(10);
        baseline.ScoredItems.Should().Be(8);
        baseline.MeanScore.Should().Be(8.0);
        baseline.PassRate.Should().Be(0.8);
        baseline.NoDecisionRate.Should().Be(0.2);
    }

    [Fact]
    public void A_run_that_scored_nothing_cannot_become_a_baseline()
    {
        var barren = Run([.. Enumerable.Range(0, 3).Select(i => Undecided($"i{i}"))]);

        var act = () => EvalBaseline.From("base-1", barren, 0.5);

        act.Should().Throw<ArgumentException>().WithMessage("*scored none*");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void A_coverage_floor_outside_zero_to_one_is_refused(double minCoverage)
    {
        var act = () => EvalBaseline.From("base-1", UniformRun(passing: 8), minCoverage);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- pinning what the survivors of the #364 mutation audit left unpinned ------------------

    /// <summary>
    /// §5.4's third trigger says the no-decision rate <b>rises</b> materially. Nothing supplied a
    /// FALLING rate, so <c>noDecisionDelta &gt; margin</c> survived becoming
    /// <c>Math.Abs(noDecisionDelta) &gt; margin</c> — under which a run that got materially BETTER
    /// at deciding is reported as a regression.
    /// </summary>
    [Fact]
    public void A_no_decision_rate_that_falls_past_the_margin_is_not_a_regression()
    {
        // Baseline: half the corpus undecided. Candidate: all of it decided. The delta is -0.5,
        // five times the default margin in the improving direction.
        var noisy = Run(
            [
                .. Enumerable.Range(0, 5).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(5, 5).Select(i => Undecided($"i{i}")),
            ]
        );
        var baseline = Baseline(noisy, minCoverage: 0.4);

        var comparison = BaselineComparer.Compare(UniformRun(passing: 10, size: 10), baseline);

        comparison.IsRefused.Should().BeFalse();
        comparison.NoDecisionRateDelta.Should().BeApproximately(-0.5, 1e-9);
        comparison
            .Triggers.Should()
            .NotHaveFlag(
                RegressionTrigger.NoDecisionRise,
                "a panel that got BETTER at deciding has not stopped being able to judge"
            );
    }

    /// <summary>
    /// <c>BaselineCoverage</c> survived being replaced by <c>run.Coverage</c>, because every test
    /// asserting on it used a fully-scored baseline against a fully-scored run, where both are 1.0.
    /// This is the comparison where the two genuinely differ.
    /// </summary>
    [Fact]
    public void The_baselines_own_coverage_is_reported_and_is_not_the_runs()
    {
        // Baseline: 6 of 10 scored. Candidate: 9 of 10 scored. Two different numbers, both asserted.
        var thin = Run(
            [
                .. Enumerable.Range(0, 6).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(6, 4).Select(i => Undecided($"i{i}")),
            ]
        );
        var baseline = Baseline(thin, minCoverage: 0.5);

        var candidate = Run(
            [
                .. Enumerable.Range(0, 9).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(9, 1).Select(i => Undecided($"i{i}")),
            ]
        );

        var comparison = BaselineComparer.Compare(candidate, baseline);

        comparison.IsRefused.Should().BeFalse();
        comparison.BaselineCoverage.Should().BeApproximately(0.6, 1e-9);
        comparison.Coverage.Should().BeApproximately(0.9, 1e-9);
        comparison.BaselineCoverage.Should().NotBe(comparison.Coverage);
    }

    /// <summary>
    /// The refusal path reports both coverages too, and it is a separate construction site — so it
    /// can drift from the comparing one without any test noticing.
    /// </summary>
    [Fact]
    public void A_refusal_also_reports_the_baselines_own_coverage_and_not_the_runs()
    {
        var thin = Run(
            [
                .. Enumerable.Range(0, 6).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(6, 4).Select(i => Undecided($"i{i}")),
            ]
        );
        var baseline = Baseline(thin, minCoverage: 0.5);

        var comparison = BaselineComparer.Compare(
            UniformRun(passing: 8, rubricVersion: "9.9"),
            baseline
        );

        comparison.Refusal.Should().Be(ComparisonRefusal.RubricVersionDiffers);
        comparison.BaselineCoverage.Should().BeApproximately(0.6, 1e-9);
        comparison.Coverage.Should().Be(1.0);
    }

    /// <summary>
    /// The 2.5th-percentile index survived becoming the median: the only test touching the interval
    /// pinned that two calls AGREE, which a reproducibly-wrong index satisfies perfectly, since the
    /// seed is fixed and the result is reproducible by construction.
    /// <para>
    /// Pinned here against facts derived independently of the implementation: a lower bound of a 95%
    /// interval sits strictly BELOW the point estimate the resampling is centred on (the median does
    /// not), and it brackets that estimate on both sides.
    /// </para>
    /// </summary>
    [Fact]
    public void The_bootstrap_lower_bound_sits_strictly_below_the_point_estimate()
    {
        // A 50% pass rate over 10 items is the highest-variance case available, so the resampled
        // distribution is wide and its 2.5th percentile is far from its median.
        var baseline = Baseline(UniformRun(passing: 10, size: 10), minCoverage: 0.4);
        var candidate = UniformRun(passing: 5, size: 10);

        var comparison = BaselineComparer.Compare(candidate, baseline);

        var pointEstimate = comparison.PassRateDelta!.Value;
        comparison
            .PassRateDeltaLower!.Value.Should()
            .BeLessThan(
                pointEstimate,
                "the 2.5th percentile of the resampled deltas is below their centre; the MEDIAN is at it"
            );
        comparison.PassRateDeltaUpper!.Value.Should().BeGreaterThan(pointEstimate);
    }

    /// <summary>
    /// The other half of the same claim: a 95% interval narrows as the corpus grows, because the
    /// sampling error it measures does. A wrong-but-reproducible percentile index does not have to.
    /// </summary>
    [Fact]
    public void The_bootstrap_interval_narrows_as_the_corpus_grows()
    {
        static double Width(int size)
        {
            var baseline = Baseline(UniformRun(passing: size, size: size), minCoverage: 0.4);
            var comparison = BaselineComparer.Compare(
                UniformRun(passing: size / 2, size: size),
                baseline
            );
            return comparison.PassRateDeltaUpper!.Value - comparison.PassRateDeltaLower!.Value;
        }

        var narrow = Width(400);
        var wide = Width(20);

        narrow.Should().BeLessThan(wide);
        narrow.Should().BePositive();
    }

    // ---- segmentation when an outage and a decision failure coincide (#380) -------------------

    /// <summary>
    /// The exclusion arms are ordered outcome-first, deliberately: flipping them would relabel
    /// every plain NoDecision as Degraded the moment a single judge faulted. But a verdict that is
    /// both NoDecision AND PanelUnavailable matches the earlier arm and never reaches the
    /// degradation one, so §5.3's segmentation count — the count that exists precisely so a reader
    /// can tell "the panel disagreed" from "the panel was down" — misses the case where those two
    /// coincide. A second, independent count over the verdict's own degradation is the fix; the
    /// exclusion-based count keeps its documented meaning.
    /// </summary>
    [Fact]
    public void A_no_decision_that_was_also_a_panel_outage_is_visible_to_the_degradation_segment()
    {
        var run = Run(
            [
                .. Enumerable.Range(0, 8).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                UndecidedAndDegraded("i8"),
                Undecided("i9"),
            ]
        );

        run.DegradedCount.Should().Be(0, "the exclusion arm it matched is NoDecision, as documented");
        run.NoDecisionCount.Should().Be(2);
        run.DegradedVerdictCount
            .Should()
            .Be(1, "one verdict was produced by a panel that could not be fully staffed");
    }

    [Fact]
    public void A_clean_run_has_no_degraded_verdicts()
    {
        UniformRun(passing: 8).DegradedVerdictCount.Should().Be(0);
    }

    // ---- a judge outage is not a candidate regression (#380) ----------------------------------

    /// <summary>
    /// A faulted item carries a null verdict, so it does not raise NoDecisionRate — null is not
    /// NoDecision — and it is not scored, so it leaves the pass rate's numerator while staying in
    /// its denominator. When the judge provider is having a bad hour the report therefore reads:
    /// pass rate collapsed, no-decision rate flat, PassRateDrop fired. Nothing got worse; the
    /// harness could not reach its judges.
    /// <para>
    /// The coverage floor catches only the severe case — a floor of 0.9 lets a 10% fault rate
    /// through untouched, and 10% of a corpus flipping from pass to not-counted is a large delta.
    /// </para>
    /// </summary>
    [Fact]
    public void A_judge_outage_is_refused_rather_than_reported_as_a_pass_rate_drop()
    {
        var baseline = Baseline(UniformRun(passing: 20, size: 20), minCoverage: 0.8);

        // 4 of 20 items faulted: coverage is 0.8, exactly on the floor, so the floor waves it
        // through — and the pass rate falls from 1.0 to 0.8, four times the default margin.
        var outage = Run(
            [
                .. Enumerable.Range(0, 16).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                .. Enumerable.Range(16, 4).Select(i => Faulted($"i{i}")),
            ]
        );

        outage.FaultRate.Should().BeApproximately(0.2, 1e-9);
        outage.Coverage.Should().BeApproximately(0.8, 1e-9);

        var comparison = BaselineComparer.Compare(outage, baseline);

        comparison.Refusal.Should().Be(ComparisonRefusal.FaultRateAboveMaximum);
        comparison.Triggers.Should().Be(RegressionTrigger.None, "a refusal is never a regression");
        comparison.PassRateDelta.Should().BeNull();
    }

    /// <summary>
    /// A fault rate under the bound is not refused: an occasional transport failure is normal and
    /// refusing on it would make the whole comparison unusable.
    /// </summary>
    [Fact]
    public void A_fault_rate_within_the_bound_still_compares()
    {
        var baseline = Baseline(UniformRun(passing: 20, size: 20), minCoverage: 0.5);

        var occasional = Run(
            [
                .. Enumerable.Range(0, 19).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                Faulted("i19"),
            ]
        );

        var comparison = BaselineComparer.Compare(occasional, baseline);

        occasional.FaultRate.Should().BeApproximately(0.05, 1e-9);
        comparison.IsRefused.Should().BeFalse();
    }

    /// <summary>
    /// The bound lives on the baseline for the same reason MinCoverage does: the run being judged
    /// must not be able to relax the bar it is judged against.
    /// </summary>
    [Fact]
    public void The_fault_rate_bound_is_the_baselines_to_set()
    {
        var strict = EvalBaseline.From(
            "base-1",
            UniformRun(passing: 20, size: 20),
            minCoverage: 0.5,
            maxFaultRate: 0.01
        );
        var lenient = EvalBaseline.From(
            "base-1",
            UniformRun(passing: 20, size: 20),
            minCoverage: 0.5,
            maxFaultRate: 0.5
        );

        var run = Run(
            [
                .. Enumerable.Range(0, 19).Select(i => Scored($"i{i}", 8.0, VerdictOutcome.Pass)),
                Faulted("i19"),
            ]
        );

        BaselineComparer.Compare(run, strict).Refusal
            .Should()
            .Be(ComparisonRefusal.FaultRateAboveMaximum);
        BaselineComparer.Compare(run, lenient).IsRefused.Should().BeFalse();
    }
}
