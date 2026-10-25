using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Covers #830's release of finished sub-agents (<see cref="SubAgentManager.ReleaseFinishedAgentsAsync"/>):
/// a conversation that stays loaded kept the whole loop of every child it had ever finished with. A
/// released child must be freed for real, stay addressable, and come back with its history when it is
/// messaged again.
/// </summary>
public class SubAgentIdleReleaseTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parent = new();
    private readonly List<List<IMessage>> _requests = [];
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _ = _parent
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    [Fact]
    public async Task AFinishedSubAgent_IsFreed_AndAFollowUpRebuildsItFromItsTranscript()
    {
        var manager = CreateManager(AnsweringProvider());
        (await manager.SpawnAsync("worker", "original task", name: "reviewer")).Should().Be("answer 1");
        var loop = WeakLoopOf(manager, "reviewer");

        (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.UtcNow)).Should().Be(1);

        await Wait.UntilAsync(
            () =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return !loop.IsAlive;
            },
            "the released loop is no longer reachable",
            TimeSpan.FromSeconds(10)
        );
        manager.ListAgents().Should().ContainSingle(a => a.Name == "reviewer" && a.Status == SubAgentStatus.Completed);
        (await manager.HasPendingAskUserQuestionInDescendantsAsync()).Should().BeFalse();

        (await manager.SendMessageAsync("reviewer", "follow-up", runInBackground: false)).Should().Be("answer 2");

        _requests.Should().HaveCount(2);
        _requests[1]
            .OfType<TextMessage>()
            .Select(m => m.Text)
            .Should()
            .ContainInOrder("original task", "answer 1", "follow-up");
        manager.TryGetAgent("reviewer", out var rebuilt).Should().BeTrue();
        rebuilt.Should().BeOfType<MultiTurnAgentLoop>();
    }

    [Fact]
    public async Task ASubAgentThatFinishedAfterTheCutoff_IsKept()
    {
        var manager = CreateManager(AnsweringProvider());
        _ = await manager.SpawnAsync("worker", "task", name: "recent");

        (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.UtcNow.AddMinutes(-5))).Should().Be(0);

        manager.TryGetAgent("recent", out var agent).Should().BeTrue();
        agent.Should().BeOfType<MultiTurnAgentLoop>();
    }

    [Fact]
    public async Task ARunningSubAgent_IsKept()
    {
        var manager = CreateManager(BlockingProvider());
        _ = await manager.SpawnAsync("worker", "long task", name: "busy", runInBackground: true);

        (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.UtcNow.AddDays(1))).Should().Be(0);

        manager.TryGetAgent("busy", out var agent).Should().BeTrue();
        agent.Should().BeOfType<MultiTurnAgentLoop>();
    }

    [Fact]
    public async Task AViewerOfAReleasedSubAgent_FollowsItIntoTheRebuiltLoop()
    {
        // The focused-agent view streams through SubscribeToAgentAcrossRestartsAsync. A release must
        // leave it waiting for the rebuilt loop, not end it as if the child were gone. The viewer is on
        // the original loop before the release starts, and the release is held after its dispose until
        // the viewer has decided: no stand-in, no rebuilt loop, only the release's restart signal.
        var manager = CreateManager(AnsweringProvider());
        _ = await manager.SpawnAsync("worker", "task", name: "watched");
        var decided = new TaskCompletionSource<ObservationContinuation>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        manager.ObservationDecidedForTest = decision => decided.TrySetResult(decision);
        manager.AfterReleaseDisposeForTest = _ => decided.Task.WaitAsync(TimeSpan.FromSeconds(10));
        manager.TryGetAgent("watched", out var agent).Should().BeTrue();
        var original = agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
        using var viewing = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sawFollowUpAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewer = Task.Run(
            async () =>
            {
                await foreach (var message in manager.SubscribeToAgentAcrossRestartsAsync("watched", viewing.Token))
                {
                    if (message is TextMessage { Text: "answer 2" })
                    {
                        sawFollowUpAnswer.TrySetResult();
                        return;
                    }
                }
            },
            viewing.Token
        );
        await Wait.UntilAsync(
            () => original.SubscriberCountForTests == 1,
            "the viewer is attached to the original loop"
        );

        (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.UtcNow)).Should().Be(1);
        (await decided.Task).Should().Be(ObservationContinuation.AwaitReplacement);
        _ = await manager.SendMessageAsync("watched", "follow-up", runInBackground: true);

        await sawFollowUpAnswer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await viewer.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ASubAgentWithoutAStore_IsKept()
    {
        // Its history lives only in its loop; a rebuild would start it from nothing.
        var manager = CreateManager(AnsweringProvider(), withStore: false);
        _ = await manager.SpawnAsync("worker", "task", name: "unsaved");

        (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.UtcNow)).Should().Be(0);

        manager.TryGetAgent("unsaved", out var agent).Should().BeTrue();
        agent.Should().BeOfType<MultiTurnAgentLoop>();
    }

    [Fact]
    public async Task ASubAgentStillDeliveringItsAnswer_IsNeitherReleasedNorClosed_UntilTheParentHasIt()
    {
        // A background child's answer reaches its parent after its run has settled. Releasing the child,
        // or unloading the conversation, in between would cancel that delivery: the parent never hears back.
        var deliveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _parent
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => new ValueTask<SendReceipt>(DeliverAsync()));
        var manager = CreateManager(AnsweringProvider());
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromSeconds(1);
        _ = await manager.SpawnAsync("worker", "task", name: "messenger", runInBackground: true);
        await deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.MaxValue)).Should().Be(0);
        manager.TryQuiesce().Should().BeFalse();

        finishDelivery.SetResult();
        await Wait.UntilAsync(
            async () => await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.MaxValue) == 1,
            "the child is released once its answer is delivered"
        );

        async Task<SendReceipt> DeliverAsync()
        {
            deliveryStarted.TrySetResult();
            await finishDelivery.Task;
            return new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task ASubAgentThatHasJustSettled_IsNeitherReleasedNorClosed_BeforeItsAnswerGoesOut()
    {
        // The instant between a run settling and its side effects starting: the child is already
        // "completed", but nothing has been delivered yet.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = CreateManager(AnsweringProvider());
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromSeconds(1);
        manager.AfterSettlingRunCompletionForTest = async (_, _) =>
        {
            settled.TrySetResult();
            await proceed.Task;
        };
        _ = await manager.SpawnAsync("worker", "task", name: "settling", runInBackground: true);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            (await manager.ReleaseFinishedAgentsAsync(DateTimeOffset.MaxValue)).Should().Be(0);
            manager.TryQuiesce().Should().BeFalse();
        }
        finally
        {
            proceed.TrySetResult();
        }

        await Wait.UntilAsync(
            () => _parent.Invocations.Any(i => i.Method.Name == nameof(IMultiTurnAgent.SendAsync)),
            "the parent receives the child's answer"
        );
    }

    [Fact]
    public async Task AManagerStartingAQueuedSpawn_DoesNotClose()
    {
        // A queued spawn leaves the queue before its agent is built and registered. In between it is
        // accepted work that neither list shows.
        var firstMayAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuilding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondMayFinish = new ManualResetEventSlim();
        var builds = 0;
        SubAgentState? first = null;
        var manager = CreateManager(
            () =>
            {
                if (Interlocked.Increment(ref builds) == 1)
                {
                    return AnswersAfter(firstMayAnswer.Task);
                }

                secondBuilding.TrySetResult();
                _ = secondMayFinish.Wait(TimeSpan.FromSeconds(30));
                return AnsweringProvider();
            },
            maxConcurrent: 1
        );
        manager.AfterSettlingRunCompletionForTest = (state, _) =>
        {
            first ??= state;
            return Task.CompletedTask;
        };
        _ = await manager.SpawnAsync("worker", "first", name: "first", runInBackground: true);
        _ = await manager.SpawnAsync("worker", "second", name: "second", runInBackground: true);

        try
        {
            firstMayAnswer.SetResult();
            await secondBuilding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Wait.UntilAsync(() => first is { IsSettled: true }, "the first sub-agent has fully settled");

            manager.TryQuiesce().Should().BeFalse();
        }
        finally
        {
            secondMayFinish.Set();
        }
    }

    [Fact]
    public async Task AClosedManager_RefusesNewMessagesAndSpawns()
    {
        // The pool closes a conversation's sub-agents just before it unloads the conversation. Anything
        // accepted after that would be killed by the disposal that follows, so it is refused instead.
        var manager = CreateManager(AnsweringProvider());
        _ = await manager.SpawnAsync("worker", "task", name: "closed");

        manager.TryQuiesce().Should().BeTrue();

        var message = () => manager.SendMessageAsync("closed", "follow-up", runInBackground: true);
        _ = await message.Should().ThrowAsync<ObjectDisposedException>();
        var spawn = () => manager.SpawnAsync("worker", "another task", name: "late");
        _ = await spawn.Should().ThrowAsync<ObjectDisposedException>();
        _requests.Should().ContainSingle("nothing ran after the manager closed");
    }

    [Fact]
    public async Task AManagerWithARunningSubAgent_DoesNotClose()
    {
        var manager = CreateManager(BlockingProvider());
        _ = await manager.SpawnAsync("worker", "long task", name: "busy", runInBackground: true);

        manager.TryQuiesce().Should().BeFalse();

        var message = () => manager.SendMessageAsync("busy", "more", runInBackground: true);
        _ = await message.Should().NotThrowAsync();
    }

    private SubAgentManager CreateManager(IStreamingAgent childProvider, bool withStore = true) =>
        CreateManager(() => childProvider, withStore);

    private SubAgentManager CreateManager(
        Func<IStreamingAgent> childFactory,
        bool withStore = true,
        int maxConcurrent = 5
    )
    {
        var store = new InMemoryConversationStore();
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new() { SystemPrompt = "You are a worker.", AgentFactory = childFactory },
            },
            MaxConcurrentSubAgents = maxConcurrent,
            DefaultConversationStoreFactory = withStore ? _ => store : null,
        };
        _manager = new SubAgentManager(
            _parent.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates)
        );
        return _manager;
    }

    // Not inlined, so no local in the test method keeps the loop reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WeakLoopOf(SubAgentManager manager, string name)
    {
        manager.TryGetAgent(name, out var agent).Should().BeTrue();
        return new WeakReference(agent.Should().BeOfType<MultiTurnAgentLoop>().Subject);
    }

    /// <summary>Answers each request with "answer N", recording what it was sent.</summary>
    private IStreamingAgent AnsweringProvider()
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
                    int count;
                    lock (_requests)
                    {
                        _requests.Add([.. messages]);
                        count = _requests.Count;
                    }

                    return Task.FromResult(
                        ToAsyncEnumerable(new TextMessage { Text = $"answer {count}", Role = Role.Assistant })
                    );
                }
            );
        return provider.Object;
    }

    /// <summary>A provider that answers "late answer" once <paramref name="mayAnswer"/> completes.</summary>
    private static IStreamingAgent AnswersAfter(Task mayAnswer)
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
                    Task.FromResult(AnswerAfterAsync(mayAnswer, ct))
            );
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> AnswerAfterAsync(
        Task mayAnswer,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await mayAnswer.WaitAsync(ct);
        yield return new TextMessage { Text = "late answer", Role = Role.Assistant };
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
}
