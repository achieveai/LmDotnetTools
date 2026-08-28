using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Judges;
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
        HarnessOptions? options = null,
        IBallotAggregator? aggregator = null
    ) => new(gates, judges, aggregator ?? new WeightedMeanAggregator(), options ?? new HarnessOptions());

    private static Task<Verdict> Run(JudgeGauntlet gauntlet, Candidate candidate) =>
        gauntlet.RunAsync(candidate, Rubric, NoReliability, CancellationToken.None);

    // ---- configuration validation (§2.12.1) --------------------------------------------------

    [Fact]
    public void A_two_judge_same_family_configuration_throws_at_construction()
    {
        var construct = () => Gauntlet([], [new FakeJudge("a", "anthropic"), new FakeJudge("b", "anthropic")]);

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

    /// <summary>
    /// Confidence is in [0,1], and AbstainFloor is compared straight against it. Writing the
    /// default as a percentage — 34 rather than 0.34 — puts EVERY ballot below the floor, so every
    /// candidate becomes NoDecision with a null score and an entire corpus run produces nothing and
    /// reports success. The class doc already claims options are "validated once at construction".
    /// </summary>
    [Fact]
    public void An_abstain_floor_written_as_a_percentage_throws_at_construction()
    {
        var construct = () => Gauntlet([], [new FakeJudge("a", "anthropic")], new HarnessOptions { AbstainFloor = 34 });

        construct.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*confidence*");
    }

    /// <summary>The mirror image: a negative floor silently disables the filter entirely.</summary>
    [Fact]
    public void A_negative_abstain_floor_throws_at_construction()
    {
        var construct = () =>
            Gauntlet([], [new FakeJudge("a", "anthropic")], new HarnessOptions { AbstainFloor = -1.0 });

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_abstain_floor_may_sit_on_either_end_of_its_range()
    {
        var atZero = () => Gauntlet([], [new FakeJudge("a", "anthropic")], new HarnessOptions { AbstainFloor = 0.0 });
        var atOne = () => Gauntlet([], [new FakeJudge("a", "anthropic")], new HarnessOptions { AbstainFloor = 1.0 });

        atZero.Should().NotThrow();
        atOne.Should().NotThrow();
    }

    /// <summary>
    /// The alarm is compared against a population standard deviation, which is never negative, so
    /// a negative bound is an alarm that fires on every verdict that has one at all.
    /// </summary>
    [Fact]
    public void A_negative_dispersion_alarm_throws_at_construction()
    {
        var construct = () =>
            Gauntlet([], [new FakeJudge("a", "anthropic")], new HarnessOptions { DispersionAlarm = -0.5 });

        construct.Should().Throw<ArgumentOutOfRangeException>();
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

        var verdict = await Run(Gauntlet([first, rejecting, later], [judge]), HarnessFixtures.Candidate());

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
        verdict.GateDecisions.Should().ContainSingle().Which.Outcome.Should().Be(GateOutcome.Inconclusive);
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

    /// <summary>
    /// §2.4 — <see cref="GateOutcome.Inconclusive"/> exists to say "the gate could not decide", and
    /// the most common way a real gate fails to decide is by throwing: a missing tool or an absent
    /// checkout surfaces as an <c>IOException</c>, not as a returned Inconclusive. The judge path
    /// already contains a fault into a <see cref="JudgeFault"/> so one outage degrades the verdict
    /// instead of losing it; the gate path must be symmetric or the candidate is lost entirely.
    /// </summary>
    [Fact]
    public async Task A_gate_that_throws_is_recorded_as_inconclusive_and_the_run_continues()
    {
        var boom = new MarkerGate("never", gateId: "boom", throwOnCandidateId: "cand-1");
        var later = new CountingGate("after", GateOutcome.Pass);
        var judge = new FakeJudge("a", "anthropic", score: 8.0);

        var verdict = await Run(Gauntlet([boom, later], [judge]), HarnessFixtures.Candidate());

        verdict.GateDecisions.Should().HaveCount(2);
        var thrown = verdict.GateDecisions[0];
        thrown.GateId.Should().Be("boom");
        thrown.Outcome.Should().Be(GateOutcome.Inconclusive);
        thrown.Reason.Should().Contain(nameof(InvalidOperationException));
        later.Calls.Should().Be(1, "a gate that could not decide does not stop the ones after it");
        judge.Calls.Should().Be(1);
        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
    }

    /// <summary>
    /// The gate reason is persisted, so it is held to the same stable, non-sensitive rail as every
    /// other persisted diagnostic: the exception's TYPE, never its message.
    /// </summary>
    [Fact]
    public async Task A_thrown_gate_reason_carries_the_exception_type_and_not_its_message()
    {
        var boom = new MarkerGate("never", gateId: "boom", throwOnCandidateId: "cand-1");

        var verdict = await Run(Gauntlet([boom], [new FakeJudge("a", "anthropic")]), HarnessFixtures.Candidate());

        verdict.GateDecisions[0].Reason.Should().NotContain("gate blew up");
    }

    /// <summary>
    /// A caller's cancellation is never a gate failure: recording it as an inconclusive decision
    /// would put a gate record on a run nobody tried to complete.
    /// </summary>
    [Fact]
    public async Task A_cancelled_gate_propagates_rather_than_becoming_inconclusive()
    {
        using var source = new CancellationTokenSource();
        var gauntlet = Gauntlet([new CancellingGate(source)], [new FakeJudge("a", "anthropic")]);

        var run = async () => await gauntlet.RunAsync(HarnessFixtures.Candidate(), Rubric, NoReliability, source.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
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
        var broken = new FakeJudge("b", "openai", fault: new HttpRequestException("provider down"));

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

        var verdict = await Run(Gauntlet([], [only]), HarnessFixtures.Candidate(generatorFamily: "anthropic"));

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

    /// <summary>
    /// §2.12.3 — the reduction partitions ballots into panel and arbiter by <c>JudgeId</c> equality
    /// alone, so an arbiter sharing a panel judge's id makes that judge's ballot read as the
    /// arbiter's. The panel then has one ballot left, which can never straddle: a genuine 9-vs-3
    /// disagreement records as a consensus and the straddle rate — the headline diagnostic this
    /// slice exists to produce — reads silently low.
    /// </summary>
    [Fact]
    public void An_arbiter_sharing_a_panel_judge_id_throws_at_construction()
    {
        var construct = () =>
            Gauntlet(
                [],
                [new FakeJudge("a", "anthropic"), new FakeJudge("b", "openai")],
                new HarnessOptions { ArbiterJudge = new FakeJudge("a", "google") }
            );

        construct.Should().Throw<ArgumentException>().WithMessage("*is also a panel judge id*");
    }

    /// <summary>
    /// The same partitioning reads a judge id as an identity, so two panel judges sharing one make
    /// every reliability weight, every exclusion record and every arbiter test ambiguous.
    /// </summary>
    [Fact]
    public void Two_panel_judges_sharing_a_judge_id_throw_at_construction()
    {
        var construct = () => Gauntlet([], [new FakeJudge("a", "anthropic"), new FakeJudge("a", "openai")]);

        construct.Should().Throw<ArgumentException>().WithMessage("*judge id*");
    }

    /// <summary>
    /// §2.12.6 — the two <see cref="PanelDegradation.None"/> arms are "no arbiter configured" and
    /// "arbiter in the generator's own family", told apart post-hoc from the arbiter's family. An
    /// arbiter that RAN and declined to decide satisfies neither, so recording it as None makes an
    /// escalation that happened and failed indistinguishable from one that was never attempted.
    /// </summary>
    [Fact]
    public async Task An_arbiter_that_abstains_is_ArbiterUnavailable_rather_than_a_chosen_non_escalation()
    {
        var arbiter = new FakeJudge("arb", "google", score: 3.0, abstained: true);
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 3.0);

        var verdict = await Run(
            Gauntlet([], [first, second], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate()
        );

        arbiter.Calls.Should().Be(1, "we tried");
        verdict.Outcome.Should().Be(VerdictOutcome.Split);
        verdict.Degradation.Should().Be(PanelDegradation.ArbiterUnavailable);
        verdict.DegradationReason.Should().Contain("abstained");
    }

    /// <summary>
    /// Abstention and below-floor confidence are two separate exclusion channels, and an arbiter
    /// whose ballot lands on the second one is just as absent from the tally as one that abstained.
    /// </summary>
    [Fact]
    public async Task An_arbiter_below_the_abstain_floor_is_ArbiterUnavailable()
    {
        var arbiter = new FakeJudge("arb", "google", score: 3.0, confidence: 0.1);
        var first = new FakeJudge("a", "anthropic", score: 9.0);
        var second = new FakeJudge("b", "openai", score: 3.0);

        var verdict = await Run(
            Gauntlet([], [first, second], new HarnessOptions { ArbiterJudge = arbiter }),
            HarnessFixtures.Candidate()
        );

        verdict.Degradation.Should().Be(PanelDegradation.ArbiterUnavailable);
        verdict.DegradationReason.Should().Contain("confidence-below-floor");
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
            .Be(PanelDegradation.None, "we chose not to escalate — that is not the same as trying and failing");
    }

    /// <summary>
    /// §2.12.3 rule 1 and §2.12.6 — escalation resolves a disagreement between two judges that both
    /// ran. A Split over a DEGRADED panel is not that: one voice is missing, so the arbiter would be
    /// breaking a tie that was never fully cast. The reducer is an injected seam, so a host reducer
    /// can hand the gauntlet this state even though WeightedMeanAggregator never produces it — which
    /// is what makes the guard reachable, and testable.
    /// </summary>
    [Fact]
    public async Task A_split_over_a_degraded_panel_does_not_escalate()
    {
        var arbiter = new FakeJudge("arb", "google", score: 3.0);
        var only = new FakeJudge("a", "anthropic", score: 9.0);

        var verdict = await Run(
            Gauntlet(
                [],
                [only],
                new HarnessOptions { ArbiterJudge = arbiter },
                new StubAggregator(VerdictOutcome.Split, PanelDegradation.SingleJudge)
            ),
            HarnessFixtures.Candidate()
        );

        arbiter.Calls.Should().Be(0, "a degraded panel's disagreement is not the arbiter's to resolve");
        verdict.Outcome.Should().Be(VerdictOutcome.Split);
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

    /// <summary>
    /// The same rule, but reaching the guard that actually implements it. The test above cancels
    /// BEFORE the run starts, so it is settled by RunAsync's entry check and never enters a judge.
    /// This one cancels INSIDE the judge, which is the only path through
    /// <c>InvokeAsync</c>'s cancellation catch — without it, that catch could be deleted and every
    /// test would stay green while a caller's cancellation silently became a PanelUnavailable
    /// verdict.
    /// </summary>
    [Fact]
    public async Task A_cancellation_raised_inside_a_judge_propagates_rather_than_becoming_a_fault()
    {
        using var cts = new CancellationTokenSource();
        var judge = new RubricJudge(
            new RubricJudgeOptions
            {
                JudgeId = "a",
                ModelId = "anthropic/model",
                ModelFamily = "anthropic",
            },
            (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("unreachable");
            }
        );
        var gauntlet = Gauntlet([], [judge]);

        var run = () => gauntlet.RunAsync(HarnessFixtures.Candidate(), Rubric, NoReliability, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- rubric/candidate agreement ----------------------------------------------------------

    [Fact]
    public async Task A_rubric_for_a_different_task_type_is_refused()
    {
        var gauntlet = Gauntlet([], [new FakeJudge("a", "anthropic")]);
        var candidate = HarnessFixtures.Candidate() with { TaskType = "summarization" };

        var run = () => gauntlet.RunAsync(candidate, Rubric, NoReliability, CancellationToken.None);

        await run.Should().ThrowAsync<ArgumentException>().WithMessage("*task type*");
    }
}
