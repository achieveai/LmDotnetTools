using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// Panel composition (P6 spec §2.12.1). Two rules live here and they are deliberately different
/// rules: judge-vs-judge family distinctness is a property of the CONFIGURATION and throws, while
/// judge-vs-candidate generator exclusion is a property of the CANDIDATE and filters.
/// </summary>
public sealed class JudgePanelTests
{
    private static readonly HarnessOptions Options = new();

    [Fact]
    public void Two_judges_of_the_same_family_are_a_configuration_error()
    {
        var judges = new IJudge[] { new FakeJudge("a", "anthropic"), new FakeJudge("b", "anthropic") };

        var compose = () => JudgePanel.Compose(judges, HarnessFixtures.Candidate(), Options);

        compose.Should().Throw<ArgumentException>().WithMessage("*same model family*");
    }

    [Fact]
    public void A_family_that_differs_only_in_case_is_not_a_second_family()
    {
        var judges = new IJudge[] { new FakeJudge("a", "anthropic"), new FakeJudge("b", "Anthropic") };

        var compose = () => JudgePanel.Compose(judges, HarnessFixtures.Candidate(), Options);

        compose.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_single_configured_judge_is_legal()
    {
        var composition = JudgePanel.Compose([new FakeJudge("a", "anthropic")], HarnessFixtures.Candidate(), Options);

        composition.Should().BeOfType<PanelComposition.Degraded>();
    }

    [Fact]
    public void Zero_configured_judges_is_a_configuration_error()
    {
        var compose = () => JudgePanel.Compose([], HarnessFixtures.Candidate(), Options);

        compose.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Three_configured_judges_is_a_configuration_error()
    {
        var judges = new IJudge[]
        {
            new FakeJudge("a", "anthropic"),
            new FakeJudge("b", "openai"),
            new FakeJudge("c", "google"),
        };

        var compose = () => JudgePanel.Compose(judges, HarnessFixtures.Candidate(), Options);

        compose.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Two_disjoint_families_compose_a_full_panel()
    {
        var judges = new IJudge[] { new FakeJudge("a", "anthropic"), new FakeJudge("b", "openai") };

        var composition = JudgePanel.Compose(judges, HarnessFixtures.Candidate(), Options);

        composition.Should().BeOfType<PanelComposition.Full>();
    }

    /// <summary>
    /// §3.2 control 1 — the generator's family is dropped per candidate. This FILTERS; it must not
    /// throw, because which judges are eligible is a property of the candidate and legitimately
    /// varies run to run.
    /// </summary>
    [Fact]
    public void The_generators_family_is_excluded_and_that_is_not_an_error()
    {
        var judges = new IJudge[] { new FakeJudge("a", "anthropic"), new FakeJudge("b", "openai") };

        var composition = JudgePanel.Compose(judges, HarnessFixtures.Candidate(generatorFamily: "anthropic"), Options);

        var degraded = composition.Should().BeOfType<PanelComposition.Degraded>().Subject;
        degraded.Only.JudgeId.Should().Be("b", "the anthropic judge shares the generator's family");
        degraded.Reason.Should().Contain("anthropic");
    }

    /// <summary>Case is not a family boundary on the exclusion path either (§3.2 control 1).</summary>
    [Fact]
    public void The_generators_family_is_excluded_case_insensitively()
    {
        var judges = new IJudge[] { new FakeJudge("a", "anthropic"), new FakeJudge("b", "openai") };

        var composition = JudgePanel.Compose(judges, HarnessFixtures.Candidate(generatorFamily: "ANTHROPIC"), Options);

        composition.Should().BeOfType<PanelComposition.Degraded>().Which.Only.JudgeId.Should().Be("b");
    }

    [Fact]
    public void Excluding_the_generators_family_can_leave_nothing_eligible()
    {
        var composition = JudgePanel.Compose(
            [new FakeJudge("a", "anthropic")],
            HarnessFixtures.Candidate(generatorFamily: "anthropic"),
            Options
        );

        composition.Should().BeOfType<PanelComposition.Unavailable>();
    }

    /// <summary>
    /// The Revobot adapter's exact shape: one configured judge and a candidate whose generator
    /// family is unknown. It must classify as <c>Degraded</c>, because a null generator family
    /// skips only the EXCLUSION step — classification still runs on the real eligible count. This
    /// is the case that has to break if <c>Compose</c> ever shortcuts on a null family again.
    /// </summary>
    [Fact]
    public void A_null_generator_family_with_one_judge_is_degraded_not_full()
    {
        var composition = JudgePanel.Compose(
            [new FakeJudge("a", "anthropic")],
            HarnessFixtures.Candidate(generatorFamily: null),
            Options
        );

        composition
            .Should()
            .BeOfType<PanelComposition.Degraded>(
                "a null generator family skips exclusion, it does not skip classification"
            );
    }

    [Fact]
    public void A_null_generator_family_with_two_judges_drops_neither()
    {
        var judges = new IJudge[] { new FakeJudge("a", "anthropic"), new FakeJudge("b", "openai") };

        var composition = JudgePanel.Compose(judges, HarnessFixtures.Candidate(generatorFamily: null), Options);

        composition.Should().BeOfType<PanelComposition.Full>();
    }

    [Fact]
    public void Compose_performs_no_judge_invocation()
    {
        var first = new FakeJudge("a", "anthropic");
        var second = new FakeJudge("b", "openai");

        _ = JudgePanel.Compose([first, second], HarnessFixtures.Candidate(), Options);

        first.Calls.Should().Be(0);
        second.Calls.Should().Be(0);
    }
}
