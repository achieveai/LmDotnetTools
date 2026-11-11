using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
///     The per-provider <c>LmStreaming:ReasoningReplay</c> switch (#769) reaches the Copilot Responses agent
///     the host really builds. The registry is replaced so no Copilot discovery runs, and the agent is only
///     built, never called, so no token is read.
/// </summary>
public sealed class ReasoningReplaySwitchTests
{
    private const string ModelId = "gpt-5.6-terra";

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void The_provider_factory_builds_the_agent_with_the_models_own_switch(string? value, bool replays)
    {
        var settings = new Dictionary<string, string?>();
        if (value is not null)
        {
            settings[$"LmStreaming:ReasoningReplay:{ModelId}"] = value;
        }

        using var factory = new E2EWebAppFactory(
            "test",
            new ScriptedBuilder(ScriptedSseResponder.New().Build().AsOpenAiHandler()),
            settings,
            services =>
            {
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(
                    new ProviderRegistry([
                        new CopilotModelInfo(
                            ModelId,
                            ModelId,
                            CopilotModelVendor.OpenAI,
                            CopilotModelTransport.Responses
                        ),
                    ])
                );
            }
        );

        var agent = factory.Services.GetRequiredService<Func<string, IStreamingAgent>>()(ModelId);

        agent.Should().BeOfType<OpenAiResponsesAgent>().Which.ReplaysReasoning.Should().Be(replays);
    }
}
