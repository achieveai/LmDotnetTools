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

    [Fact]
    public async Task Dispose_FlushesUsage_MakingItDurableWithoutPolling()
    {
        // The awaited flush at disposal is the durability boundary: a run's final usage must be persisted
        // synchronously by DisposeAsync, not left to a fire-and-forget write that may be lost at shutdown.
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
        var loop = new MultiTurnAgentLoop(
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

        await loop.DisposeAsync();

        // No polling: if the disposal flush works, usage is durable the instant DisposeAsync returns.
        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate.Should().NotBeNull();
        aggregate!.TotalTokens.Should().Be(140);
    }

    [Fact]
    public async Task Dispose_MarksUsageComplete_OnTerminalFlush()
    {
        // A terminal loop (disposed after its run finished) must persist Completeness=Complete, not the
        // live InProgress default — otherwise the conversation-wide completeness state machine is dead and
        // no consumer can distinguish a finished conversation from a still-running one (#196, BUG 2).
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
        var loop = new MultiTurnAgentLoop(
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

        await loop.DisposeAsync();

        var aggregate = await ConversationUsageProjection.LoadAsync(store, "usage-thread");
        aggregate.Should().NotBeNull();
        aggregate!.Completeness.Should().Be(UsageCompleteness.Complete);
    }

    [Fact]
    public async Task DuringRun_UsageAggregate_StaysInProgress()
    {
        // While the loop is still alive (not yet terminal), coalesced fire-and-forget writes must keep the
        // aggregate InProgress — a live conversation must not falsely advertise Complete (#196, BUG 2).
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
            // drain the run to completion; the loop stays alive waiting for more input
        }

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
        aggregate!.Completeness.Should().Be(UsageCompleteness.InProgress);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Run_PublishesLiveUsageFrame_ReflectingFoldedTotal()
    {
        // The loop broadcasts a ConversationUsageMessage to subscribers as usage folds in, so the client
        // banner reflects it live rather than only after a reload of the persisted aggregate (#196, BUG 1b).
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

        var frames = new List<ConversationUsageMessage>();
        var subscribeTask = Task.Run(
            async () =>
            {
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    if (msg is ConversationUsageMessage frame)
                    {
                        frames.Add(frame);
                    }
                }
            },
            cts.Token);

        // Let the subscription register before the run produces usage.
        await Task.Delay(100, cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "Hi", Role = Role.User }],
            InputId: "input-1");

        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain the run to completion
        }

        // Poll briefly for the frame to arrive on the subscription (delivery is asynchronous).
        for (var attempt = 0; attempt < 100 && frames.Count == 0; attempt++)
        {
            await Task.Delay(20, cts.Token);
        }

        frames.Should().NotBeEmpty();
        frames[^1].TotalTokens.Should().Be(140);
        frames[^1].ThreadId.Should().Be("usage-thread");

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
