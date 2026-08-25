using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The default reduction (P6 spec §2.7). It is pure and synchronous: it never calls a judge, and
/// the arbiter's result-or-fault reaches it only as data the gauntlet already appended.
/// </summary>
public sealed class WeightedMeanAggregatorTests
{
    private static readonly WeightedMeanAggregator Aggregator = new();
    private static readonly Rubric Rubric = HarnessFixtures.Rubric();

    private static Ballot Ballot(
        string judgeId,
        string family,
        double score,
        double confidence = 0.9,
        bool abstained = false
    ) =>
        new()
        {
            JudgeId = judgeId,
            ModelId = $"{family}/model",
            ModelFamily = family,
            CriterionScores = new Dictionary<string, int> { ["quality"] = (int)Math.Round(score) },
            WeightedScore = score,
            Reasoning = $"{judgeId} says {score}",
            Confidence = confidence,
            Abstained = abstained,
        };

    private static AggregationContext Context(
        HarnessOptions? options = null,
        IReadOnlyDictionary<string, double>? reliability = null,
        params JudgeFault[] faults
    ) =>
        new()
        {
            Options = options ?? new HarnessOptions(),
            Reliability = reliability ?? new Dictionary<string, double>(),
            Faults = faults,
        };

    private static Verdict Aggregate(IReadOnlyList<Ballot> ballots, AggregationContext? context = null) =>
        Aggregator.Aggregate(
            HarnessFixtures.Candidate(),
            Rubric,
            [],
            ballots,
            context ?? Context()
        );

    /// <summary>
    /// §2.6 — an abstention is DISTINCT from a zero. If it were counted as zero, a two-judge panel
    /// where one abstains would average to 4.0 and read as a Fail; excluded, the surviving 8.0
    /// stands.
    /// </summary>
    [Fact]
    public void An_abstention_is_excluded_rather_than_counted_as_zero()
    {
        var verdict = Aggregate(
            [Ballot("a", "anthropic", 8.0), Ballot("b", "openai", 0.0, abstained: true)]
        );

        verdict.Score.Should().Be(8.0, "the abstention is not a zero dragging the mean down");
        verdict.Ballots.Should().ContainSingle().Which.JudgeId.Should().Be("a");
        verdict
            .ExcludedBallots.Should()
            .ContainSingle()
            .Which.ExclusionReason.Should()
            .Be("abstained");
        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public void A_ballot_below_the_abstain_floor_is_excluded_and_recorded()
    {
        var verdict = Aggregate(
            [Ballot("a", "anthropic", 8.0), Ballot("b", "openai", 2.0, confidence: 0.2)]
        );

        verdict.Score.Should().Be(8.0);
        verdict
            .ExcludedBallots.Should()
            .ContainSingle()
            .Which.ExclusionReason.Should()
            .Be("confidence-below-floor");
    }

    /// <summary>
    /// §2.7 step 2 — an unmeasured candidate and a bad candidate are different facts, so no
    /// surviving ballot is NoDecision, never Fail.
    /// </summary>
    [Fact]
    public void No_surviving_ballot_is_NoDecision_not_Fail()
    {
        var verdict = Aggregate(
            [
                Ballot("a", "anthropic", 0.0, abstained: true),
                Ballot("b", "openai", 0.0, abstained: true),
            ]
        );

        verdict.Outcome.Should().Be(VerdictOutcome.NoDecision);
        verdict.Score.Should().BeNull("there are no counted ballots, so there is no score");
        verdict.Dispersion.Should().BeNull();
        verdict.ExcludedBallots.Should().HaveCount(2);
    }

    [Fact]
    public void Every_judge_faulting_is_NoDecision_with_PanelUnavailable()
    {
        var verdict = Aggregate(
            [],
            Context(
                faults:
                [
                    new JudgeFault("a", "anthropic", "HttpRequestException"),
                    new JudgeFault("b", "openai", "HttpRequestException"),
                ]
            )
        );

        verdict.Outcome.Should().Be(VerdictOutcome.NoDecision);
        verdict.Degradation.Should().Be(PanelDegradation.PanelUnavailable);
        verdict.DegradationReason.Should().Contain("anthropic").And.Contain("openai");
    }

    /// <summary>
    /// §2.12.6 — a lone judge is not a panel in perfect agreement. The null is the whole claim:
    /// 0.0 would read as two judges agreeing exactly.
    /// </summary>
    [Fact]
    public void One_counted_ballot_is_SingleJudge_with_a_null_dispersion()
    {
        var verdict = Aggregate([Ballot("a", "anthropic", 8.0)]);

        verdict.Degradation.Should().Be(PanelDegradation.SingleJudge);
        verdict.Dispersion.Should().BeNull("null is not 0.0 — there is nothing to disperse over");
        verdict.Score.Should().Be(8.0);
        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
        verdict.TieBreakRule.Should().Be("single-judge");
    }

    [Fact]
    public void One_counted_ballot_below_the_threshold_fails()
    {
        var verdict = Aggregate([Ballot("a", "anthropic", 3.0)]);

        verdict.Outcome.Should().Be(VerdictOutcome.Fail);
        verdict.Degradation.Should().Be(PanelDegradation.SingleJudge);
    }

    /// <summary>§2.12.2 — same side of the threshold is agreement on the decision.</summary>
    [Fact]
    public void Two_scores_on_the_same_side_are_a_consensus_even_when_they_differ()
    {
        var verdict = Aggregate([Ballot("a", "anthropic", 9.0), Ballot("b", "openai", 6.0)]);

        verdict.Outcome.Should().Be(VerdictOutcome.Pass);
        verdict.TieBreakRule.Should().Be("consensus");
        verdict.Score.Should().Be(7.5);
        verdict.Degradation.Should().Be(PanelDegradation.None);
        verdict
            .Dispersion.Should()
            .BeApproximately(1.5, 1e-9, "9 and 6 agree on the decision but not on the quality");
    }

    [Fact]
    public void Two_scores_below_the_threshold_are_a_consensus_fail()
    {
        var verdict = Aggregate([Ballot("a", "anthropic", 2.0), Ballot("b", "openai", 5.0)]);

        verdict.Outcome.Should().Be(VerdictOutcome.Fail);
        verdict.TieBreakRule.Should().Be("consensus");
    }

    /// <summary>
    /// §2.12.3 rule 2 — a straddle with no arbiter ballot and no arbiter fault terminates as a
    /// Split. Not a pass, not a fail, and not a NoDecision.
    /// </summary>
    [Fact]
    public void A_straddle_with_no_arbiter_is_an_unresolved_split()
    {
        var verdict = Aggregate([Ballot("a", "anthropic", 9.0), Ballot("b", "openai", 3.0)]);

        verdict.Outcome.Should().Be(VerdictOutcome.Split);
        verdict.TieBreakRule.Should().Be("split:unresolved");
        verdict.Degradation.Should().Be(PanelDegradation.None);
    }

    /// <summary>
    /// §2.10 step 5 — the aggregator tells the second pass apart from data it already holds: a
    /// ballot from the configured arbiter's judge id is the deciding vote.
    /// </summary>
    [Fact]
    public void An_arbiter_ballot_decides_the_straddle_and_its_score_is_not_a_blend()
    {
        var arbiter = new FakeJudge("arb", "google", score: 4.0);
        var verdict = Aggregate(
            [
                Ballot("a", "anthropic", 9.0),
                Ballot("b", "openai", 3.0),
                Ballot("arb", "google", 4.0),
            ],
            Context(new HarnessOptions { ArbiterJudge = arbiter })
        );

        verdict.Outcome.Should().Be(VerdictOutcome.Fail, "the arbiter's side decides");
        verdict.Score.Should().Be(4.0, "the arbiter's score, not a blend of the three");
        verdict.TieBreakRule.Should().Be("arbiter:arb:google");
        verdict.Degradation.Should().Be(PanelDegradation.None);
    }

    /// <summary>
    /// §2.12.6 — "we tried and could not" is distinguishable from "we chose not to escalate", and
    /// the discriminator is the degradation, not the rule string.
    /// </summary>
    [Fact]
    public void An_arbiter_fault_yields_a_split_marked_ArbiterUnavailable()
    {
        var arbiter = new FakeJudge("arb", "google");
        var verdict = Aggregate(
            [Ballot("a", "anthropic", 9.0), Ballot("b", "openai", 3.0)],
            Context(
                new HarnessOptions { ArbiterJudge = arbiter },
                faults: new JudgeFault("arb", "google", "HttpRequestException")
            )
        );

        verdict.Outcome.Should().Be(VerdictOutcome.Split);
        verdict.Degradation.Should().Be(PanelDegradation.ArbiterUnavailable);
        verdict.DegradationReason.Should().Contain("google");
    }

    /// <summary>
    /// An arbiter fault is an arbiter fault, never a panel outage: the panel returned two ballots
    /// and they are still in the verdict.
    /// </summary>
    [Fact]
    public void An_arbiter_fault_is_not_a_panel_outage()
    {
        var arbiter = new FakeJudge("arb", "google");
        var verdict = Aggregate(
            [Ballot("a", "anthropic", 9.0), Ballot("b", "openai", 3.0)],
            Context(
                new HarnessOptions { ArbiterJudge = arbiter },
                faults: new JudgeFault("arb", "google", "HttpRequestException")
            )
        );

        verdict.Degradation.Should().NotBe(PanelDegradation.PanelUnavailable);
        verdict.Ballots.Should().HaveCount(2);
    }

    /// <summary>
    /// §2.9 — Score is the reliability-WEIGHTED mean, not the arithmetic one. An unweighted mean of
    /// 9 and 5 is 7.0; weighting the 9 at 3.0 moves it to 8.0.
    /// </summary>
    [Fact]
    public void The_score_is_weighted_by_judge_reliability()
    {
        var verdict = Aggregate(
            [Ballot("a", "anthropic", 9.0), Ballot("b", "openai", 5.0)],
            Context(reliability: new Dictionary<string, double> { ["a"] = 3.0, ["b"] = 1.0 })
        );

        verdict.Score.Should().Be(8.0, "9*3 + 5*1 over 4 is 8, not the unweighted 7");
    }

    [Fact]
    public void A_judge_absent_from_the_reliability_snapshot_weighs_one()
    {
        var verdict = Aggregate(
            [Ballot("a", "anthropic", 9.0), Ballot("b", "openai", 7.0)],
            Context(reliability: new Dictionary<string, double> { ["a"] = 1.0 })
        );

        verdict.Score.Should().Be(8.0);
    }

    /// <summary>
    /// §2.6 — the aggregator is the ONLY component that writes AppliedWeight, and the invariant is
    /// two-sided: non-null on every counted ballot, null on every excluded one.
    /// </summary>
    [Fact]
    public void Every_counted_ballot_carries_a_weight_and_every_excluded_one_carries_none()
    {
        var verdict = Aggregate(
            [
                Ballot("a", "anthropic", 9.0),
                Ballot("b", "openai", 7.0),
                Ballot("c", "openai", 1.0, abstained: true),
            ],
            Context(reliability: new Dictionary<string, double> { ["a"] = 0.5 })
        );

        verdict.Ballots.Should().OnlyContain(b => b.AppliedWeight != null);
        verdict.Ballots.Single(b => b.JudgeId == "a").AppliedWeight.Should().Be(0.5);
        verdict.Ballots.Single(b => b.JudgeId == "b").AppliedWeight.Should().Be(1.0);
        verdict.ExcludedBallots.Should().OnlyContain(e => e.Ballot.AppliedWeight == null);
    }

    /// <summary>
    /// A judge cannot know its own weight, so a value it invented must not survive into the record
    /// as though the aggregator had computed it.
    /// </summary>
    [Fact]
    public void An_excluded_ballot_that_arrived_with_a_weight_is_normalised_back_to_null()
    {
        var verdict = Aggregate(
            [
                Ballot("a", "anthropic", 9.0),
                Ballot("b", "openai", 1.0, abstained: true) with
                {
                    AppliedWeight = 42.0,
                },
            ]
        );

        verdict
            .ExcludedBallots.Should()
            .ContainSingle()
            .Which.Ballot.AppliedWeight.Should()
            .BeNull();
    }

    [Fact]
    public void The_rubric_identity_is_carried_onto_the_verdict()
    {
        var verdict = Aggregate([Ballot("a", "anthropic", 8.0)]);

        verdict.RubricId.Should().Be("test-rubric");
        verdict.RubricVersion.Should().Be("1.0");
        verdict.CandidateId.Should().Be("cand-1");
    }
}
