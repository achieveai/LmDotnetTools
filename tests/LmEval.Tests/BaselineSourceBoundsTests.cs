using AchieveAi.LmDotnetTools.LmEval.Running;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// <see cref="EvalBaseline.From"/> holds its <i>source</i> run to every bound the baseline it mints
/// will hold candidates to — not just the inconclusive-gate one (#441).
/// <para>
/// #427 closed this hole for gates. The sibling bounds were left open, and the failure they admit is
/// the same one field over: a run with a fault rate of 0.5 has a non-null <see cref="EvalRun.MeanScore"/>,
/// walks past the "scored nothing" check exactly as a gate-outage run does, and freezes a pass rate
/// depressed by an outage as the number every later run is compared against. A poisoned baseline is
/// strictly worse than a poisoned candidate: the candidate distorts one comparison and is refused,
/// the baseline distorts every comparison after it and is refused by nothing.
/// </para>
/// <para>
/// Ordering is behaviour here, not presentation, so each pair of adjacent arms gets the input that
/// can tell the two orderings apart — a run that genuinely breaches <b>both</b>. The order mirrors
/// <see cref="BaselineComparer"/>'s: fault bound, then gate bound, then coverage floor, then the
/// scored-nothing arm. Freezing a run and comparing it then name the same cause.
/// </para>
/// </summary>
public class BaselineSourceBoundsTests
{
    private static GateDecision Inconclusive(string gateId) =>
        GateDecision.Inconclusive(gateId, nameof(IOException));

    /// <summary>An item that scored a clean pass, carrying whatever the gates did.</summary>
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
    /// An item the panel could not decide. It holds a verdict, so it is not a fault, and it yields
    /// no score, so it leaves coverage — the row that moves the floor without moving the fault rate.
    /// </summary>
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

    /// <summary><paramref name="size"/> clean passes, no gates and no faults.</summary>
    private static EvalRun CleanRun(int size) =>
        Run([.. Enumerable.Range(0, size).Select(i => Scored($"i{i}"))]);

    /// <summary>
    /// <paramref name="faulted"/> of <paramref name="size"/> items faulted; the rest scored. Nothing
    /// gates, so the gate bound cannot preempt the fault refusal.
    /// </summary>
    private static EvalRun FaultedRun(int faulted, int size)
    {
        var items = new List<EvalItemResult>();
        items.AddRange(Enumerable.Range(0, faulted).Select(i => Faulted($"f{i}")));
        items.AddRange(Enumerable.Range(0, size - faulted).Select(i => Scored($"i{i}")));
        return Run(items);
    }

    /// <summary>
    /// <paramref name="undecided"/> of <paramref name="size"/> items yielded no score; the rest
    /// scored. Nothing faults and nothing gates, so only the floor can fire.
    /// </summary>
    private static EvalRun ThinRun(int undecided, int size)
    {
        var items = new List<EvalItemResult>();
        items.AddRange(Enumerable.Range(0, undecided).Select(i => Undecided($"u{i}")));
        items.AddRange(Enumerable.Range(0, size - undecided).Select(i => Scored($"i{i}")));
        return Run(items);
    }

    // ---- the fault bound on the source run ------------------------------------------------------

    /// <summary>
    /// The #427 failure, one field over. Every number a reader would sanity-check the source run on
    /// is either pristine or explained away, and the pass rate it freezes was measured while half
    /// the corpus never reached a judge.
    /// </summary>
    [Fact]
    public void A_baseline_is_not_frozen_from_a_run_whose_judges_faulted()
    {
        var outage = FaultedRun(faulted: 10, size: 20);

        // Guards. Without them this test could be passing for any of three other reasons.
        outage.FaultRate.Should().Be(0.5);
        outage
            .MeanScore.Should()
            .NotBeNull("the scored-nothing arm must NOT be what refuses this run");
        outage
            .InconclusiveGateRate.Should()
            .BeNull("no gate decision exists, so the gate bound cannot preempt");
        outage
            .Coverage.Should()
            .Be(0.5, "and the floor below is set to admit exactly this coverage");

        var freeze = () => EvalBaseline.From("base-1", outage, minCoverage: 0.5);

        freeze
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*fault rate*",
                "the refusal names the cause, as the comparison refusal does"
            );
    }

    /// <summary>
    /// The boundary, at the exact default, and it is <b>exclusive</b>: a rate <i>at</i> the bound is
    /// not above it, matching <c>run.FaultRate &gt; baseline.MaxFaultRate</c> at comparison. Without
    /// this case the check could tighten to <c>&gt;=</c> and a single transport failure in twenty —
    /// the case <see cref="EvalBaseline.DefaultMaxFaultRate"/>'s own doc says must stay usable —
    /// would become a run that can never be frozen.
    /// </summary>
    [Fact]
    public void A_run_at_exactly_the_fault_bound_still_freezes()
    {
        var flaky = FaultedRun(faulted: 1, size: 20);

        flaky
            .FaultRate.Should()
            .BeApproximately(
                EvalBaseline.DefaultMaxFaultRate,
                1e-9,
                "the rate must sit exactly on the bound or this proves nothing about the boundary"
            );

        EvalBaseline.From("base-1", flaky, minCoverage: 0.5).PassRate.Should().Be(0.95);
    }

    /// <summary>A run nothing faulted on is untouched by the refusal.</summary>
    [Fact]
    public void A_run_with_no_faults_at_all_still_freezes()
    {
        EvalBaseline.From("base-1", CleanRun(20), minCoverage: 0.5).PassRate.Should().Be(1.0);
    }

    /// <summary>
    /// The bound refused against is the one <b>this</b> baseline will impose — the same stored
    /// parameter, not a second literal that can drift from it. A caller who widens the bound is not
    /// refused a baseline it would then have accepted at comparison, and a caller who tightens it
    /// cannot freeze a source run its own bound would reject.
    /// </summary>
    [Fact]
    public void The_fault_bound_refused_against_is_the_one_the_baseline_will_impose()
    {
        var impaired = FaultedRun(faulted: 10, size: 20);

        impaired.FaultRate.Should().Be(0.5);

        var widened = EvalBaseline.From("base-1", impaired, minCoverage: 0.5, maxFaultRate: 0.6);
        widened
            .MaxFaultRate.Should()
            .Be(0.6, "the accepted run's baseline carries the bound it was accepted under");

        var atDefault = () => EvalBaseline.From("base-1", impaired, minCoverage: 0.5);
        atDefault.Should().Throw<ArgumentException>();

        var tightened = () =>
            EvalBaseline.From("base-1", impaired, minCoverage: 0.5, maxFaultRate: 0.49);
        tightened.Should().Throw<ArgumentException>();
    }

    // ---- the coverage floor on the source run ---------------------------------------------------

    /// <summary>
    /// A source run too thin to be compared against is also too thin to be compared <i>from</i>. Its
    /// frozen mean and P10 are conditional metrics over a subset the floor exists to declare
    /// unrepresentative, and freezing them publishes that subset as the run every later candidate is
    /// held to.
    /// </summary>
    [Fact]
    public void A_baseline_is_not_frozen_from_a_run_below_its_own_coverage_floor()
    {
        var thin = ThinRun(undecided: 10, size: 20);

        thin.Coverage.Should().Be(0.5);
        thin.FaultRate.Should().Be(0.0, "the fault bound cannot preempt");
        thin.InconclusiveGateRate.Should().BeNull("the gate bound cannot preempt");
        thin.MeanScore.Should().NotBeNull("the scored-nothing arm cannot preempt either");

        var freeze = () => EvalBaseline.From("base-1", thin, minCoverage: 0.9);

        freeze.Should().Throw<ArgumentException>().WithMessage("*coverage*");
    }

    /// <summary>
    /// The floor is <b>inclusive</b>: coverage <i>at</i> the floor clears it, matching
    /// <c>run.Coverage &lt; baseline.MinCoverage</c> at comparison. A run that would compare cleanly
    /// against a baseline must be able to become one.
    /// </summary>
    [Fact]
    public void A_run_exactly_at_the_coverage_floor_still_freezes()
    {
        var thin = ThinRun(undecided: 10, size: 20);

        thin.Coverage.Should().Be(0.5, "the coverage must sit exactly on the floor asserted below");

        EvalBaseline.From("base-1", thin, minCoverage: 0.5).ScoredItems.Should().Be(10);
    }

    /// <summary>
    /// The floor refused against is the one this baseline will impose, from the same stored
    /// parameter.
    /// </summary>
    [Fact]
    public void The_coverage_floor_refused_against_is_the_one_the_baseline_will_impose()
    {
        var thin = ThinRun(undecided: 10, size: 20);

        var lenient = EvalBaseline.From("base-1", thin, minCoverage: 0.4);
        lenient.MinCoverage.Should().Be(0.4);

        var strict = () => EvalBaseline.From("base-1", thin, minCoverage: 0.6);
        strict.Should().Throw<ArgumentException>();
    }

    // ---- ordering: one distinguishing case per adjacent pair ------------------------------------

    /// <summary>
    /// Fault bound ahead of gate bound, mirroring <see cref="BaselineComparer"/>: a faulted item
    /// holds no verdict at all where a gate-impaired item still produced one, so when both bounds
    /// break the judge outage is the strictly larger loss and the cause worth naming.
    /// </summary>
    [Fact]
    public void A_run_that_both_faulted_and_lost_its_gates_is_refused_for_the_faults()
    {
        var both = Run(
            [
                .. Enumerable.Range(0, 10).Select(i => Faulted($"f{i}")),
                .. Enumerable.Range(0, 10).Select(i => Scored($"i{i}", Inconclusive("schema"))),
            ]
        );

        both.FaultRate.Should().Be(0.5, "both bounds must genuinely break or the order is untested");
        both.InconclusiveGateRate.Should().Be(0.5);

        var freeze = () => EvalBaseline.From("base-1", both, minCoverage: 0.5);

        freeze.Should().Throw<ArgumentException>().WithMessage("*fault rate*");
    }

    /// <summary>
    /// Gate bound ahead of the coverage floor, mirroring <see cref="BaselineComparer"/>: the floor
    /// names only the symptom, and it cannot see a gate outage at any severity — an inconclusive
    /// gate does not block, so every impaired item still scores and coverage never moves for that
    /// reason at all.
    /// </summary>
    [Fact]
    public void A_gate_outage_below_the_coverage_floor_is_refused_for_the_outage()
    {
        var both = Run(
            [
                .. Enumerable.Range(0, 12).Select(i => Scored($"i{i}", Inconclusive("schema"))),
                .. Enumerable.Range(0, 8).Select(i => Undecided($"u{i}")),
            ]
        );

        both.FaultRate.Should().Be(0.0, "the fault bound must not be what refuses this");
        both.InconclusiveGateRate.Should().BeApproximately(0.6, 1e-9);
        both.Coverage.Should().BeApproximately(0.6, 1e-9);

        var freeze = () => EvalBaseline.From("base-1", both, minCoverage: 0.9);

        freeze.Should().Throw<ArgumentException>().WithMessage("*inconclusive-gate rate*");
    }

    /// <summary>
    /// Coverage floor ahead of the scored-nothing arm, mirroring <see cref="BaselineComparer"/>,
    /// where both refusals share one <see cref="ComparisonRefusal"/> value and the floor's detail is
    /// the one a reader gets. A run that scored nothing has a coverage of zero, so it breaches every
    /// positive floor as well — and the floor is the fact that generalises.
    /// </summary>
    [Fact]
    public void A_run_below_its_floor_that_also_scored_nothing_is_refused_for_the_floor()
    {
        var barren = ThinRun(undecided: 20, size: 20);

        barren.Coverage.Should().Be(0.0);
        barren.MeanScore.Should().BeNull("the scored-nothing arm must genuinely apply too");

        var freeze = () => EvalBaseline.From("base-1", barren, minCoverage: 0.5);

        freeze.Should().Throw<ArgumentException>().WithMessage("*coverage*");
    }

    /// <summary>
    /// The non-vacuity half of the case above: with no floor to breach, the scored-nothing arm is
    /// still reachable and still the arm that fires. Without this, moving the floor check ahead of
    /// it could have made that arm dead code and nothing would have said so.
    /// </summary>
    [Fact]
    public void With_no_floor_at_all_a_run_that_scored_nothing_is_still_refused_for_scoring_nothing()
    {
        var barren = ThinRun(undecided: 20, size: 20);

        barren.Coverage.Should().Be(0.0, "and 0.0 < 0.0 is false, so the floor cannot fire");

        var freeze = () => EvalBaseline.From("base-1", barren, minCoverage: 0.0);

        freeze.Should().Throw<ArgumentException>().WithMessage("*scored none*");
    }
}
