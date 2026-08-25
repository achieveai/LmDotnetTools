using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The pipeline (P6 spec §2.10) and the escalation boundary. Every claim about how many model
/// calls happened is asserted against a counter on the fake, because "no model call", "exactly
/// once" and "never reaches the arbiter" are claims about counts and nothing else can carry them.
/// </summary>
public sealed class JudgeGauntletTests
{
    private static readonly Rubric Rubric = HarnessFixtures.Rubric();
    private static readonly Dictionary<string, double> NoReliability = [];

    private static JudgeGauntlet Gauntlet(
        IReadOnlyList<IGate> gates,
        IReadOnlyList<IJudge> judges,
        HarnessOptions? options = null
    ) => new(gates, judges, new WeightedMeanAggregator(), options ?? new HarnessOptions());

    private static Task<Verdict> Run(JudgeGauntlet gauntlet, Candidate candidate) =>
        gauntlet.RunAsync(candidate, Rubric, NoReliability, CancellationToken.None);

    // ---- configuration validation (§2.12.1) --------------------------------------------------

    [Fact]
    public void A_two_judge_same_family_configuration_throws_at_construction()
    {
        var construct = () =>
            Gauntlet([], [new FakeJudge("a", "anthropic"), new FakeJudge("b", "anthropic")]);

        construct.Should().Throw<ArgumentException>().WithMessage("*same model family*");
    }

    [Fact]
    public void A_one_judge_configuration_does_not_throw_at_construction()
    {
        var construct = () => Gauntlet([], [new FakeJudge("a", "anthropic")]);

        construct.Should().NotThrow();
    }

    [Fact]
    public void A_zero_judge_configuration_throws_at_construction()
    {
        var construct = () => Gauntlet([], []);

        construct.Should().Throw<ArgumentException>();
    }

    // ---- gates (§2.4) ------------------------------------------------------------------------

    /// <summary>
    /// §2.4 — the cheapest way to reject an outright failure is to never ask a model about it. The
    /// zero on the judge's counter is the whole claim; the Fail outcome alone would also be
    /// produced by a panel that ran and cost tokens.
    /// </summary>
    [Fact]
    public async Task The_first_rejecting_gate_short_circuits_with_no_model_call()
    {
        var first = new CountingGate("g1", GateOutcome.Pass);
        var rejecting = new CountingGate("g2", GateOutcome.Reject);
        var later = new CountingGate("g3", GateOutcome.Pass);
        var judge = new FakeJudge("a", "anthropic");

        var verdict = await Run(
            Gauntlet([first, rejecting, later], [judge]),
            HarnessFixtures.Candidate()
        );

        judge.Calls.Should().Be(0, "a gate rejection must cost no tokens at all");
        later.Calls.Should().Be(0, "the first reject short-circuits the remaining gates");
        first.Calls.Should().Be(1);
        verdict.Outcome.Should().Be(VerdictOutcome.Fail);
        verdict.Score.Should().BeNull("a gate rejection carries no numeric score");
        verdict.Ballots.Should().BeEmpty();
        verdict.GateDecisions.Should().HaveCount(2);
        verdict.TieBreakRule.Should().Be("gate-reject");
    }

    /// <summary>
    /// §2.4 — Inconclusive is NOT a reject: an infrastructure failure escalates to the judge and is
    /// carried into the verdict, so it can never be mistaken for a clean bill of health.
    /// </summary>
    [Fact]
    public async Task An_inconclusive_gate_does_not_short_circuit_but_is_recorded()
    {
        var gate = new CountingGate("g1", GateOutcome.Inconclusive);
        var judge = new FakeJudge("a", "anthropic", score: 8.0);

        var verdict = await Run(Gauntlet([gate], [judge]), HarnessFixtures.Candidate());

        judge.Calls.Should().Be(1);
        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
        verdict
            .GateDecisions.Should()
            .ContainSingle()
            .Which.Outcome.Should()
            .Be(GateOutcome.Inconclusive);
    }

    [Fact]
    public async Task A_gate_that_does_not_apply_to_the_task_type_is_skipped()
    {
        var other = new CountingGate("g1", GateOutcome.Reject, "summarization");
        var judge = new FakeJudge("a", "anthropic");

        var verdict = await Run(Gauntlet([other], [judge]), HarnessFixtures.Candidate());

        other.Calls.Should().Be(0);
        verdict.GateDecisions.Should().BeEmpty();
        judge.Calls.Should().Be(1);
    }

    // ---- panel fan-out and degradation (§2.12.6) ----------------------------------------------

    /// <summary>
    /// §2.12.6 — one judge returns, one faults. Two separate claims, so they get two separate
    /// assertions: the verdict is marked SingleJudge, and its dispersion is null rather than 0.0.
    /// </summary>
    [Fact]
    public async Task One_judge_faulting_yields_a_SingleJudge_verdict_with_a_null_dispersion()
    {
        var healthy = new FakeJudge("a", "anthropic", score: 8.0);
        var broken = new FakeJudge(
            "b",
            "openai",
            fault: new HttpRequestException("provider down")
        );

        var verdict = await Run(Gauntlet([], [healthy, broken]), HarnessFixtures.Candidate());

        verdict.Degradation.Should().Be(PanelDegradation.SingleJudge);
        verdict.Dispersion.Should().BeNull("a lone judge is not a panel in perfect agreement");
        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
        verdict.Score.Should().Be(8.0);
        verdict.DegradationReason.Should().Contain("openai", "the unreachable family is named");
    }

    /// <summary>
    /// The reason string reaches persistence, so it is held to the PII rail: an exception TYPE, not
    /// its message, which can carry a URL, a token or a candidate excerpt.
    /// </summary>
    [Fact]
    public async Task A_fault_reason_records_the_exception_type_never_its_message()
    {
        var healthy = new FakeJudge("a", "anthropic", score: 8.0);
        var broken = new FakeJudge(
            "b",
            "openai",
            fault: new HttpRequestException("https://secret.internal/?token=abcd")
        );

        var verdict = await Run(Gauntlet([], [healthy, broken]), HarnessFixtures.Candidate());

        verdict.DegradationReason.Should().NotContain("secret.internal").And.NotContain("abcd");
        verdict.DegradationReason.Should().Contain("HttpRequestException");
    }

    [Fact]
    public async Task Both_judges_faulting_is_NoDecision_with_PanelUnavailable_and_never_a_Pass()
    {
        var first = new FakeJudge("a", "anthropic", fault: new HttpRequestException("down"));
        var second = new FakeJudge("b", "openai", fault: new HttpRequestException("down"));

        var verdict = await Run(Gauntlet([], [first, second]), HarnessFixtures.Candidate());

        verdict.Outcome.Should().Be(VerdictOutcome.NoDecision);
        verdict.Degradation.Should().Be(PanelDegradation.PanelUnavailable);
    }

    /// <summary>
    /// §2.12.5 — excluding the generator's family can leave one judge. The harness runs it rather
    /// than admitting the generator's own family, and rather than throwing.
    /// </summary>
    [Fact]
    public async Task Excluding_the_generators_family_down_to_one_judge_degrades_rather_than_throws()
    {
        var sameFamily = new FakeJudge("a", "anthropic", score: 2.0);
        var other = new FakeJudge("b", "openai", score: 8.0);

        var verdict = await Run(
            Gauntlet([], [sameFamily, other]),
            HarnessFixtures.Candidate(generatorFamily: "anthropic")
        );

        sameFamily.Calls.Should().Be(0, "a generator-family judge is never admitted");
        other.Calls.Should().Be(1);
        verdict.Degradation.Should().Be(PanelDegradation.SingleJudge);
        verdict.Score.Should().Be(8.0);
        verdict.DegradationReason.Should().Contain("anthropic");
    }

    [Fact]
    public async Task No_eligible_judge_is_NoDecision_with_PanelUnavailable_and_no_model_call()
    {
        var only = new FakeJudge("a", "anthropic");

        var verdict = await Run(
            Gauntlet([], [only]),
            HarnessFixtures.Candidate(generatorFamily: "anthropic")
        );

        only.Calls.Should().Be(0);
        verdict.Outcome.Should().Be(VerdictOutcome.NoDecision);
        verdict.Degradation.Should().Be(PanelDegradation.PanelUnavailable);
    }

    /// <summary>
    /// The Revobot adapter's shape: one configured judge, no arbiter reached, a null dispersion.
    /// </summary>
    [Fact]
    public async Task A_single_judge_configuration_is_SingleJudge_and_never_reaches_the_arbiter()
    {
        var arbiter = new FakeJudge("arb", "google", score: 1.0);
        var only = new FakeJudge("a", "anthropic", score: 8.0);

        var verdict = await Run(
            Gauntlet([], [only], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate()
        );

        arbiter.Calls.Should().Be(0, "one judge cannot straddle, so nothing escalates");
        verdict.Degradation.Should().Be(PanelDegradation.SingleJudge);
        verdict.Dispersion.Should().BeNull();
        verdict.Score.Should().Be(8.0);
    }

    // ---- the two-panel logic and the arbiter (§2.12.3) ----------------------------------------

    /// <summary>
    /// §2.12.2 — same side of the threshold resolves the common case for free. The zero on the
    /// arbiter's counter is what "for free" means.
    /// </summary>
    [Fact]
    public async Task Same_side_scores_decide_without_invoking_the_arbiter()
    {
        var arbiter = new FakeJudge("arb", "google", score: 1.0);
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 6.0);

        var verdict = await Run(
            Gauntlet([], [first, second], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate()
        );

        arbiter.Calls.Should().Be(0, "the panel agreed on the decision, so nothing escalates");
        verdict.TieBreakRule.Should().Be("consensus");
        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
    }

    /// <summary>
    /// §2.10 step 5 — one call, and one only. An arbiter invoked twice would mean the second
    /// reduction re-entered escalation.
    /// </summary>
    [Fact]
    public async Task Opposite_side_scores_escalate_exactly_once()
    {
        var arbiter = new FakeJudge("arb", "google", score: 3.0);
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 3.0);

        var verdict = await Run(
            Gauntlet([], [first, second], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate()
        );

        arbiter.Calls.Should().Be(1, "exactly once — not zero, and not once per reduction");
        verdict.Outcome.Should().Be(VerdictOutcome.Fail);
        verdict.Score.Should().Be(3.0, "the arbiter's score decides, it is not blended");
        verdict.TieBreakRule.Should().Be("arbiter:arb:google");
        verdict.Ballots.Should().HaveCount(3);
    }

    /// <summary>
    /// §2.12.6 — the escalation was attempted and failed. Distinguishable from the
    /// not-configured case below by the degradation, which is the persisted discriminator.
    /// </summary>
    [Fact]
    public async Task An_unavailable_arbiter_yields_a_split_marked_ArbiterUnavailable()
    {
        var arbiter = new FakeJudge("arb", "google", fault: new HttpRequestException("down"));
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 3.0);

        var verdict = await Run(
            Gauntlet([], [first, second], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate()
        );

        arbiter.Calls.Should().Be(1, "we tried");
        verdict.Outcome.Should().Be(VerdictOutcome.Split);
        verdict.Degradation.Should().Be(PanelDegradation.ArbiterUnavailable);
    }

    /// <summary>
    /// The other half of the pair above: no arbiter configured is "we chose not to escalate", and
    /// it is Degradation.None. Reading the two tests together is what makes them distinguishable.
    /// </summary>
    [Fact]
    public async Task A_straddle_with_no_arbiter_configured_is_a_split_with_no_degradation()
    {
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 3.0);

        var verdict = await Run(Gauntlet([], [first, second]), HarnessFixtures.Candidate());

        verdict.Outcome.Should().Be(VerdictOutcome.Split);
        verdict.Degradation.Should().Be(PanelDegradation.None);
        verdict.TieBreakRule.Should().Be("split:unresolved");
    }

    /// <summary>
    /// §2.12.3 rule 1 — escalation needs BOTH an arbiter and a family that is not the generator's.
    /// This is the case that has to break if step 5 ever escalates on configuration alone: the
    /// arbiter is configured, so a condition that only checks configuration would call it.
    /// </summary>
    [Fact]
    public async Task A_straddle_makes_zero_arbiter_calls_when_the_arbiter_is_the_generators_family()
    {
        var arbiter = new FakeJudge("arb", "google", score: 3.0);
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 3.0);

        var verdict = await Run(
            Gauntlet([], [first, second], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate(generatorFamily: "google")
        );

        arbiter.Calls.Should().Be(0, "an arbiter from the generator's family is never admitted");
        verdict.Outcome.Should().Be(VerdictOutcome.Split);
        verdict.TieBreakRule.Should().Be("split:unresolved");
        verdict
            .Degradation.Should()
            .Be(
                PanelDegradation.None,
                "we chose not to escalate — that is not the same as trying and failing"
            );
    }

    // ---- reference-guided grading (§3.4) ------------------------------------------------------

    /// <summary>§3.4 — the candidate's reference reaches the judge through the context.</summary>
    [Fact]
    public async Task The_candidates_reference_is_plumbed_through_to_the_judge()
    {
        var judge = new FakeJudge("a", "anthropic");
        var candidate = HarnessFixtures.Candidate() with { Reference = "the accepted answer" };

        _ = await Run(Gauntlet([], [judge]), candidate);

        judge.LastReference.Should().Be("the accepted answer");
    }

    // ---- cancellation ------------------------------------------------------------------------

    /// <summary>
    /// A caller's cancellation is not a provider outage. Recording it as a fault would report a
    /// PanelUnavailable verdict for a run the caller deliberately stopped.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_propagates_rather_than_becoming_a_fault()
    {
        var judge = new FakeJudge("a", "anthropic");
        var gauntlet = Gauntlet([], [judge]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = () => gauntlet.RunAsync(HarnessFixtures.Candidate(), Rubric, NoReliability, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- rubric/candidate agreement ----------------------------------------------------------

    [Fact]
    public async Task A_rubric_for_a_different_task_type_is_refused()
    {
        var gauntlet = Gauntlet([], [new FakeJudge("a", "anthropic")]);
        var candidate = HarnessFixtures.Candidate() with { TaskType = "summarization" };

        var run = () =>
            gauntlet.RunAsync(candidate, Rubric, NoReliability, CancellationToken.None);

        await run.Should().ThrowAsync<ArgumentException>().WithMessage("*task type*");
    }
}
