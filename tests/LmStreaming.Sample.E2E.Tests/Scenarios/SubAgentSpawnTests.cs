using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Exercises the parent → <c>SubAgentManager</c> → sub-agent fan-out path under scripted
/// SSE. The parent's first turn emits an <c>Agent</c> tool call that spawns a sub-agent;
/// the sub-agent returns a plain-text result which <c>SubAgentManager</c> relays back into
/// the parent as a user message; the parent's second turn summarizes the relayed result.
/// </summary>
public sealed class SubAgentSpawnTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_spawns_subagent_and_relays_result(string providerMode)
    {
        const string ResearcherPromptMarker = "You are the research sub-agent";

        var responder = ScriptedSseResponder.New()
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherPromptMarker))
                .Turn(t => t.Text("Found three fresh AI papers today."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new { template_name = "researcher", task = "Find AI papers" }))
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
