namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The tie-break vocabulary is a persisted field the eval runner segments on, so these tests pin
/// the two things a reader depends on: which rules count as a straddle, and that an arbiter rule
/// round-trips back to the identity that produced it.
/// </summary>
public class TieBreakRulesTests
{
    [Fact]
    public void Arbiter_round_trips_through_the_parser()
    {
        var rule = TieBreakRules.Arbiter("arbiter-1", "anthropic");

        TieBreakRules.TryParseArbiter(rule, out var judgeId, out var family).Should().BeTrue();
        judgeId.Should().Be("arbiter-1");
        family.Should().Be("anthropic");
    }

    [Fact]
    public void Arbiter_parses_a_judge_id_that_itself_contains_a_colon()
    {
        // A judge id is host-supplied. Splitting from the LEFT would truncate this id to "org" and
        // report its family as "team", which is a wrong answer rather than a refused one.
        var rule = TieBreakRules.Arbiter("org:team:arbiter", "openai");

        TieBreakRules.TryParseArbiter(rule, out var judgeId, out var family).Should().BeTrue();
        judgeId.Should().Be("org:team:arbiter");
        family.Should().Be("openai");
    }

    [Theory]
    [InlineData(TieBreakRules.Consensus)]
    [InlineData(TieBreakRules.SingleJudge)]
    [InlineData(TieBreakRules.NoDecision)]
    [InlineData(TieBreakRules.GateReject)]
    [InlineData(null)]
    public void Non_arbiter_rules_do_not_parse_as_arbiter(string? rule)
    {
        TieBreakRules.TryParseArbiter(rule, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Straddle_covers_both_arms_of_a_disagreement()
    {
        // The straddle rate measures DISAGREEMENT, not outcome. An arbiter-resolved straddle ends
        // up Pass or Fail, and leaving it out would report a lower disagreement rate on exactly the
        // configuration that pays to resolve disagreements.
        TieBreakRules.IsStraddle(TieBreakRules.SplitUnresolved).Should().BeTrue();
        TieBreakRules.IsStraddle(TieBreakRules.Arbiter("a", "openai")).Should().BeTrue();
    }

    [Theory]
    [InlineData(TieBreakRules.Consensus)]
    [InlineData(TieBreakRules.SingleJudge)]
    [InlineData(TieBreakRules.NoDecision)]
    [InlineData(TieBreakRules.GateReject)]
    [InlineData(null)]
    public void Agreement_and_absence_are_not_straddles(string? rule)
    {
        TieBreakRules.IsStraddle(rule).Should().BeFalse();
    }

    [Theory]
    [InlineData("arbiter:")]
    [InlineData("arbiter::openai")]
    [InlineData("arbiter:judge-1:")]
    public void A_malformed_arbiter_rule_is_refused_rather_than_half_parsed(string rule)
    {
        TieBreakRules.TryParseArbiter(rule, out var judgeId, out var family).Should().BeFalse();
        judgeId.Should().BeNull();
        family.Should().BeNull();
    }
}
