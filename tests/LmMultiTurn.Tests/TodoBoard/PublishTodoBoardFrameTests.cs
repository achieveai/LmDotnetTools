using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;
using LmMultiTurn.Tests;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.TodoBoard;

/// <summary>
///     The live todo-board push (#583, PR 2): a board snapshot handed to
///     <see cref="MultiTurnAgentBase.PublishTodoBoardFrame" /> reaches the loop's subscribers as a
///     <see cref="ConversationTodoMessage" /> stamped with the LOOP's own thread id — never the
///     snapshot's — and only THAT loop's subscribers see it.
/// </summary>
public class PublishTodoBoardFrameTests
{
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(5);

    private static MultiTurnAgentLoop BuildLoop(string threadId)
    {
        return new MultiTurnAgentLoop(new Mock<IStreamingAgent>().Object, new FunctionRegistry(), threadId);
    }

    private static TodoBoardSnapshot BuildSnapshot(string threadId)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = threadId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = "Wire the SSE endpoint",
                    Notes = ["waiting on schema"],
                },
            ],
        };
    }

    [Fact]
    public async Task PublishTodoBoardFrame_DeliversTheChangedBoard_ToSubscribers()
    {
        await using var loop = BuildLoop("todo-thread");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var received = new TaskCompletionSource<ConversationTodoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var drain = LoopSubscription.StartDraining(
            loop,
            msg =>
            {
                if (msg is ConversationTodoMessage frame)
                {
                    _ = received.TrySetResult(frame);
                }
            },
            cts.Token
        );

        loop.PublishTodoBoardFrame(() => BuildSnapshot("todo-thread"));

        await drain.WaitAsync(received.Task, FrameWait);
        var delivered = await received.Task;

        // The frame carries the board that changed, not a stale or empty one. Mutation that must go
        // red: publishing a frame without the snapshot's tasks (e.g. defaulting Tasks).
        delivered.Tasks.Should().ContainSingle();
        delivered.Tasks[0].Id.Should().Be("1");
        delivered.Tasks[0].Status.Should().Be(TodoTaskStatus.InProgress);
        delivered.Tasks[0].Title.Should().Be("Wire the SSE endpoint");
        delivered.Tasks[0].Notes.Should().ContainSingle().Which.Should().Be("waiting on schema");
        delivered.Should().BeAssignableTo<ITransientMessage>();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task PublishTodoBoardFrame_StampsTheLoopsOwnThreadId_OverTheSnapshotsStamp()
    {
        // Sub-agents mutate the parent conversation's shared board, so a snapshot can arrive stamped
        // with an acting agent's own id (subagent-*). The client drops frames whose threadId is not the
        // open conversation, so a frame carrying the snapshot's stamp would vanish silently. Mutation
        // that must go red: FromSnapshot/PublishTodoBoardFrame using snapshot.ThreadId for the frame.
        await using var loop = BuildLoop("root-conversation");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var received = new TaskCompletionSource<ConversationTodoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var drain = LoopSubscription.StartDraining(
            loop,
            msg =>
            {
                if (msg is ConversationTodoMessage frame)
                {
                    _ = received.TrySetResult(frame);
                }
            },
            cts.Token
        );

        loop.PublishTodoBoardFrame(() => BuildSnapshot("subagent-abc123"));

        await drain.WaitAsync(received.Task, FrameWait);
        var delivered = await received.Task;

        delivered.ThreadId.Should().Be("root-conversation");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task PublishTodoBoardFrame_DoesNotReachAnotherLoopsSubscribers()
    {
        // One conversation's board change must never repaint another conversation's panel. Structural
        // today (each loop owns its subscriber set), but the test pins the behaviour so a future shared
        // publish bus cannot regress it silently.
        await using var publishingLoop = BuildLoop("thread-a");
        await using var otherLoop = BuildLoop("thread-b");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var strayFrames = 0;
        var expected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = LoopSubscription.StartDraining(
            otherLoop,
            msg =>
            {
                if (msg is ConversationTodoMessage)
                {
                    _ = Interlocked.Increment(ref strayFrames);
                }
            },
            cts.Token
        );
        var publishingDrain = LoopSubscription.StartDraining(
            publishingLoop,
            msg =>
            {
                if (msg is ConversationTodoMessage)
                {
                    _ = expected.TrySetResult(true);
                }
            },
            cts.Token
        );

        publishingLoop.PublishTodoBoardFrame(() => BuildSnapshot("thread-a"));

        // Wait until the frame demonstrably made it through the publishing loop's channel; only then is
        // "the other loop saw nothing" evidence rather than a race won by asserting too early.
        await publishingDrain.WaitAsync(expected.Task, FrameWait);

        strayFrames.Should().Be(0);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task PublishTodoBoardFrame_NullCapture_PublishesNoFrame_AndLaterFramesStillFlow()
    {
        // "No board yet" (the capture returned null) must publish nothing — but proving a NEGATIVE
        // needs a control, or the test passes vacuously when the subscription itself is broken
        // (#590 review F-008). So: publish a null-yielding capture, then a real one, and assert the
        // ONLY frame that arrives is the real one. Mutation that must go red: publishing a frame for
        // a null capture (e.g. substituting an empty board).
        await using var loop = BuildLoop("todo-thread");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var frames = new List<ConversationTodoMessage>();
        var realFrameSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = LoopSubscription.StartDraining(
            loop,
            msg =>
            {
                if (msg is ConversationTodoMessage frame)
                {
                    lock (frames)
                    {
                        frames.Add(frame);
                    }

                    _ = realFrameSeen.TrySetResult(true);
                }
            },
            cts.Token
        );

        loop.PublishTodoBoardFrame(() => null);
        loop.PublishTodoBoardFrame(() => BuildSnapshot("todo-thread"));

        await drain.WaitAsync(realFrameSeen.Task, FrameWait);

        lock (frames)
        {
            frames.Should().ContainSingle("the null capture must not have produced a frame of its own");
            frames[0].Tasks.Should().ContainSingle().Which.Title.Should().Be("Wire the SSE endpoint");
        }

        await cts.CancelAsync();
    }

    [Fact]
    public async Task PublishTodoBoardFrame_ThrowingCapture_IsContained_AndLaterFramesStillFlow()
    {
        // #590 review SC-2: the capture delegate runs INSIDE the publish guard. #587 made
        // TaskManager.GetTodoBoardSnapshot throw loudly on an unmapped status, and TaskManager's
        // OnChanged dispatch would swallow that silently — so the capture must fault where the loop
        // logs it, and a faulted capture must not poison subsequent publishes. Mutation that must go
        // red: hoisting the capture invocation out of the guarded region (the throw would then
        // escape to the caller and this act would blow up).
        await using var loop = BuildLoop("todo-thread");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var received = new TaskCompletionSource<ConversationTodoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var drain = LoopSubscription.StartDraining(
            loop,
            msg =>
            {
                if (msg is ConversationTodoMessage frame)
                {
                    _ = received.TrySetResult(frame);
                }
            },
            cts.Token
        );

        var act = () =>
            loop.PublishTodoBoardFrame(() =>
                throw new InvalidOperationException("Unmapped TaskStatus value 'Removed'")
            );
        act.Should().NotThrow("a partial-capture fault must be logged, not thrown into the mutation path");

        loop.PublishTodoBoardFrame(() => BuildSnapshot("todo-thread"));

        await drain.WaitAsync(received.Task, FrameWait);
        var delivered = await received.Task;
        delivered.Tasks.Should().ContainSingle();

        await cts.CancelAsync();
    }
}
