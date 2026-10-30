using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
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
    private InMemoryConversationStore? _store;

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
        (await SpawnSettledAsync(manager, "original task", "reviewer")).Should().Be("answer 1");
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
        _ = await SpawnSettledAsync(manager, "task", "recent");

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
        _ = await SpawnSettledAsync(manager, "task", "watched");
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
        _ = await SpawnSettledAsync(manager, "task", "unsaved");

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
        _ = await SpawnSettledAsync(manager, "task", "closed");

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

    [Fact]
    public async Task ARebuiltParent_CanStillMessageTheSubAgentItFinishedWith()
    {
        // #868: unloading a conversation disposes its root loop, and the rebuilt root started with no
        // sub-agents, so a finished child could no longer be listed or messaged although its transcript
        // was still in the store.
        var store = new InMemoryConversationStore();
        await RebuildParentAndFollowUpAsync(() => store);
    }

    [Fact]
    public async Task ARebuiltParent_RestoresItsFinishedSubAgent_FromASqliteStore()
    {
        // A SQLite store hands metadata properties back as JSON elements, not the strings written.
        var databasePath = Path.Combine(Path.GetTempPath(), $"rebuilt_parent_{Guid.NewGuid():N}.db");
        try
        {
            await RebuildParentAndFollowUpAsync(() => new SqliteConversationStore(databasePath));
        }
        finally
        {
            SqliteConnectionFactory.ClearPool(databasePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    [Fact]
    public async Task ARebuiltParent_SkipsSubAgentsItCannotRestore_AndKeepsTheRest()
    {
        // Restore runs inside the rebuilt root's history recovery, so one child it cannot restore must
        // neither fail the root nor cost the other children their place. Each record is edited into a
        // state the restore must leave alone: unreadable, from a template that no longer exists, still
        // running when the host died, or stopped because its parent loop went away (host shutdown or a
        // recreated parent); or not this root's to restore:
        // no record, another parent's record, or a record naming an agent whose thread is elsewhere. A child
        // whose run failed is kept, and its error must read exactly as it did before the rebuild.
        const string threadId = "thread-rebuilt-skips";
        var store = new InMemoryConversationStore();
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new() { SystemPrompt = "You are a worker.", AgentFactory = AnsweringProvider },
                ["failing"] = new() { SystemPrompt = "You fail.", AgentFactory = FailingProvider },
            },
            DefaultConversationStoreFactory = _ => store,
        };

        var threads = new Dictionary<string, string>();
        var agentIds = new Dictionary<string, string>();
        string failure;
        await using (var first = RootLoop(store, threadId, options))
        {
            _ = first.RunAsync();
            await foreach (
                var _ in first.ExecuteRunAsync(new UserInput([new TextMessage { Text = "hi", Role = Role.User }]))
            ) { }

            foreach (
                var name in new[]
                {
                    "kept",
                    "unreadable",
                    "retired",
                    "running",
                    "shutdown",
                    "recreated",
                    "unrecorded",
                    "adopted",
                    "moved",
                }
            )
            {
                _ = await SpawnSettledAsync(first.SubAgentManager!, $"{name} task", name);
            }

            failure = (
                await FluentActions
                    .Awaiting(() => SpawnSettledAsync(first.SubAgentManager!, "failed task", "failed", "failing"))
                    .Should()
                    .ThrowAsync<SubAgentExecutionException>()
            )
                .Which
                .Message;

            foreach (var agent in first.SubAgentManager!.ListAgents())
            {
                threads[agent.Name!] = agent.ThreadId;
                agentIds[agent.Name!] = agent.AgentId;
            }
        }

        await EditRecordAsync("unreadable", _ => "{not json");
        await EditRecordAsync(
            "retired",
            json => json.Replace("\"templateName\":\"worker\"", "\"templateName\":\"retired\"")
        );
        await EditRecordAsync("running", json => json.Replace("\"status\":\"completed\"", "\"status\":\"running\""));
        await EditRecordAsync(
            "shutdown",
            json => json.Replace("\"status\":\"completed\"", "\"status\":\"stopped\",\"failureCode\":\"host_shutdown\"")
        );
        await EditRecordAsync(
            "recreated",
            json =>
                json.Replace("\"status\":\"completed\"", "\"status\":\"stopped\",\"failureCode\":\"parent_recreated\"")
        );
        await store.UpdateMetadataAsync(
            threads["unrecorded"],
            metadata => metadata! with { Properties = metadata.Properties!.Remove(SubAgentRecord.Property) }
        );
        await EditRecordAsync(
            "adopted",
            json => json.Replace($"\"parentThreadId\":\"{threadId}\"", "\"parentThreadId\":\"thread-another-parent\"")
        );
        await EditRecordAsync(
            "moved",
            json => json.Replace($"\"agentId\":\"{agentIds["moved"]}\"", "\"agentId\":\"agent-99\"")
        );

        await using var rebuilt = RootLoop(store, threadId, options);
        _ = rebuilt.RunAsync();
        await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));

        rebuilt.SubAgentManager!.ListAgents().Select(a => a.Name).Should().BeEquivalentTo("kept", "failed");
        (await rebuilt.SubAgentManager.RestoreFinishedAgentsAsync(CancellationToken.None))
            .Should()
            .Be(0, "a sub-agent already registered is not registered twice");
        (await rebuilt.SubAgentManager.SendMessageAsync("kept", "follow-up", runInBackground: false))
            .Should()
            .StartWith("answer");

        failure.Should().Contain("boom-868");
        var failed = rebuilt.SubAgentManager.ListAgents().Single(a => a.Name == "failed");
        failed.Status.Should().Be(SubAgentStatus.Error);
        (
            await FluentActions
                .Awaiting(() => rebuilt.SubAgentManager.ObserveCompletionAsync(failed.AgentId, CancellationToken.None))
                .Should()
                .ThrowAsync<SubAgentExecutionException>()
        )
            .Which.Message.Should()
            .Be(failure);

        async Task EditRecordAsync(string name, Func<string, string> edit)
        {
            var record = (await store.LoadMetadataAsync(threads[name]))!.Properties![SubAgentRecord.Property];
            var edited = edit((string)record);
            edited.Should().NotBe((string)record, "the edit must change the {0} record", name);
            await store.UpdateMetadataAsync(
                threads[name],
                metadata =>
                    metadata! with
                    {
                        Properties = metadata.Properties!.SetItem(SubAgentRecord.Property, edited),
                    }
            );
        }
    }

    [Fact]
    public async Task AFollowUpOnAFinishedSubAgent_RecordsItAsRunning()
    {
        // #868: without the running write, a host that dies during the follow-up leaves the record saying
        // the previous run finished, and a rebuilt parent restores the child as finished over a
        // transcript that ends mid-run.
        var manager = CreateManager(AnswersOnceThenBlocks());
        _ = await SpawnSettledAsync(manager, "task", "followed");
        var threadId = manager.ListAgents().Single().ThreadId;
        (await RecordStatusAsync(threadId)).Should().Be(SubAgentStatus.Completed);

        _ = await manager.SendMessageAsync("followed", "follow-up", runInBackground: true);

        await Wait.UntilAsync(
            async () => await RecordStatusAsync(threadId) == SubAgentStatus.Running,
            "the follow-up's run is recorded as running",
            TimeSpan.FromSeconds(5)
        );
    }

    [Fact]
    public async Task ADirectInputToAFinishedSubAgent_RecordsItAsRunning()
    {
        // The same record, written when the warm loop restarts on its own (a direct input or a delayed
        // tool result) instead of through the manager.
        var manager = CreateManager(AnswersOnceThenBlocks());
        _ = await SpawnSettledAsync(manager, "task", "direct");
        var threadId = manager.ListAgents().Single().ThreadId;
        (await RecordStatusAsync(threadId)).Should().Be(SubAgentStatus.Completed);
        manager.TryGetAgent("direct", out var loop).Should().BeTrue();

        _ = await loop!.SendAsync([new TextMessage { Text = "direct input", Role = Role.User }]);

        await Wait.UntilAsync(
            async () => await RecordStatusAsync(threadId) == SubAgentStatus.Running,
            "the direct input's run is recorded as running",
            TimeSpan.FromSeconds(5)
        );
    }

    [Fact]
    public async Task ARebuiltParentWhoseStoreCannotListTranscripts_RestoresNothing_WithoutThrowing()
    {
        // Restore runs inside the root's history recovery, where a throw ends that recovery early and the
        // notify waits it restores next are not restored. So a store that cannot list leaves the root with
        // no restored sub-agents rather than throwing. Recovery runs only for a thread that already has
        // metadata, as a rebuilt root's thread does.
        const string threadId = "thread-unlistable";
        var store = new UnlistableStore(new InMemoryConversationStore());
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        );
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new() { SystemPrompt = "You are a worker.", AgentFactory = AnsweringProvider },
            },
            DefaultConversationStoreFactory = _ => store,
        };

        await using var rebuilt = RootLoop(store, threadId, options);
        _ = rebuilt.RunAsync();
        await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));

        store.ListingAttempts.Should().Be(1, "the root's recovery asked the store for the sub-agent transcripts");
        rebuilt.SubAgentManager!.ListAgents().Should().BeEmpty();
        (await rebuilt.SubAgentManager.RestoreFinishedAgentsAsync(CancellationToken.None)).Should().Be(0);
        store.ListingAttempts.Should().Be(2);
    }

    [Fact]
    public async Task ARecordFromANewerBuild_IsNotOverwritten_AndItsSubAgentIsNotRestored()
    {
        // During a rollback or a mixed-version deployment, this build must neither misread a record a newer
        // build wrote nor replace it with its own older shape.
        var store = new InMemoryConversationStore();
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new() { SystemPrompt = "You are a worker.", AgentFactory = AnsweringProvider },
            },
            DefaultConversationStoreFactory = _ => store,
        };
        await using var first = NewManager(options);
        _ = await SpawnSettledAsync(first, "kept task", "kept");
        _ = await SpawnSettledAsync(first, "newer task", "newer");
        var newerThread = first.ListAgents().Single(a => a.Name == "newer").ThreadId;
        var current = (string)(await store.LoadMetadataAsync(newerThread))!.Properties![SubAgentRecord.Property];
        var newer = current.Replace(
            $"\"{SubAgentRecord.SchemaVersionPropertyName}\":{SubAgentRecord.CurrentSchemaVersion}",
            $"\"{SubAgentRecord.SchemaVersionPropertyName}\":2,\"addedLater\":\"x\"",
            StringComparison.Ordinal
        );
        newer.Should().NotBe(current, "the record carries its schema version");
        await store.UpdateMetadataAsync(
            newerThread,
            metadata => metadata! with { Properties = metadata.Properties!.SetItem(SubAgentRecord.Property, newer) }
        );

        // A follow-up runs the child again, so its running and terminal writes both happen.
        SubAgentState? settled = null;
        first.AfterSettlingRunCompletionForTest = (state, _) =>
        {
            settled ??= state;
            return Task.CompletedTask;
        };
        _ = await first.SendMessageAsync("newer", "follow-up", runInBackground: false);
        await Wait.UntilAsync(() => settled is { IsSettled: true }, "the follow-up's run has settled");

        var metadata = await store.LoadMetadataAsync(newerThread);
        metadata!.Properties![SubAgentRecord.Property].Should().Be(newer, "this build left the newer record alone");
        SubAgentRecord.Read(metadata).Should().BeNull("a record from a newer build reads as absent");

        await using var rebuilt = NewManager(options);
        (await rebuilt.RestoreFinishedAgentsAsync(CancellationToken.None)).Should().Be(1);
        rebuilt.ListAgents().Select(a => a.Name).Should().Equal("kept");
    }

    [Fact]
    public async Task ARestoredSubAgent_KeepsItsSpawnArgumentsLineageAndCollaborationIdentity()
    {
        // The record carries everything a follow-up rebuilds the child from. A field dropped on the way
        // in or out would change the child after its parent is rebuilt: a removed tool handed back, a
        // lost role in the directory, a tool call no longer traced to the child.
        var store = new InMemoryConversationStore();
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new() { SystemPrompt = "You are a worker.", AgentFactory = AnsweringProvider },
            },
            DefaultConversationStoreFactory = _ => store,
        };

        await using var first = NewManager(options, RegisteredCollaborationRoot());
        SubAgentState? spawned = null;
        first.AfterSettlingRunCompletionForTest = (state, _) =>
        {
            spawned ??= state;
            return Task.CompletedTask;
        };
        _ = await first.SpawnAsync(
            "worker",
            "review the change",
            name: "reviewer",
            model: "model-override",
            addTools: ["extra_tool"],
            removeTools: ["withheld_tool"],
            spawningToolCallId: "call-42",
            role: "code reviewer",
            description: "Reviews changes for defects."
        );
        await Wait.UntilAsync(() => spawned is { IsSettled: true }, "the spawn's run has settled");
        var original = SubAgentRecord.Read(await store.LoadMetadataAsync(spawned!.Agent.ThreadId))!;

        var collaboration = RegisteredCollaborationRoot();
        await using var rebuilt = NewManager(options, collaboration);
        (await rebuilt.RestoreFinishedAgentsAsync(CancellationToken.None)).Should().Be(1);

        var entry = collaboration.Directory.Snapshot().Single(e => e.AgentId == original.AgentId);
        entry.Role.Should().Be("code reviewer");
        entry.Description.Should().Be("Reviews changes for defects.");
        entry.Name.Should().Be("reviewer");

        // The follow-up rebuilds the child from the restored state and records it again.
        SubAgentState? restarted = null;
        rebuilt.AfterSettlingRunCompletionForTest = (state, _) =>
        {
            restarted ??= state;
            return Task.CompletedTask;
        };
        _ = await rebuilt.SendMessageAsync("reviewer", "follow-up", runInBackground: false);
        await Wait.UntilAsync(() => restarted is { IsSettled: true }, "the follow-up's run has settled");

        restarted!.AddTools.Should().Equal("extra_tool");
        restarted.RemoveTools.Should().Equal("withheld_tool");
        restarted.ModelOverride.Should().Be("model-override");
        restarted.Lineage.SpawningToolCallId.Should().Be("call-42");
        var rewritten = SubAgentRecord.Read(await store.LoadMetadataAsync(restarted.Agent.ThreadId))!;
        rewritten
            .Should()
            .BeEquivalentTo(
                original with
                {
                    LastResult = rewritten.LastResult,
                    TerminalAtUtc = rewritten.TerminalAtUtc,
                },
                "everything but the new run's outcome survives the rebuild"
            );
    }

    /// <summary>A manager whose parent is root thread "thread-restored-parent", as every rebuild of it is.</summary>
    private static SubAgentManager NewManager(SubAgentOptions options, AgentCollaborationSetup? collaboration = null)
    {
        var parent = new Mock<IMultiTurnAgent>();
        _ = parent.SetupGet(p => p.ThreadId).Returns("thread-restored-parent");
        _ = parent
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));
        return new SubAgentManager(
            parent.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates),
            collaboration: collaboration
        );
    }

    /// <summary>A collaboration root registered in its own directory, as a root loop registers itself.</summary>
    private static AgentCollaborationSetup RegisteredCollaborationRoot()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(setup.Context, setup.Name, AgentCollaborationStatuses.Running);
        return setup;
    }

    private async Task<SubAgentStatus?> RecordStatusAsync(string threadId) =>
        SubAgentRecord.Read(await _store!.LoadMetadataAsync(threadId))?.Status;

    /// <summary>
    /// A root that spawned a sub-agent and saw it finish is disposed and rebuilt over the same thread; the
    /// rebuilt root must list the sub-agent as it was and reach it with its transcript intact.
    /// </summary>
    /// <param name="openStore">Opens a store over the one database every loop and child shares.</param>
    private async Task RebuildParentAndFollowUpAsync(Func<IConversationStore> openStore)
    {
        const string threadId = "thread-rebuilt-parent";
        var childBuilds = 0;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new()
                {
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () =>
                    {
                        _ = Interlocked.Increment(ref childBuilds);
                        return AnsweringProvider();
                    },
                },
            },
            DefaultConversationStoreFactory = _ => openStore(),
        };

        var firstStore = openStore();
        DateTimeOffset? finishedAt;
        await using (var first = RootLoop(firstStore, threadId, options))
        {
            _ = first.RunAsync();
            await foreach (
                var _ in first.ExecuteRunAsync(new UserInput([new TextMessage { Text = "hi", Role = Role.User }]))
            ) { }

            (await SpawnSettledAsync(first.SubAgentManager!, "original task", "reviewer")).Should().Be("answer 1");
            finishedAt = first.SubAgentManager!.ListAgents().Single().TerminalAtUtc;
        }

        var secondStore = openStore();
        await using var rebuilt = RootLoop(secondStore, threadId, options);
        _ = rebuilt.RunAsync();
        await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var manager = rebuilt.SubAgentManager!;

        manager.TryGetAgent("reviewer", out _).Should().BeTrue();
        manager
            .ListAgents()
            .Should()
            .ContainSingle(a =>
                a.Name == "reviewer" && a.Status == SubAgentStatus.Completed && a.TerminalAtUtc == finishedAt
            );
        manager.CheckAgents(["reviewer"]).Entries.Single().LastResult.Should().Be("answer 1");
        manager.ObserveCompletionAsync("agent-1", CancellationToken.None).IsCompleted.Should().BeTrue();

        (await manager.SendMessageAsync("reviewer", "follow-up", runInBackground: false)).Should().Be("answer 2");

        childBuilds.Should().Be(2, "the follow-up rebuilt the sub-agent's loop");
        _requests[1]
            .OfType<TextMessage>()
            .Select(m => m.Text)
            .Should()
            .ContainInOrder("original task", "answer 1", "follow-up");
        await DisposeIfOwnedAsync(firstStore);
        await DisposeIfOwnedAsync(secondStore);

        static async ValueTask DisposeIfOwnedAsync(IConversationStore store)
        {
            if (store is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }

    private static MultiTurnAgentLoop RootLoop(IConversationStore store, string threadId, SubAgentOptions options)
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
                Task.FromResult(ToAsyncEnumerable(new TextMessage { Text = "root answer", Role = Role.Assistant }))
            );
        return new MultiTurnAgentLoop(
            provider.Object,
            new FunctionRegistry(),
            threadId,
            store: store,
            subAgentOptions: options
        );
    }

    private SubAgentManager CreateManager(IStreamingAgent childProvider, bool withStore = true) =>
        CreateManager(() => childProvider, withStore);

    /// <summary>
    /// Spawns a sub-agent in the foreground and waits until its run has settled. The spawn returns with the
    /// answer before the run's completion side effects end, and until they do the sub-agent can be neither
    /// released nor closed.
    /// </summary>
    private static async Task<string> SpawnSettledAsync(
        SubAgentManager manager,
        string task,
        string name,
        string template = "worker"
    )
    {
        SubAgentState? finished = null;
        manager.AfterSettlingRunCompletionForTest = (state, _) =>
        {
            finished ??= state;
            return Task.CompletedTask;
        };
        try
        {
            return await manager.SpawnAsync(template, task, name: name);
        }
        finally
        {
            await Wait.UntilAsync(() => finished is { IsSettled: true }, "the sub-agent's run has settled");
            manager.AfterSettlingRunCompletionForTest = null;
        }
    }

    private SubAgentManager CreateManager(
        Func<IStreamingAgent> childFactory,
        bool withStore = true,
        int maxConcurrent = 5
    )
    {
        var store = _store = new InMemoryConversationStore();
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

    /// <summary>Fails every request with "boom-868".</summary>
    private static IStreamingAgent FailingProvider()
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
            .ThrowsAsync(new InvalidOperationException("boom-868"));
        return provider.Object;
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

    /// <summary>
    /// Builds providers that answer "answer 1" to the first request any of them gets, then block like
    /// <see cref="BlockingProvider"/>. Shared across builds, so a rebuilt loop's provider blocks too.
    /// </summary>
    private static Func<IStreamingAgent> AnswersOnceThenBlocks()
    {
        var requests = 0;
        return () =>
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
                        Task.FromResult(
                            Interlocked.Increment(ref requests) == 1
                                ? ToAsyncEnumerable(new TextMessage { Text = "answer 1", Role = Role.Assistant })
                                : BlockingStream(ct)
                        )
                );
            return provider.Object;
        };
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

    /// <summary>A store whose thread-id prefix lookup always throws; everything else is delegated.</summary>
    private sealed class UnlistableStore(IConversationStore inner)
        : IConversationStore,
            IConversationThreadIdLookupStore
    {
        private int _listingAttempts;

        public int ListingAttempts => Volatile.Read(ref _listingAttempts);

        public Task<IReadOnlyList<string>> ListThreadIdsByPrefixAsync(string prefix, CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _listingAttempts);
            throw new IOException("the store cannot list threads");
        }

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) => inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => inner.ListThreadsAsync(limit, offset, options, ct);
    }
}
