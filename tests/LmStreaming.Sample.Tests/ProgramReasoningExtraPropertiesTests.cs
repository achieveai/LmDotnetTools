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

        var thinking = props["Thinking"].Should().BeOfType<AnthropicThinking>().Which;
        thinking.Type.Should().Be("enabled");
        thinking.BudgetTokens.Should().NotBeNull("a classic model's request shape must not change (#709)");
    }

    [Fact]
    public void Copilot_adaptive_thinking_models_request_summarized_adaptive_thinking()
    {
        // Models advertising adaptive_thinking (e.g. claude-sonnet-5) reject thinking.type.enabled with
        // HTTP 400, so the classic budget request must NOT be sent for them. Sending nothing at all —
        // what this branch used to do — leaves Anthropic's `display` at its "omitted" default, which
        // returns thinking blocks with empty text; a Claude root conversation then renders blank
        // thinking pills (#709). The adaptive shape asking for a summary is what makes the text arrive.
        var props = Build("claude-sonnet-5", CopilotModelTransport.Anthropic, copilotSupportsAdaptiveThinking: true);

        var thinking = props["Thinking"].Should().BeOfType<AnthropicThinking>().Which;
        thinking.Type.Should().Be("adaptive");
        thinking.Display.Should().Be("summarized");
        thinking.BudgetTokens.Should().BeNull();
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
    public void Conversation_root_explicit_xhigh_is_capability_shaped_for_responses_models()
    {
        var registry = RegistryWith(
            new CopilotModelInfo("gpt-5.6-sol", "Sol", CopilotModelVendor.OpenAI, CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            }
        );

        var props = BuildConversationRoot(registry, "gpt-5.6-sol", "gpt-5.6-sol", "xhigh");

        var reasoning = props["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which;
        reasoning.Effort.Should().Be("xhigh");
        reasoning.Summary.Should().Be("auto");
    }

    [Fact]
    public void Conversation_root_xhigh_is_clamped_and_reported_as_the_exact_provider_effort()
    {
        var registry = RegistryWith(
            new CopilotModelInfo("gpt-5.6-terra", "Terra", CopilotModelVendor.OpenAI, CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high"],
            }
        );

        var props = BuildConversationRoot(registry, "gpt-5.6-terra", "gpt-5.6-terra", "xhigh");

        ReadConversationRootShapedEffort(props).Should().Be("high");
    }

    [Fact]
    public void Conversation_root_explicit_empty_omits_reasoning_metadata()
    {
        var registry = RegistryWith(
            new CopilotModelInfo("gpt-5.6-sol", "Sol", CopilotModelVendor.OpenAI, CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            }
        );

        BuildConversationRoot(registry, "gpt-5.6-sol", "gpt-5.6-sol", string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Conversation_root_explicit_empty_still_requests_summarized_display_on_adaptive_models()
    {
        // Opting out of an EFFORT request must not also opt out of thinking DISPLAY (#709): an adaptive
        // model left at display="omitted" returns thinking blocks with empty text either way, so the
        // conversation would render blank pills while the user only asked not to nudge the effort.
        var registry = RegistryWith(
            new CopilotModelInfo(
                "claude-sonnet-5",
                "Claude Sonnet 5",
                CopilotModelVendor.Anthropic,
                CopilotModelTransport.Anthropic,
                SupportsAdaptiveThinking: true
            )
            {
                ReasoningEfforts = ["low", "medium", "high"],
            }
        );

        var props = BuildConversationRoot(registry, "claude-sonnet-5", "claude-sonnet-5", string.Empty);

        props["Thinking"].Should().BeOfType<AnthropicThinking>().Which.Display.Should().Be("summarized");
        props.Should().NotContainKey("OutputConfig", "the conversation explicitly opted out of an effort");
    }

    [Fact]
    public void Conversation_root_invalid_persisted_effort_preserves_the_provider_default_shape()
    {
        var registry = RegistryWith(
            new CopilotModelInfo("gpt-5.6-sol", "Sol", CopilotModelVendor.OpenAI, CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            }
        );

        var props = BuildConversationRoot(registry, "gpt-5.6-sol", "gpt-5.6-sol", "turbo");

        var reasoning = props["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which;
        reasoning.Effort.Should().BeNull();
        reasoning
            .Summary.Should()
            .Be("auto", "a legacy invalid row must not suppress the provider's default reasoning shape");
    }

    [Fact]
    public void Conversation_root_explicit_effort_on_non_copilot_anthropic_preserves_the_thinking_budget()
    {
        var props = BuildConversationRoot(RegistryWith(), "anthropic", "anthropic", "xhigh");

        props.Should().ContainKey("Thinking");
        props["Thinking"].Should().BeOfType<AnthropicThinking>();
        props.Should().NotContainKey("OutputConfig", "direct Anthropic capabilities were not advertised for shaping");
    }

    [Fact]
    public void Conversation_root_null_preserves_the_existing_provider_default_shape()
    {
        var registry = RegistryWith(
            new CopilotModelInfo("gpt-5.6-sol", "Sol", CopilotModelVendor.OpenAI, CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            }
        );

        var props = BuildConversationRoot(registry, "gpt-5.6-sol", "gpt-5.6-sol", requestedEffort: null);

        var reasoning = props["Reasoning"].Should().BeOfType<ResponseReasoningOptions>().Which;
        reasoning.Effort.Should().BeNull();
        reasoning.Summary.Should().Be("auto");
    }

    private static string? ReadConversationRootShapedEffort(ImmutableDictionary<string, object?> properties)
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod("ReadShapedReasoningEffort", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("Program must report the exact effort placed on the root provider request");
        return (string?)method!.Invoke(null, [properties]);
    }

    private static ImmutableDictionary<string, object?> BuildConversationRoot(
        ProviderRegistry providerRegistry,
        string copilotModelKey,
        string fallbackProviderId,
        string? requestedEffort
    )
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod(
            "BuildConversationReasoningExtraProperties",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        method.Should().NotBeNull("Program must expose conversation-root reasoning shaping");
        return (ImmutableDictionary<string, object?>)
            method!.Invoke(null, [providerRegistry, copilotModelKey, fallbackProviderId, requestedEffort, null])!;
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
