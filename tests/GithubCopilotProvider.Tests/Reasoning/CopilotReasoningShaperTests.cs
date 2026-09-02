using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Reasoning;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Reasoning;

public sealed class CopilotReasoningShaperTests
{
    [Theory]
    [InlineData(CopilotModelTransport.Anthropic, "OutputConfig")]
    [InlineData(CopilotModelTransport.Responses, "Reasoning")]
    public void Shape_uses_transport_specific_request_metadata(CopilotModelTransport transport, string expectedKey)
    {
        var model = CreateModel(transport, supportsAdaptiveThinking: false, "low", "medium", "high");

        var result = CopilotReasoningShaper.Shape(model, ReasoningEffort.Medium);

        result.Should().ContainSingle().Which.Key.Should().Be(expectedKey);
        if (transport == CopilotModelTransport.Anthropic)
        {
            result[expectedKey].Should().BeEquivalentTo(new AnthropicOutputConfig { Effort = "medium" });
        }
        else
        {
            result[expectedKey]
                .Should()
                .BeEquivalentTo(new ResponseReasoningOptions { Effort = "medium", Summary = "auto" });
        }
    }

    [Fact]
    public void Shape_requests_displayable_summary_for_responses_transport()
    {
        // Regression: a Responses-transport reasoning request MUST also ask for a displayable summary
        // (summary="auto"). Without it the provider returns ONLY the encrypted reasoning item, which maps
        // to ReasoningVisibility.Encrypted (GetDisplayText() == null) — so workflow-controller and
        // sub-agent thinking never renders even though the main chat's does. The Anthropic branch has no
        // summary concept; it carries effort via OutputConfig only.
        var model = CreateModel(
            CopilotModelTransport.Responses,
            supportsAdaptiveThinking: false,
            "low",
            "medium",
            "high"
        );

        var result = CopilotReasoningShaper.Shape(model, ReasoningEffort.High);

        var reasoning = result["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which;
        reasoning.Effort.Should().Be("high");
        reasoning.Summary.Should().Be("auto");
    }

    [Fact]
    public void Shape_requests_summarized_thinking_for_adaptive_anthropic_models()
    {
        // Regression (#709): an adaptive-thinking Claude model returns a thinking block whose text is
        // EMPTY unless the request opts into `display: "summarized"` — Anthropic's default display is
        // "omitted". Sending output_config.effort alone (what this arm used to do) is what left every
        // Claude thinking pill blank.
        var model = CreateModel(CopilotModelTransport.Anthropic, supportsAdaptiveThinking: true, "low", "high");

        var result = CopilotReasoningShaper.Shape(model, ReasoningEffort.High);

        result["OutputConfig"].Should().BeEquivalentTo(new AnthropicOutputConfig { Effort = "high" });
        var thinking = result["Thinking"].Should().BeOfType<AnthropicThinking>().Which;
        thinking.Type.Should().Be("adaptive");
        thinking.Display.Should().Be("summarized");
        // Adaptive models reject budget_tokens outright, so it must never ride along.
        thinking.BudgetTokens.Should().BeNull();
    }

    [Fact]
    public void Shape_omits_thinking_for_classic_anthropic_models()
    {
        // Classic Claude models reject thinking.type.adaptive; their budget-based thinking is wired
        // by the host's provider defaults, not by effort shaping. This arm must stay effort-only.
        var model = CreateModel(CopilotModelTransport.Anthropic, supportsAdaptiveThinking: false, "low", "high");

        var result = CopilotReasoningShaper.Shape(model, ReasoningEffort.High);

        result.Should().NotContainKey("Thinking");
        result.Should().ContainSingle().Which.Key.Should().Be("OutputConfig");
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low,medium,high", "low")]
    [InlineData(ReasoningEffort.High, "low,medium,high", "high")]
    [InlineData(ReasoningEffort.Xhigh, "low,medium,high", "high")]
    [InlineData(ReasoningEffort.Low, "medium,high", "medium")]
    [InlineData(ReasoningEffort.High, "unknown,none,minimal,max", "minimal")]
    [InlineData(ReasoningEffort.Low, "none,max", "none")]
    [InlineData(ReasoningEffort.Xhigh, "low,xhigh,max", "xhigh")]
    public void Shape_selects_supported_effort(ReasoningEffort requested, string advertised, string expected)
    {
        var model = CreateModel(
            CopilotModelTransport.Responses,
            supportsAdaptiveThinking: false,
            advertised.Split(',')
        );

        var result = CopilotReasoningShaper.Shape(model, requested);

        result["Reasoning"]
            .Should()
            .BeEquivalentTo(new ResponseReasoningOptions { Effort = expected, Summary = "auto" });
    }

    [Theory]
    [InlineData(ReasoningEffort.Xhigh, "low,medium,high", "high")]
    [InlineData(ReasoningEffort.Low, "medium,high", "medium")]
    [InlineData(ReasoningEffort.High, "max", null)]
    public void SelectEffort_reports_provider_owned_selection(
        ReasoningEffort requested,
        string advertised,
        string? expected
    )
    {
        var model = CreateModel(
            CopilotModelTransport.Responses,
            supportsAdaptiveThinking: false,
            advertised.Split(',')
        );

        var selected = CopilotReasoningShaper.SelectEffort(model, requested);

        selected.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "low")]
    [InlineData(ReasoningEffort.Low, "")]
    [InlineData(ReasoningEffort.High, "max")]
    [InlineData(ReasoningEffort.High, "unknown,max")]
    public void Shape_omits_request_metadata_when_effort_cannot_be_shaped(ReasoningEffort? requested, string advertised)
    {
        var efforts = string.IsNullOrEmpty(advertised) ? [] : advertised.Split(',');
        var model = CreateModel(CopilotModelTransport.Responses, supportsAdaptiveThinking: true, efforts);

        CopilotReasoningShaper.Shape(model, requested).Should().BeEmpty();
    }

    [Fact]
    public void Shape_omits_request_metadata_for_unsupported_transport()
    {
        var model = CreateModel(CopilotModelTransport.Unsupported, supportsAdaptiveThinking: true, "low");

        CopilotReasoningShaper.Shape(model, ReasoningEffort.Low).Should().BeEmpty();
    }

    [Fact]
    public void SelectEffort_UnknownEnumValueFallsBackWithoutThrowing()
    {
        var model = CreateModel(CopilotModelTransport.Responses, supportsAdaptiveThinking: true, "low", "medium");

        var selected = CopilotReasoningShaper.SelectEffort(model, (ReasoningEffort)999);

        selected.Should().BeNull();
    }

    private static CopilotModelInfo CreateModel(
        CopilotModelTransport transport,
        bool supportsAdaptiveThinking,
        params string[] reasoningEfforts
    )
    {
        return new CopilotModelInfo(
            "test-model",
            "Test Model",
            transport == CopilotModelTransport.Anthropic ? CopilotModelVendor.Anthropic : CopilotModelVendor.OpenAI,
            transport,
            supportsAdaptiveThinking
        )
        {
            ReasoningEfforts = reasoningEfforts,
        };
    }
}
