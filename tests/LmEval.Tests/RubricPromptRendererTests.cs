using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The judge prompt, and the bias controls that are enforced by what the prompt does and does not
/// contain (P6 spec §3.1, §3.2 control 2, §3.4). Each test below names the control it pins, so a
/// mutation that removes the control has exactly one test to break.
/// </summary>
public sealed class RubricPromptRendererTests
{
    private static readonly Rubric Rubric = HarnessFixtures.Rubric(
        HarnessFixtures.Criterion("evidence"),
        HarnessFixtures.Criterion("noise")
    );

    private static string Render(Candidate candidate, Rubric? rubric = null, JudgeContext? context = null) =>
        RubricPromptRenderer.Render(candidate, rubric ?? Rubric, context ?? new JudgeContext());

    [Fact]
    public void The_prompt_carries_the_task_the_candidate_and_every_criterion()
    {
        var prompt = Render(HarnessFixtures.Candidate(content: "the review under judgement"));

        prompt.Should().Contain("Grade this code review:");
        prompt.Should().Contain("the review under judgement");
        prompt.Should().Contain("evidence").And.Contain("noise");
        prompt.Should().Contain("every finding cites a file and line that resolves");
    }

    /// <summary>
    /// §3.2 control 2 — the model that produced the candidate is NEVER rendered. Self-preference is
    /// causally tied to self-recognition, so the cheapest mitigation is to remove the cue.
    /// </summary>
    [Fact]
    public void The_generating_model_is_never_rendered_into_the_prompt()
    {
        var candidate = HarnessFixtures.Candidate() with
        {
            ModelId = "claude-opus-4-6-20260101",
            GeneratorFamily = "anthropic",
            VariantId = "gpt-5-experimental-arm",
            Metadata = new Dictionary<string, string> { ["reviewer"] = "gemini-3-pro" },
        };

        var prompt = Render(candidate);

        prompt.Should().NotContain("claude-opus", "the generator's model id is a self-recognition cue");
        prompt.Should().NotContain("anthropic", "so is its family");
        prompt.Should().NotContain("gpt-5-experimental-arm", "and so is a variant label naming a model");
        prompt.Should().NotContain("gemini-3-pro", "host metadata is not prompt material");
    }

    /// <summary>
    /// §3.1 — the one residual ordering a pointwise judge could prefer is the order of the rubric's
    /// criteria, and that order is fixed by <see cref="Rubric.Criteria"/> under a versioned rubric.
    /// </summary>
    [Fact]
    public void Criteria_are_rendered_in_the_rubrics_own_order()
    {
        var prompt = Render(HarnessFixtures.Candidate());

        prompt
            .IndexOf("evidence", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                prompt.IndexOf("noise", StringComparison.Ordinal),
                "criterion order is fixed by the versioned rubric, not by the renderer"
            );
    }

    /// <summary>§3.4 — the reference answer is the single largest accuracy lever available.</summary>
    [Fact]
    public void A_reference_answer_is_rendered_when_the_context_carries_one()
    {
        var prompt = Render(
            HarnessFixtures.Candidate(),
            context: new JudgeContext { Reference = "the accepted review" }
        );

        prompt.Should().Contain("the accepted review");
    }

    [Fact]
    public void No_reference_section_appears_when_there_is_no_reference()
    {
        Render(HarnessFixtures.Candidate()).Should().NotContain("Reference");
    }

    /// <summary>
    /// §2.5 — reasoning before the score, enforced structurally by the field order of the response
    /// schema the judge is handed rather than by asking it nicely.
    /// </summary>
    [Fact]
    public void The_response_schema_puts_reasoning_before_the_scores()
    {
        var prompt = Render(HarnessFixtures.Candidate());

        prompt
            .IndexOf("\"reasoning\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(prompt.IndexOf("\"scores\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Turning_reasoning_first_off_flips_the_schema_order()
    {
        var prompt = Render(
            HarnessFixtures.Candidate(),
            Rubric with
            {
                RequireReasoningBeforeScore = false,
            }
        );

        prompt
            .IndexOf("\"scores\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(prompt.IndexOf("\"reasoning\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rendered_scale_is_the_rubrics_own()
    {
        Render(HarnessFixtures.Candidate()).Should().Contain("0").And.Contain("10");
    }
}
