using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Covers <see cref="UserInput.SuppressSubAgentSpawning"/>: a single input can forbid the run it drives from
/// starting NEW sub-agents, without touching the thread's standing configuration. This is what lets a caller
/// that has just waited on a sub-agent completion barrier ask for a synthesis turn that cannot re-open the
/// barrier it waited on — while reading from and following up with the children it already has stays available.
/// </summary>
public class SubAgentSpawnSuppressionTests
{
    /// <summary>
    /// The guarantee is PER TURN: Agent disappears from the advertised contracts on the suppressed run and is
    /// back on the next one, and the read/follow-up tools are never withdrawn.
    /// </summary>
    [Fact]
    public async Task Suppressed_input_hides_the_Agent_tool_for_that_run_only()
    {
        var advertisedPerCall = new List<IReadOnlyList<string>>();
        var parent = TextOnlyParent(advertisedPerCall);

        await using var loop = CreateLoop(parent);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await DrainAsync(loop, NewInput("first"), cts.Token);
        await DrainAsync(loop, NewInput("second", suppressSpawning: true), cts.Token);
        await DrainAsync(loop, NewInput("third"), cts.Token);

        advertisedPerCall.Should().HaveCount(3, "each run should have produced exactly one model call");

        advertisedPerCall[0].Should().Contain("Agent", "a normal turn may spawn sub-agents");
        advertisedPerCall[1].Should().NotContain(
            "Agent", "the suppressed input must not advertise a way to start new sub-agents");
        advertisedPerCall[2].Should().Contain(
            "Agent", "suppression is scoped to its own run and released afterwards");

        advertisedPerCall.Should().AllSatisfy(
            names => names.Should().Contain(["SendMessage", "CheckAgent"]),
            "reading from and following up with EXISTING sub-agents is never suppressed");

        await cts.CancelAsync();
    }

    /// <summary>
    /// Hiding the contract is not enough on its own: the model can replay an <c>Agent</c> call from earlier
    /// history. The handler must refuse it too, so the guarantee holds even then.
    /// </summary>
    [Fact]
    public async Task Suppressed_input_refuses_a_replayed_Agent_tool_call()
    {
        var spawnAttempts = 0;
        var parent = SpawnThenTextParent();

        await using var loop = CreateLoop(parent, onSpawn: () => spawnAttempts++);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(loop, NewInput("go", suppressSpawning: true), cts.Token);

        var results = messages.OfType<ToolCallResultMessage>().ToList();
        results.Should().Contain(
            r => r.ToolName == "Agent" && r.IsError && r.Result.Contains("not available for this turn"),
            "the Agent handler must refuse a replayed spawn on a suppressed run");
        spawnAttempts.Should().Be(0, "no sub-agent should have been started");

        await cts.CancelAsync();
    }

    #region Helpers

    private static UserInput NewInput(string text, bool suppressSpawning = false) =>
        new(
            [new TextMessage { Text = text, Role = Role.User }],
            SuppressSubAgentSpawning: suppressSpawning);

    private static MultiTurnAgentLoop CreateLoop(IStreamingAgent parent, Action? onSpawn = null)
    {
        var subAgent = new Mock<IStreamingAgent>();
        _ = subAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([
                new TextMessage { Text = "child done", Role = Role.Assistant },
            ])));

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new()
                {
                    Name = "researcher",
                    SystemPrompt = "You are a research assistant.",
                    AgentFactory = () =>
                    {
                        onSpawn?.Invoke();
                        return subAgent.Object;
                    },
                },
            },
            MaxConcurrentSubAgents = 3,
        };

        return new MultiTurnAgentLoop(
            parent,
            new FunctionRegistry(),
            threadId: "spawn-suppression-thread",
            subAgentOptions: options);
    }

    /// <summary>A parent that answers with plain text, recording the tool names it was offered each call.</summary>
    private static IStreamingAgent TextOnlyParent(List<IReadOnlyList<string>> advertisedPerCall)
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, options, _) =>
                {
                    lock (advertisedPerCall)
                    {
                        advertisedPerCall.Add(
                            options?.Functions?.Select(f => f.Name).ToList() ?? []);
                    }

                    return Task.FromResult(ToAsyncEnumerable([
                        new TextMessage { Text = "answer", Role = Role.Assistant },
                    ]));
                });
        return mock.Object;
    }

    /// <summary>A parent that calls <c>Agent</c> on its first turn (as if replaying history) then answers.</summary>
    private static IStreamingAgent SpawnThenTextParent()
    {
        var calls = 0;
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                        ? ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "Agent",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    subagent_type = "researcher",
                                    prompt = "Research the topic",
                                }),
                                ToolCallId = "call_agent_1",
                                Role = Role.Assistant,
                            },
                        ])
                        : ToAsyncEnumerable([
                            new TextMessage { Text = "answer", Role = Role.Assistant },
                        ])));
        return mock.Object;
    }

    private static async Task<List<IMessage>> DrainAsync(
        MultiTurnAgentLoop loop,
        UserInput input,
        CancellationToken ct)
    {
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, ct))
        {
            messages.Add(msg);
        }

        return messages;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion
}
