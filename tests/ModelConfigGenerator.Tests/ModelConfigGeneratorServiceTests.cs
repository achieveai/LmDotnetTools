using System.Reflection;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Configuration;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.ModelConfigGenerator.Tests;

/// <summary>
///     Tests for ModelConfigGeneratorService with focus on family detection and filtering logic.
/// </summary>
public class ModelConfigGeneratorServiceTests
{
    private static ModelConfigGeneratorService CreateTestService()
    {
        var httpClient = new HttpClient();
        var logger = new Mock<ILogger<OpenRouterModelService>>().Object;
        var openRouterService = new OpenRouterModelService(httpClient, logger);
        var serviceLogger = new Mock<ILogger<ModelConfigGeneratorService>>().Object;
        return new ModelConfigGeneratorService(openRouterService, serviceLogger);
    }

    [Fact]
    public void GetSupportedFamilies_ShouldReturnExpectedFamilies()
    {
        // Act
        var families = ModelConfigGeneratorService.GetSupportedFamilies();

        // Assert
        Assert.NotEmpty(families);
        Assert.Contains("llama", families);
        Assert.Contains("claude", families);
        Assert.Contains("gpt", families);
        Assert.Contains("qwen", families);
        Assert.Contains("deepseek", families);
        Assert.Contains("kimi", families);
        Assert.Contains("mistral", families);
        Assert.Contains("cohere", families);
    }

    [Theory]
    [InlineData("meta-llama/llama-3.1-70b", "llama", true)]
    [InlineData("anthropic/claude-3-sonnet", "claude", true)]
    [InlineData("openai/gpt-4-turbo", "gpt", true)]
    [InlineData("qwen/qwen-2.5-72b", "qwen", true)]
    [InlineData("deepseek/deepseek-v2", "deepseek", true)]
    [InlineData("moonshot/kimi-chat", "kimi", true)]
    [InlineData("mistral/mistral-7b", "mistral", true)]
    [InlineData("cohere/command-r", "cohere", true)]
    [InlineData("meta-llama/llama-3.1-70b", "claude", false)]
    [InlineData("anthropic/claude-3-sonnet", "gpt", false)]
    [InlineData("random/model-name", "nonexistent", false)]
    public void ModelFamilyMatching_ShouldWorkCorrectly(string modelId, string family, bool shouldMatch)
    {
        // Arrange
        var model = new ModelConfig
        {
            Id = modelId,
            Capabilities = new ModelCapabilities
            {
                TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                SupportsStreaming = true,
            },
            Providers =
            [
                new ProviderConfig
                {
                    Name = "TestProvider",
                    ModelName = modelId,
                    Priority = 1,
                    Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                },
            ],
        };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var matchesFamilyMethod = reflection.GetMethod("MatchesFamily", BindingFlags.NonPublic | BindingFlags.Static);
        var result = (bool)matchesFamilyMethod!.Invoke(null, [model, family])!;

        // Assert
        Assert.Equal(shouldMatch, result);
    }

    /// <summary>
    ///     Every family's ALIAS branch, matched by an id that does NOT contain the family name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These cases exist to be un-fakeable. <c>MatchesFamily</c> falls back to
    ///         <c>model.Id.Contains(family)</c> for any family missing from <c>ModelFamilyPatterns</c>, so a case
    ///         like ("moonshot/kimi-chat", "kimi") passes whether the regex works or has been deleted outright —
    ///         the id contains "kimi" either way. Every id below matches only through its family's regex and is
    ///         false under the fallback, so a broken or missing pattern fails here instead of passing quietly.
    ///     </para>
    ///     <para>
    ///         "llama" has no such case and is deliberately absent. Its pattern is the bare literal
    ///         <c>llama</c> — the family name itself — so <c>Contains("llama")</c> and the regex match exactly
    ///         the same ids and NO input can tell them apart. (It was <c>llama|meta-llama</c>, which was no
    ///         better: <c>meta-llama</c> contains <c>llama</c>, so the alias never distinguished anything
    ///         either.) Listing it with a weaker case would make the matrix look complete while proving less
    ///         than these rows do, so the gap is left stated rather than filled.
    ///     </para>
    ///     <para>
    ///         "gemini" and "phi" JOINED that gap in #66, and their rows were removed rather than re-pointed
    ///         because no input can fill them. Each had exactly one alternative, and it was a vendor —
    ///         <c>gemini|google</c> and <c>phi-|microsoft</c> — which is what made <c>("google/gemma-2-27b",
    ///         "gemini")</c> and <c>("microsoft/orca-2", "phi")</c> possible. Removing the vendor is the fix:
    ///         a vendor alternative asserts "this vendor ships exactly one family", and both vendors ship two.
    ///         What is left is <c>gemini</c>, identical to the fallback like llama's, and <c>phi-</c>, which is
    ///         STRICTLY NARROWER than <c>Contains("phi")</c> — so anything the regex matches the fallback
    ///         matches too, and again no id can separate them. The discrimination did not vanish, it moved:
    ///         <see cref="ModelIdCorpus"/> carries <c>google/gemma-2-27b</c> and <c>microsoft/orca-2</c>
    ///         (vendor present, family absent), and <c>phind/model-34b</c> still separates <c>phi-</c> from
    ///         <c>phi</c>. The positive direction is pinned by
    ///         <see cref="A_vendor_that_ships_two_families_resolves_each_to_its_own"/>.
    ///     </para>
    ///     <para>
    ///         The cohere row is a BARE <c>command-r-plus</c>, and must stay bare. It read
    ///         <c>("provider/command-r-plus", "cohere")</c> when the pattern was an unanchored <c>command</c> —
    ///         a correct description of what the code did and a wrong statement of what it should do, since a
    ///         foreign vendor's <c>command-*</c> model is not a Cohere model. The pattern is now
    ///         <c>cohere|^command</c>; restoring a vendor prefix here asserts the defect again.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("alibaba/tongyi-max", "qwen")]
    [InlineData("moonshot/moonshot-v1-128k", "kimi")]
    [InlineData("anthropic/opus-4", "claude")]
    [InlineData("openai/o3-mini", "gpt")]
    [InlineData("xai/proto-2-vision", "grok")]
    [InlineData("thudm/codegeex-4", "glm")]
    [InlineData("cloaked/stealth-model", "openrouter")]
    [InlineData("command-r-plus", "cohere")]
    [InlineData("01-ai/large-preview", "yi")]
    [InlineData("teknium/hermes-3", "nous")]
    [InlineData("provider/wizard-vicuna-13b", "wizardlm")]
    public void Each_family_alias_matches_through_its_pattern_and_not_the_contains_fallback(
        string modelId, string family)
    {
        Assert.False(
            modelId.Contains(family, StringComparison.OrdinalIgnoreCase),
            $"'{modelId}' must not contain '{family}', or this case proves nothing about the regex");

        Assert.True(InvokeMatchesFamily(modelId, family), $"'{modelId}' should classify as '{family}'");
    }

    /// <summary>
    ///     The primary branch of every family, including the seven the older theory never reached.
    /// </summary>
    [Theory]
    [InlineData("meta-llama/llama-3.1-70b", "llama")]
    [InlineData("qwen/qwen-2.5-72b", "qwen")]
    [InlineData("moonshot/kimi-k2", "kimi")]
    [InlineData("deepseek/deepseek-v3", "deepseek")]
    [InlineData("anthropic/claude-3-sonnet", "claude")]
    [InlineData("openai/gpt-4-turbo", "gpt")]
    [InlineData("google/gemini-2.0-flash", "gemini")]
    [InlineData("google/gemma-2-27b", "gemma")]
    [InlineData("xai/grok-2", "grok")]
    [InlineData("thudm/chatglm-6b", "glm")]
    [InlineData("openrouter/auto", "openrouter")]
    [InlineData("mistralai/mistral-7b", "mistral")]
    [InlineData("minimax/minimax-m2", "minimax")]
    [InlineData("cohere/cohere-command", "cohere")]
    [InlineData("01-ai/yi-34b", "yi")]
    [InlineData("microsoft/phi-3-medium", "phi")]
    [InlineData("tiiuae/falcon-180b", "falcon")]
    [InlineData("provider/wizardlm-2-8x22b", "wizardlm")]
    [InlineData("lmsys/vicuna-13b", "vicuna")]
    [InlineData("provider/alpaca-7b", "alpaca")]
    [InlineData("nousresearch/nous-capybara", "nous")]
    public void Every_supported_family_still_matches_its_own_models(string modelId, string family)
    {
        Assert.True(InvokeMatchesFamily(modelId, family), $"'{modelId}' should classify as '{family}'");
        Assert.Contains(family, ModelConfigGeneratorService.GetSupportedFamilies());
    }

    /// <summary>
    ///     Case-insensitivity, which is the property the culture argument was there to configure. The patterns
    ///     are ASCII, so invariant folding is the whole of what these ids need.
    /// </summary>
    [Theory]
    [InlineData("META-LLAMA/Llama-3.1-70B", "llama")]
    [InlineData("Anthropic/CLAUDE-3-Opus", "claude")]
    [InlineData("OpenAI/GPT-4O", "gpt")]
    [InlineData("MoonShot/KIMI-K2", "kimi")]
    public void Family_matching_ignores_case(string modelId, string family) =>
        Assert.True(InvokeMatchesFamily(modelId, family), $"'{modelId}' should classify as '{family}'");

    /// <summary>
    ///     A model must not be dragged into a family it has nothing to do with. Without these, widening a
    ///     pattern to <c>.*</c> would satisfy every positive case above.
    /// </summary>
    [Theory]
    [InlineData("meta-llama/llama-3.1-70b", "claude")]
    [InlineData("anthropic/claude-3-sonnet", "gpt")]
    [InlineData("openai/gpt-4-turbo", "gemini")]
    [InlineData("deepseek/deepseek-v3", "qwen")]
    [InlineData("mistralai/mistral-7b", "falcon")]
    [InlineData("tiiuae/falcon-180b", "vicuna")]
    public void A_model_does_not_match_an_unrelated_family(string modelId, string family) =>
        Assert.False(InvokeMatchesFamily(modelId, family), $"'{modelId}' must not classify as '{family}'");

    /// <summary>
    ///     The `command` alternative belongs to Cohere's OWN bare model ids, not to the word wherever it turns
    ///     up. Unanchored, it claimed <c>*/command-*</c> from any vendor — a model attributed to a provider
    ///     that did not make it, emitted into generated config as a fact and never surfacing as an error.
    /// </summary>
    /// <remarks>
    ///     Latent rather than live when found: no id in the sampled config contains <c>command</c> at all.
    ///     Pinned anyway, because the cost of it going live is a silently wrong provider attribution, and the
    ///     sample carries no cohere models — it cannot show this working, only fail to show it broken.
    /// </remarks>
    [Theory]
    [InlineData("provider/command-r-plus")]
    [InlineData("nvidia/command-center-7b")]
    [InlineData("somevendor/slash-command-model")]
    public void Another_vendors_command_model_is_not_cohere(string modelId) =>
        Assert.False(
            InvokeMatchesFamily(modelId, "cohere"),
            $"'{modelId}' is not a Cohere model and must not be classified as one");

    /// <summary>The side of that boundary which must NOT move: Cohere's own ids, prefixed or bare.</summary>
    [Theory]
    [InlineData("cohere/command-r-plus")]
    [InlineData("cohere/command-a-03-2025")]
    [InlineData("command-r")]
    [InlineData("Command-R-Plus")]
    public void Coheres_own_models_still_classify_as_cohere(string modelId) =>
        Assert.True(InvokeMatchesFamily(modelId, "cohere"), $"'{modelId}' is a Cohere model");

    /// <summary>
    ///     A model's family is the vendor that published it, not a family name borrowed from the model it was
    ///     distilled from. Every id here is one the previous rule got WRONG: it tested the whole id and took
    ///     the first family in dictionary order, so <c>llama</c> (first) and <c>qwen</c> (second) both outrank
    ///     <c>deepseek</c> (fourth) and claimed DeepSeek's distillations.
    /// </summary>
    /// <remarks>
    ///     These seven are the entire live blast radius of the ordering defect across the 191 ids in the
    ///     sampled config. The defect reached only the <c>--verbose</c> debug log — <c>GetModelFamily</c> has
    ///     one call site and the generated config carries no family field — so this is a reporting fix, not a
    ///     config fix. Pinned because a debug line that misattributes a vendor is read as fact.
    /// </remarks>
    [Theory]
    [InlineData("deepseek/deepseek-r1-distill-llama-70b")]
    [InlineData("deepseek/deepseek-r1-distill-llama-8b")]
    [InlineData("deepseek/deepseek-r1-distill-qwen-1.5b")]
    [InlineData("deepseek/deepseek-r1-distill-qwen-7b")]
    [InlineData("deepseek/deepseek-r1-distill-qwen-14b")]
    [InlineData("deepseek/deepseek-r1-distill-qwen-32b")]
    [InlineData("deepseek/deepseek-r1-0528-qwen3-8b")]
    public void A_distillation_belongs_to_the_vendor_that_published_it_not_to_its_teacher(string modelId) =>
        Assert.Equal("deepseek", InvokeGetModelFamily(modelId));

    /// <summary>
    ///     The rule stated directly, independent of the seven ids above: when the vendor segment names one
    ///     family and the model segment names another, the vendor wins — including when the model segment's
    ///     family comes FIRST in <c>ModelFamilyPatterns</c> order and would therefore have won before.
    /// </summary>
    /// <remarks>
    ///     The second case is the control. <c>llama</c> is first in dictionary order, so a whole-id match
    ///     would return it for both rows; only the vendor pass can tell them apart. If someone reverts
    ///     <c>GetModelFamily</c> to a single whole-id scan, row one fails and row two still passes — which is
    ///     what makes row one evidence rather than coincidence.
    /// </remarks>
    [Theory]
    [InlineData("mistralai/mistral-llama-hybrid-8b", "mistral")]
    [InlineData("meta-llama/llama-3.3-70b-instruct", "llama")]
    public void The_vendor_segment_decides_the_family_even_when_the_model_name_disagrees(
        string modelId,
        string expected) => Assert.Equal(expected, InvokeGetModelFamily(modelId));

    /// <summary>
    ///     When the vendor is one no pattern recognises, the rest of the id decides. Without this fallback the
    ///     vendor pass would turn every unrecognised publisher into <c>unknown</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>x-ai/</c> is the live instance: the grok pattern's alternative is <c>xai</c>, which the
    ///         hyphenated vendor does not contain, so all fifteen <c>x-ai/*</c> models reach their family through
    ///         the fallback and not the vendor pass. <c>command-r</c> covers the other shape — a bare id with no
    ///         vendor segment at all.
    ///     </para>
    ///     <para>
    ///         The third row was <c>("minimax/minimax-m2", "unknown")</c> until #66 gave minimax a pattern. It
    ///         is re-pointed, not deleted: the row's job is "vendor unrecognised AND model segment unrecognised
    ///         ⇒ unknown", and minimax stopped being an instance of it the moment the vendor became recognised
    ///         — the row would have kept passing while asserting something its own remark no longer described.
    ///         <c>some/entirely-unrelated-model</c> is the replacement and is already in
    ///         <see cref="ModelIdCorpus"/>. Nothing in the live corpus classifies unknown now, so this row is
    ///         necessarily synthetic.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("x-ai/grok-4", "grok")]
    [InlineData("command-r", "cohere")]
    [InlineData("some/entirely-unrelated-model", "unknown")]
    public void An_unrecognised_vendor_leaves_the_rest_of_the_id_to_decide(string modelId, string expected) =>
        Assert.Equal(expected, InvokeGetModelFamily(modelId));

    /// <summary>
    ///     The three MiniMax ids that classified <c>unknown</c> until #66, and therefore could not be selected
    ///     by <c>--family</c> at all.
    /// </summary>
    /// <remarks>
    ///     Distinct in kind from the gemma rows above, and worth keeping separate: gemma was a WRONG answer
    ///     (<c>gemini</c>), minimax was an ABSENT one. A wrong answer misroutes; an absent one makes the model
    ///     unreachable through the family filter while looking like honest ignorance. Both are live in the
    ///     sampled corpus and both were invisible to a suite whose ids all happened to classify.
    /// </remarks>
    [Theory]
    [InlineData("minimax/minimax-01")]
    [InlineData("minimax/minimax-m1")]
    [InlineData("minimax/minimax-m2")]
    public void A_family_with_no_pattern_is_unreachable_by_the_family_filter(string modelId) =>
        Assert.Equal("minimax", InvokeGetModelFamily(modelId));

    /// <summary>
    ///     The claim the remark above makes, asserted instead of described: the fallback has a live user, and
    ///     it has one because the grok pattern's alternative is <c>xai</c> while x.ai's vendor segment is
    ///     <c>x-ai/</c>.
    /// </summary>
    /// <remarks>
    ///     That is a property of the pattern, not an observation about a sample, so it can execute. Add
    ///     <c>x-ai</c> to the grok pattern — an entirely reasonable thing to do — and the fifteen
    ///     <c>x-ai/*</c> models start resolving in the vendor pass, the whole-id fallback silently loses its
    ///     only real coverage, and every remaining test of it is a hypothetical id someone invented. This
    ///     fails at that moment rather than after, which is the difference between a comment and a check.
    /// </remarks>
    [Fact]
    public void The_fallback_has_a_live_user_and_not_only_invented_ones()
    {
        Assert.False(
            InvokeFamilyPatternMatches("grok", "x-ai/"),
            "the grok pattern must not match the bare 'x-ai/' vendor segment — if it now does, x.ai models "
                + "resolve in the vendor pass and the whole-id fallback is left pinned only by invented ids");

        Assert.Equal("grok", InvokeGetModelFamily("x-ai/grok-4"));
    }

    /// <summary>Runs one family's compiled pattern against a raw candidate string.</summary>
    private static bool InvokeFamilyPatternMatches(string family, string candidate)
    {
        var patterns = (Dictionary<string, Regex>)
            typeof(ModelConfigGeneratorService)
                .GetField("ModelFamilyPatterns", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
        return patterns[family].IsMatch(candidate);
    }

    /// <summary>
    ///     <see cref="FamilyAlternatives"/> claims to be the record of what each pattern is. This is what
    ///     makes that claim true rather than aspirational: the table joined with <c>|</c> must reproduce the
    ///     pattern text verbatim, for every family.
    /// </summary>
    /// <remarks>
    ///     The behavioural comparison below it — regex versus ordinal literals over
    ///     <see cref="ModelIdCorpus"/> — can only see a pattern edit that some id in the corpus exercises.
    ///     Adding an alternative no id happens to contain passes it silently, which is exactly what happened
    ///     when I added <c>x-ai</c> to the grok pattern as a mutation: that test did not fire. Comparing the
    ///     pattern strings closes the gap, because it does not depend on the corpus being rich enough to
    ///     notice.
    /// </remarks>
    [Fact]
    public void The_alternatives_table_reproduces_every_pattern_verbatim()
    {
        var patterns = (Dictionary<string, Regex>)
            typeof(ModelConfigGeneratorService)
                .GetField("ModelFamilyPatterns", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

        Assert.Equal(patterns.Count, FamilyAlternatives.Length);

        foreach (var (family, alternatives) in FamilyAlternatives)
        {
            Assert.True(patterns.ContainsKey(family), $"'{family}' is in the table but not in the patterns");
            Assert.Equal(string.Join("|", alternatives), patterns[family].ToString());
        }
    }

    /// <summary>
    ///     The vendor segment is matched with its trailing slash, because one pattern's alternative includes
    ///     one: <c>openrouter/</c>. Drop the slash and all twelve <c>openrouter/*</c> models miss the vendor
    ///     pass.
    /// </summary>
    /// <remarks>
    ///     They would still land on <c>openrouter</c> via the fallback, so the slash only changes the answer
    ///     for an <c>openrouter/*</c> id whose NAME contains a family literal ranking above openrouter — the
    ///     third row. None of the twelve <c>openrouter/*</c> ids in the sampled config does; they are all
    ///     codenames (<c>sonoma-sky-alpha</c>, <c>quasar-alpha</c>). So that row is hypothetical, and stated
    ///     as such: it is the only shape under which dropping the slash is observable, and without it the
    ///     slash is pinned by nothing. The <c>cloaked</c> row is the alternative carrying no slash, which must
    ///     keep working either way.
    /// </remarks>
    [Theory]
    [InlineData("openrouter/auto", "openrouter")]
    [InlineData("openrouter/cloaked-model", "openrouter")]
    [InlineData("openrouter/llama-guard-4-12b", "openrouter")]
    public void A_pattern_carrying_the_vendor_slash_still_matches_in_the_vendor_pass(
        string modelId,
        string expected) => Assert.Equal(expected, InvokeGetModelFamily(modelId));

    /// <summary>
    ///     The near-miss the vendor rule defuses: <c>tongyi-</c> contains <c>yi-</c>, the yi family's
    ///     alternative. It classified as qwen before only because qwen sits at index 1 and yi at index 12.
    /// </summary>
    /// <remarks>
    ///     Same answer under both rules, which is exactly why it is worth pinning: it was correct by accident
    ///     of ordering and is now correct because <c>alibaba</c> is Qwen's vendor. Reordering the dictionary
    ///     used to change this id's family; now it cannot.
    /// </remarks>
    [Fact]
    public void A_vendor_match_settles_an_id_whose_name_accidentally_contains_another_family() =>
        Assert.Equal("qwen", InvokeGetModelFamily("alibaba/tongyi-deepresearch-30b-a3b"));

    /// <summary>
    ///     A vendor alternative in a family pattern asserts "this vendor ships exactly one family". For two
    ///     vendors that claim was false, so the vendor pass answered with the wrong family — confidently.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>google/gemma-*</c> is the live instance and it is not hypothetical: seven ids in the sampled
    ///         config resolved to <c>gemini</c>, because the gemini pattern's alternative was <c>google</c> and
    ///         the vendor pass matched it before anything read the model segment. Gemma is a different family
    ///         from Gemini. <c>microsoft/wizardlm-*</c> is the same shape through <c>phi-|microsoft</c>; it has
    ///         no id in the corpus, which is why it survived unnoticed and why the gemma rows are the ones that
    ///         make this a defect rather than a tidy-up.
    ///     </para>
    ///     <para>
    ///         Fixed by deleting the false vendor alternatives rather than by reordering. Ordering cannot fix
    ///         it: whichever of two families for one vendor is placed first wins for BOTH, so any order is
    ///         wrong for one of them.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("google/gemma-3-27b-it", "gemma")]
    [InlineData("google/gemma-2b-it", "gemma")]
    [InlineData("google/gemma-3n-e4b-it", "gemma")]
    [InlineData("microsoft/wizardlm-2-8x22b", "wizardlm")]
    public void A_vendor_that_ships_two_families_resolves_each_to_its_own(string modelId, string expected) =>
        Assert.Equal(expected, InvokeGetModelFamily(modelId));

    /// <summary>
    ///     The control for the rows above, and the reason removing a vendor alternative is safe: the vendor's
    ///     OTHER family still resolves, through the whole-id pass, because its ids carry the family name.
    /// </summary>
    /// <remarks>
    ///     All 21 <c>google/gemini-*</c> ids in the corpus contain <c>gemini</c>, and the one
    ///     <c>microsoft/phi-*</c> id contains <c>phi-</c>, so nothing reached its family through the deleted
    ///     alternatives that cannot reach it through the id. If a future <c>google/</c> or <c>microsoft/</c>
    ///     model omits its own family name it will classify as <c>unknown</c> — which is the intended answer
    ///     per the cohere/command precedent, not a regression: the table not knowing an id and saying so beats
    ///     naming the wrong family.
    /// </remarks>
    [Theory]
    [InlineData("google/gemini-2.5-pro", "gemini")]
    [InlineData("google/gemini-3-pro-preview", "gemini")]
    [InlineData("microsoft/phi-4-multimodal-instruct", "phi")]
    public void The_other_family_of_a_two_family_vendor_still_resolves(string modelId, string expected) =>
        Assert.Equal(expected, InvokeGetModelFamily(modelId));

    /// <summary>Runs the private <c>GetModelFamily</c>, which takes the raw id rather than a model.</summary>
    private static string InvokeGetModelFamily(string modelId)
    {
        var getModelFamily = typeof(ModelConfigGeneratorService)
            .GetMethod("GetModelFamily", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)getModelFamily!.Invoke(null, [modelId])!;
    }

    /// <summary>Runs the private <c>MatchesFamily</c> over a minimal <see cref="ModelConfig"/>.</summary>
    private static bool InvokeMatchesFamily(string modelId, string family)
    {
        var model = new ModelConfig
        {
            Id = modelId,
            Capabilities = new ModelCapabilities
            {
                TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                SupportsStreaming = true,
            },
            Providers =
            [
                new ProviderConfig
                {
                    Name = "TestProvider",
                    ModelName = modelId,
                    Priority = 1,
                    Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                },
            ],
        };

        var matchesFamily = typeof(ModelConfigGeneratorService)
            .GetMethod("MatchesFamily", BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)matchesFamily!.Invoke(null, [model, family])!;
    }

    /// <summary>
    ///     Every family's regex transcribed as the literals it is an alternation of. A leading <c>^</c> marks
    ///     an alternative anchored to the start of the id rather than matched anywhere in it. Doubles as the
    ///     record of what each pattern is: an edit that alters a pattern diverges from this table.
    /// </summary>
    private static readonly (string Family, string[] Alternatives)[] FamilyAlternatives =
    [
        ("llama", ["llama"]),
        ("qwen", ["qwen", "alibaba"]),
        ("kimi", ["kimi", "moonshot"]),
        ("deepseek", ["deepseek"]),
        ("claude", ["claude", "anthropic"]),
        ("gpt", ["gpt", "openai"]),
        ("gemini", ["gemini"]),
        ("gemma", ["gemma"]),
        ("grok", ["grok", "xai"]),
        ("glm", ["glm", "thudm", "chatglm"]),
        ("openrouter", ["openrouter/", "cloaked"]),
        ("mistral", ["mistral"]),
        ("minimax", ["minimax"]),
        ("cohere", ["cohere", "^command"]),
        ("yi", ["yi-", "01-ai"]),
        ("phi", ["phi-"]),
        ("falcon", ["falcon"]),
        ("wizardlm", ["wizard"]),
        ("vicuna", ["vicuna"]),
        ("alpaca", ["alpaca"]),
        ("nous", ["nous", "hermes"]),
    ];

    private static readonly string[] ModelIdCorpus =
    [
        "meta-llama/llama-3.1-70b", "qwen/qwen-2.5-72b", "alibaba/tongyi-max", "moonshot/kimi-k2",
        "deepseek/deepseek-v3", "anthropic/claude-3-sonnet", "openai/gpt-4-turbo", "openai/o3-mini",
        "google/gemini-2.0-flash", "google/gemma-2-27b", "xai/grok-2", "thudm/chatglm-6b",
        "openrouter/auto", "cloaked/stealth-model", "mistralai/mistral-7b", "cohere/command-r-plus",
        "01-ai/yi-34b", "microsoft/phi-3-medium", "tiiuae/falcon-180b", "provider/wizardlm-2-8x22b",
        "lmsys/vicuna-13b", "provider/alpaca-7b", "nousresearch/nous-capybara", "teknium/hermes-3",
        "META-LLAMA/Llama-3.1-70B", "OpenAI/GPT-4O", "MoonShot/KIMI-K2", "Anthropic/CLAUDE-3-Opus",
        "some/entirely-unrelated-model", "openrouter-mirror/no-slash-here", "yi/no-hyphen",

        // Ids that distinguish an ANCHORED literal from its bare stem. Without these, widening `phi-` to
        // `phi` or `yi-` to `yi` — or narrowing `wizard` to `wizardlm` — is invisible: every other id in this
        // corpus matches both spellings, so the suite reports a pass while the classifier has changed. Found
        // by mutating `phi-` and watching nothing fail.
        "phind/model-34b",
        "beijing/haiyin-7b",
        "provider/wizard-vicuna-13b",

        // The `^command` anchor: bare ids are Cohere's, prefixed ones are somebody else's. Without both
        // spellings the anchor is invisible — every other id here matches the same either way.
        "command-r-plus",
        "provider/command-r-plus",
        "nvidia/command-center-7b",

        // A vendor id belonging to NEITHER family that vendor ships, for each of the two vendors whose
        // alternative was removed in #66. `google/gemma-2-27b` above already carries "google" without
        // "gemini"; this is the same discriminator on the microsoft side, carrying "microsoft" without
        // "phi-" and without "wizard". Re-adding either vendor alternative to a pattern turns regex True
        // against an ordinal False here. `microsoft/phi-3-medium` alone cannot see it — it contains both.
        "microsoft/orca-2",
    ];

    /// <summary>
    ///     The property that makes the removed <c>"en-US"</c> argument irrelevant, checked rather than argued.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Swapping a culture for <c>CultureInvariant</c> changes case-folding semantics in general, and a
    ///         family that quietly stopped matching would surface as a model classified <c>"unknown"</c> rather
    ///         than as any kind of error. A clean build proves nothing about that.
    ///     </para>
    ///     <para>
    ///         What this asserts is stronger and testable: over ASCII model ids, each family's regex agrees
    ///         EXACTLY with ordinal case-insensitive literal matching on the alternatives it is built from. A
    ///         matcher that is ordinal is one no culture participates in — so the culture argument could not
    ///         have been contributing anything, and removing it cannot have changed a classification.
    ///     </para>
    ///     <para>
    ///         Deliberately limited to ASCII ids, which is what model identifiers are. Whether en-US and
    ///         invariant folding differ on non-ASCII input is not decidable on a box with no ICU — every
    ///         culture there collapses to invariant, so a test comparing them would pass no matter what the
    ///         answer was. That claim is left unmade rather than faked.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_family_pattern_matches_exactly_what_ordinal_literal_matching_would()
    {
        var mismatches = new List<string>();

        foreach (var (family, alternatives) in FamilyAlternatives)
        {
            foreach (var modelId in ModelIdCorpus)
            {
                var viaRegex = InvokeMatchesFamily(modelId, family);
                var viaOrdinal = alternatives.Any(
                    alternative => alternative.StartsWith('^')
                        ? modelId.StartsWith(alternative[1..], StringComparison.OrdinalIgnoreCase)
                        : modelId.Contains(alternative, StringComparison.OrdinalIgnoreCase));

                if (viaRegex != viaOrdinal)
                {
                    mismatches.Add($"{family} / '{modelId}': regex={viaRegex}, ordinal={viaOrdinal}");
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void FilteringLogic_WithReasoningModels_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { ReasoningOnly = true };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.True(model.IsReasoning || model.HasCapability("thinking")));
    }

    [Fact]
    public void FilteringLogic_WithMultimodalModels_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { MultimodalOnly = true };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.True(model.HasCapability("multimodal")));
    }

    [Fact]
    public void FilteringLogic_WithMaxModels_ShouldLimitResults()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { MaxModels = 2 };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.True(result.Count <= 2);
    }

    [Fact]
    public void FilteringLogic_WithFamilyFilter_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { ModelFamilies = ["llama"] };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.Contains("llama", model.Id.ToLowerInvariant()));
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSince_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModelsWithDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2024, 6, 1) };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(
            result,
            model =>
            {
                Assert.True(model.CreatedDate.HasValue);
                Assert.True(model.CreatedDate.Value.Date >= new DateTime(2024, 6, 1).Date);
            }
        );
        Assert.Equal(2, result.Count); // Should exclude models created before June 1, 2024
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSinceAndOtherFilters_ShouldApplyAllFilters()
    {
        // Arrange
        var models = CreateTestModelsWithDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2024, 1, 1), ReasoningOnly = true };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(
            result,
            model =>
            {
                Assert.True(model.CreatedDate.HasValue);
                Assert.True(model.CreatedDate.Value.Date >= new DateTime(2024, 1, 1).Date);
                Assert.True(model.IsReasoning || model.HasCapability("thinking"));
            }
        );
        _ = Assert.Single(result); // Should only include reasoning models from 2024 onwards
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSinceExcludesModelsWithoutDates_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModelsWithMixedDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2024, 1, 1) };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.True(model.CreatedDate.HasValue));
        Assert.Equal(1, result.Count); // Should exclude models without dates and models before 2024
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSinceNoMatches_ShouldReturnEmpty()
    {
        // Arrange
        var models = CreateTestModelsWithDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2025, 1, 1) }; // Future date

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.Empty(result);
    }

    private static IReadOnlyList<ModelConfig> CreateTestModels()
    {
        return
        [
            new ModelConfig
            {
                Id = "meta-llama/llama-3.1-70b",
                IsReasoning = false,
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 131072, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["long-context"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "meta-llama/llama-3.1-70b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.5, CompletionPerMillion = 0.75 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "anthropic/claude-3-sonnet",
                IsReasoning = true,
                Capabilities = new ModelCapabilities
                {
                    Thinking = new ThinkingCapability
                    {
                        Type = ThinkingType.Anthropic,
                        SupportsBudgetTokens = true,
                        IsBuiltIn = false,
                        IsExposed = true,
                    },
                    Multimodal = new MultimodalCapability
                    {
                        SupportsImages = true,
                        SupportedImageFormats = ["jpeg", "png"],
                    },
                    TokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["thinking", "multimodal"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "Anthropic",
                        ModelName = "claude-3-sonnet-20240229",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 3.0, CompletionPerMillion = 15.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "openai/gpt-4-turbo",
                IsReasoning = false,
                Capabilities = new ModelCapabilities
                {
                    FunctionCalling = new FunctionCallingCapability
                    {
                        SupportsTools = true,
                        SupportsParallelCalls = true,
                        MaxToolsPerRequest = 128,
                    },
                    Multimodal = new MultimodalCapability
                    {
                        SupportsImages = true,
                        SupportedImageFormats = ["jpeg", "png", "webp"],
                    },
                    TokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["multimodal", "function-calling"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenAI",
                        ModelName = "gpt-4-turbo",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 10.0, CompletionPerMillion = 30.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "qwen/qwen-2.5-72b",
                IsReasoning = false,
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 32768, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["chat"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "qwen/qwen-2.5-72b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.4, CompletionPerMillion = 1.2 },
                    },
                ],
            },
        ];
    }

    private static IReadOnlyList<ModelConfig> CreateTestModelsWithDates()
    {
        return
        [
            new ModelConfig
            {
                Id = "meta-llama/llama-3.1-70b",
                IsReasoning = false,
                CreatedDate = new DateTime(2024, 7, 15), // After June 1
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 131072, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["long-context"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "meta-llama/llama-3.1-70b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.5, CompletionPerMillion = 0.75 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "anthropic/claude-3-sonnet",
                IsReasoning = true,
                CreatedDate = new DateTime(2024, 8, 20), // After June 1
                Capabilities = new ModelCapabilities
                {
                    Thinking = new ThinkingCapability
                    {
                        Type = ThinkingType.Anthropic,
                        SupportsBudgetTokens = true,
                        IsBuiltIn = false,
                        IsExposed = true,
                    },
                    TokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["thinking"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "Anthropic",
                        ModelName = "claude-3-sonnet-20240229",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 3.0, CompletionPerMillion = 15.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "openai/gpt-4-turbo",
                IsReasoning = false,
                CreatedDate = new DateTime(2024, 3, 10), // Before June 1
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenAI",
                        ModelName = "gpt-4-turbo",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 10.0, CompletionPerMillion = 30.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "qwen/qwen-2.5-72b",
                IsReasoning = false,
                CreatedDate = new DateTime(2023, 12, 5), // Before June 1
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 32768, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "qwen/qwen-2.5-72b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.4, CompletionPerMillion = 1.2 },
                    },
                ],
            },
        ];
    }

    private static IReadOnlyList<ModelConfig> CreateTestModelsWithMixedDates()
    {
        return
        [
            new ModelConfig
            {
                Id = "model-with-date",
                IsReasoning = false,
                CreatedDate = new DateTime(2024, 6, 15), // Has date after 2024
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "TestProvider",
                        ModelName = "model-with-date",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "model-without-date",
                IsReasoning = false,
                CreatedDate = null, // No date information
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "TestProvider",
                        ModelName = "model-without-date",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "old-model-with-date",
                IsReasoning = false,
                CreatedDate = new DateTime(2023, 5, 10), // Has date before 2024
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "TestProvider",
                        ModelName = "old-model-with-date",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                ],
            },
        ];
    }
}
