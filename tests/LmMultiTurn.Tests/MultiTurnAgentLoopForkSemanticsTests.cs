using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Verifies fork-signal propagation in <see cref="MultiTurnAgentLoop"/>. The rule
/// is shared via <c>MultiTurnAgentBase.ResolveBatchParent</c>; this test ensures
/// the wire-up at the loop's run-start / run-complete sites is correct for the
/// raw-LLM loop.
/// </summary>
public class MultiTurnAgentLoopForkSemanticsTests
{
    [Fact]
    public async Task ExecuteRunAsync_WithParentRunId_PublishesForkedCompletion()
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsync([
                new TextMessage { Text = "ok", Role = Role.Assistant },
            ])));

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            agent.Object,
            registry,
            threadId: "raw-fork-test");

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput(
            [new TextMessage { Text = "hi", Role = Role.User }],
            ParentRunId: "raw-parent-1");

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var assignment = messages.OfType<RunAssignmentMessage>().Should().ContainSingle().Subject;
        assignment.Assignment.ParentRunId.Should().Be("raw-parent-1");

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeTrue();
        completed.ForkedToRunId.Should().Be(completed.CompletedRunId);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_WithoutParentRunId_NotForked()
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsync([
                new TextMessage { Text = "ok", Role = Role.Assistant },
            ])));

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            agent.Object,
            registry,
            threadId: "raw-no-fork-test");

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Text = "hi", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeFalse();
        completed.ForkedToRunId.Should().BeNull();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_WithParentRunId_OnError_StillPublishesForkedCompletion()
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ThrowingAsync<IMessage>(new InvalidOperationException("boom"))));

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            agent.Object,
            registry,
            threadId: "raw-fork-error-test");

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput(
            [new TextMessage { Text = "hi", Role = Role.User }],
            ParentRunId: "raw-parent-err");

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeTrue();
        completed.WasForked.Should().BeTrue();
        completed.ForkedToRunId.Should().Be(completed.CompletedRunId);

        await cts.CancelAsync();
    }

    private static async IAsyncEnumerable<T> ThrowingAsync<T>(
        Exception ex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IMessage> ToAsync(
        IReadOnlyList<IMessage> messages,
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
