using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The harness's reply parse. This is where Revobot's <c>ParseVerdict</c>/<c>UnwrapJson</c> coverage
/// lands after the migration (P6 spec §4.2), so the legacy cases are pinned here VERBATIM: the
/// daemon's adapter maps an abstention back to score 0 with the ballot's reasoning, and these tests
/// are what make that mapping reproduce the old behaviour rather than approximate it.
/// </summary>
public sealed class JudgeReplyParserTests
{
    private static readonly Rubric SingleCriterion = HarnessFixtures.Rubric();

    private static Ballot Parse(string reply, Rubric? rubric = null) =>
        JudgeReplyParser.Parse(reply, rubric ?? SingleCriterion, judgeId: "j", modelId: "m", modelFamily: "f");

    // ---- the canonical harness shape ---------------------------------------------------------

    [Fact]
    public void A_canonical_reply_yields_per_criterion_scores_reasoning_and_confidence()
    {
        var rubric = HarnessFixtures.Rubric(HarnessFixtures.Criterion("evidence"), HarnessFixtures.Criterion("noise"));

        var ballot = Parse(
            """{"reasoning":"cites every line","scores":{"evidence":8,"noise":6},"confidence":0.7}""",
            rubric
        );

        ballot.Abstained.Should().BeFalse();
        ballot.CriterionScores.Should().Equal(new Dictionary<string, int> { ["evidence"] = 8, ["noise"] = 6 });
        ballot.WeightedScore.Should().Be(7.0);
        ballot.Reasoning.Should().Be("cites every line");
        ballot.Confidence.Should().Be(0.7);
    }

    /// <summary>
    /// §2.6 — the weighted score is normalised by TOTAL weight, which is what keeps it inside the
    /// rubric's own scale and directly comparable to the pass threshold.
    /// </summary>
    [Fact]
    public void The_weighted_score_is_normalised_by_total_criterion_weight()
    {
        var rubric = HarnessFixtures.Rubric(
            HarnessFixtures.Criterion("evidence", weight: 3.0),
            HarnessFixtures.Criterion("noise", weight: 1.0)
        );

        var ballot = Parse("""{"scores":{"evidence":8,"noise":4},"reasoning":"r"}""", rubric);

        ballot.WeightedScore.Should().Be(7.0, "(8*3 + 4*1) / 4 — not the unnormalised 28");
        ballot.WeightedScore.Should().BeLessThanOrEqualTo(rubric.MaxScore);
    }

    /// <summary>
    /// A criterion the judge did not score makes the ballot invalid — a schema violation, not a
    /// zero. Scoring it zero would put a partially-answered ballot into the tally as a real
    /// worst-case score.
    /// </summary>
    [Fact]
    public void A_missing_criterion_abstains_rather_than_scoring_zero()
    {
        var rubric = HarnessFixtures.Rubric(HarnessFixtures.Criterion("evidence"), HarnessFixtures.Criterion("noise"));

        var ballot = Parse("""{"scores":{"evidence":8},"reasoning":"partial"}""", rubric);

        ballot.Abstained.Should().BeTrue();
        ballot.AbstainReason.Should().Be("schema-violation");
        ballot.CriterionScores.Should().BeEmpty("a partial ballot contributes no scores at all");
    }

    [Fact]
    public void An_explicit_abstention_is_honoured()
    {
        var ballot = Parse("""{"abstain":true,"reasoning":"outside my competence"}""");

        ballot.Abstained.Should().BeTrue();
        ballot.AbstainReason.Should().Be("declined");
        ballot.Reasoning.Should().Be("outside my competence");
    }

    [Fact]
    public void A_missing_confidence_defaults_to_full_confidence()
    {
        Parse("""{"score":8,"rationale":"r"}""").Confidence.Should().Be(1.0);
    }

    [Fact]
    public void An_out_of_range_confidence_is_clamped_into_the_unit_interval()
    {
        Parse("""{"score":8,"rationale":"r","confidence":4.2}""").Confidence.Should().Be(1.0);
        Parse("""{"score":8,"rationale":"r","confidence":-1}""").Confidence.Should().Be(0.0);
    }

    // ---- the legacy shape, pinned verbatim (§4.2) ---------------------------------------------

    [Fact]
    public void A_flat_score_and_rationale_reply_parses_on_a_single_criterion_rubric()
    {
        var ballot = Parse("""{"score": 8, "rationale": "Thorough; caught the null deref."}""");

        ballot.Abstained.Should().BeFalse();
        ballot.WeightedScore.Should().Be(8.0);
        ballot.Reasoning.Should().Be("Thorough; caught the null deref.");
    }

    [Fact]
    public void A_fenced_json_reply_is_unwrapped()
    {
        var ballot = Parse("Here is my verdict:\n```json\n{\"score\": 5, \"rationale\": \"Adequate.\"}\n```");

        ballot.WeightedScore.Should().Be(5.0);
        ballot.Reasoning.Should().Be("Adequate.");
    }

    [Fact]
    public void Prose_around_a_bare_json_object_is_discarded()
    {
        var ballot = Parse("Sure! {\"score\": 7, \"rationale\": \"Fine.\"} Hope that helps.");

        ballot.WeightedScore.Should().Be(7.0);
        ballot.Reasoning.Should().Be("Fine.");
    }

    /// <summary>
    /// The legacy default the daemon adapter still has to reproduce: no JSON at all abstains, and
    /// the reasoning is the raw text trimmed — which is exactly the rationale v1 persisted.
    /// </summary>
    [Fact]
    public void A_reply_with_no_json_abstains_and_keeps_the_raw_text_as_its_reasoning()
    {
        var ballot = Parse("  I could not produce a structured verdict.  ");

        ballot.Abstained.Should().BeTrue();
        ballot.AbstainReason.Should().Be("unparseable");
        ballot.Reasoning.Should().Be("I could not produce a structured verdict.");
    }

    [Fact]
    public void A_malformed_json_span_abstains_and_keeps_the_raw_text_as_its_reasoning()
    {
        var ballot = Parse("{\"score\": }");

        ballot.Abstained.Should().BeTrue();
        ballot.AbstainReason.Should().Be("unparseable");
        ballot.Reasoning.Should().Be("{\"score\": }");
    }

    /// <summary>
    /// v1 returned <c>(0, "x")</c> here: score missing, rationale present. Under the harness that is
    /// an abstention whose reasoning is <c>"x"</c> — and the adapter's abstain mapping turns that
    /// back into the same <c>(0, "x")</c>. Preferring the raw text here would silently change the
    /// persisted rationale for every partially-structured reply.
    /// </summary>
    [Fact]
    public void A_reply_with_a_rationale_but_no_score_abstains_keeping_the_rationale()
    {
        var ballot = Parse("""{"rationale":"x"}""");

        ballot.Abstained.Should().BeTrue();
        ballot.Reasoning.Should().Be("x", "not the raw JSON — v1 persisted the rationale here");
    }

    /// <summary>v1 returned <c>(8, rawText)</c> here: score present, rationale missing.</summary>
    [Fact]
    public void A_reply_with_a_score_but_no_rationale_keeps_the_raw_text_as_its_reasoning()
    {
        var ballot = Parse("""{"score":8}""");

        ballot.Abstained.Should().BeFalse();
        ballot.WeightedScore.Should().Be(8.0);
        ballot.Reasoning.Should().Be("""{"score":8}""");
    }

    [Fact]
    public void A_non_numeric_score_abstains()
    {
        Parse("""{"score":"8","rationale":"r"}""").Abstained.Should().BeTrue();
    }

    [Fact]
    public void A_non_string_rationale_falls_back_to_the_raw_text()
    {
        var ballot = Parse("""{"score":8,"rationale":5}""");

        ballot.WeightedScore.Should().Be(8.0);
        ballot.Reasoning.Should().Be("""{"score":8,"rationale":5}""");
    }

    /// <summary>
    /// A flat scalar score is the single-criterion form; on a multi-criterion rubric it does not say
    /// which dimension it scored, so it is a schema violation rather than a guess.
    /// </summary>
    [Fact]
    public void A_flat_score_on_a_multi_criterion_rubric_abstains()
    {
        var rubric = HarnessFixtures.Rubric(HarnessFixtures.Criterion("evidence"), HarnessFixtures.Criterion("noise"));

        Parse("""{"score":8,"rationale":"r"}""", rubric).Abstained.Should().BeTrue();
    }

    [Fact]
    public void The_ballot_carries_the_judges_identity_and_never_an_applied_weight()
    {
        var ballot = Parse("""{"score":8,"rationale":"r"}""");

        ballot.JudgeId.Should().Be("j");
        ballot.ModelId.Should().Be("m");
        ballot.ModelFamily.Should().Be("f");
        ballot.AppliedWeight.Should().BeNull("a judge cannot know its own weight — only the aggregator writes that");
    }
}
