using System.Collections.Immutable;
using System.Reflection;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

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
        CopilotModelTransport? copilotTransport = null,
        bool copilotSupportsAdaptiveThinking = false
    )
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod(
            "BuildReasoningExtraProperties",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        method.Should().NotBeNull("Program must expose the provider→reasoning extra-properties helper");
        return (ImmutableDictionary<string, object?>)
            method!.Invoke(null, [normalizedProviderId, copilotTransport, copilotSupportsAdaptiveThinking])!;
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
        var props = Build("claude-sonnet-4.5", CopilotModelTransport.Anthropic);

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
    }

    [Fact]
    public void Copilot_adaptive_thinking_models_omit_classic_thinking_budget()
    {
        // Models advertising adaptive_thinking (e.g. claude-sonnet-5) reject thinking.type.enabled with
        // HTTP 400, so the classic budget request must NOT be sent for them.
        var props = Build("claude-sonnet-5", CopilotModelTransport.Anthropic, copilotSupportsAdaptiveThinking: true);

        props.Should().NotContainKey("Thinking");
        props.Should().BeEmpty();
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

    // ---- Controller reasoning shaping (BuildControllerReasoningExtraProperties) ----
    // The controller inherits the parent's thinking at a fixed High floor (Option A), shaped for the
    // controller model's own transport. A Copilot model that advertises efforts is shaped directly
    // (OutputConfig / Reasoning); a classic Copilot Anthropic model (no advertised efforts) and a
    // non-Copilot provider fall back to the plain provider→reasoning wiring.

    private static ImmutableDictionary<string, object?> BuildController(
        ProviderRegistry providerRegistry,
        string copilotModelKey,
        string fallbackProviderId,
        ReasoningEffort effort = ReasoningEffort.High
    )
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod(
            "BuildControllerReasoningExtraProperties",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        method.Should().NotBeNull("Program must expose the controller reasoning shaping helper");
        return (ImmutableDictionary<string, object?>)
            method!.Invoke(null, [providerRegistry, copilotModelKey, fallbackProviderId, effort])!;
    }

    private static ProviderRegistry RegistryWith(params CopilotModelInfo[] copilotModels) =>
        new(new FakeFileSystemProbe(), () => false, copilotModels);

    [Fact]
    public void Controller_effort_advertising_copilot_anthropic_model_gets_output_config_effort()
    {
        var registry = RegistryWith(
            new CopilotModelInfo(
                "claude-opus-4.8",
                "Claude Opus 4.8",
                CopilotModelVendor.Anthropic,
                CopilotModelTransport.Anthropic,
                SupportsAdaptiveThinking: true
            )
            {
                ReasoningEfforts = ["low", "medium", "high"],
            }
        );

        var props = BuildController(registry, "claude-opus-4.8", "claude-opus-4.8");

        props.Should().ContainKey("OutputConfig");
        props["OutputConfig"].Should().BeOfType<AnthropicOutputConfig>().Which.Effort.Should().Be("high");
    }

    [Fact]
    public void Controller_effort_advertising_copilot_responses_model_gets_reasoning_effort()
    {
        var registry = RegistryWith(
            new CopilotModelInfo("gpt-5.5", "GPT-5.5", CopilotModelVendor.OpenAI, CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high"],
            }
        );

        var props = BuildController(registry, "gpt-5.5", "gpt-5.5");

        props.Should().ContainKey("Reasoning");
        var reasoning = props["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which;
        reasoning.Effort.Should().Be("high");
        // The controller must also request a displayable summary, or its thinking comes back
        // encrypted-only and never renders (the sub-agent/controller "no thinking traces" bug).
        reasoning.Summary.Should().Be("auto");
    }

    [Fact]
    public void Controller_classic_copilot_anthropic_model_falls_back_to_thinking_budget()
    {
        // A classic Copilot Anthropic model advertises no selectable effort, so Copilot shaping is
        // empty; the controller must still inherit the classic Thinking budget for that transport.
        var registry = RegistryWith(
            new CopilotModelInfo(
                "claude-sonnet-4.5",
                "Claude Sonnet 4.5",
                CopilotModelVendor.Anthropic,
                CopilotModelTransport.Anthropic
            )
        );

        var props = BuildController(registry, "claude-sonnet-4.5", "claude-sonnet-4.5");

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
    }

    [Fact]
    public void Controller_non_copilot_anthropic_provider_gets_thinking_budget()
    {
        var registry = RegistryWith();

        var props = BuildController(registry, "anthropic", "anthropic");

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
    }

    [Fact]
    public void Controller_non_copilot_openai_provider_gets_no_reasoning()
    {
        var registry = RegistryWith();

        var props = BuildController(registry, "openai", "openai");

        props.Should().BeEmpty();
    }
}
