using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// A completed sub-agent keeps a reusable loop, so the very next thing that happens to it is often
/// another run: a direct follow-up message, or a delayed tool result arriving. Both go through the
/// manager's run-admission callback, which shares one gate with the monitor's completion handling.
///
/// The monitor must therefore hold that gate for the TRANSITION only — the stale-run check, the
/// terminal status/generation flip, the permit release, the completion latch. The ordered SIDE
/// EFFECTS of that transition (the durable metadata push and the parent relay) talk to a store and
/// to the parent's input channel, and either can block for as long as its peer wants. Holding the
/// admission gate across them makes a warm child's next run wait on the parent's backpressure, which
/// is exactly what warm retention exists to avoid.
///
/// These tests use a REAL <c>MultiTurnAgentLoop</c> child over a mock provider, because the
/// admission callback only exists on a real loop.
/// </summary>
public class SubAgentCompletionSideEffectDecouplingTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
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
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    [Fact]
    public async Task ABlockedParentRelay_DoesNotHoldUpTheNextRunOfTheWarmChild()
    {
        var relayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<List<IMessage>, string?, string?, CancellationToken>(
                async (_, _, _, ct) =>
                {
                    _ = relayEntered.TrySetResult();
                    await releaseRelay.Task.WaitAsync(ct);
                    return new SendReceipt("relayed", null, DateTimeOffset.UtcNow);
                }
            );

        var followUpEntered = ArrangeTwoRunProvider();
        _manager = CreateManager();
        var child = CaptureChildState(_manager);

        _ = await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);

        // The child's terminal completion is now relaying to a parent that will not accept it.
        await relayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        child.State.Should().NotBeNull("the monitor must have classified the completion by now");

        try
        {
            // A direct follow-up into the warm loop. Its run has to be admitted and reach the
            // provider while the relay above is still parked.
            _ = await child.State!.Agent.SendAsync([new TextMessage { Text = "follow-up", Role = Role.User }]);

            await followUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _ = releaseRelay.TrySetResult();
        }
    }

    [Fact]
    public async Task ABlockedTerminalMetadataWrite_DoesNotHoldUpTheNextRunOfTheWarmChild()
    {
        var persistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedMetadataStore(persistEntered, releasePersist);

        var followUpEntered = ArrangeTwoRunProvider();
        _manager = CreateManager(store);
        var child = CaptureChildState(_manager, store.Arm);

        _ = await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);

        // The child's terminal completion is now inside the durable push, which is not returning.
        await persistEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        child.State.Should().NotBeNull("the monitor must have classified the completion by now");

        try
        {
            _ = await child.State!.Agent.SendAsync([new TextMessage { Text = "follow-up", Role = Role.User }]);

            await followUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _ = releasePersist.TrySetResult();
        }
    }

    [Fact]
    public async Task ALateTerminalStatusPublish_DoesNotOverwriteTheRunThatOvertookIt()
    {
        // Running the side effects off the admission gate is what lets a newer run overtake them, so a
        // late one has to know it is late. Its durable push and its directory status describe a run that
        // is over; re-publishing "completed" over a child that is running again would make every other
        // agent in the hierarchy treat a live delegate as finished.
        var persistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedMetadataStore(persistEntered, releasePersist);

        // The stale completion's LAST ordered side effect. The monitor performs the durable push, then
        // the status-publish decision, then this relay, all in one sequential await chain — so observing
        // the relay is observing that the publish decision has already been taken, with no sleep to
        // guess at how long that took.
        var parentRelayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(() =>
            {
                _ = parentRelayed.TrySetResult();
                return new SendReceipt("relayed", null, DateTimeOffset.UtcNow);
            });

        var collaboration = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = collaboration.Directory.TryRegister(
            collaboration.Context,
            collaboration.Name,
            AgentCollaborationStatuses.Running,
            writeEndpoint: null
        );
        var followUpEntered = ArrangeTwoRunProvider(holdFollowUp: true);
        _manager = CreateManager(store, collaboration);
        var child = CaptureChildState(_manager, store.Arm);

        var spawnJson = await _manager.SpawnAsync(
            "test-agent",
            "first task",
            runInBackground: true,
            role: "tester",
            description: "Stands in for a delegate."
        );
        using var spawnDoc = System.Text.Json.JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // The terminal transition is parked in its durable push, which runs before the status publish.
        await persistEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A follow-up run is admitted and republishes Running while that push is still parked.
        _ = await child.State!.Agent.SendAsync([new TextMessage { Text = "follow-up", Role = Role.User }]);
        await followUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DirectoryStatusOf(collaboration, agentId)
            .Should()
            .Be(AgentCollaborationStatuses.Running, "the overtaking run publishes its own admission");

        // Releasing the stale push must not resurrect the finished run's view of the child.
        _ = releasePersist.TrySetResult();
        await parentRelayed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        DirectoryStatusOf(collaboration, agentId)
            .Should()
            .Be(
                AgentCollaborationStatuses.Running,
                "a terminal side effect that lands after a newer run was admitted must skip its publish, "
                    + "or the collaboration sees a live delegate as completed"
            );
        child.State!.Status.Should().Be(SubAgentStatus.Running);
    }

    [Fact]
    public async Task ABlockedDescendantQuestionNotification_DoesNotHoldUpTheDelayedResultSuccessorRun()
    {
        // The other barrier tests drive the SUCCESSOR through a direct follow-up message. This one
        // drives it through the delayed-result route instead: the child parks on its own
        // AskUserQuestion, the monitor's non-terminal completion owes a descendant-question
        // notification, and the answer arrives while that notification is still blocked on whoever
        // consumes it. Resolving the deferred call mints a child run on the SAME loop, and that run
        // must be admitted and reach the provider before the notification is released — otherwise the
        // human answering a question would be made to wait on the client that was told about it.
        var sinkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSink = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var successorEntered = ArrangeParkThenAnswerProvider();
        _manager = CreateManager(
            descendantQuestionSink: async (_, ct) =>
            {
                sinkEntered.TrySetResult();
                await releaseSink.Task.WaitAsync(ct);
            }
        );
        var child = CaptureChildState(_manager);

        _ = await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);

        // The parked completion is now inside the descendant-question notification, which is not
        // returning.
        await sinkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        child.State.Should().NotBeNull("the monitor must have classified the parked completion by now");
        var loop = child.State!.Agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;

        try
        {
            var deferred = await loop.GetDeferredToolCallsAsync();
            var pending = deferred
                .Should()
                .ContainSingle(d => d.FunctionName == AskUserQuestionToolProvider.ToolName)
                .Subject;

            // The human answers. This mints the delayed-result child run, which the loop's pump takes
            // through the same run-admission callback a direct input uses.
            _ = await loop.TryResolveToolCallAsync(pending.ToolCallId, "{\"answers\":[]}");

            await successorEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _ = releaseSink.TrySetResult();
        }
    }

    private static string? DirectoryStatusOf(AgentCollaborationSetup collaboration, string agentId) =>
        collaboration.Directory.Resolve(agentId).Entry?.Status;

    #region Helpers

    private sealed class ChildStateHolder
    {
        public SubAgentState? State { get; set; }
    }

    private static ChildStateHolder CaptureChildState(SubAgentManager manager, Action? onCapture = null)
    {
        var holder = new ChildStateHolder();
        manager.BeforeClassifyingRunCompletionForTest = (state, _) =>
        {
            if (holder.State is null)
            {
                holder.State = state;
                onCapture?.Invoke();
            }

            return Task.CompletedTask;
        };

        return holder;
    }

    /// <summary>
    /// Provider whose first call answers the spawn task and whose second call (the follow-up run)
    /// signals the returned source — the observable proof that the next run was admitted.
    /// </summary>
    private TaskCompletionSource ArrangeTwoRunProvider(bool holdFollowUp = false)
    {
        var followUpEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _subAgentMock
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
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        return ToAsyncEnumerable([new TextMessage { Text = "first reply", Role = Role.Assistant }]);
                    }

                    _ = followUpEntered.TrySetResult();
                    if (holdFollowUp)
                    {
                        // Keeps the follow-up run in flight so the child stays Running while the stale
                        // side effect is released.
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }

                    return ToAsyncEnumerable([new TextMessage { Text = "follow-up reply", Role = Role.Assistant }]);
                }
            );

        return followUpEntered;
    }

    /// <summary>
    /// Provider whose first call parks the run on the loop's own <c>AskUserQuestion</c> (the deferring
    /// tool every <see cref="MultiTurnAgentLoop"/> registers for itself) and whose second call — the
    /// delayed-result successor run the answer mints — signals the returned source.
    /// </summary>
    private TaskCompletionSource ArrangeParkThenAnswerProvider()
    {
        var successorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        return Task.FromResult(
                            ToAsyncEnumerable([
                                new ToolCallMessage
                                {
                                    Role = Role.Assistant,
                                    ToolCallId = "ask-1",
                                    FunctionName = AskUserQuestionToolProvider.ToolName,
                                    FunctionArgs =
                                        "{\"context\":\"needs a decision\",\"questions\":"
                                        + "[{\"prompt\":\"which one?\",\"options\":[{\"label\":\"a\"}]}]}",
                                },
                            ])
                        );
                    }

                    _ = successorEntered.TrySetResult();
                    return Task.FromResult(
                        ToAsyncEnumerable([new TextMessage { Text = "the answer", Role = Role.Assistant }])
                    );
                }
            );

        return successorEntered;
    }

    private SubAgentManager CreateManager(
        IConversationStore? store = null,
        AgentCollaborationSetup? collaboration = null,
        Func<NotifyMessage, CancellationToken, ValueTask>? descendantQuestionSink = null
    )
    {
        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 2,
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = new()
                {
                    Name = "test-agent",
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => _subAgentMock.Object,
                    ConversationStoreFactory = store is null ? null : _ => store,
                },
            },
        };

        return new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates),
            collaboration: collaboration,
            descendantQuestionSink: descendantQuestionSink
        );
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            await Task.Yield();
        }
    }

    /// <summary>
    /// In-memory store that parks the FIRST <see cref="IConversationStore.UpdateMetadataAsync"/> it sees
    /// after <see cref="Arm"/> — the manager's terminal durable push, which the caller arms from the
    /// monitor's pre-classification hook so an ordinary metadata write cannot be mistaken for it.
    /// </summary>
    private sealed class GatedMetadataStore(TaskCompletionSource entered, TaskCompletionSource release)
        : IConversationStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public async Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        )
        {
            if (Interlocked.CompareExchange(ref _armed, 2, 1) == 1)
            {
                _ = entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            await _inner.UpdateMetadataAsync(threadId, update, ct);
        }

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(int limit = 50, CancellationToken ct = default) =>
            _inner.ListThreadsAsync(limit, 0, null, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit,
            int offset,
            ConversationListOptions? options,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);
    }

    #endregion
}
