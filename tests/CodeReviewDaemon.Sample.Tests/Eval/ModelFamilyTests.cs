using AchieveAi.LmDotnetTools.LmEval;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Eval;

namespace CodeReviewDaemon.Sample.Tests.Eval;

/// <summary>
/// The one family rule, and the two panel-exclusion cases that tell it apart from the routing-provider
/// rule it replaced (#456).
/// <para>
/// The daemon held two policies at once. <see cref="DaemonCorpusReader"/> stamped a candidate's
/// generator family with the <i>routing provider</i>, while <c>JudgeAgent</c> refused to derive a
/// family at all and recorded the request's <c>Provider</c> — which is the repo host, <c>github</c>,
/// not an LLM vendor. Neither is a model family, and the first <c>EvalRunner</c> over this corpus
/// inherits whichever one it happens to read.
/// </para>
/// <para>
/// The exclusion is asserted through the real <see cref="JudgePanel"/> rather than by comparing two
/// strings, because the string comparison is not the claim — the claim is which judges a candidate
/// leaves eligible, and that is a decision only <c>Compose</c> makes.
/// </para>
/// </summary>
public class ModelFamilyTests
{
    /// <summary>Minimal <see cref="IJudge"/>: the panel filter reads identity and family only.</summary>
    private sealed class FamilyJudge(string judgeId, string modelId) : IJudge
    {
        public string JudgeId { get; } = judgeId;

        public string ModelId { get; } = modelId;

        public string ModelFamily { get; } = ModelFamilies.Of(modelId) ?? ModelFamilies.Unresolved;

        public Task<Ballot> JudgeAsync(
            Candidate candidate,
            Rubric rubric,
            JudgeContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Compose never runs a judge.");
    }

    private static Candidate GeneratedBy(string? generatorModelId) =>
        new()
        {
            CandidateId = "1:primary",
            TaskType = DaemonCorpusReader.CodeReviewTaskType,
            TaskInput = "a diff",
            Content = "a review",
            VariantId = "primary",
            ModelId = generatorModelId,
            GeneratorFamily = ModelFamilies.Of(generatorModelId),
        };

    // ---- the rule itself -------------------------------------------------------------------------

    /// <summary>
    /// The family is the vendor of the underlying model — the segment before the model name — and an
    /// id this rule cannot read resolves to unknown rather than to whatever sat in that position.
    /// </summary>
    [Theory]
    [InlineData("openai/gpt-5", "openai")]
    [InlineData("anthropic/claude-opus-4.5", "anthropic")]
    [InlineData("openrouter/meta/llama-4", "meta")]
    [InlineData("openrouter/anthropic/claude-opus-4.5", "anthropic")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("gpt-5", null)]
    [InlineData("/gpt-5", null)]
    [InlineData("openai/", null)]
    public void The_vendor_before_the_model_name_is_the_family_and_anything_else_is_unknown(
        string? modelId,
        string? expected
    ) => ModelFamilies.Of(modelId).Should().Be(expected);

    /// <summary>
    /// The unresolved sentinel cannot be mistaken for a family this rule derived — it carries the
    /// separator, and a derived family is one path segment, which cannot. Without that, an
    /// unclassifiable judge could collide with an unclassifiable generator and exclude itself.
    /// </summary>
    [Fact]
    public void The_unresolved_sentinel_can_never_equal_a_derived_family()
    {
        ModelFamilies.Unresolved.Should().Contain("/");
        ModelFamilies.Of("openrouter/meta/llama-4").Should().NotContain("/");
    }

    // ---- the two cross-router cases §7.1(2) exists for -------------------------------------------

    /// <summary>
    /// <b>Same vendor, different routers → excluded.</b> This is the case the rule exists for: a
    /// judge that is the generator's own model reached over another gateway. Under the routing-provider
    /// reading these are <c>openrouter</c> and <c>anthropic</c> — two families — so the self-preferring
    /// judge is admitted and its agreement with itself is reported as a verdict.
    /// </summary>
    [Fact]
    public void A_judge_of_the_generators_vendor_behind_another_router_is_excluded()
    {
        var composition = JudgePanel.Compose(
            [new FamilyJudge("judge-a", "openrouter/anthropic/claude-opus-4.5")],
            GeneratedBy("anthropic/claude-opus-4.5"),
            new HarnessOptions()
        );

        composition
            .Should()
            .BeOfType<PanelComposition.Unavailable>(
                "the judge is the generator's own vendor; the router in front of it is not a family"
            );
    }

    /// <summary>
    /// <b>Different vendors, same router → NOT excluded.</b> The mirror failure, and the one that is
    /// total: under the routing-provider reading every model reached through one gateway shares a
    /// family, so a whole deployment routed through OpenRouter would empty its panel on every single
    /// candidate — and report <c>PanelUnavailable</c>, which reads as an outage rather than as a
    /// misconfigured family rule.
    /// </summary>
    [Fact]
    public void A_judge_of_another_vendor_behind_the_same_router_stays_eligible()
    {
        var composition = JudgePanel.Compose(
            [new FamilyJudge("judge-a", "openrouter/anthropic/claude-opus-4.5")],
            GeneratedBy("openrouter/meta/llama-4"),
            new HarnessOptions()
        );

        composition
            .Should()
            .BeOfType<PanelComposition.Degraded>("one eligible judge is Degraded, not Unavailable")
            .Which.Reason.Should()
            .Be("single-judge-configured", "nothing was excluded — the shared router is not a shared family");
    }

    /// <summary>
    /// A generator whose id this rule cannot read arms nothing: the exclusion step is skipped, and the
    /// judge stays eligible. Unknown is not "not the judge's family" (§2.12.1).
    /// </summary>
    [Fact]
    public void An_unclassifiable_generator_arms_no_exclusion()
    {
        var composition = JudgePanel.Compose(
            [new FamilyJudge("judge-a", "anthropic/claude-opus-4.5")],
            GeneratedBy("some-internal-deployment"),
            new HarnessOptions()
        );

        composition.Should().BeOfType<PanelComposition.Degraded>();
    }

    // ---- both sides speak the rule ---------------------------------------------------------------

    /// <summary>
    /// The judge side of the contradiction. <c>JudgeAgent</c> recorded <c>JudgeRequest.Provider</c> as
    /// the judge's model family — and that field is the <b>repo host</b> (<c>github</c> / <c>ado</c>),
    /// not an LLM vendor at all. Read against a generator family produced by the corpus reader it can
    /// never match, so the exclusion is dead; read against a repo host that happened to share a name
    /// with a vendor it would fire for no reason.
    /// </summary>
    [Fact]
    public void The_judge_family_is_derived_from_the_judge_model_not_the_repo_host()
    {
        var request = new JudgeRequest(1, "github", "primary", "grade this")
        {
            JudgeModelId = "openrouter/anthropic/claude-opus-4.5",
            GeneratorModelId = "openai/gpt-5",
        };

        JudgeAgent.JudgeFamilyOf(request).Should().Be("anthropic");
    }

    /// <summary>
    /// A judge run whose model id was never recorded resolves to the sentinel rather than to the repo
    /// host — refuse to guess, and say which value is the guess-free one.
    /// </summary>
    [Fact]
    public void A_judge_whose_model_was_never_recorded_carries_the_unresolved_family()
    {
        var request = new JudgeRequest(1, "github", "primary", "grade this");

        JudgeAgent.JudgeFamilyOf(request).Should().Be(ModelFamilies.Unresolved);
    }

    /// <summary>
    /// The same substitution one field over: the ballot's model <i>id</i> also stood as the repo host.
    /// A reader asking "which model issued this grade?" was answered <c>github</c>.
    /// </summary>
    [Fact]
    public void The_judge_model_id_is_the_judge_model_and_never_the_repo_host()
    {
        var recorded = new JudgeRequest(1, "github", "primary", "grade this") { JudgeModelId = "openai/gpt-5" };

        JudgeAgent.JudgeModelIdOf(recorded).Should().Be("openai/gpt-5");

        var unrecorded = new JudgeRequest(1, "github", "primary", "grade this");

        JudgeAgent
            .JudgeModelIdOf(unrecorded)
            .Should()
            .Be(JudgeAgent.UnrecordedModelId)
            .And.NotBe("github", "the repo host is not the model that issued the grade");
    }
}
