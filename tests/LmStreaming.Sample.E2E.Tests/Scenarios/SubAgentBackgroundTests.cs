using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Exercises the <c>run_in_background: true</c> path of the <c>Agent</c> tool. Unlike the
/// default synchronous spawn (which blocks and returns the sub-agent's final text), a
/// background spawn returns a JSON receipt — <c>{ agent_id, name, template, status:"spawned" }</c>
/// — immediately, before the sub-agent has produced any answer. The parent then continues
/// without waiting. This is the receipt shape a parent would later poll with <c>CheckAgent</c>.
/// </summary>
/// <remarks>
/// The runtime <c>agent_id</c> is a GUID minted at spawn time, so a static scripted responder
/// cannot feed it back into a follow-up <c>CheckAgent</c> call — that dynamic poll loop is
/// covered by the in-process integration test (<c>SubAgentIntegrationTests</c>). Here we assert
/// the deterministic fact: the background spawn's tool result is the receipt, NOT the answer.
/// </remarks>
public sealed class SubAgentBackgroundTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Background_spawn_returns_receipt_not_answer(string providerMode)
    {
        const string WorkerMarker = "You are the background worker sub-agent";
        const string SubAgentAnswer = "Background work finished with secret payload.";

        var responder = ScriptedSseResponder.New()
            .ForRole("bg-worker", ctx => ctx.SystemPromptContains(WorkerMarker))
                .Turn(t => t.Text(SubAgentAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "bg_worker",
                        prompt = "do background work",
                        name = "bg1",
                        run_in_background = true,
                    }))
                .Turn(t => t.Text("Kicked off the background worker."))
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
                    ["bg_worker"] = new SubAgentTemplate
                    {
                        Name = "BackgroundWorker",
                        SystemPrompt = WorkerMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 5,
                    },
                },
                MaxConcurrentSubAgents = 5,
            });

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-bg-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("spawn a background worker");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var toolCalls = frames.ToolCallNames();
        toolCalls.Should().Contain("Agent");

        // The background spawn's tool result is a JSON receipt, returned synchronously before
        // the sub-agent finishes — so it carries the spawn metadata, NOT the sub-agent's answer.
        var receipt = frames.ToolCallResults()
            .FirstOrDefault(r => r.Contains("agent_id", StringComparison.Ordinal));
        receipt.Should().NotBeNull("background spawn returns a JSON receipt with an agent id");

        using (var doc = JsonDocument.Parse(receipt!))
        {
            var root = doc.RootElement;
            root.GetProperty("agent_id").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("name").GetString().Should().Be("bg1");
            root.GetProperty("template").GetString().Should().Be("bg_worker");
            root.GetProperty("status").GetString().Should().Be("spawned");
        }

        // The receipt must NOT contain the sub-agent's eventual answer — that only flows back
        // later (relayed to the parent for background spawns), never as the spawn tool result.
        receipt!.Should().NotContain(SubAgentAnswer);

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("Kicked off the background worker");
    }
}
