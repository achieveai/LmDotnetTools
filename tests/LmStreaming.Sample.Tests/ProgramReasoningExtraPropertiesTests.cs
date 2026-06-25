using System.Collections.Immutable;
using System.Reflection;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Regression coverage for the provider → reasoning/thinking extra-properties wiring in Program.
///     This is what makes thinking blocks appear for Copilot-backed models: the Anthropic-format
///     providers (incl. sonnet/haiku via Copilot) must get a "Thinking" budget, and the OpenAI
///     Responses providers (gpt-5.5/gpt-5.5-mini) must get a "Reasoning" summary request. Without
///     this test, deleting either provider-id branch would silently turn thinking back off.
/// </summary>
public sealed class ProgramReasoningExtraPropertiesTests
{
    private static ImmutableDictionary<string, object?> Build(string normalizedProviderId)
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController)
            .Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod(
            "BuildReasoningExtraProperties",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        method.Should().NotBeNull("Program must expose the provider→reasoning extra-properties helper");
        return (ImmutableDictionary<string, object?>)method!.Invoke(null, [normalizedProviderId])!;
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("test-anthropic")]
    [InlineData("sonnet")]
    [InlineData("haiku")]
    public void Anthropic_format_providers_get_thinking_budget(string providerId)
    {
        var props = Build(providerId);

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.5-mini")]
    public void OpenAi_responses_providers_get_reasoning_summary(string providerId)
    {
        var props = Build(providerId);

        props.Should().ContainKey("Reasoning");
        props["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which.Summary.Should().Be("auto");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("codex")]
    [InlineData("test")]
    public void Other_providers_get_no_reasoning_extra_properties(string providerId)
    {
        var props = Build(providerId);

        props.Should().BeEmpty();
    }
}
