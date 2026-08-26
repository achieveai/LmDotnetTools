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
    /// The gate bound is refused ahead of the coverage floor for the reason the fault bound is: the
    /// floor names only the symptom ("too thin to compare", equally true of a genuinely hard
    /// corpus), while the bound names the cause. Here the floor does not even fire — a gate outage
    /// leaves coverage untouched, which is exactly why the floor cannot stand in for this bound.
    /// </summary>
    [Fact]
    public void The_gate_bound_catches_what_the_coverage_floor_cannot_see()
    {
        var baseline = Baseline(GatedRun(20), minCoverage: 0.9);

        var outage = Run(
            [.. Enumerable.Range(0, 20).Select(i => Scored($"i{i}", Inconclusive("schema")))]
        );

        outage.Coverage.Should().Be(1.0, "an inconclusive gate does not stop the item scoring");
        outage.Coverage.Should().BeGreaterThan(baseline.MinCoverage);

        BaselineComparer.Compare(outage, baseline).Refusal
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
    /// </summary>
    [Fact]
    public async Task An_environmental_gate_failure_reaches_the_comparison_as_a_refusal()
    {
        var snapshot = EvalFixtures.Snapshot(
            [.. Enumerable.Range(0, 10).Select(i => EvalFixtures.Item($"i{i}"))]
        );

        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config([new BrokenGate("checkout")]),
            snapshot
        );

        run.PassRate.Should().Be(1.0, "an inconclusive gate does not stop the item scoring");
        run.FaultedCount.Should().Be(0);
        run.InconclusiveGateCount.Should().Be(10);
        run.InconclusiveGateRate.Should().Be(1.0);
        run.InconclusiveGateIds.Should().Equal("checkout");

        // Frozen from the outage run itself, so every identity hash matches and no other refusal is
        // even reachable. The run is refused against a baseline it agrees with on everything else,
        // which is the strongest form of the claim: nothing but the gate signal is doing the work.
        var comparison = BaselineComparer.Compare(run, EvalBaseline.From("base-1", run, 0.5));

        comparison.Refusal.Should().Be(ComparisonRefusal.InconclusiveGateRateAboveMaximum);
        comparison.RefusalDetail.Should().Contain("checkout");
    }
}
