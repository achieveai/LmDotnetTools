using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

public class PassiveContextTests
{
    [Fact]
    public async Task ActiveContext_PersistsWithoutQueuedInputOrAnExtraProviderCall_AndIdleContextIsRefused()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new Mock<IStreamingAgent>();
        provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return Reply(ct);
                }
            );
        var store = new InMemoryConversationStore();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var loop = new MultiTurnAgentLoop(
            provider.Object,
            new FunctionRegistry(),
            "passive-context",
            store: store
        );
        var sink = (IContextNotificationSink)loop;
        var idle = new NotifyMessage { NotifyKind = NotifyKinds.ContextDiscovery, Detail = "idle context" };
        var active = new NotifyMessage { NotifyKind = NotifyKinds.ContextDiscovery, Detail = "active context" };

        (await sink.TryAppendContextAsync(idle, timeout.Token)).Should().BeFalse();
        loop.GetHistorySnapshot().Should().BeEmpty();
        var running = loop.RunAsync(timeout.Token);
        var output = new List<IMessage>();
        var run = CollectAsync();
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var runId = loop.CurrentRunId;
            (await sink.TryAppendContextAsync(active, timeout.Token)).Should().BeTrue();
            loop.GetHistorySnapshot().OfType<NotifyMessage>().Should().ContainSingle(m => m.Detail == active.Detail);
            var persisted = (await store.LoadMessagesAsync(loop.ThreadId, timeout.Token))
                .Should()
                .ContainSingle(m => m.MessageType == nameof(NotifyMessage))
                .Subject;
            persisted.RunId.Should().Be(runId);
            persisted.MessageJson.Should().Contain("active context");

            release.TrySetResult();
            await run.WaitAsync(timeout.Token);
            var completed = output.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
            completed.IsError.Should().BeFalse();
            completed.PendingMessageCount.Should().Be(0, "passive context never enters the input queue");
            completed.HasPendingMessages.Should().BeFalse();
            output.OfType<NotifyMessage>().Should().ContainSingle(m => m.Detail == active.Detail);
            await loop.StopAsync(TimeSpan.FromSeconds(5));
            await running.WaitAsync(timeout.Token);
            calls.Should().Be(1, "context must not start another provider run after completion");
            (await sink.TryAppendContextAsync(idle, timeout.Token)).Should().BeFalse();
            loop.GetHistorySnapshot().OfType<NotifyMessage>().Should().ContainSingle(m => m.Detail == active.Detail);
        }
        finally
        {
            release.TrySetResult();
        }

        async Task CollectAsync()
        {
            await foreach (
                var message in loop.ExecuteRunAsync(
                    new UserInput([new TextMessage { Role = Role.User, Text = "original task" }]),
                    timeout.Token
                )
            )
            {
                output.Add(message);
            }
        }
    }

    [Fact]
    public void OnlyTheLoopWhoseProviderReadsHostHistory_AcceptsPassiveContext()
    {
        // A CLI loop keeps its own session history, so an append would be reported delivered while the
        // model never saw it. Callers test for the sink and treat its absence as not deliverable.
        typeof(MultiTurnAgentLoop).Should().Implement<IContextNotificationSink>();
        typeof(MultiTurnAgentBase).Should().NotImplement<IContextNotificationSink>();
        foreach (var cliLoop in new[] { typeof(ClaudeAgentLoop), typeof(CodexAgentLoop), typeof(CopilotAgentLoop) })
            cliLoop.Should().NotImplement<IContextNotificationSink>();
    }

    private static async IAsyncEnumerable<IMessage> Reply([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new TextMessage { Role = Role.Assistant, Text = "done" };
        await Task.CompletedTask;
    }
}
