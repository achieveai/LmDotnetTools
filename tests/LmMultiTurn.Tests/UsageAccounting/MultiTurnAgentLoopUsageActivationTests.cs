using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

/// <summary>
/// End-to-end activation of conversation usage accounting in the live loop (#196): a run whose provider
/// reports a UsageMessage results in a persisted usage aggregate on the conversation.
/// </summary>
public class MultiTurnAgentLoopUsageActivationTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();

    [Fact]
    public async Task Run_PersistsUsageAggregate_FromPrimaryUsageMessage()
    {
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "usage-thread",
            store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "Hi", Role = Role.User }],
            InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion
        }

        // Persistence of the aggregate is fire-and-forget, so poll briefly for it to land.
        ConversationUsageAggregate? aggregate = null;
        for (var attempt = 0; attempt < 100 && aggregate is null; attempt++)
        {
            aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
            if (aggregate is null)
            {
                await Task.Delay(20, cts.Token);
            }
        }

        aggregate.Should().NotBeNull();
        aggregate!.TotalTokens.Should().Be(140);
        aggregate.RootConversationId.Should().Be("usage-thread");
        aggregate.PerModel.Should().ContainSingle();

        await cts.CancelAsync();
    }

    private void SetupMockAgentResponse(List<IMessage> messages)
    {
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
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
}
