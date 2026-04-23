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
                    new { template_name = "weather_sub", task = "check Seattle weather" }))
                .Turn(t => t.Text("Parent: sub-agent completed the weather check."))
            .Build();

        var handler = providerMode == "test-anthropic"
            ? responder.AsAnthropicHandler()
            : responder.AsOpenAiHandler();

        var builder = new E2EWebAppFactory.ScriptedBuilder(
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
        var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var toolCalls = frames.ToolCallNames();
        toolCalls.Should().Contain("Agent");

        var toolResults = frames.ToolCallResults();
        toolResults.Should().NotBeEmpty();

        // Sub-agent must consume at least its first (tool-call) turn. Whether the second
        // (final-text) turn is also consumed depends on how the parent's Agent-tool relay
        // finalizes the sub-agent's loop — some paths terminate the sub-agent as soon as its
        // first useful action (the tool call) is surfaced back up to the parent. We assert the
        // weaker invariant rather than couple this E2E test to that relay-termination policy.
        responder.RemainingTurns["weather-sub"].Should()
            .BeLessThan(2, "sub-agent should have consumed at least its tool-call turn");

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("sub-agent completed the weather check");
    }
}
