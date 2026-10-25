using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
        await using var pool = CreatePool(
            time,
            threadId => new MultiTurnAgentLoop(AsksOnceThenAnswers(), new FunctionRegistry(), threadId)
        );
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-asking", Mode);
        await DrainRunAsync(loop, "which colour?");
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
            async () => (await loop.GetDeferredToolCallsAsync()).Count == 0 && !pool.IsRunInProgress("thread-asking"),
            "the answered question's continuation finished"
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
        _ = await parent!.SubAgentManager!.SpawnAsync("worker", "short task", name: "done");

        time.Advance(IdleAfter + TimeSpan.FromMinutes(1));
        _ = pool.GetOrCreateAgent("thread-parent", Mode);

        (await pool.UnloadIdleAgentsAsync(IdleAfter)).Should().Be(0);
        parent.SubAgentManager.TryGetAgent("done", out var child).Should().BeTrue();
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
        _ = await manager.SpawnAsync("worker", "short task", name: "done");
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
    private static IStreamingAgent AsksOnceThenAnswers()
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
        var calls = 0;
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
                            Interlocked.Increment(ref calls) == 1
                                ? askCall
                                : new TextMessage { Text = "Blue it is.", Role = Role.Assistant }
                        )
                    )
            );
        return provider.Object;
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
        public string? PendingFor { get; init; }

        public void PublishEstablishedBinding(string threadId, SandboxEstablishedBinding binding) { }

        public void ClearEstablishedBinding(string threadId) { }

        public bool HasPendingBackgroundTask(string threadId) => threadId == PendingFor;
    }
}
