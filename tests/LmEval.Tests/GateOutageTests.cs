using AchieveAi.LmDotnetTools.LmEval.Running;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// An inconclusive gate does not block, so the item proceeds to the judges and scores normally.
/// That is right for one flaky gate on one item (#352). The faults that matter are not per item:
/// the checkout is gone, a path template is wrong, a schema file did not deploy — and then
/// <b>every</b> gate goes inconclusive on <b>every</b> item, every item scores a clean pass, and no
/// aggregate moves at all.
/// <para>
/// The judge path already has the machinery for exactly this misreading —
/// <see cref="EvalRun.FaultedCount"/>, <see cref="EvalRun.FaultRate"/> and
/// <see cref="ComparisonRefusal.FaultRateAboveMaximum"/>. These tests pin the gate path's half of
/// it: a run-level signal that cannot be read as a clean bill of health, and a comparison that
/// refuses rather than reporting an outage as a candidate regression.
/// </para>
/// </summary>
public class GateOutageTests
{
    private static GateDecision Inconclusive(string gateId) =>
        GateDecision.Inconclusive(gateId, nameof(IOException));

    private static GateDecision Passed(string gateId) => GateDecision.Pass(gateId, "content is clean");

    /// <summary>An item that scored a clean pass — with whatever the gates did recorded on it.</summary>
    private static EvalItemResult Scored(string id, params GateDecision[] gates) =>
        new()
        {
            CandidateId = id,
            Exclusion = ScoreExclusion.None,
            Verdict = new Verdict
            {
                CandidateId = id,
                Outcome = VerdictOutcome.Pass,
                Score = 8.0,
                GateDecisions = gates,
                Ballots = [],
                ExcludedBallots = [],
                RubricId = "test-rubric",
                RubricVersion = "1.0",
                TieBreakRule = TieBreakRules.Consensus,
                Degradation = PanelDegradation.None,
            },
        };

    /// <summary>
    /// An item the panel could not decide — it holds a verdict, so it is not a fault, but it yields
    /// no score and therefore leaves coverage. Carries whatever the gates did, so a run can breach
    /// the coverage floor and the gate bound at the same time.
    /// </summary>
    private static EvalItemResult Undecided(string id, params GateDecision[] gates) =>
        new()
        {
            CandidateId = id,
            Exclusion = ScoreExclusion.NoDecision,
            Verdict = new Verdict
            {
                CandidateId = id,
                Outcome = VerdictOutcome.NoDecision,
                Score = null,
                GateDecisions = gates,
                Ballots = [],
                ExcludedBallots = [],
                RubricId = "test-rubric",
                RubricVersion = "1.0",
                TieBreakRule = TieBreakRules.NoDecision,
                Degradation = PanelDegradation.None,
            },
        };

    /// <summary>
    /// An item the panel could neither decide nor fully staff, carrying whatever the gates did. Its
    /// exclusion arm is <see cref="ScoreExclusion.NoDecision"/> — outcome-first ordering — and its
    /// degradation is set, so it is the row that distinguishes a count over
    /// <see cref="Verdict.GateDecisions"/> from one that also reads the exclusion.
    /// </summary>
    private static EvalItemResult UndecidedAndDegraded(string id, params GateDecision[] gates) =>
        new()
        {
            CandidateId = id,
            Exclusion = ScoreExclusion.NoDecision,
            Verdict = new Verdict
            {
                CandidateId = id,
                Outcome = VerdictOutcome.NoDecision,
                Score = null,
                GateDecisions = gates,
                Ballots = [],
                ExcludedBallots = [],
                RubricId = "test-rubric",
                RubricVersion = "1.0",
                TieBreakRule = TieBreakRules.NoDecision,
                Degradation = PanelDegradation.PanelUnavailable,
                DegradationReason = "judge-faulted:openai:HttpRequestException",
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

    private static EvalRun Run(IReadOnlyList<EvalItemResult> items) =>
        new()
        {
            RunId = "run-1",
            TaskType = HarnessFixtures.TaskType,
            CorpusId = "corpus-1",
            CorpusSnapshotHash = "corpus-hash",
            EvaluatorConfigHash = "evaluator-hash",
            RubricId = "test-rubric",
            RubricVersion = "1.0",
            Items = items,
        };

    /// <summary><paramref name="size"/> clean passes, each carrying one passing gate decision.</summary>
    private static EvalRun GatedRun(int size) =>
        Run([.. Enumerable.Range(0, size).Select(i => Scored($"i{i}", Passed("schema")))]);

    private static EvalBaseline Baseline(EvalRun run, double minCoverage = 0.5) =>
        EvalBaseline.From("base-1", run, minCoverage);

    // ---- the run-level signal (#401 proposal 1) ------------------------------------------------

    /// <summary>
    /// Acceptance: "A test proving the count is per item, not per gate execution." Three gates on
    /// one item is one impaired item, so the number is comparable to
    /// <see cref="EvalRun.FaultedCount"/> rather than being a count of gate executions — which
    /// would exceed the corpus size and make the rate over it exceed 1.
    /// </summary>
    [Fact]
    public void The_inconclusive_count_is_per_item_not_per_gate_execution()
    {
        var run = Run(
            [
                Scored("i0", Inconclusive("schema"), Inconclusive("anchor"), Inconclusive("length")),
                Scored("i1", Passed("schema"), Passed("anchor"), Passed("length")),
            ]
        );

        run.InconclusiveGateCount.Should().Be(1, "one item was impaired, not three gates");
        run.InconclusiveGateRate.Should().BeApproximately(0.5, 1e-9);
    }

    /// <summary>
    /// The count reads <see cref="Verdict.GateDecisions"/> and nothing else, so it is
    /// <b>independent</b> of which <see cref="ScoreExclusion"/> arm the row matched. This is the
    /// counterpart of
    /// <c>A_no_decision_that_was_also_a_panel_outage_is_visible_to_the_degradation_segment</c>, and
    /// it exists for the same reason: the exclusion arms are ordered outcome-first, so a compound
    /// failure — the gates were down AND the panel could not decide, which is exactly what a bad
    /// deploy produces — matches an earlier arm and would vanish from a count that also read the
    /// exclusion. The gate outage must stay visible in precisely the case where it coincides with
    /// another one.
    /// <para>
    /// Every other test here scores its items, so all of them leave <c>Exclusion</c> at
    /// <see cref="ScoreExclusion.None"/> and none of them can tell the two constructions apart.
    /// </para>
    /// </summary>
    [Fact]
    public void An_impaired_gate_is_counted_whatever_exclusion_arm_the_row_matched()
    {
        var run = Run(
            [
                Scored("scored", Inconclusive("schema")),
                Undecided("undecided", Inconclusive("schema")),
                UndecidedAndDegraded("degraded", Inconclusive("schema")),
                Scored("clean", Passed("schema")),
            ]
        );

        run.Items.Where(i => i.Exclusion != ScoreExclusion.None)
            .Should()
            .HaveCount(2, "two rows matched a non-None arm — otherwise this proves nothing");

        run.InconclusiveGateCount
            .Should()
            .Be(3, "the count reads the gate decisions, not the exclusion");
        run.InconclusiveGateRate.Should().BeApproximately(0.75, 1e-9);
    }

    /// <summary>
    /// The sentinel. A rate of <c>0.0</c> on a run where no gate ever ran would say "no gate went
    /// inconclusive", which a skimmer reads as "the gates checked this run and it was clean" — a
    /// silent widening of unknown into fine. The rate is therefore <b>null</b> when the run recorded
    /// no gate decision at all, the same way <see cref="EvalRun.MeanScore"/> is null rather than
    /// zero when nothing was scored.
    /// <para>
    /// Null must not <i>refuse</i>, though: a harness with no gates configured is a legitimate
    /// configuration, and refusing every one of its runs would make the bound unusable.
    /// </para>
    /// </summary>
    [Fact]
    public void A_run_that_ran_no_gate_reports_an_absent_signal_rather_than_a_clean_one()
    {
        var ungated = Run([.. Enumerable.Range(0, 10).Select(i => Scored($"i{i}"))]);

        ungated.InconclusiveGateRate.Should().BeNull("no gate decision exists to be clean");
        ungated.InconclusiveGateCount.Should().Be(0);
        ungated.InconclusiveGateIds.Should().BeEmpty();

        BaselineComparer.Compare(ungated, Baseline(GatedRun(10))).Refusal
            .Should()
            .Be(
                ComparisonRefusal.None,
                "an absent gate signal is not an outage — a gateless harness is a real configuration"
            );
    }

    /// <summary>
    /// A run whose gates all ran to a conclusion reports <c>0.0</c>, which is a measurement. This is
    /// the case the null above must stay distinguishable from.
    /// </summary>
    [Fact]
    public void A_run_whose_gates_all_concluded_reports_a_zero_rate_not_a_null_one()
    {
        GatedRun(10).InconclusiveGateRate.Should().Be(0.0);
    }

    /// <summary>
    /// Acceptance: "the refusal names the gates". A refusal detail that says only "some gates were
    /// inconclusive" leaves the reader with nothing to act on; the environmental faults this exists
    /// to catch — a missing checkout, a wrong path template, an undeployed schema file — are each
    /// identified by <i>which</i> gate stopped working.
    /// </summary>
    [Fact]
    public void The_run_names_the_gates_that_went_inconclusive_once_each()
    {
        var run = Run(
            [
                Scored("i0", Inconclusive("schema"), Passed("anchor")),
                Scored("i1", Inconclusive("schema"), Inconclusive("anchor")),
            ]
        );

        run.InconclusiveGateIds.Should().Equal("schema", "anchor");
    }

    // ---- the comparison refusal (#401 proposal 2) ----------------------------------------------

    /// <summary>
    /// Acceptance: "A run in which every gate throws is refused rather than compared, and the
    /// refusal names the gates."
    /// <para>
    /// Every item still scores a clean pass, so no aggregate moves at all: pass rate flat, coverage
    /// full, fault rate zero, no trigger fires. Without this refusal the run reports "no regression"
    /// when what actually happened is that the deterministic layer was off for the whole run.
    /// </para>
    /// </summary>
    [Fact]
    public void A_gate_outage_is_refused_rather_than_reported_as_a_clean_run()
    {
        var baseline = Baseline(GatedRun(20), minCoverage: 0.8);

        var outage = Run(
            [
                .. Enumerable
                    .Range(0, 20)
                    .Select(i => Scored($"i{i}", Inconclusive("schema"), Inconclusive("anchor"))),
            ]
        );

        // Nothing an existing aggregate reports has moved: this is the whole hole.
        outage.PassRate.Should().Be(1.0);
        outage.Coverage.Should().Be(1.0);
        outage.FaultRate.Should().Be(0.0);
        outage.InconclusiveGateRate.Should().Be(1.0);

        var comparison = BaselineComparer.Compare(outage, baseline);

        comparison.Refusal.Should().Be(ComparisonRefusal.InconclusiveGateRateAboveMaximum);
        comparison.RefusalDetail.Should().Contain("schema").And.Contain("anchor");
        comparison.Triggers.Should().Be(RegressionTrigger.None, "a refusal is never a regression");
        comparison.PassRateDelta.Should().BeNull();
    }

    /// <summary>
    /// Acceptance: "A run with a single inconclusive gate is still compared — the bound must not
    /// turn one flaky gate into a refusal." Same argument as the fault-rate bound: an occasional
    /// transport failure is normal, and refusing on it would make the comparison unusable.
    /// </summary>
    [Fact]
    public void One_flaky_gate_on_one_item_still_compares()
    {
        var baseline = Baseline(GatedRun(20));

        var flaky = Run(
            [
                .. Enumerable.Range(0, 19).Select(i => Scored($"i{i}", Passed("schema"))),
                Scored("i19", Inconclusive("schema")),
            ]
        );

        flaky.InconclusiveGateRate.Should().BeApproximately(0.05, 1e-9);
        BaselineComparer.Compare(flaky, baseline).IsRefused.Should().BeFalse();
    }

    /// <summary>
    /// A run breaching BOTH the fault bound and the gate bound reports one refusal, and it must be
    /// the judge outage: a faulted item holds no verdict at all, where a gate-impaired item still
    /// produced one. The strictly larger loss is the cause worth naming, and the ordering is the
    /// whole of the behaviour, so it needs a case that can tell the two orderings apart.
    /// </summary>
    [Fact]
    public void A_run_breaching_both_outage_bounds_is_refused_for_the_larger_loss()
    {
        var baseline = Baseline(GatedRun(20));

        var both = Run(
            [
                .. Enumerable
                    .Range(0, 12)
                    .Select(i => Scored($"i{i}", Inconclusive("schema"))),
                .. Enumerable.Range(12, 8).Select(i => Faulted($"i{i}")),
            ]
        );

        both.FaultRate.Should().BeApproximately(0.4, 1e-9);
        both.InconclusiveGateRate.Should().BeApproximately(0.6, 1e-9);

        BaselineComparer.Compare(both, baseline).Refusal
            .Should()
            .Be(ComparisonRefusal.FaultRateAboveMaximum);
    }

    /// <summary>
    /// A gate outage on its own leaves coverage untouched — an inconclusive gate does not stop the
    /// item scoring — so the coverage floor cannot stand in for this bound at any severity.
    /// </summary>
    [Fact]
    public void The_gate_bound_catches_what_the_coverage_floor_cannot_see()
    {
        var baseline = Baseline(GatedRun(20), minCoverage: 0.9);

        var outage = Run(
            [.. Enumerable.Range(0, 20).Select(i => Scored($"i{i}", Inconclusive("schema")))]
        );

        outage.Coverage.Should().Be(1.0, "an inconclusive gate does not stop the item scoring");

        BaselineComparer.Compare(outage, baseline).Refusal
            .Should()
            .Be(ComparisonRefusal.InconclusiveGateRateAboveMaximum);
    }

    /// <summary>
    /// The gate bound is refused <b>ahead of</b> the coverage floor, for the reason the fault bound
    /// is: the floor names only the symptom ("too thin to compare", equally true of a genuinely hard
    /// corpus), while the bound names the cause a reader can act on.
    /// <para>
    /// Order is the whole of the behaviour, so it needs an input that can tell the two orderings
    /// apart — and the test above cannot, because a gate outage alone leaves coverage at 1.0 and the
    /// floor never fires whichever way round the two checks sit. This is the sibling case that
    /// <c>A_run_breaching_both_bounds_is_refused_for_the_cause_not_the_symptom</c> warns about on
    /// the fault path. It is reachable because the two conditions are independent: an inconclusive
    /// gate does not move coverage, but the <i>judge</i> side of other items in the same run does.
    /// </para>
    /// </summary>
    [Fact]
    public void A_run_breaching_the_gate_bound_and_the_coverage_floor_is_refused_for_the_cause()
    {
        var baseline = Baseline(GatedRun(20), minCoverage: 0.8);

        // 12 impaired-but-scored items and 8 the panel could not decide: coverage 0.6 (under the
        // 0.8 floor) AND gate rate 0.6 (over the 0.05 default). Both refusals apply; only the one
        // naming the cause is worth reporting.
        var both = Run(
            [
                .. Enumerable
                    .Range(0, 12)
                    .Select(i => Scored($"i{i}", Inconclusive("schema"))),
                .. Enumerable.Range(12, 8).Select(i => Undecided($"i{i}")),
            ]
        );

        both.FaultRate.Should().Be(0.0, "no item faulted, so the fault refusal cannot preempt");
        both.Coverage.Should().BeApproximately(0.6, 1e-9);
        both.Coverage.Should().BeLessThan(baseline.MinCoverage, "the floor is genuinely breached");
        both.InconclusiveGateRate.Should().BeApproximately(0.6, 1e-9);

        BaselineComparer.Compare(both, baseline).Refusal
            .Should()
            .Be(ComparisonRefusal.InconclusiveGateRateAboveMaximum);
    }

    // ---- the bound itself ----------------------------------------------------------------------

    /// <summary>
    /// The bound lives on the baseline for the same reason <see cref="EvalBaseline.MinCoverage"/>
    /// and <see cref="EvalBaseline.MaxFaultRate"/> do: the run being judged must not be able to
    /// relax the bar it is judged against.
    /// </summary>
    [Fact]
    public void The_inconclusive_gate_bound_is_the_baselines_to_set()
    {
        var strict = EvalBaseline.From(
            "base-1",
            GatedRun(20),
            minCoverage: 0.5,
            maxInconclusiveGateRate: 0.01
        );
        var lenient = EvalBaseline.From(
            "base-1",
            GatedRun(20),
            minCoverage: 0.5,
            maxInconclusiveGateRate: 0.5
        );

        var run = Run(
            [
                .. Enumerable.Range(0, 19).Select(i => Scored($"i{i}", Passed("schema"))),
                Scored("i19", Inconclusive("schema")),
            ]
        );

        BaselineComparer.Compare(run, strict).Refusal
            .Should()
            .Be(ComparisonRefusal.InconclusiveGateRateAboveMaximum);
        BaselineComparer.Compare(run, lenient).IsRefused.Should().BeFalse();
    }

    /// <summary>
    /// The same hazard <see cref="EvalBaseline.MaxFaultRate"/> was hardened against in #380: a
    /// record built by a factory is still rewritable through a <c>with</c> expression, and NaN is
    /// the reachable value that does the most damage, because every comparison against it is false
    /// and the refusal is permanently disarmed with no output to notice.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void The_gate_bound_cannot_be_rewritten_past_the_validation_its_factory_applied(
        double bad
    )
    {
        var baseline = Baseline(GatedRun(20));

        var rewrite = () => baseline with { MaxInconclusiveGateRate = bad };
        var mint = () =>
            EvalBaseline.From("base-1", GatedRun(20), 0.5, maxInconclusiveGateRate: bad);

        rewrite.Should().Throw<ArgumentOutOfRangeException>();
        mint.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- end to end through the runner ----------------------------------------------------------

    /// <summary>
    /// The shape the issue is actually about, driven through <see cref="EvalRunner"/> rather than
    /// hand-built runs: a gate whose environment is gone throws on every item, the gauntlet contains
    /// each throw into an inconclusive decision (#352), every item scores a clean pass — and the
    /// comparison refuses.
    /// <para>
    /// The baseline is frozen from a run of the <b>same configuration</b> with its checkout intact,
    /// not from the outage run itself. Both runs therefore agree on every identity hash and no other
    /// refusal is reachable — the strongest form of the claim, since nothing but the gate signal can
    /// be doing the work — and it is the pairing that actually occurs: one deploy, one run before
    /// the checkout went missing and one after. #427: freezing the outage run instead would now be
    /// refused at construction, and relying on that gap was what made it visible.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_environmental_gate_failure_reaches_the_comparison_as_a_refusal()
    {
        var snapshot = EvalFixtures.Snapshot(
            [.. Enumerable.Range(0, 10).Select(i => EvalFixtures.Item($"i{i}"))]
        );

        var healthy = await EvalFixtures.RunAsync(
            EvalFixtures.Config([new CheckoutGate("checkout", checkoutPresent: true)]),
            snapshot
        );

        var outage = await EvalFixtures.RunAsync(
            EvalFixtures.Config([new CheckoutGate("checkout")]),
            snapshot
        );

        healthy
            .EvaluatorConfigHash.Should()
            .Be(
                outage.EvaluatorConfigHash,
                "the two runs differ in their environment and not in their configuration, so "
                    + "EvaluatorConfigDiffers cannot be the refusal under test"
            );
        healthy.CorpusSnapshotHash.Should().Be(outage.CorpusSnapshotHash);
        healthy.InconclusiveGateRate.Should().Be(0.0, "the checkout resolved on every item");

        outage.PassRate.Should().Be(1.0, "an inconclusive gate does not stop the item scoring");
        outage.FaultedCount.Should().Be(0);
        outage.InconclusiveGateCount.Should().Be(10);
        outage.InconclusiveGateRate.Should().Be(1.0);
        outage.InconclusiveGateIds.Should().Equal("checkout");

        var comparison = BaselineComparer.Compare(
            outage,
            EvalBaseline.From("base-1", healthy, 0.5)
        );

        comparison.Refusal.Should().Be(ComparisonRefusal.InconclusiveGateRateAboveMaximum);
        comparison.RefusalDetail.Should().Contain("checkout");
    }

    // ---- the baseline the outage would otherwise have frozen (#427) -----------------------------

    /// <summary>
    /// The comparison refusal above protects the <i>candidate</i> side only.
    /// <see cref="EvalBaseline.From"/> refused a run that scored nothing and nothing else — and an
    /// outage run scores <b>everything</b>, so it walked straight through and froze a pass rate
    /// measured with the gates off as the number every later run is judged against.
    /// <para>
    /// A poisoned baseline is strictly worse than a poisoned candidate: the candidate distorts one
    /// comparison and is refused, the baseline distorts every comparison after it and is refused by
    /// nothing. Same class as a8369cc0 — refuse at construction, not downstream.
    /// </para>
    /// </summary>
    [Fact]
    public void A_baseline_is_not_frozen_from_a_run_whose_gates_were_off()
    {
        var outage = Run(
            [.. Enumerable.Range(0, 20).Select(i => Scored($"i{i}", Inconclusive("schema")))]
        );

        // Every number a reader would sanity-check the source run on looks pristine. That is why
        // nothing downstream could have caught this.
        outage.PassRate.Should().Be(1.0);
        outage.Coverage.Should().Be(1.0);
        outage.FaultRate.Should().Be(0.0);
        outage.MeanScore.Should().NotBeNull("the run scored every item, so the old check passes it");

        var freeze = () => EvalBaseline.From("base-1", outage, 0.5);

        freeze
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*schema*",
                "the refusal names the gate that broke, as the comparison refusal does"
            );
    }

    /// <summary>
    /// The null sentinel is respected on the way in as it is at comparison: a harness with no gates
    /// configured is a real configuration, and refusing every baseline it could ever mint would make
    /// the bound unusable rather than safe.
    /// </summary>
    [Fact]
    public void A_gateless_run_is_still_a_legitimate_baseline_source()
    {
        var ungated = Run([.. Enumerable.Range(0, 20).Select(i => Scored($"i{i}"))]);

        ungated.InconclusiveGateRate.Should().BeNull("no gate decision exists to be clean");

        EvalBaseline.From("base-1", ungated, 0.5).PassRate.Should().Be(1.0);
    }

    /// <summary>A run whose gates all ran to a conclusion is untouched by the refusal.</summary>
    [Fact]
    public void A_run_whose_gates_all_concluded_still_freezes()
    {
        EvalBaseline.From("base-1", GatedRun(20), 0.5).PassRate.Should().Be(1.0);
    }

    /// <summary>
    /// The construction check is the same predicate as the comparison one, boundary included: a rate
    /// <b>at</b> the bound is not above it. Without this case the check could tighten to
    /// <c>&gt;=</c> and turn the one-flaky-gate run that
    /// <see cref="One_flaky_gate_on_one_item_still_compares"/> insists on comparing into a run that
    /// can never be frozen.
    /// </summary>
    [Fact]
    public void A_run_at_exactly_the_bound_still_freezes()
    {
        var flaky = Run(
            [
                .. Enumerable.Range(0, 19).Select(i => Scored($"i{i}", Passed("schema"))),
                Scored("i19", Inconclusive("schema")),
            ]
        );

        flaky
            .InconclusiveGateRate.Should()
            .BeApproximately(
                EvalBaseline.DefaultMaxInconclusiveGateRate,
                1e-9,
                "the rate must sit exactly on the bound or this proves nothing about the boundary"
            );

        EvalBaseline.From("base-1", flaky, 0.5).PassRate.Should().Be(1.0);
    }

    /// <summary>
    /// The bound refused against is the one <i>this</i> baseline will impose, not the constant
    /// default — so a caller who deliberately widens the bound is not refused a baseline it would
    /// then have accepted at comparison, and a caller who tightens it cannot freeze a source run its
    /// own bound would reject.
    /// </summary>
    [Fact]
    public void The_construction_bound_is_the_one_the_baseline_will_enforce()
    {
        var impaired = Run(
            [
                .. Enumerable.Range(0, 10).Select(i => Scored($"i{i}", Inconclusive("schema"))),
                .. Enumerable.Range(10, 10).Select(i => Scored($"i{i}", Passed("schema"))),
            ]
        );

        impaired.InconclusiveGateRate.Should().BeApproximately(0.5, 1e-9);

        var widened = EvalBaseline.From("base-1", impaired, 0.5, maxInconclusiveGateRate: 0.6);
        widened.MaxInconclusiveGateRate.Should().Be(0.6);

        var atDefault = () => EvalBaseline.From("base-1", impaired, 0.5);
        atDefault.Should().Throw<ArgumentException>();

        var tightened = () =>
            EvalBaseline.From("base-1", impaired, 0.5, maxInconclusiveGateRate: 0.49);
        tightened.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A source run that breaches the gate bound <b>and</b> scored nothing is refused for the
    /// outage, mirroring the comparer's ordering — where the gate bound is checked ahead of the
    /// coverage floor and ahead of the "scored no items at all" arm that shares its refusal. Freezing
    /// a run and comparing it therefore name the same cause, and a reader is never told "this run
    /// scored nothing" about a run whose gates were the reason.
    /// <para>
    /// Order is the whole of the behaviour here, so it needs the input that can tell the two
    /// orderings apart: both conditions must genuinely hold, which the guards below assert.
    /// </para>
    /// </summary>
    [Fact]
    public void A_gate_outage_that_also_scored_nothing_is_refused_for_the_outage()
    {
        var both = Run(
            [.. Enumerable.Range(0, 20).Select(i => Undecided($"i{i}", Inconclusive("schema")))]
        );

        both.MeanScore.Should().BeNull("the scored-nothing arm must genuinely apply too");
        both.InconclusiveGateRate.Should().Be(1.0);

        var freeze = () => EvalBaseline.From("base-1", both, 0.5);

        freeze
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*schema*", "the outage is the cause; scoring nothing is a consequence");
    }
}
