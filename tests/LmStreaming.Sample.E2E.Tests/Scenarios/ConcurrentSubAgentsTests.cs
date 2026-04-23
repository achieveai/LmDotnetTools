using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Parent emits two <c>Agent</c> tool calls in a single turn. Two sub-agents should
/// spawn, complete independently, and both results relay back before the parent produces
/// its final summary. The two sub-agents share a system-prompt marker but differ on a
/// tag (A/B) so each one picks from its own plan queue.
/// </summary>
public sealed class ConcurrentSubAgentsTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_spawns_two_subagents_concurrently(string providerMode)
    {
        const string WorkerAMarker = "You are worker-A sub-agent";
        const string WorkerBMarker = "You are worker-B sub-agent";

        var responder = ScriptedSseResponder.New()
            .ForRole("worker-a", ctx => ctx.SystemPromptContains(WorkerAMarker))
                .Turn(t => t.Text("worker-A done"))
            .ForRole("worker-b", ctx => ctx.SystemPromptContains(WorkerBMarker))
                .Turn(t => t.Text("worker-B done"))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCalls(
                    ("Agent", new { template_name = "worker_a", task = "do A" }),
                    ("Agent", new { template_name = "worker_b", task = "do B" })))
                .Turn(t => t.Text("Both workers finished."))
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
                    ["worker_a"] = new SubAgentTemplate
                    {
                        Name = "WorkerA",
                        SystemPrompt = WorkerAMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 3,
                    },
                    ["worker_b"] = new SubAgentTemplate
                    {
                        Name = "WorkerB",
                        SystemPrompt = WorkerBMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 3,
                    },
                },
                MaxConcurrentSubAgents = 5,
            });

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"concurrent-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("spawn two workers");
        var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var toolCalls = frames.ToolCallNames();
        toolCalls.Count(n => string.Equals(n, "Agent", StringComparison.Ordinal))
            .Should().BeGreaterOrEqualTo(2, "parent emitted two Agent tool calls");

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("Both workers finished");

        responder.RemainingTurns["worker-a"].Should().Be(0);
        responder.RemainingTurns["worker-b"].Should().Be(0);
    }
}
