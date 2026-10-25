using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// Covers issue #830: the pool kept every conversation it had ever loaded, with its sub-agent loops,
/// until the conversation was deleted - about 36 MB per finished review on the review host, never
/// returned. <see cref="MultiTurnAgentPool.UnloadIdleAgentsAsync"/> drops a conversation that has
/// had no work in hand for the idle window, and the next request rebuilds it from the store.
/// </summary>
/// <remarks>
/// Each "keeps" test pins one kind of work whose runtime half lives only in this process. The clock is
/// a <see cref="FakeTimeProvider"/> moved well past the window, so a pass means the work held the
/// entry, not that the window had not elapsed.
/// </remarks>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolIdleUnloadTests
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    private static readonly AgentProfile Mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    [Fact]
    public async Task UnloadsAnIdleLoop_OnceTheWindowHasPassed_AndTheNextRequestRebuildsIt()
    {
        var time = NewClock();
        var built = new List<MultiTurnAgentLoop>();
        await using var pool = CreatePool(time, threadId => Track(built, IdleLoop(threadId)));
        var removed = new List<string>();
        pool.ThreadRemoved += removed.Add;
        var first = pool.GetOrCreateAgent("thread-idle", Mode);

        time.Advance(IdleAfter - TimeSpan.FromSeconds(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the window has not passed yet");
        pool.HasAgent("thread-idle").Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
        pool.HasAgent("thread-idle").Should().BeFalse();
        removed.Should().Equal("thread-idle");

        var rebuilt = pool.GetOrCreateAgent("thread-idle", Mode);
        rebuilt.Should().NotBeSameAs(first, "an unloaded conversation is rebuilt, exactly as after a restart");
        built.Should().HaveCount(2);
    }

    [Fact]
    public async Task ARebuiltLoop_CarriesOnTheConversationFromTheStore()
    {
        // Unloading frees only the in-memory loop. The model must still see everything said before it.
        var time = NewClock();
        var store = new InMemoryConversationStore();
        var requests = new List<IMessage[]>();
        var provider = RecordingProvider(requests);
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(provider, new FunctionRegistry(), threadId, store: store)
        );
        var first = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-kept", Mode);
        await DrainRunAsync(first, "my name is Ada");
        await WaitUntilSettledAsync(pool, first, "thread-kept");

        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);

        var rebuilt = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-kept", Mode);
        rebuilt.Should().NotBeSameAs(first);
        rebuilt.ThreadId.Should().Be("thread-kept");
        await DrainRunAsync(rebuilt, "what is my name?");

        requests.Should().HaveCount(2);
        requests[1]
            .OfType<TextMessage>()
            .Select(m => (m.Role, m.Text))
            .Should()
            .ContainInOrder(
                (Role.User, "my name is Ada"),
                (Role.Assistant, "Reply 1."),
                (Role.User, "what is my name?")
            );
    }

    [Fact]
    public async Task AHandOut_RestartsTheIdleClock()
    {
        var time = NewClock();
        await using var pool = CreatePool(time, IdleLoop);
        _ = pool.GetOrCreateAgent("thread-used", Mode);

        time.Advance(TimeSpan.FromMinutes(4));
        _ = pool.GetOrCreateAgent("thread-used", Mode);
        time.Advance(TimeSpan.FromMinutes(4));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "it was handed out four minutes ago");

        time.Advance(TimeSpan.FromMinutes(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    [Fact]
    public async Task APeek_DoesNotRestartTheIdleClock()
    {
        // TryGet serves status, transcript and agent-tree reads. A UI polling a finished conversation
        // through it must not keep that conversation loaded for ever.
        var time = NewClock();
        await using var pool = CreatePool(time, IdleLoop);
        _ = pool.GetOrCreateAgent("thread-peeked", Mode);

        time.Advance(TimeSpan.FromMinutes(4));
        pool.TryGet("thread-peeked", out _).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(1));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    [Fact]
    public async Task KeepsALoop_WithARunInProgress()
    {
        var time = NewClock();
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(BlockingProvider(), new FunctionRegistry(), threadId)
        );
        var loop = pool.GetOrCreateAgent("thread-running", Mode);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], "input-1");
        await Wait.UntilAsync(() => pool.IsRunInProgress("thread-running"), "the run started");

        time.Advance(TimeSpan.FromHours(1));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
        pool.HasAgent("thread-running").Should().BeTrue();
    }

    [Fact]
    public async Task KeepsALoop_HoldingAnAcceptedInputNoRunHasTakenYet()
    {
        var time = NewClock();
        await using var pool = CreatePool(time, IdleLoop);
        var loop = pool.GetOrCreateAgent("thread-queued", Mode);
        ((IInputAcceptanceObserver)pool).OnInputAccepted("thread-queued", "input-1", loop).Should().BeTrue();

        time.Advance(TimeSpan.FromHours(1));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "a turn is owed to the sender");
    }

    [Fact]
    public async Task KeepsALoop_ParkedOnADeferredCall_AndCountsTheWindowFromWhenItWasAnswered()
    {
        // An unanswered AskUserQuestion has no run in progress, yet unloading would orphan the question.
        // Answering it ends the work, and the window then counts from the last sweep that saw the work -
        // not from an earlier stamp, which would unload it moments after the answer landed.
        var time = NewClock();
        var requests = new StrongBox<int>();
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(AsksOnceThenAnswers(requests), new FunctionRegistry(), threadId)
        );
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-asking", Mode);
        await DrainRunAsync(loop, "which colour?");
        await WaitUntilInputRetiredAsync(pool, "thread-asking");
        (await pool.HasPendingAskUserQuestionAsync("thread-asking")).Should().BeTrue();

        time.Advance(TimeSpan.FromHours(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the question is still open");

        // A sweep inside the window that still sees the open question must restart the clock too.
        time.Advance(TimeSpan.FromMinutes(4));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the question is still open");

        (await loop.TryResolveToolCallAsync(AskCallId, "blue", isError: false))
            .Should()
            .Be(ResolveToolCallOutcome.Resolved);
        await Wait.UntilAsync(
            () => Volatile.Read(ref requests.Value) == 2 && !pool.IsRunInProgress("thread-asking"),
            "the answered question's continuation ran to its end"
        );

        time.Advance(TimeSpan.FromMinutes(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the work was last seen a minute ago");

        time.Advance(TimeSpan.FromMinutes(4));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    [Fact]
    public async Task KeepsALoop_WhoseSubAgentIsStillRunning()
    {
        // The parent's own run has ended, but a background child is working; its report comes back to
        // the parent, and unloading the parent would kill the child mid-task.
        var time = NewClock();
        MultiTurnAgentLoop? parent = null;
        await using var pool = CreatePool(
            time,
            threadId =>
                parent = new MultiTurnAgentLoop(
                    BlockingProvider(),
                    new FunctionRegistry(),
                    threadId,
                    subAgentOptions: WorkerTemplate()
                )
        );
        _ = pool.GetOrCreateAgent("thread-parent", Mode);
        _ = await parent!.SubAgentManager!.SpawnAsync("worker", "long task", name: "alpha", runInBackground: true);

        time.Advance(TimeSpan.FromHours(1));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
        pool.HasAgent("thread-parent").Should().BeTrue();
    }

    [Fact]
    public async Task ALoadedConversation_FreesTheSubAgentsItHasFinishedWith()
    {
        // The parent stays loaded (it was just handed out), but its child finished more than the window
        // ago, so the child's loop is freed. The child stays addressable: its manager rebuilds it.
        // Sub-agents stamp their finish on the system clock, so the fake one starts from it.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        MultiTurnAgentLoop? parent = null;
        await using var pool = CreatePool(
            time,
            threadId =>
                parent = new MultiTurnAgentLoop(
                    BlockingProvider(),
                    new FunctionRegistry(),
                    threadId,
                    subAgentOptions: WorkerTemplate(AnsweringProvider)
                )
        );
        _ = pool.GetOrCreateAgent("thread-parent", Mode);
        var manager = parent!.SubAgentManager!;
        _ = await SpawnSettledAsync(manager, "short task", "done");

        time.Advance(IdleAfter + TimeSpan.FromMinutes(1));
        _ = pool.GetOrCreateAgent("thread-parent", Mode);

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
        manager.TryGetAgent("done", out var child).Should().BeTrue();
        child.Should().NotBeOfType<MultiTurnAgentLoop>("the finished child's loop was released");
    }

    [Fact]
    public async Task KeepsALoop_WhileAMessageIsRebuildingASubAgentItReleased()
    {
        // A message can reach a finished sub-agent without going through the pool (its focused view,
        // the to-do board, another agent). While it rebuilds the child, nothing the pool reads looks
        // busy: the child is still "completed". Unloading the parent then would kill the rebuilt child.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builds = 0;
        var rebuilding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishRebuild = new ManualResetEventSlim();
        MultiTurnAgentLoop? parent = null;
        await using var pool = CreatePool(
            time,
            threadId =>
                parent = new MultiTurnAgentLoop(
                    BlockingProvider(),
                    new FunctionRegistry(),
                    threadId,
                    subAgentOptions: WorkerTemplate(() =>
                    {
                        if (Interlocked.Increment(ref builds) == 2)
                        {
                            rebuilding.TrySetResult();
                            _ = finishRebuild.Wait(TimeSpan.FromSeconds(30));
                        }

                        return AnsweringProvider();
                    })
                )
        );
        _ = pool.GetOrCreateAgent("thread-parent", Mode);
        var manager = parent!.SubAgentManager!;
        _ = await SpawnSettledAsync(manager, "short task", "done");
        time.Advance(IdleAfter + TimeSpan.FromMinutes(1));
        _ = pool.GetOrCreateAgent("thread-parent", Mode);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the parent was just handed out");

        var followUp = Task.Run(() => manager.SendMessageAsync("done", "follow-up", runInBackground: true));
        try
        {
            await rebuilding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(IdleAfter + TimeSpan.FromMinutes(1));

            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
            pool.HasAgent("thread-parent").Should().BeTrue();
        }
        finally
        {
            finishRebuild.Set();
        }

        _ = await followUp.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task KeepsALoop_WhenAGrandchildIsMessagedBetweenTheSweepsReadsAndItsLock()
    {
        // The reads see a settled tree. A message straight to a grandchild then restarts it without
        // going through the pool; only the quiesce under the lock, which walks the whole tree, sees it.
        var time = NewClock();
        var holding = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MultiTurnAgentLoop? parent = null;
        await using var pool = CreatePool(
            time,
            threadId =>
                parent = CollaboratingLoop(
                    threadId,
                    () => GatedProvider(() => Volatile.Read(ref holding) ? release.Task : Task.CompletedTask)
                )
        );
        _ = pool.GetOrCreateAgent("thread-parent", Mode);
        _ = await SpawnAsync(parent!.SubAgentManager!, "worker", "child");
        var child = ChildManager(parent.SubAgentManager!, "child");
        _ = await SpawnAsync(child, "worker", "grandchild");
        await WaitUntilSettledAsync(pool, parent, "thread-parent");

        var readsPassed = false;
        pool.BeforeUnloadLockForTest = async _ =>
        {
            readsPassed = true;
            Volatile.Write(ref holding, true);
            _ = await child.SendMessageAsync("grandchild", "follow-up", runInBackground: true);
        };
        time.Advance(TimeSpan.FromHours(1));
        try
        {
            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the grandchild is working again");
            readsPassed.Should().BeTrue("the sweep's reads saw a settled tree");
            pool.HasAgent("thread-parent").Should().BeTrue();
        }
        finally
        {
            pool.BeforeUnloadLockForTest = null;
            release.TrySetResult();
        }

        await Wait.UntilAsync(
            () => child.ListAgents().Any(a => a.Name == "grandchild" && a.Status == SubAgentStatus.Completed),
            "the grandchild answered the follow-up"
        );

        // Its answer then travels up the tree, a turn at a time; each sweep that sees it keeps the
        // conversation another window.
        await Wait.UntilAsync(
            async () =>
            {
                time.Advance(IdleAfter);
                return await pool.UnloadIdleAgentsAsync(IdleAfter) == 1;
            },
            "the conversation unloads once nothing in the tree is working"
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ARefusedQuiesce_LeavesEverySubAgentOpen_WhicheverSubtreeRefused(int refusing)
    {
        // The quiesce closes each child's manager in turn, and a refusal further on reopens the ones it
        // already closed. ListAgents walks the same table in the same order, so one case refuses first
        // and the other refuses after closing the other child.
        await using var parent = CollaboratingLoop("thread-parent", AnsweringProvider);
        var manager = parent.SubAgentManager!;
        _ = await SpawnAsync(manager, "worker", "alpha");
        _ = await SpawnAsync(manager, "worker", "beta");
        var children = manager.ListAgents().Select(a => ChildManager(manager, a.Name!)).ToList();
        _ = await SpawnAsync(children[refusing], "blocker", "busy", runInBackground: true);

        manager.TryQuiesce().Should().BeFalse("a grandchild is running");

        for (var i = 0; i < children.Count; i++)
        {
            (await SpawnAsync(children[i], "worker", $"later-{i}"))
                .Should()
                .Be("Done.", "a refused quiesce leaves every manager open");
        }
    }

    [Fact]
    public async Task KeepsALoop_ThatOwnsAPendingBackgroundTask()
    {
        // A sandbox background task's completion notice is delivered only to a loaded conversation.
        var time = NewClock();
        var sink = new BackgroundTaskSink { PendingFor = "thread-bg" };
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(IdleLoop(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        )
        {
            TimeProvider = time,
        };
        _ = pool.GetOrCreateAgent("thread-bg", Mode);
        _ = pool.GetOrCreateAgent("thread-no-bg", Mode);

        time.Advance(TimeSpan.FromHours(1));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
        pool.HasAgent("thread-bg").Should().BeTrue();
        pool.HasAgent("thread-no-bg").Should().BeFalse();
    }

    [Fact]
    public async Task LeavesAgentsThatAreNotLoops_Loaded()
    {
        // CLI-backed agents expose neither deferred calls nor sub-agents, so the pool cannot tell
        // they are idle.
        var time = NewClock();
        await using var pool = CreatePool(
            time,
            threadId => new FakeMultiTurnAgent(threadId) { KeepSubscriptionOpen = true }
        );
        _ = pool.GetOrCreateAgent("thread-cli", Mode);

        time.Advance(TimeSpan.FromHours(1));

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
    }

    [Fact]
    public async Task AnAcceptReportedByAnUnloadedAgent_IsRefused_ButAnUnknownAgentsIsNot()
    {
        // #457: a sender that took the agent just before the unload must not have its turn queued in
        // an agent being torn down. Refused, the send fails and a retry reaches the rebuilt agent. An
        // agent the pool never held is still let through, as before.
        var time = NewClock();
        await using var pool = CreatePool(time, IdleLoop);
        var unloaded = pool.GetOrCreateAgent("thread-late", Mode);
        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
        var observer = (IInputAcceptanceObserver)pool;

        observer.OnInputAccepted("thread-late", "input-late", unloaded).Should().BeFalse();

        await using var stranger = IdleLoop("thread-late");
        observer.OnInputAccepted("thread-late", "input-other", stranger).Should().BeTrue();
    }

    [Fact]
    public async Task ATeardownThatEndsAfterAReplacementWasCreated_DoesNotReportTheThreadRemoved()
    {
        // ThreadRemoved makes the host drop the thread's sandbox routing, workflow registry entry and
        // transcript mirror. A request that rebuilt the conversation while the old agent was still being
        // torn down has set all of those up again; the old teardown must not take them away.
        var time = NewClock();
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        await using var pool = CreatePool(
            time,
            threadId => new FakeMultiTurnAgent(threadId)
            {
                DisposeGate = Interlocked.Increment(ref builds) == 1 ? disposing.Task : null,
            }
        );
        var removed = new List<string>();
        pool.ThreadRemoved += removed.Add;
        _ = pool.GetOrCreateAgent("thread-swap", Mode);

        var teardown = pool.RemoveAgentAsync("thread-swap").AsTask();
        var replacement = pool.GetOrCreateAgent("thread-swap", Mode);
        disposing.SetResult();
        await teardown.WaitAsync(TimeSpan.FromSeconds(10));

        removed.Should().BeEmpty("the thread is loaded again");
        pool.TryGet("thread-swap", out var current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
    }

    [Fact]
    public async Task KeepsALoop_WhenASendThatBypassedThePoolParksWorkBetweenTheSweepsReadsAndItsLock()
    {
        // A send can reach a loaded conversation without a hand-out: the background-task notifier takes
        // the agent with TryGet and sends to it. A whole run that lands after the sweep read the loop as
        // idle and before it took the lock leaves a parked question the reads never saw.
        var time = NewClock();
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(AsksOnceThenAnswers(), new FunctionRegistry(), threadId)
        );
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-direct", Mode);
        time.Advance(TimeSpan.FromHours(1));
        pool.BeforeUnloadLockForTest = async threadId =>
        {
            _ = await loop.SendAsync([new TextMessage { Text = "which colour?", Role = Role.User }], "input-direct");
            await Wait.UntilAsync(
                async () =>
                    (await loop.GetDeferredToolCallsAsync()).Count == 1
                    && !pool.IsRunInProgress(threadId)
                    && pool.GetOutstandingInputCountForTest(threadId) == 0,
                "the run parked its question and retired its input"
            );
        };

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
        pool.HasAgent("thread-direct").Should().BeTrue();
    }

    [Fact]
    public async Task AnInputAcceptedDuringTheSweep_RestartsTheIdleClock()
    {
        // The window counts from the last work. An input accepted after the sweep's reads is work, even
        // when its run has already ended by the time the sweep takes the lock.
        var time = NewClock();
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(AnsweringProvider(), new FunctionRegistry(), threadId)
        );
        var loop = pool.GetOrCreateAgent("thread-answered", Mode);
        time.Advance(TimeSpan.FromHours(1));
        pool.BeforeUnloadLockForTest = async threadId =>
        {
            _ = await loop.SendAsync([new TextMessage { Text = "hello", Role = Role.User }], "input-answered");
            await Wait.UntilAsync(
                () => pool.GetOutstandingInputCountForTest(threadId) == 0 && !pool.IsRunInProgress(threadId),
                "the run took the input and ended"
            );
        };

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "an input was accepted moments ago");

        pool.BeforeUnloadLockForTest = null;
        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    [Fact]
    public async Task KeepsALoop_WhenABackgroundTaskStartsBetweenTheSweepsReadsAndItsLock()
    {
        var time = NewClock();
        var sink = new BackgroundTaskSink();
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(IdleLoop(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        )
        {
            TimeProvider = time,
        };
        _ = pool.GetOrCreateAgent("thread-bg-late", Mode);
        time.Advance(TimeSpan.FromHours(1));
        pool.BeforeUnloadLockForTest = threadId =>
        {
            sink.PendingFor = threadId;
            return Task.CompletedTask;
        };

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
        pool.HasAgent("thread-bg-late").Should().BeTrue();
    }

    [Fact]
    public async Task KeepsALoop_ThatOwesAContinuationItHasNotStartedYet()
    {
        // Answering a parked call queues its continuation. Until the loop takes it, no run is in progress
        // and no deferred call is left, yet the conversation still owes a turn.
        var time = NewClock();
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(AsksOnceThenAnswers(), new FunctionRegistry(), threadId)
        );
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-owed", Mode);
        await DrainRunAsync(loop, "which colour?");
        await WaitUntilInputRetiredAsync(pool, "thread-owed");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.BeforeTakingNextWorkForTest = ct => release.Task.WaitAsync(ct);
        try
        {
            (await loop.TryResolveToolCallAsync(AskCallId, "blue", isError: false))
                .Should()
                .Be(ResolveToolCallOutcome.Resolved);
            await Wait.UntilAsync(
                async () => loop.HasPendingLoopWork && (await loop.GetDeferredToolCallsAsync()).Count == 0,
                "the answer's continuation is queued"
            );
            pool.IsRunInProgress("thread-owed").Should().BeFalse();

            time.Advance(TimeSpan.FromHours(1));

            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "a continuation is still owed");
        }
        finally
        {
            loop.BeforeTakingNextWorkForTest = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task KeepsALoop_WhoseContinuationIsTakenButHasNoRunYet()
    {
        // An answer that reaches an idle loop wakes it, and the loop takes the continuation. Until its run
        // has an id, the continuation is neither queued nor running, and only the coordinator still counts it.
        var time = NewClock();
        var requests = new StrongBox<int>();
        var waitsForInput = 0;
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(AsksOnceThenAnswers(requests), new FunctionRegistry(), threadId)
            {
                WaitingForInputForTest = () => Interlocked.Increment(ref waitsForInput),
                BeforeStartingDelayedRunForTest = ct =>
                {
                    taken.TrySetResult();
                    return release.Task.WaitAsync(ct);
                },
            }
        );
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-admitting", Mode);
        try
        {
            await DrainRunAsync(loop, "which colour?");

            // Idle on its input, the loop takes the answer's wake-up before the continuation. Otherwise
            // the wake-up would still be queued, and would keep the loop for the wrong reason.
            await Wait.UntilAsync(() => Volatile.Read(ref waitsForInput) >= 2, "the loop waits for input again");

            // The first turn's input is retired too, or its grace would keep the loop for the wrong reason.
            await Wait.UntilAsync(
                () => pool.GetOutstandingInputCountForTest("thread-admitting") == 0,
                "the first turn's input is retired"
            );
            (await loop.TryResolveToolCallAsync(AskCallId, "blue", isError: false))
                .Should()
                .Be(ResolveToolCallOutcome.Resolved);
            await taken.Task.WaitAsync(TimeSpan.FromSeconds(10));
            loop.CurrentRunId.Should().BeNull("the continuation's run has no id yet");
            (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty();
            pool.GetOutstandingInputCountForTest("thread-admitting").Should().Be(0);

            time.Advance(TimeSpan.FromHours(1));

            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the loop is starting the continuation");
        }
        finally
        {
            release.TrySetResult();
        }

        await Wait.UntilAsync(
            () => Volatile.Read(ref requests.Value) == 2 && !pool.IsRunInProgress("thread-admitting"),
            "the continuation ran to its end"
        );
    }

    [Fact]
    public async Task UnloadsALoopWhoseRunLoopFailed_AndTheNextRequestRebuildsIt()
    {
        // A store fault while starting a run ends the run loop. Input sent after that is queued for a loop
        // that will never take it, so it must not hold the conversation: unloading is the way back.
        var time = NewClock();
        var store = new RunLedgerFaultStore { FailUpserts = true };
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(
                AnsweringProvider(),
                new FunctionRegistry(),
                threadId,
                store: store,
                persistRunLedger: true
            )
        );
        var failed = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-failed", Mode);
        await Wait.UntilAsync(() => failed.IsRunning, "the pool started the run loop");
        _ = await failed.SendAsync([new TextMessage { Text = "hello", Role = Role.User }]);
        await Wait.UntilAsync(() => !failed.IsRunning, "the run loop failed starting the run");
        _ = await failed.SendAsync([new TextMessage { Text = "anyone there?", Role = Role.User }]);
        failed.HasPendingLoopWork.Should().BeTrue("the follow-up is queued");

        time.Advance(TimeSpan.FromHours(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the inputs' grace starts at this sweep");

        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);

        store.FailUpserts = false;
        var rebuilt = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-failed", Mode);
        rebuilt.Should().NotBeSameAs(failed);
        await DrainRunAsync(rebuilt, "hello again");
        rebuilt.IsRunning.Should().BeTrue("the rebuilt loop ran the turn and is waiting for the next");
    }

    [Fact]
    public async Task KeepsALoop_UnfrozenBetweenTheSweepsReadsAndItsLock_WithInputStillQueued()
    {
        // A frozen loop's queued input does not count: the loop cannot run it, and the accepted-input
        // grace lets the conversation go. Unfrozen between the sweep's reads and its lock, the loop can
        // run it, and nothing the pool has heard of says so: the input was accepted long ago. Only the
        // re-read of the loop's own work under the lock keeps the conversation.
        var time = NewClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = CreatePool(
            time,
            threadId =>
            {
                // Frozen and held before it starts, so the loop parks at the freeze gate and never takes
                // the input: no run can start and keep the conversation for another reason.
                var frozen = new MultiTurnAgentLoop(AnsweringProvider(), new FunctionRegistry(), threadId)
                {
                    BeforeTakingNextWorkForTest = ct => release.Task.WaitAsync(ct),
                };
                frozen.Freeze();
                return frozen;
            }
        );
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-frozen", Mode);
        // The pool starts the run loop on the thread pool. Only a running loop's queued input counts.
        await Wait.UntilAsync(() => loop.IsRunning, "the pool started the run loop");
        var unfrozen = false;
        try
        {
            _ = await loop.SendAsync([new TextMessage { Text = "hello", Role = Role.User }]);
            time.Advance(TimeSpan.FromHours(1));
            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the input's grace starts at this sweep");

            loop.HasPendingLoopWork.Should().BeTrue("the input is still queued");
            loop.CurrentRunId.Should().BeNull("no run has taken it");
            time.Advance(IdleAfter);
            pool.BeforeUnloadLockForTest = _ =>
            {
                loop.Unfreeze();
                unfrozen = true;
                return Task.CompletedTask;
            };

            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the unfrozen loop can run the input now");
            unfrozen.Should().BeTrue("the sweep got past its reads to the lock");
            pool.HasAgent("thread-frozen").Should().BeTrue();
        }
        finally
        {
            pool.BeforeUnloadLockForTest = null;
            loop.BeforeTakingNextWorkForTest = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task KeepsALoop_WithAnArmedWait_UntilTheWaitIsDone()
    {
        // A notify-mode Wait returns at once and stays armed. Unloading would cancel it, and nothing
        // would bring the conversation back to hear it fire.
        var time = NewClock();
        var source = new ManualTriggerSource();
        var requests = new StrongBox<int>();
        await using var pool = CreatePool(time, threadId => WaitingLoop(threadId, "notify", source, requests));
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-waiting", Mode);
        await DrainRunAsync(loop, "tell me when it happens");
        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty("a notify wait does not park the run");
        await WaitUntilInputRetiredAsync(pool, "thread-waiting");

        time.Advance(TimeSpan.FromHours(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the wait is still armed");

        // Two requests armed the wait and read its immediate result; the fire's turn is the third.
        await source.FireAsync("it happened");
        await Wait.UntilAsync(
            async () =>
                Volatile.Read(ref requests.Value) == 3
                && !pool.IsRunInProgress("thread-waiting")
                && pool.GetOutstandingInputCountForTest("thread-waiting") == 0
                && !await loop.HasLiveWorkAsync(),
            "the wait fired once, its turn ran, and the wait is done"
        );

        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    [Fact]
    public async Task KeepsALoop_WithABlockingWaitSettledEarly_UntilTheWaitIsDone()
    {
        // A user message settles a parked Wait early so its turn can run. The call is answered, but the
        // wait stays armed and its real result still has to reach this conversation.
        var time = NewClock();
        var source = new ManualTriggerSource();
        var requests = new StrongBox<int>();
        await using var pool = CreatePool(time, threadId => WaitingLoop(threadId, "block", source, requests));
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-waiting", Mode);
        await DrainRunAsync(loop, "wait for the build");
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle("the run is parked on the wait");

        _ = await loop.SendAsync([new TextMessage { Text = "do something else meanwhile", Role = Role.User }]);
        await Wait.UntilAsync(
            async () =>
                Volatile.Read(ref requests.Value) == 2
                && !pool.IsRunInProgress("thread-waiting")
                && pool.GetOutstandingInputCountForTest("thread-waiting") == 0
                && (await loop.GetDeferredToolCallsAsync()).Count == 0,
            "the message settled the wait early and its turn ran"
        );

        time.Advance(TimeSpan.FromHours(1));
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the wait is still armed");

        await source.FireAsync("the build finished");
        await Wait.UntilAsync(
            async () =>
                Volatile.Read(ref requests.Value) == 3
                && !pool.IsRunInProgress("thread-waiting")
                && pool.GetOutstandingInputCountForTest("thread-waiting") == 0
                && !await loop.HasLiveWorkAsync(),
            "the wait fired, its late result ran as a turn, and the wait is done"
        );

        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    [Fact]
    public async Task KeepsALoop_WhoseFiredWaitHasNotDeliveredItsResultYet()
    {
        // A fired wait stops counting as armed before its result reaches the loop: teardown first
        // disposes its trigger. Unloading in between would drop the result.
        var time = NewClock();
        var source = new ManualTriggerSource();
        var requests = new StrongBox<int>();
        await using var pool = CreatePool(time, threadId => WaitingLoop(threadId, "block", source, requests));
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-waiting", Mode);
        await DrainRunAsync(loop, "wait for the build");
        _ = await loop.SendAsync([new TextMessage { Text = "do something else meanwhile", Role = Role.User }]);
        await Wait.UntilAsync(
            async () =>
                Volatile.Read(ref requests.Value) == 2
                && !pool.IsRunInProgress("thread-waiting")
                && pool.GetOutstandingInputCountForTest("thread-waiting") == 0
                && (await loop.GetDeferredToolCallsAsync()).Count == 0,
            "the message settled the wait early and its turn ran"
        );

        var disarming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.HoldNextDisarm(async () =>
        {
            disarming.TrySetResult();
            await release.Task;
        });
        var fired = Task.Run(() => source.FireAsync("the build finished"));
        try
        {
            await disarming.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromHours(1));

            (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0, "the wait's result has not reached the loop");
        }
        finally
        {
            release.TrySetResult();
        }

        await fired.WaitAsync(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(
            async () =>
                Volatile.Read(ref requests.Value) == 3
                && !pool.IsRunInProgress("thread-waiting")
                && pool.GetOutstandingInputCountForTest("thread-waiting") == 0
                && !await loop.HasLiveWorkAsync(),
            "the wait's late result ran as a turn, and the wait is done"
        );
        time.Advance(IdleAfter);
        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(1);
    }

    private const string AskCallId = "tc_colour";

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static MultiTurnAgentPool CreatePool(FakeTimeProvider time, Func<string, IMultiTurnAgent> agentFactory) =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(agentFactory(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance
        )
        {
            TimeProvider = time,
        };

    private static MultiTurnAgentLoop Track(List<MultiTurnAgentLoop> built, MultiTurnAgentLoop loop)
    {
        built.Add(loop);
        return loop;
    }

    private static MultiTurnAgentLoop IdleLoop(string threadId) =>
        new(BlockingProvider(), new FunctionRegistry(), threadId);

    // The default child never yields, so it stays Running for the whole test. Children keep their
    // transcripts in a store, as in the host: only a child with one can be released.
    private static SubAgentOptions WorkerTemplate(Func<IStreamingAgent>? childProvider = null)
    {
        var store = new InMemoryConversationStore();
        return new()
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = childProvider ?? BlockingProvider,
                },
            },
            MaxConcurrentSubAgents = 5,
            DefaultConversationStoreFactory = _ => store,
        };
    }

    /// <summary>
    /// A root loop taking part in a collaboration, so each child gets a manager of its own. Children
    /// run on <c>worker</c> (<paramref name="workerProvider"/>) or <c>blocker</c>, which never answers.
    /// </summary>
    private static MultiTurnAgentLoop CollaboratingLoop(string threadId, Func<IStreamingAgent> workerProvider)
    {
        var options = WorkerTemplate(workerProvider);
        return new MultiTurnAgentLoop(
            AnsweringProvider(),
            new FunctionRegistry(),
            threadId,
            subAgentOptions: options with
            {
                Templates = new Dictionary<string, SubAgentTemplate>(options.Templates)
                {
                    ["blocker"] = new SubAgentTemplate
                    {
                        Name = "blocker",
                        SystemPrompt = "You are a worker.",
                        AgentFactory = BlockingProvider,
                    },
                },
            },
            collaboration: AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions { MaxDelegationDepth = 2 })
        );
    }

    private static Task<string> SpawnAsync(
        SubAgentManager manager,
        string template,
        string name,
        bool runInBackground = false
    ) =>
        manager.SpawnAsync(
            template,
            "a task",
            name: name,
            runInBackground: runInBackground,
            role: "helper",
            description: "Helps."
        );

    private static SubAgentManager ChildManager(SubAgentManager manager, string name)
    {
        manager.TryGetAgent(name, out var child).Should().BeTrue();
        return child.Should().BeOfType<MultiTurnAgentLoop>().Subject.SubAgentManager!;
    }

    /// <summary>
    /// Spawns a sub-agent in the foreground and waits until its run has settled. The spawn returns with the
    /// answer before the run's completion side effects end, and until they do the sub-agent cannot be
    /// released.
    /// </summary>
    private static async Task<string> SpawnSettledAsync(SubAgentManager manager, string task, string name)
    {
        SubAgentState? finished = null;
        manager.AfterSettlingRunCompletionForTest = (state, _) =>
        {
            finished ??= state;
            return Task.CompletedTask;
        };
        var answer = await manager.SpawnAsync("worker", task, name: name);
        await Wait.UntilAsync(() => finished is { IsSettled: true }, "the sub-agent's run has settled");
        manager.AfterSettlingRunCompletionForTest = null;
        return answer;
    }

    /// <summary>
    /// Waits until no run is active and the pool has retired the thread's accepted input. The pool retires
    /// it on its own subscriber, which <see cref="DrainRunAsync"/> does not wait for. Input still
    /// outstanding starts a grace at the next sweep, which would keep the loop for the wrong reason.
    /// </summary>
    private static Task WaitUntilInputRetiredAsync(MultiTurnAgentPool pool, string threadId) =>
        Wait.UntilAsync(
            () => !pool.IsRunInProgress(threadId) && pool.GetOutstandingInputCountForTest(threadId) == 0,
            "no run is active and the pool retired the accepted input"
        );

    private static Task WaitUntilSettledAsync(MultiTurnAgentPool pool, MultiTurnAgentLoop loop, string threadId) =>
        Wait.UntilAsync(
            async () =>
                !await loop.HasLiveWorkAsync()
                && !pool.IsRunInProgress(threadId)
                && pool.GetOutstandingInputCountForTest(threadId) == 0,
            "the whole tree has settled"
        );

    private static async Task DrainRunAsync(MultiTurnAgentLoop loop, string text)
    {
        await foreach (
            var _ in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = text, Role = Role.User }]))
        )
        {
            // drain until the run parks
        }
    }

    /// <summary>
    /// Answers the first request with a deferred AskUserQuestion and every later one with text, so the
    /// continuation that runs once the question is answered cannot re-defer it.
    /// </summary>
    private static IStreamingAgent AsksOnceThenAnswers(StrongBox<int>? requests = null)
    {
        var askCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(
                new
                {
                    context = "Need a colour.",
                    questions = new[]
                    {
                        new
                        {
                            prompt = "Which colour?",
                            options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                        },
                    },
                }
            ),
            ToolCallId = AskCallId,
            Role = Role.Assistant,
        };
        requests ??= new StrongBox<int>();
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                    Task.FromResult(
                        ToAsyncEnumerable(
                            Interlocked.Increment(ref requests.Value) == 1
                                ? askCall
                                : new TextMessage { Text = "Blue it is.", Role = Role.Assistant }
                        )
                    )
            );
        return provider.Object;
    }

    /// <summary>A loop whose model arms a two-hour <c>host_event</c> Wait in <paramref name="mode"/>.</summary>
    private static MultiTurnAgentLoop WaitingLoop(
        string threadId,
        string mode,
        ManualTriggerSource source,
        StrongBox<int> requests
    ) =>
        new(
            ArmsAWaitThenAnswers(mode, requests),
            new FunctionRegistry(),
            threadId,
            triggerOptions: new TriggerOptions
            {
                AdditionalRegistrations =
                [
                    new TriggerSourceRegistration
                    {
                        Kind = "host_event",
                        Description = "a host event",
                        ArgsSchema = "{}",
                        Capabilities = new TriggerCapabilities(
                            SupportsBlock: true,
                            SupportsNotify: true,
                            SupportsRestore: false
                        ),
                        Source = source,
                    },
                ],
            }
        );

    /// <summary>
    /// Arms a Wait on the first request and answers every later one with text. A notify wait is
    /// limited to one fire.
    /// </summary>
    private static IStreamingAgent ArmsAWaitThenAnswers(string mode, StrongBox<int> requests)
    {
        var args = new Dictionary<string, object>
        {
            ["kind"] = "host_event",
            ["args"] = new { },
            ["timeout"] = "2h",
            ["mode"] = mode,
        };
        if (mode == "notify")
        {
            args["maxFires"] = 1;
        }

        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = JsonSerializer.Serialize(args),
            ToolCallId = "tc_wait",
            Role = Role.Assistant,
        };
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                    Task.FromResult(
                        ToAsyncEnumerable(
                            Interlocked.Increment(ref requests.Value) == 1
                                ? waitCall
                                : new TextMessage { Text = "Noted.", Role = Role.Assistant }
                        )
                    )
            );
        return provider.Object;
    }

    /// <summary>A provider that answers request N with "Reply N." and records what each request carried.</summary>
    private static IStreamingAgent RecordingProvider(List<IMessage[]> requests)
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (messages, _, _) =>
                {
                    lock (requests)
                    {
                        requests.Add([.. messages]);
                        return Task.FromResult(
                            ToAsyncEnumerable(
                                new TextMessage { Text = $"Reply {requests.Count}.", Role = Role.Assistant }
                            )
                        );
                    }
                }
            );
        return provider.Object;
    }

    /// <summary>A provider that answers each request once <paramref name="gate"/> lets it.</summary>
    private static IStreamingAgent GatedProvider(Func<Task> gate)
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                    Task.FromResult(GatedStream(gate(), ct))
            );
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> GatedStream(
        Task gate,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await gate.WaitAsync(ct);
        yield return new TextMessage { Text = "Done.", Role = Role.Assistant };
    }

    /// <summary>A provider that answers every request at once.</summary>
    private static IStreamingAgent AnsweringProvider()
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
                Task.FromResult(ToAsyncEnumerable(new TextMessage { Text = "Done.", Role = Role.Assistant }))
            );
        return provider.Object;
    }

    /// <summary>A provider whose stream never yields and only unwinds on cancellation.</summary>
    private static IStreamingAgent BlockingProvider()
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                    Task.FromResult(BlockingStream(ct))
            );
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(params IMessage[] messages)
    {
        foreach (var message in messages)
        {
            yield return message;
        }

        await Task.CompletedTask;
    }

    private sealed class BackgroundTaskSink : ISandboxBindingSink
    {
        public string? PendingFor { get; set; }

        public void PublishEstablishedBinding(string threadId, SandboxEstablishedBinding binding) { }

        public void ClearEstablishedBinding(string threadId) { }

        public bool HasPendingBackgroundTask(string threadId) => threadId == PendingFor;
    }

    /// <summary>An in-memory store whose run-ledger writes fail while <see cref="FailUpserts"/> is set.</summary>
    private sealed class RunLedgerFaultStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();

        public bool FailUpserts { get; set; }

        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
            FailUpserts
                ? throw new InvalidOperationException("simulated run-ledger write failure")
                : _inner.UpsertRunLedgerAsync(entry, ct);

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
            _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.ListRunLedgerAsync(threadId, ct);

        public Task RecordAcceptedInputAsync(
            string threadId,
            string inputId,
            DateTimeOffset acceptedAt,
            CancellationToken ct = default
        ) => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
            _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListAcceptedInputIdsAsync(threadId, ct);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.LoadMessagesAsync(threadId, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) => _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);
    }

    /// <summary>A trigger source that fires only when the test says so.</summary>
    private sealed class ManualTriggerSource : ITriggerSource
    {
        private volatile ITriggerEventSink? _sink;
        private Func<Task>? _nextDisarm;

        public ValueTask<IArmedTrigger> ArmAsync(
            TriggerArmRequest request,
            ITriggerEventSink eventSink,
            CancellationToken cancellationToken
        )
        {
            _sink = eventSink;
            return ValueTask.FromResult<IArmedTrigger>(new Handle(this, request.WaitId));
        }

        public async Task FireAsync(string payload)
        {
            if (_sink is { } sink)
            {
                await sink.FireAsync(new TriggerFireEvent(payload), CancellationToken.None);
            }
        }

        /// <summary>The next disposal of an armed trigger runs <paramref name="hold"/> first.</summary>
        public void HoldNextDisarm(Func<Task> hold) => Volatile.Write(ref _nextDisarm, hold);

        private sealed class Handle(ManualTriggerSource source, string waitId) : IArmedTrigger
        {
            public string WaitId { get; } = waitId;

            public ValueTask DisposeAsync() =>
                Interlocked.Exchange(ref source._nextDisarm, null) is { } hold
                    ? new ValueTask(hold())
                    : ValueTask.CompletedTask;
        }
    }
}
