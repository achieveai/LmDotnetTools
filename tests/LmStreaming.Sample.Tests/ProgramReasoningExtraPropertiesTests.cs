using System.Collections.Immutable;
using System.Reflection;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Regression coverage for the provider → reasoning/thinking extra-properties wiring in Program.
///     This is what makes thinking blocks appear for Copilot-backed models: the Anthropic-format
///     providers (the direct anthropic/test-anthropic providers and any discovered Copilot model on
///     the Anthropic transport) must get a "Thinking" budget, and Copilot models on the OpenAI
///     Responses transport must get a "Reasoning" summary request. Without this test, deleting either
///     branch would silently turn thinking back off.
/// </summary>
public sealed class ProgramReasoningExtraPropertiesTests
{
    private static ImmutableDictionary<string, object?> Build(
        string normalizedProviderId,
        CopilotModelTransport? copilotTransport = null)
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController)
            .Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod(
            "BuildReasoningExtraProperties",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        method.Should().NotBeNull("Program must expose the provider→reasoning extra-properties helper");
        return (ImmutableDictionary<string, object?>)method!.Invoke(null, [normalizedProviderId, copilotTransport])!;
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("test-anthropic")]
    public void Anthropic_format_providers_get_thinking_budget(string providerId)
    {
        var props = Build(providerId);

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
    }

    [Fact]
    public void Copilot_anthropic_transport_models_get_thinking_budget()
    {
        var props = Build("claude-sonnet-5", CopilotModelTransport.Anthropic);

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
    }

    [Fact]
    public void Copilot_responses_transport_models_get_reasoning_summary()
    {
        var props = Build("gpt-5.5", CopilotModelTransport.Responses);

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
