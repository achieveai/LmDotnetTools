using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// §3.3 control 3 — the rubric must not reward length, plus the structural drafting rules of §2.5.
/// </summary>
public sealed class RubricValidatorTests
{
    private static RubricCriterion Criterion(string description, params (int Score, string Text)[] anchors) =>
        new()
        {
            CriterionId = "quality",
            Description = description,
            Anchors = anchors.ToDictionary(a => a.Score, a => a.Text),
        };

    private static readonly (int, string)[] WellAnchored =
    [
        (0, "no finding cites a file and line that resolves"),
        (5, "some findings cite a file and line that resolves"),
        (10, "every finding cites a file and line that resolves"),
    ];

    [Fact]
    public void A_well_drafted_rubric_validates()
    {
        RubricValidator.Validate(HarnessFixtures.Rubric()).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// The control itself: reward-for-volume language with nothing capping it is an instruction to
    /// prefer the longer candidate.
    /// </summary>
    [Fact]
    public void A_criterion_rewarding_volume_with_no_capping_anchor_is_refused()
    {
        var rubric = HarnessFixtures.Rubric(
            Criterion("The review is comprehensive and thorough.", WellAnchored)
        );

        var result = RubricValidator.Validate(rubric);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("comprehensive");
    }

    [Fact]
    public void The_same_criterion_validates_once_an_anchor_caps_it()
    {
        var rubric = HarnessFixtures.Rubric(
            Criterion(
                "The review is comprehensive.",
                (0, "no finding cites a file and line that resolves"),
                (5, "some findings cite a file and line that resolves"),
                (
                    10,
                    "every finding cites a file and line that resolves, without restating the diff"
                )
            )
        );

        RubricValidator.Validate(rubric).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("comprehensive")]
    [InlineData("thorough")]
    [InlineData("detailed")]
    [InlineData("in depth")]
    [InlineData("in-depth")]
    [InlineData("exhaustive")]
    public void Every_reward_for_volume_term_is_caught(string term)
    {
        var rubric = HarnessFixtures.Rubric(Criterion($"The review is {term}.", WellAnchored));

        RubricValidator.Validate(rubric).IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_volume_check_is_case_insensitive()
    {
        var rubric = HarnessFixtures.Rubric(Criterion("A THOROUGH review.", WellAnchored));

        RubricValidator.Validate(rubric).IsValid.Should().BeFalse();
    }

    // ---- structural rules (§2.5) --------------------------------------------------------------

    [Fact]
    public void A_criterion_missing_its_floor_midpoint_or_ceiling_anchor_is_refused()
    {
        var rubric = HarnessFixtures.Rubric(
            Criterion("Findings cite lines that resolve.", (0, "none"), (10, "all"))
        );

        var result = RubricValidator.Validate(rubric);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("score 5");
    }

    [Fact]
    public void A_non_positive_criterion_weight_is_refused()
    {
        var rubric = HarnessFixtures.Rubric(
            HarnessFixtures.Criterion("quality") with
            {
                Weight = 0.0,
            }
        );

        RubricValidator
            .Validate(rubric)
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("non-positive weight");
    }

    [Fact]
    public void A_duplicate_criterion_id_is_refused()
    {
        var rubric = HarnessFixtures.Rubric(
            HarnessFixtures.Criterion("quality"),
            HarnessFixtures.Criterion("quality")
        );

        RubricValidator
            .Validate(rubric)
            .Errors.Should()
            .Contain(e => e.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pass_threshold_outside_the_scale_is_refused()
    {
        var rubric = HarnessFixtures.Rubric() with { PassThreshold = 42 };

        RubricValidator
            .Validate(rubric)
            .Errors.Should()
            .Contain(e => e.Contains("PassThreshold 42", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_scale_is_refused()
    {
        var rubric = HarnessFixtures.Rubric() with { MinScore = 10, MaxScore = 10 };

        RubricValidator
            .Validate(rubric)
            .Errors.Should()
            .Contain(e => e.Contains("scale", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rubric_with_no_criteria_is_refused()
    {
        var rubric = HarnessFixtures.Rubric() with { Criteria = [] };

        RubricValidator
            .Validate(rubric)
            .Errors.Should()
            .Contain(e => e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_problem_is_reported_not_only_the_first()
    {
        var rubric = HarnessFixtures.Rubric(
            Criterion("A thorough review.", (0, "none"), (10, "all")) with
            {
                Weight = -1.0,
            }
        );

        RubricValidator.Validate(rubric).Errors.Should().HaveCountGreaterThan(2);
    }
}
