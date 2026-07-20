using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The provider-routing decision <see cref="LiveReviewAgentLoopFactory.ResolveReasoning"/> makes for one
/// review turn. This is the seam <see cref="LiveReviewAgentLoopFactory.Create"/> drives both the provider
/// agent selection AND the reasoning-request shape off, so asserting it here pins the per-call routing:
/// <c>gpt-*</c>/<c>o1|o3|o4</c> → an OpenAI Responses <c>Reasoning</c>; <c>claude-*</c> → an Anthropic
/// Messages <c>OutputConfig</c>; and, critically, the EFFECTIVE per-call model wins over the configured one
/// (so an A/B claude variant under a gpt-* primary — or the reverse — is routed correctly). No live Copilot
/// call is made — the decision is a pure function.
/// </summary>
public class LiveReviewAgentRoutingTests
{
    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.5-mini")]
    [InlineData("o1")]
    [InlineData("o3")]
    [InlineData("o4")]
    public void ResolveReasoning_OpenAiModel_AttachesReasoningWithEffortAndAutoSummary(string modelId)
    {
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId, configuredModelId: "claude-sonnet-5", effort: "medium");

        isOpenAi.Should().BeTrue();
        extra.Should().NotContainKey("OutputConfig");
        var reasoning = extra["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Subject;
        reasoning.Effort.Should().Be("medium");
        reasoning.Summary.Should().Be("auto");
    }

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-haiku-4.5")]
    [InlineData("claude-opus-4-8")]
    public void ResolveReasoning_ClaudeModel_AttachesOutputConfigEffort(string modelId)
    {
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId, configuredModelId: "gpt-5.5", effort: "high");

        isOpenAi.Should().BeFalse();
        extra.Should().NotContainKey("Reasoning");
        var outputConfig = extra["OutputConfig"].Should().BeOfType<AnthropicOutputConfig>().Subject;
        outputConfig.Effort.Should().Be("high");
    }

    [Fact]
    public void ResolveReasoning_PrimaryGptWithClaudeVariantOverride_RoutesToAnthropicShape()
    {
        // Regression: with gpt-5.5 configured as the primary, the A/B B arm passes VariantModelId
        // (claude-haiku-4.5) per call. The effective model — not the configured one — must decide, so the
        // claude variant is routed through the Anthropic Messages shape, never the OpenAI Responses one.
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId: "claude-haiku-4.5", configuredModelId: "gpt-5.5", effort: "");

        isOpenAi.Should().BeFalse();
        extra.Should().NotContainKey("Reasoning");
    }

    [Fact]
    public void ResolveReasoning_PrimaryClaudeWithGptVariantOverride_RoutesToOpenAiShape()
    {
        // The reverse mix: a gpt-* per-call override under a claude primary must route to the OpenAI
        // Responses reasoning shape.
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId: "gpt-5.5", configuredModelId: "claude-sonnet-5", effort: "low");

        isOpenAi.Should().BeTrue();
        extra.Should().NotContainKey("OutputConfig");
        var reasoning = extra["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Subject;
        reasoning.Effort.Should().Be("low");
        reasoning.Summary.Should().Be("auto");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveReasoning_NoPerCallModel_FallsBackToConfiguredVendor(string? modelId)
    {
        var (openAiFromGpt, gptExtra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId, configuredModelId: "gpt-5.5", effort: "medium");
        openAiFromGpt.Should().BeTrue();
        gptExtra.Should().ContainKey("Reasoning");

        var (openAiFromClaude, claudeExtra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId, configuredModelId: "claude-sonnet-5", effort: "medium");
        openAiFromClaude.Should().BeFalse();
        claudeExtra.Should().ContainKey("OutputConfig");
    }

    [Fact]
    public void ResolveReasoning_ClaudeWithEmptyEffort_OmitsOutputConfig()
    {
        // A non-adaptive model (e.g. haiku) REJECTS an effort it does not support, so an empty effort must
        // leave the request shape empty rather than attaching an OutputConfig.
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId: "claude-haiku-4.5", configuredModelId: "claude-sonnet-5", effort: "");

        isOpenAi.Should().BeFalse();
        extra.Should().BeEmpty();
    }

    [Fact]
    public void ResolveReasoning_OpenAiWithEmptyEffort_AttachesReasoningWithNullEffort()
    {
        // The OpenAI Responses shape is always attached (summary="auto"); an empty effort maps to a null
        // Effort so the provider applies its default rather than being sent a blank string.
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId: "gpt-5.5", configuredModelId: "gpt-5.5", effort: "");

        isOpenAi.Should().BeTrue();
        var reasoning = extra["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Subject;
        reasoning.Effort.Should().BeNull();
        reasoning.Summary.Should().Be("auto");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRequestModelId_NoPerCallModel_FallsBackToConfiguredModel(string? modelId)
    {
        // The knowledge-extraction loop passes modelId: null ("use the daemon default"). The request model
        // must then be the configured ReviewModelId — never an empty string, which the Copilot backend
        // rejects with model_not_supported (the achieveai Knowledge Base never populated because of this).
        LiveReviewAgentLoopFactory.ResolveRequestModelId(modelId, configuredModelId: "gpt-5.6-luna")
            .Should().Be("gpt-5.6-luna");
    }

    [Fact]
    public void ResolveRequestModelId_PerCallModel_WinsOverConfigured()
    {
        LiveReviewAgentLoopFactory.ResolveRequestModelId("claude-haiku-4.5", configuredModelId: "gpt-5.6-luna")
            .Should().Be("claude-haiku-4.5");
    }

    [Fact]
    public void ResolveRequestModelId_NoModelAndNoConfigured_IsEmptyNotNull()
    {
        // Degenerate (no model anywhere): return empty rather than null so GenerateReplyOptions.ModelId is
        // never null — the daemon has bigger problems, but this must not NRE.
        LiveReviewAgentLoopFactory.ResolveRequestModelId(null, configuredModelId: null).Should().BeEmpty();
    }

    [Fact]
    public void KnowledgeModel_ClaudeOpus_UnderGptDispatcher_RoutesToAnthropicAndSendsOpus()
    {
        // The at-close knowledge-extraction loop is created with KnowledgeModelId (claude-opus-4.8) while the
        // primary dispatcher is gpt-5.6-luna. The EFFECTIVE per-call model must win: extraction routes through
        // the Copilot Anthropic Messages shape and POSTs model=claude-opus-4.8 — NOT the gpt dispatcher's id,
        // and NOT an empty model (which the backend rejects with model_not_supported, the bug this fixes).
        var (isOpenAi, extra) = LiveReviewAgentLoopFactory.ResolveReasoning(
            modelId: "claude-opus-4.8", configuredModelId: "gpt-5.6-luna", effort: "medium");

        isOpenAi.Should().BeFalse("claude-* routes through Anthropic Messages, not OpenAI Responses");
        extra.Should().NotContainKey("Reasoning");
        extra.Should().ContainKey("OutputConfig");

        LiveReviewAgentLoopFactory.ResolveRequestModelId("claude-opus-4.8", configuredModelId: "gpt-5.6-luna")
            .Should().Be("claude-opus-4.8");
    }
}
