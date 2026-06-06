using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Exercises the <c>SendMessage</c> tool: the parent first spawns a named sub-agent with
/// <c>Agent</c>, then continues that same sub-agent with a follow-up via <c>SendMessage</c>,
/// addressing it by the caller-chosen <c>name</c> rather than its generated id. Because both
/// calls are synchronous, each one's tool result is the sub-agent's final answer for that
/// round, and the sub-agent is restarted for the continuation (its first run had already
/// completed before the parent's follow-up turn).
/// </summary>
/// <remarks>
/// Addressing by <c>name</c> (a caller-supplied, static value) is what makes this scenario
/// scriptable: the generated <c>agent_id</c> is a runtime GUID that a static responder cannot
/// feed back into a follow-up call, but the <c>name</c> is known at scripting time. The
/// researcher role's two queued turns are consumed across the two synchronous runs (spawn +
/// continuation), so both drain deterministically — no background relay, no race.
/// </remarks>
public sealed class SendMessageTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_continues_named_subagent_via_send_message(string providerMode)
    {
        const string ResearcherMarker = "You are the research sub-agent";
        const string FirstAnswer = "Initial research done.";
        const string SecondAnswer = "Follow-up research done.";

        var responder = ScriptedSseResponder.New()
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherMarker))
                .Turn(t => t.Text(FirstAnswer))
                .Turn(t => t.Text(SecondAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "researcher",
                        prompt = "initial research",
                        name = "researcher",
                    }))
                .Turn(t => t.ToolCall(
                    "SendMessage",
                    new { target = "researcher", prompt = "dig deeper" }))
                .Turn(t => t.Text("Done: both rounds complete."))
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
                    ["researcher"] = new SubAgentTemplate
                    {
                        Name = "Researcher",
                        SystemPrompt = ResearcherMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 5,
                    },
                },
                MaxConcurrentSubAgents = 5,
            });

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"sendmessage-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("research then dig deeper");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var toolCalls = frames.ToolCallNames();
        toolCalls.Should().Contain("Agent");
        toolCalls.Should().Contain("SendMessage");

        // Each synchronous call returns the sub-agent's final answer for that round: the
        // spawn returns the first run's text, the SendMessage continuation returns the
        // restarted run's text. Both surface as Agent/SendMessage tool results.
        var toolResults = frames.ToolCallResults();
        toolResults.Should().Contain(
            r => r.Contains(FirstAnswer, StringComparison.Ordinal),
            "the Agent spawn result is the sub-agent's first-round answer");
        toolResults.Should().Contain(
            r => r.Contains(SecondAnswer, StringComparison.Ordinal),
            "the SendMessage continuation result is the restarted run's answer");

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("Done: both rounds complete");

        // Both parent turns (Agent + SendMessage + final text) and both researcher runs
        // (spawn + continuation) drained their queues — proving the continuation actually
        // restarted the same sub-agent and ran its second scripted turn.
        responder.RemainingTurns["parent"].Should().Be(0);
        responder.RemainingTurns["researcher"].Should().Be(0);
    }
}
