using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC4 — Recursive instruction-chain scenario per PR #9 reviewer comment. Parent's
/// scripted plan spawns a sub-agent via the <c>Agent</c> tool. The sub-agent's own
/// instruction chain performs multiple steps (tool call -> follow-up text -> final summary)
/// before returning control to the parent, which emits a final summary text.
/// </summary>
/// <remarks>
/// This exercises both the parent and the sub-agent's instruction chains through the
/// full WebSocket -> renderer path — the gap PR #9 explicitly left open. Transport-level
/// correctness is covered by <c>LmStreaming.Sample.E2E.Tests</c>; this test asserts the
/// browser-visible outcome only.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class RecursiveInstructionChainTests
{
    private readonly PlaywrightFixture _fixture;

    public RecursiveInstructionChainTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_and_sub_agent_instruction_chains_render_end_to_end(string providerMode)
    {
        const string WeatherSubMarker = "You are the weather sub-agent";

        var responder = ScriptedSseResponder
            .New()
            .ForRole("weather-sub", ctx => ctx.SystemPromptContains(WeatherSubMarker))
            // Sub-agent instruction chain: tool call -> follow-up text -> final summary.
            .Turn(t => t.ToolCall("get_weather", new { location = "Seattle" }))
            .Turn(t => t.Text("Observed Seattle weather: 55F, light rain."))
            .Turn(t => t.Text("Sub-agent summary: Seattle is rainy."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            // Parent instruction chain: spawn sub-agent -> summarize.
            .Turn(t => t.ToolCall("Agent", new { template_name = "weather_sub", task = "check Seattle weather" }))
            .Turn(t => t.Text("Parent final: sub-agent confirmed Seattle is rainy."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["weather_sub"] = new SubAgentTemplate
                        {
                            Name = "WeatherSub",
                            SystemPrompt = WeatherSubMarker,
                            AgentFactory = providerAgentFactory,
                            MaxTurnsPerRun = 5,
                        },
                    },
                    MaxConcurrentSubAgents = 5,
                }
        );
        var page = session.Page;

        await page.SendMessageAsync("use the weather sub-agent for Seattle");
        await page.WaitForStreamIdleAsync(timeoutMs: 45_000);

        // Parent's Agent tool-call pill must render.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent");

        // Parent's final summary is the user-visible end-to-end result.
        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", assistantTexts).Should().Contain("Parent final");
        string.Join(" ", assistantTexts).Should().Contain("Seattle is rainy");

        // Sub-agent's tool-call turn must be consumed; later sub-agent turns may be
        // short-circuited by the Agent-relay termination policy (see SubAgentToolUseTests
        // in the transport suite for the exact invariant).
        responder
            .RemainingTurns["weather-sub"]
            .Should()
            .BeLessThan(3, "sub-agent should have consumed at least its tool-call turn");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync($"RecursiveChain.Parent_and_sub_agent_instruction_chains_render_end_to_end_{providerMode}");
    }
}
