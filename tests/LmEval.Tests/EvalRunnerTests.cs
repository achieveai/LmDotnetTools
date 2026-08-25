using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Running;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The runner's contract is almost entirely about the denominator: which items count, which are
/// excluded from a numerator, and which are nonetheless still counted below the line.
/// </summary>
public class EvalRunnerTests
{
    private static ScoringJudge Judge(
        string id,
        string family,
        Func<Candidate, double?> score,
        Func<Candidate, Exception?>? fault = null
    ) => new(id, family, score, fault: fault);

    /// <summary>A panel of two disjoint families whose scores are read from per-item scripts.</summary>
    private static EvaluatorConfig Panel(
        Func<Candidate, double?> first,
        Func<Candidate, double?> second,
        IJudge? arbiter = null,
        IReadOnlyList<IGate>? gates = null,
        IReadOnlyDictionary<string, double>? reliabilityWeights = null
    ) =>
        EvalFixtures.Config(
            gates,
            [Judge("j-a", "anthropic", first), Judge("j-b", "google", second)],
            new HarnessOptions { ArbiterJudge = arbiter },
            reliabilityWeights: reliabilityWeights
        );

    [Fact]
    public async Task Every_rate_is_over_the_corpus_size_not_the_processed_count()
    {
        // Four items: two pass, one is gate-rejected, one abstains into a NoDecision. Only two of
        // the four yield a score, and PassRate must still be 2/4 — not 2/2. Declining to score a
        // hard item lowers the pass rate rather than flattering it.
        var snapshot = EvalFixtures.Snapshot(
            EvalFixtures.Item("pass-1"),
            EvalFixtures.Item("pass-2"),
            EvalFixtures.Item("gated", content: EvalFixtures.RejectMarker),
            EvalFixtures.Item("abstained")
        );

        static double? Score(Candidate c) => c.CandidateId == "abstained" ? null : 8.0;

        var config = EvalFixtures.Config(
            [new MarkerGate(EvalFixtures.RejectMarker)],
            [Judge("j-a", "anthropic", Score), Judge("j-b", "google", Score)]
        );

        var run = await EvalFixtures.RunAsync(config, snapshot);

        run.CorpusSize.Should().Be(4);
        run.ScoredItems.Should().Be(2);
        run.Coverage.Should().Be(0.5);
        run.PassRate.Should().Be(0.5);
        run.GateRejectedCount.Should().Be(1);
        run.NoDecisionCount.Should().Be(1);
    }

    [Fact]
    public async Task A_gate_rejection_is_a_fail_with_no_score_and_stays_in_the_denominator()
    {
        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config([new MarkerGate(EvalFixtures.RejectMarker)]),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("ok"),
                EvalFixtures.Item("gated", content: EvalFixtures.RejectMarker)
            )
        );

        var gated = run.Items.Single(i => i.CandidateId == "gated");
        gated.Verdict!.Outcome.Should().Be(VerdictOutcome.Fail);
        gated.Verdict.Score.Should().BeNull();
        gated.Exclusion.Should().Be(ScoreExclusion.GateRejected);
        gated.IsScored.Should().BeFalse();

        run.CorpusSize.Should().Be(2);
        run.PassRate.Should().Be(0.5);
    }

    [Fact]
    public async Task A_straddle_is_counted_even_when_the_arbiter_resolved_it()
    {
        // The straddle rate measures disagreement, not outcome. This item ends up a Pass because
        // the arbiter said so, and it is still a straddle.
        var run = await EvalFixtures.RunAsync(
            Panel(
                first: _ => 9.0,
                second: _ => 2.0,
                arbiter: Judge("arb", "openai", _ => 8.0)
            ),
            EvalFixtures.Snapshot(EvalFixtures.Item("disputed"))
        );

        var item = run.Items.Single();
        item.Verdict!.Outcome.Should().Be(VerdictOutcome.Pass);
        TieBreakRules.IsArbiterResolved(item.Verdict.TieBreakRule).Should().BeTrue();

        run.StraddleCount.Should().Be(1);
        run.StraddleRate.Should().Be(1.0);
        run.ArbiterResolvedStraddles.Should().Be(1);
        run.UnresolvedStraddles.Should().Be(0);
    }

    [Fact]
    public async Task An_unresolved_straddle_is_a_split_that_scores_nothing()
    {
        var run = await EvalFixtures.RunAsync(
            Panel(first: _ => 9.0, second: _ => 2.0),
            EvalFixtures.Snapshot(EvalFixtures.Item("disputed"), EvalFixtures.Item("agreed"))
        );

        var disputed = run.Items.Single(i => i.CandidateId == "disputed");
        disputed.Verdict!.Outcome.Should().Be(VerdictOutcome.Split);
        disputed.Exclusion.Should().Be(ScoreExclusion.Straddled);
        disputed.IsScored.Should().BeFalse();

        run.StraddleCount.Should().Be(2);
        run.UnresolvedStraddles.Should().Be(2);
        run.ArbiterResolvedStraddles.Should().Be(0);
        run.ScoredItems.Should().Be(0);
        run.MeanScore.Should().BeNull();
    }

    [Fact]
    public async Task Two_judges_agreeing_on_the_side_is_a_consensus_and_scores()
    {
        var run = await EvalFixtures.RunAsync(
            Panel(first: _ => 9.0, second: _ => 7.0),
            EvalFixtures.Snapshot(EvalFixtures.Item("agreed"))
        );

        var item = run.Items.Single();
        item.Verdict!.TieBreakRule.Should().Be(TieBreakRules.Consensus);
        item.IsScored.Should().BeTrue();
        run.StraddleCount.Should().Be(0);
        run.MeanScore.Should().Be(8.0);
    }

    [Fact]
    public async Task A_degraded_row_is_out_of_the_numerator_and_still_in_the_denominator()
    {
        // One judge faults, so the verdict is real but single-judge. It is reported and it is not
        // pooled with full-panel rows — and it does not shrink the denominator.
        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config(
                [],
                [
                    Judge("j-a", "anthropic", _ => 9.0),
                    Judge(
                        "j-b",
                        "google",
                        _ => 9.0,
                        fault: c => c.CandidateId == "degraded"
                            ? new InvalidOperationException("provider down")
                            : null
                    ),
                ]
            ),
            EvalFixtures.Snapshot(EvalFixtures.Item("clean"), EvalFixtures.Item("degraded"))
        );

        var degraded = run.Items.Single(i => i.CandidateId == "degraded");
        degraded.Verdict!.Degradation.Should().Be(PanelDegradation.SingleJudge);
        degraded.Exclusion.Should().Be(ScoreExclusion.Degraded);

        run.DegradedCount.Should().Be(1);
        run.CorpusSize.Should().Be(2);
        run.ScoredItems.Should().Be(1);
        run.PassRate.Should().Be(0.5);
    }

    [Fact]
    public async Task An_item_with_no_generator_family_is_segmented_out_never_treated_as_a_match()
    {
        // A null generator family means the exclusion filter never ran on it. That is unknown, not
        // "not the judge's family", so its score is not pooled with rows that were checked.
        var run = await EvalFixtures.RunAsync(
            Panel(first: _ => 9.0, second: _ => 8.0),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("checked"),
                EvalFixtures.Item("unchecked", generatorFamily: null)
            )
        );

        var unchecked_ = run.Items.Single(i => i.CandidateId == "unchecked");
        unchecked_.Verdict!.Outcome.Should().Be(VerdictOutcome.Pass);
        unchecked_.Exclusion.Should().Be(ScoreExclusion.UnknownGeneratorFamily);

        run.UnknownGeneratorFamilyCount.Should().Be(1);
        run.CorpusSize.Should().Be(2);
        run.ScoredItems.Should().Be(1);
        run.PassRate.Should().Be(0.5);
    }

    [Fact]
    public async Task One_throwing_reducer_does_not_take_out_the_batch()
    {
        // A corpus is host data of unknown quality. Losing every item's work to one item's fault is
        // an operational failure, not a measurement — and the faulted item keeps its place in the
        // denominator, so the loss cannot flatter the result.
        //
        // The reducer, not a gate: the gauntlet now contains a gate fault into an inconclusive
        // decision the same way it already contained a judge fault, so neither of those reaches
        // this isolation any more. The injected reducer is the remaining seam that genuinely can.
        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config(
                aggregator: new ThrowingAggregator("poison", new WeightedMeanAggregator())
            ),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("before"),
                EvalFixtures.Item("poison"),
                EvalFixtures.Item("after")
            )
        );

        run.Items.Should().HaveCount(3);

        var poison = run.Items.Single(i => i.CandidateId == "poison");
        poison.Verdict.Should().BeNull();
        poison.FaultReason.Should().Be(nameof(InvalidOperationException));
        poison.Exclusion.Should().Be(ScoreExclusion.Faulted);

        run.FaultedCount.Should().Be(1);
        run.CorpusSize.Should().Be(3);
        run.ScoredItems.Should().Be(2);
        run.PassRate.Should().BeApproximately(2.0 / 3.0, 1e-9);

        // The items after the poison one were still evaluated: isolation, not a short-circuit.
        run.Items.Single(i => i.CandidateId == "after").IsScored.Should().BeTrue();
    }

    /// <summary>
    /// A gate that throws costs the run one gate DECISION, not one corpus item. Before the gauntlet
    /// contained the fault, the candidate was lost entirely — it left the pass rate's numerator,
    /// stayed in its denominator, and read as an item nothing could be measured about.
    /// </summary>
    [Fact]
    public async Task One_throwing_gate_costs_a_gate_decision_and_not_the_item()
    {
        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config(
                [new MarkerGate(EvalFixtures.RejectMarker, throwOnCandidateId: "poison")]
            ),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("before"),
                EvalFixtures.Item("poison"),
                EvalFixtures.Item("after")
            )
        );

        var poison = run.Items.Single(i => i.CandidateId == "poison");
        poison.Exclusion.Should().Be(ScoreExclusion.None);
        poison.IsScored.Should().BeTrue();
        poison
            .Verdict!.GateDecisions.Should()
            .ContainSingle()
            .Which.Outcome.Should()
            .Be(GateOutcome.Inconclusive);

        run.FaultedCount.Should().Be(0);
        run.ScoredItems.Should().Be(3);
    }

    [Fact]
    public async Task P10_is_the_nearest_rank_over_scored_items_only()
    {
        var scores = new Dictionary<string, double>
        {
            ["i1"] = 10.0,
            ["i2"] = 9.0,
            ["i3"] = 8.0,
            ["i4"] = 7.0,
            ["i5"] = 6.0,
            ["i6"] = 10.0,
            ["i7"] = 9.0,
            ["i8"] = 8.0,
            ["i9"] = 7.0,
            ["i10"] = 6.0,
        };

        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config(
                [],
                [
                    Judge("j-a", "anthropic", c => scores[c.CandidateId]),
                    Judge("j-b", "google", c => scores[c.CandidateId]),
                ]
            ),
            EvalFixtures.Snapshot([.. scores.Keys.Select(k => EvalFixtures.Item(k))])
        );

        run.ScoredItems.Should().Be(10);

        // ceil(0.10 * 10) - 1 = 0, so the lowest scored value.
        run.P10Score.Should().Be(6.0);
        run.MeanScore.Should().Be(8.0);
    }

    [Fact]
    public async Task Cost_is_read_from_the_host_and_totalled_over_the_corpus()
    {
        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config(),
            EvalFixtures.Snapshot(EvalFixtures.Item("a"), EvalFixtures.Item("b")),
            costSource: (candidate, _) =>
                ValueTask.FromResult<long?>(candidate.CandidateId == "a" ? 1000L : 3000L)
        );

        run.TotalCostMicros.Should().Be(4000);
        run.MeanCostMicros.Should().Be(2000);
    }

    [Fact]
    public async Task Cost_is_null_rather_than_zero_when_the_host_supplies_no_source()
    {
        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config(),
            EvalFixtures.Snapshot(EvalFixtures.Item("a"))
        );

        run.Items.Single().CostMicros.Should().BeNull();
    }

    [Fact]
    public async Task The_run_records_the_hashes_of_what_it_actually_ran()
    {
        var config = EvalFixtures.Config();
        var snapshot = EvalFixtures.Snapshot(EvalFixtures.Item("a"));

        var run = await EvalFixtures.RunAsync(config, snapshot);

        run.CorpusSnapshotHash.Should().Be(snapshot.SnapshotHash);
        run.EvaluatorConfigHash.Should().Be(config.Hash);
        run.RubricVersion.Should().Be("1.0");
        run.TaskType.Should().Be(HarnessFixtures.TaskType);
    }

    [Fact]
    public async Task A_rubric_for_another_task_type_is_refused()
    {
        var runner = new EvalRunner(EvalFixtures.Config());

        var act = async () =>
            await runner.RunAsync(
                "run-1",
                EvalFixtures.Snapshot(EvalFixtures.Item("a")),
                HarnessFixtures.Rubric() with
                {
                    TaskType = "summarization",
                },
                new Dictionary<string, double>(),
                null,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*task type*");
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_being_recorded_as_a_faulted_item()
    {
        // Recording a caller's cancellation as a faulted item would put a hole in a run nobody
        // tried to complete, and the aggregates over that run would be read as real.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var runner = new EvalRunner(EvalFixtures.Config());

        var act = async () =>
            await runner.RunAsync(
                "run-1",
                EvalFixtures.Snapshot(EvalFixtures.Item("a")),
                HarnessFixtures.Rubric(),
                new Dictionary<string, double>(),
                null,
                cts.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Six items whose scored content is fixed, so a replay is deterministic by construction.</summary>
    private static Corpus.CorpusSnapshot FixtureCorpus() =>
        EvalFixtures.Snapshot(
            [.. Enumerable.Range(0, 6).Select(i => EvalFixtures.Item($"c{i}"))]
        );

    [Fact]
    public async Task A_replay_over_the_fixture_corpus_reproduces_its_baseline_exactly()
    {
        // The end-to-end determinism claim, and the one the whole pillar rests on: if a replay over
        // identical inputs can move at all, every regression this system reports is unfalsifiable.
        // No provider is contacted — the panel is scripted, which is stronger than replaying a
        // recorded transcript because there is no transport in the picture to be flaky.
        static double? Score(Candidate c) => c.CandidateId is "c0" or "c1" ? 4.0 : 8.0;

        var corpus = FixtureCorpus();
        var first = await EvalFixtures.RunAsync(Panel(Score, Score), corpus);
        var second = await EvalFixtures.RunAsync(Panel(Score, Score), corpus);

        second.CorpusSnapshotHash.Should().Be(first.CorpusSnapshotHash);
        second.EvaluatorConfigHash.Should().Be(first.EvaluatorConfigHash);
        second.PassRate.Should().Be(first.PassRate);
        second.MeanScore.Should().Be(first.MeanScore);
        second.P10Score.Should().Be(first.P10Score);

        var comparison = BaselineComparer.Compare(
            second,
            EvalBaseline.From("base-1", first, minCoverage: 0.9)
        );

        comparison.IsRefused.Should().BeFalse();
        comparison.IsRegression.Should().BeFalse();
        comparison.PassRateDelta.Should().Be(0.0);
        comparison.MeanScoreDelta.Should().Be(0.0);
        comparison.P10ScoreDelta.Should().Be(0.0);
    }

    [Fact]
    public async Task Swapping_only_the_judge_model_refuses_the_comparison_rather_than_scoring_it()
    {
        // Same corpus, same rubric, byte-identical candidate output, one judge model swapped. The
        // scores here happen to be unchanged too, which is the point: the refusal is not a
        // reaction to a moved number, it is a refusal to interpret ANY number across a moved
        // evaluator — because the run where the number does move looks exactly like this one.
        static double? Score(Candidate c) => 8.0;

        var corpus = FixtureCorpus();
        var before = await EvalFixtures.RunAsync(
            EvalFixtures.Config(
                judges:
                [
                    new ScoringJudge("j-a", "anthropic", Score, modelId: "anthropic/m1"),
                    new ScoringJudge("j-b", "google", Score),
                ]
            ),
            corpus
        );

        var after = await EvalFixtures.RunAsync(
            EvalFixtures.Config(
                judges:
                [
                    new ScoringJudge("j-a", "anthropic", Score, modelId: "anthropic/m2"),
                    new ScoringJudge("j-b", "google", Score),
                ]
            ),
            corpus
        );

        after.PassRate.Should().Be(before.PassRate);

        var comparison = BaselineComparer.Compare(
            after,
            EvalBaseline.From("base-1", before, minCoverage: 0.9)
        );

        comparison.Refusal.Should().Be(ComparisonRefusal.EvaluatorConfigDiffers);
        comparison.IsRegression.Should().BeFalse();
        comparison.PassRateDelta.Should().BeNull();
    }

    [Fact]
    public async Task Moving_only_a_gate_bound_refuses_the_comparison_rather_than_scoring_it()
    {
        // Gates are in the evaluator hash because a gate-rejected item stays in the pass rate's
        // denominator and never enters its numerator, so retuning one bound moves the reported
        // rate with nothing about the candidate having changed. Both bounds here admit every item,
        // which isolates the hash from the outcome.
        static double? Score(Candidate c) => 8.0;

        var corpus = FixtureCorpus();
        async Task<EvalRun> RunUnder(int maximumLength) =>
            await EvalFixtures.RunAsync(
                EvalFixtures.Config(
                    [new Gates.LengthBoundsGate(minimumLength: 1, maximumLength: maximumLength)],
                    [
                        new ScoringJudge("j-a", "anthropic", Score),
                        new ScoringJudge("j-b", "google", Score),
                    ]
                ),
                corpus
            );

        var tight = await RunUnder(1000);
        var loose = await RunUnder(2000);

        loose.PassRate.Should().Be(tight.PassRate);
        loose.GateRejectedCount.Should().Be(0);

        BaselineComparer
            .Compare(loose, EvalBaseline.From("base-1", tight, minCoverage: 0.9))
            .Refusal.Should()
            .Be(ComparisonRefusal.EvaluatorConfigDiffers);
    }

    [Fact]
    public async Task A_cancellation_raised_inside_the_gauntlet_is_not_recorded_as_a_fault()
    {
        // The test above cancels BEFORE the run starts, so the loop's own pre-item check throws and
        // the runner's cancellation filter is never reached — it would pass with that filter
        // inverted or deleted. This one raises the cancellation from inside a gate, which is the
        // only path that actually exercises it, and the distinction matters: a swallowed
        // cancellation becomes a Faulted row in a run that then reports aggregates as if it had
        // completed.
        using var cts = new CancellationTokenSource();

        var runner = new EvalRunner(EvalFixtures.Config([new CancellingGate(cts)]));

        var act = async () =>
            await runner.RunAsync(
                "run-1",
                EvalFixtures.Snapshot(EvalFixtures.Item("a")),
                HarnessFixtures.Rubric(),
                new Dictionary<string, double>(),
                null,
                cts.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_valid_non_uniform_reliability_weighting_reaches_the_reduction()
    {
        // Every other runner test passes an EMPTY reliability map, so the whole parameter travels
        // this class at its 1.0 default and nothing proves the runner hands it to the reduction at
        // all. Here the two judges disagree and the weights decide the answer: an unweighted mean
        // of 10 and 2 is 6.0, and the weighted one is (10*1.0 + 2*0.25) / 1.25 = 8.4.
        var weights = new Dictionary<string, double> { ["j-a"] = 1.0, ["j-b"] = 0.25 };
        var runner = new EvalRunner(
            Panel(first: _ => 10.0, second: _ => 2.0, reliabilityWeights: weights)
        );

        var run = await runner.RunAsync(
            "run-1",
            EvalFixtures.Snapshot(EvalFixtures.Item("a")),
            HarnessFixtures.Rubric(),
            weights,
            null,
            CancellationToken.None
        );

        run.Items.Single().Verdict!.Score.Should().BeApproximately(8.4, 1e-9);
    }

    /// <summary>
    /// The evaluator hash covers the weights by content, which is worth something only if the run
    /// cannot then execute under a different set. A hash describing a configuration other than the
    /// one executing is worse than no hash: every refusal built on it checks the wrong fact.
    /// </summary>
    [Fact]
    public async Task Weights_the_frozen_configuration_did_not_declare_are_refused()
    {
        var judge = new ScoringJudge("j-a", "anthropic", _ => 8.0);
        var runner = new EvalRunner(
            EvalFixtures.Config(
                judges: [judge, Judge("j-b", "google", _ => 8.0)],
                reliabilityWeights: new Dictionary<string, double> { ["j-a"] = 1.0 }
            )
        );

        var act = async () =>
            await runner.RunAsync(
                "run-1",
                EvalFixtures.Snapshot(EvalFixtures.Item("a"), EvalFixtures.Item("b")),
                HarnessFixtures.Rubric(),
                new Dictionary<string, double> { ["j-a"] = 0.25 },
                null,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*froze*");
        judge.SeenCandidateIds.Should().BeEmpty("no judge is billed on the way to finding out");
    }

    [Fact]
    public async Task An_out_of_range_reliability_weight_is_refused_rather_than_faulting_every_item()
    {
        // The reliability snapshot is a property of the RUN, not of any one corpus item, so a
        // misfitted weight is a caller error and has to surface as one. Left to the gauntlet's own
        // check it threw on item 1, the runner's per-item catch recorded that as a Faulted corpus
        // item, and the loop repeated it for every remaining item -- the run then returned normally
        // with nothing scored, and the operator was told their corpus was unscoreable when in fact
        // their configuration had been rejected.
        var judge = new ScoringJudge("j-a", "anthropic", _ => 8.0);
        var runner = new EvalRunner(
            EvalFixtures.Config(judges: [judge, Judge("j-b", "google", _ => 8.0)])
        );

        var act = async () =>
            await runner.RunAsync(
                "run-1",
                EvalFixtures.Snapshot(
                    EvalFixtures.Item("a"),
                    EvalFixtures.Item("b"),
                    EvalFixtures.Item("c")
                ),
                HarnessFixtures.Rubric(),
                new Dictionary<string, double> { ["j-a"] = 1.4 },
                null,
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("reliability");

        // And no judge was billed for the corpus on the way to finding out.
        judge.SeenCandidateIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_NaN_reliability_weight_is_refused_rather_than_faulting_every_item()
    {
        // NaN is the second arm of the same predicate and fails differently in the reduction: it
        // poisons the weighted mean instead of pushing it off the rubric's scale, so it gets its
        // own case rather than riding on the out-of-range one.
        var judge = new ScoringJudge("j-a", "anthropic", _ => 8.0);
        var runner = new EvalRunner(
            EvalFixtures.Config(judges: [judge, Judge("j-b", "google", _ => 8.0)])
        );

        var act = async () =>
            await runner.RunAsync(
                "run-1",
                EvalFixtures.Snapshot(EvalFixtures.Item("a"), EvalFixtures.Item("b")),
                HarnessFixtures.Rubric(),
                new Dictionary<string, double> { ["j-a"] = double.NaN },
                null,
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("reliability");

        judge.SeenCandidateIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Mean_cost_is_per_corpus_item_so_declining_to_score_cannot_look_cheap()
    {
        // Four items at 100 each, one of which abstains into a NoDecision. Over the corpus that is
        // 400/4 = 100; over the scored subset it would be 400/3 = 133. Every other test in this
        // class scores every item it prices, so the two denominators are numerically equal there
        // and the division could be swapped with nothing going red.
        static double? Score(Candidate c) => c.CandidateId == "abstained" ? null : 8.0;

        var run = await EvalFixtures.RunAsync(
            Panel(Score, Score),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("a"),
                EvalFixtures.Item("b"),
                EvalFixtures.Item("c"),
                EvalFixtures.Item("abstained")
            ),
            costSource: (_, _) => ValueTask.FromResult<long?>(100L)
        );

        run.ScoredItems.Should().Be(3);
        run.TotalCostMicros.Should().Be(400L);
        run.MeanCostMicros.Should().Be(100L);
    }

    [Fact]
    public async Task The_straddle_rate_is_over_the_corpus_size_not_the_scored_subset()
    {
        // The headline judge-reliability diagnostic, and the one the arbiter-id refusal exists to
        // keep honest. Four items: two agree and score, one straddles, one abstains. Over the
        // corpus the rate is 1/4 = 0.25; over the scored subset it would be 1/2 = 0.5. A straddle
        // is itself excluded from the scored set, so the two denominators can never agree on a
        // corpus that has one -- yet every other test here reads StraddleCount, never the rate.
        static double? First(Candidate c) =>
            c.CandidateId switch
            {
                "straddled" => 9.0,
                "abstained" => null,
                _ => 8.0,
            };

        static double? Second(Candidate c) =>
            c.CandidateId switch
            {
                "straddled" => 3.0,
                "abstained" => null,
                _ => 8.0,
            };

        var run = await EvalFixtures.RunAsync(
            Panel(First, Second),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("a"),
                EvalFixtures.Item("b"),
                EvalFixtures.Item("straddled"),
                EvalFixtures.Item("abstained")
            )
        );

        run.CorpusSize.Should().Be(4);
        run.ScoredItems.Should().Be(2);
        run.StraddleCount.Should().Be(1);
        run.StraddleRate.Should().Be(0.25);
    }

    [Fact]
    public async Task A_run_that_scored_nothing_is_refused_even_when_the_baseline_sets_no_floor()
    {
        // A baseline with MinCoverage 0 imposes no floor, so a run that scored nothing walks past
        // the coverage check. The refusal that catches it next is the only thing standing between
        // Compare and dereferencing a null MeanScore -- a crash path, not just an unpinned number,
        // and no other test in the suite reaches it.
        var corpus = EvalFixtures.Snapshot(EvalFixtures.Item("a"), EvalFixtures.Item("b"));

        // Same evaluator configuration on both sides -- only the scripted scores differ, which the
        // hash cannot and must not see -- so the comparison is genuinely comparable.
        var baselineRun = await EvalFixtures.RunAsync(Panel(_ => 8.0, _ => 8.0), corpus);
        var emptyRun = await EvalFixtures.RunAsync(Panel(_ => null, _ => null), corpus);

        emptyRun.EvaluatorConfigHash.Should().Be(baselineRun.EvaluatorConfigHash);
        emptyRun.ScoredItems.Should().Be(0);
        emptyRun.MeanScore.Should().BeNull();

        var comparison = BaselineComparer.Compare(
            emptyRun,
            EvalBaseline.From("base-1", baselineRun, minCoverage: 0.0)
        );

        comparison.Refusal.Should().Be(ComparisonRefusal.CoverageBelowMinimum);
        comparison.RefusalDetail.Should().Contain("scored no items at all");
        comparison.PassRateDelta.Should().BeNull();
    }

    // ---- gate scoping (IGate.AppliesTo) -------------------------------------------------------

    /// <summary>
    /// <see cref="IGate.AppliesTo"/> decides whether a gate participates at all, and a gate that
    /// runs short-circuits to a fail with no score that stays in the pass rate's denominator.
    /// Silently not running is therefore a large, quiet move in the reported rate — and every gate
    /// fixture in this assembly declared the set empty, so nothing anywhere exercised it.
    /// </summary>
    [Fact]
    public async Task A_gate_scoped_to_another_task_type_sees_no_candidate_and_moves_no_rate()
    {
        var elsewhere = new MarkerGate(
            EvalFixtures.RejectMarker,
            gateId: "scoped",
            appliesTo: ["summarization"]
        );

        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config([elsewhere]),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("clean"),
                EvalFixtures.Item("dirty", content: EvalFixtures.RejectMarker)
            )
        );

        run.Items.Should().OnlyContain(i => i.Verdict!.GateDecisions.Count == 0);
        run.GateRejectedCount.Should().Be(0, "a gate scoped elsewhere cannot reject anything here");
        run.ScoredItems.Should().Be(2);
        run.PassRate.Should().Be(1.0);
    }

    /// <summary>
    /// The other half: scoped to the corpus's OWN task type, the same gate runs against every
    /// candidate and the rejection lands. Read against the test above, the pair is what makes the
    /// scoping field behaviourally pinned rather than merely present.
    /// </summary>
    [Fact]
    public async Task A_gate_scoped_to_the_corpus_task_type_sees_every_candidate()
    {
        var here = new MarkerGate(
            EvalFixtures.RejectMarker,
            gateId: "scoped",
            appliesTo: [HarnessFixtures.TaskType]
        );

        var run = await EvalFixtures.RunAsync(
            EvalFixtures.Config([here]),
            EvalFixtures.Snapshot(
                EvalFixtures.Item("clean"),
                EvalFixtures.Item("dirty", content: EvalFixtures.RejectMarker)
            )
        );

        run.Items.Should().OnlyContain(i => i.Verdict!.GateDecisions.Count == 1);
        run.GateRejectedCount.Should().Be(1);
        run.Items.Single(i => i.CandidateId == "dirty")
            .Exclusion.Should()
            .Be(ScoreExclusion.GateRejected);
        run.ScoredItems.Should().Be(1);
        run.PassRate.Should().Be(0.5, "the rejected item leaves the numerator and stays in the denominator");
    }
}
