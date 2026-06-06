using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Exercises the parent → <c>SubAgentManager</c> → sub-agent fan-out path under scripted
/// SSE. The parent's first turn emits an <c>Agent</c> tool call that spawns a sub-agent;
/// the call is synchronous, so the sub-agent's plain-text answer comes back as the
/// <c>Agent</c> tool result (no parent relay) and the parent's second turn summarizes it.
/// </summary>
public sealed class SubAgentSpawnTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_spawns_subagent_and_receives_result(string providerMode)
    {
        const string ResearcherPromptMarker = "You are the research sub-agent";

        var responder = ScriptedSseResponder.New()
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherPromptMarker))
                .Turn(t => t.Text("Found three fresh AI papers today."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new { subagent_type = "researcher", prompt = "Find AI papers" }))
                .Turn(t => t.Text("Summary: researcher surfaced three AI papers."))
            .Build();

        var handler = providerMode == "test-anthropic"
            ? responder.AsAnthropicHandler()
            : responder.AsOpenAiHandler();

        var builder = new ScriptedBuilder(
            handler,
            subAgentFactory: (_, providerAgentFactory) => BuildSubAgentOptions(
                ResearcherPromptMarker,
                providerAgentFactory));

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("research AI papers for me");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var toolCalls = frames.ToolCallNames();
        toolCalls.Should().Contain("Agent");

        // Synchronous Agent: the sub-agent's final text comes back as the tool result
        // (not a JSON spawn receipt), proving the parent blocked on completion.
        var toolResults = frames.ToolCallResults();
        toolResults.Should().Contain(
            r => r.Contains("Found three fresh AI papers today.", StringComparison.Ordinal),
            "the Agent tool result is the sub-agent's final answer");

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("Summary: researcher surfaced three AI papers");

        responder.RemainingTurns["parent"].Should().Be(0);
        responder.RemainingTurns["researcher"].Should().Be(0);
    }

    private static SubAgentOptions BuildSubAgentOptions(
        string researcherPrompt,
        Func<IStreamingAgent> providerAgentFactory)
    {
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["researcher"] = new SubAgentTemplate
            {
                Name = "Researcher",
                SystemPrompt = researcherPrompt,
                AgentFactory = providerAgentFactory,
                MaxTurnsPerRun = 5,
            },
        };

        return new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = 5,
        };
    }
}
