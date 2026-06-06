using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// A sub-agent, after being spawned by the parent, invokes one of the sample tools
/// (<c>get_weather</c>) via the standard tool pipeline before producing its final text
/// output. Verifies that sub-agents share the parent's tool registry (the scripted
/// <c>get_weather</c> turn is consumed from the sub-agent's plan queue) and that the
/// parent's summary follows after the <c>Agent</c> tool relay.
/// </summary>
/// <remarks>
/// Sub-agent tool-call / result events run inside the sub-agent's isolated multi-turn loop
/// and do not propagate to the client WebSocket — only the parent's <c>Agent</c> tool call
/// and its aggregated result are streamed. We therefore verify sub-agent execution via the
/// scripted queue's <c>RemainingTurns</c> count rather than inspecting the client frames.
/// </remarks>
public sealed class SubAgentToolUseTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task SubAgent_invokes_parent_tool_and_finishes(string providerMode)
    {
        const string WeatherSubAgentMarker = "You are the weather sub-agent";

        var responder = ScriptedSseResponder.New()
            .ForRole("weather-sub", ctx => ctx.SystemPromptContains(WeatherSubAgentMarker))
                .Turn(t => t.ToolCall("get_weather", new { location = "Seattle" }))
                .Turn(t => t.Text("Seattle weather captured."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new { subagent_type = "weather_sub", prompt = "check Seattle weather" }))
                .Turn(t => t.Text("Parent: sub-agent completed the weather check."))
            .Build();

        var handler = providerMode == "test-anthropic"
            ? responder.AsAnthropicHandler()
            : responder.AsOpenAiHandler();

        var builder = new ScriptedBuilder(
            handler,
            subAgentFactory: (_, providerAgentFactory) => new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["weather_sub"] = new SubAgentTemplate
                    {
                        Name = "WeatherSub",
                        SystemPrompt = WeatherSubAgentMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 5,
                    },
                },
                MaxConcurrentSubAgents = 5,
            });

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-tool-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("use a weather sub-agent for Seattle");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var toolCalls = frames.ToolCallNames();
        toolCalls.Should().Contain("Agent");

        var toolResults = frames.ToolCallResults();
        toolResults.Should().NotBeEmpty();

        // Synchronous Agent: the parent blocks until the sub-agent's run COMPLETES, so by the
        // time the parent produces its summary the sub-agent has deterministically consumed
        // BOTH of its turns (the get_weather tool call AND the final text). No background relay,
        // no race — the queue is fully drained.
        responder.RemainingTurns["weather-sub"].Should()
            .Be(0, "the parent awaits the sub-agent's full run, draining both of its turns");

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("sub-agent completed the weather check");
    }
}
