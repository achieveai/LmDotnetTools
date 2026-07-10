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
            result[expectedKey].Should().BeEquivalentTo(new ResponseReasoningOptions { Effort = "medium" });
        }
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

        result["Reasoning"].Should().BeEquivalentTo(new ResponseReasoningOptions { Effort = expected });
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
