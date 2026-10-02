using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins fork creation and deletion: where an anchor cuts the history, when a fork is refused, what the
/// new conversation starts with, and that deleting never takes away messages a fork still reads.
/// </summary>
public sealed class ConversationForksTests : IAsyncLifetime
{
    private const string Source = "thread-src";
    private const string Fork = "thread-fork";
    private const string ForkOfFork = "thread-fork-2";
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    public static TheoryData<string> DurableKinds => ConversationStoreHarness.DurableKinds;

    [Fact]
    public async Task Resolve_CutsAfterARun_AfterAMessage_OrBeforeAUserMessage()
    {
        var store = await TwoTurnSourceAsync();

        var afterRun = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), null);
        afterRun
            .Point.Should()
            .BeEquivalentTo(
                new ForkPoint
                {
                    ThreadId = Source,
                    MessageId = "a1",
                    Seq = 2,
                }
            );
        afterRun.LatestRunId.Should().Be("run-1");

        var afterMessage = await ConversationForks.ResolveAsync(store, Source, new(AfterMessageId: "u2"), null);
        afterMessage.Point!.Seq.Should().Be(3);
        afterMessage.LatestRunId.Should().Be("run-2");

        var edit = await ConversationForks.ResolveAsync(store, Source, new(BeforeMessageId: "u2"), null);
        edit.Point.Should()
            .BeEquivalentTo(
                new ForkPoint
                {
                    ThreadId = Source,
                    MessageId = "a1",
                    Seq = 2,
                }
            );
        edit.EditedMessage!.Id.Should().Be("u2", "the caller offers the edited message's text for editing");

        var editFirst = await ConversationForks.ResolveAsync(store, Source, new(BeforeMessageId: "u1"), null);
        editFirst
            .Point.Should()
            .BeEquivalentTo(
                new ForkPoint
                {
                    ThreadId = Source,
                    MessageId = null,
                    Seq = 0,
                }
            );
        editFirst.LatestRunId.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_RefusesBadAnchors_ARunningTurn_AndAPendingDelayedResult()
    {
        var store = await TwoTurnSourceAsync();

        (await ConversationForks.ResolveAsync(store, Source, new(), null))
            .Refusal.Should()
            .Be(ForkRefusals.InvalidAnchor);
        (await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1", AfterMessageId: "u1"), null))
            .Refusal.Should()
            .Be(ForkRefusals.InvalidAnchor);
        (await ConversationForks.ResolveAsync(store, Source, new(BeforeMessageId: "a1"), null))
            .Refusal.Should()
            .Be(ForkRefusals.InvalidAnchor, "only a user message can be edited");
        (await ConversationForks.ResolveAsync(store, Source, new(AfterMessageId: "nope"), null))
            .Refusal.Should()
            .Be(ForkRefusals.AnchorNotFound);

        (await ConversationForks.ResolveAsync(store, Source, new(AfterMessageId: "u2"), activeRunId: "run-2"))
            .Refusal.Should()
            .Be(ForkRefusals.TurnInProgress);
        (await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), activeRunId: "run-2"))
            .Point.Should()
            .NotBeNull("a turn running after the fork point shares nothing with the fork");

        await store.AppendMessagesAsync(Source, [DeferredResult(Source, "run-3")]);
        (await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-3"), null))
            .Refusal.Should()
            .Be(ForkRefusals.PendingDelayedResult);
        (await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-2"), null))
            .Point.Should()
            .NotBeNull("the pending result lies after this fork point");
    }

    [Theory]
    [MemberData(nameof(DurableKinds))]
    public async Task Resolve_OfAnIdleLegacyConversation_NumbersItsRows_InTheOrderTheyRead(string kind)
    {
        // Rows written before Seq existed are numbered only by an append. An idle old conversation
        // gets no append, yet must still be forkable, with the fork seeing the order the user saw.
        _ = _harness.Open(kind);
        await _harness.SeedLegacyRowsAsync(
            kind,
            Source,
            [
                ConversationStoreHarness.Row(Source, "a1", 200, runId: "run-1", role: "Assistant"),
                ConversationStoreHarness.Row(Source, "u1", 100, runId: "run-1", role: "User"),
                ConversationStoreHarness.Row(Source, "u2", 300, runId: "run-2", role: "User"),
            ]
        );
        var store = _harness.Reopen(kind);
        await store.SaveMetadataAsync(Source, Seed(Source));

        var afterRun = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), null);
        afterRun.Refusal.Should().BeNull();
        afterRun.Point!.Seq.Should().Be(2);
        (await ConversationForks.ResolveAsync(store, Source, new(BeforeMessageId: "u2"), null))
            .Point!.MessageId.Should()
            .Be("a1");

        (await ConversationForks.CreateAsync(store, Fork, afterRun, Seed(Fork))).Should().NotBeNull();
        (await store.LoadMessagesAsync(Fork)).Select(m => m.Id).Should().Equal("u1", "a1");
        (await _harness.Reopen(kind).LoadMessagesAsync(Source))
            .Select(m => (m.Id, m.Seq))
            .Should()
            .Equal(("u1", 1L), ("a1", 2L), ("u2", 3L));
    }

    [Fact]
    public async Task Create_WritesLineage_TheRootOfTheFamily_AndTheLatestRun_AndRefusesAnExistingThread()
    {
        var store = await TwoTurnSourceAsync();

        var first = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-2"), null);
        var lineage = await ConversationForks.CreateAsync(store, Fork, first, Seed(Fork));

        lineage!.RootThreadId.Should().Be(Source);
        var metadata = await store.LoadMetadataAsync(Fork);
        metadata!.LatestRunId.Should().Be("run-2");
        metadata.Properties!["title"].Should().Be("seeded", "the host's seed is kept");
        ConversationLineage.Read(metadata)!.ForkedFrom.Should().BeEquivalentTo(first.Point);
        (await store.LoadMessagesAsync(Fork)).Select(m => m.Id).Should().Equal("u1", "a1", "u2", "a2");

        await store.AppendMessagesAsync(Fork, [ConversationStoreHarness.Row(Fork, "f1", 900, runId: "run-f")]);
        var second = await ConversationForks.ResolveAsync(store, Fork, new(AfterMessageId: "f1"), null);
        (await ConversationForks.CreateAsync(store, ForkOfFork, second, Seed(ForkOfFork)))!
            .RootThreadId.Should()
            .Be(Source, "a fork of a fork belongs to the original's family");

        var again = () => ConversationForks.CreateAsync(store, Fork, first, Seed(Fork));
        await again.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_InheritsTheCheckpointAndClearingThatCoverTheSharedHistory()
    {
        var store = await TwoTurnSourceAsync();
        _ = await CompactionStateProjection.UpdateAsync(
            store,
            Source,
            s =>
                s with
                {
                    ActiveCheckpointId = "cp-late",
                    ActiveBoundarySeq = 3,
                    ToolResultsClearedThroughSeq = 4,
                    History =
                    [
                        Checkpoint("cp-early", 1, CheckpointStatus.Superseded),
                        Checkpoint("cp-late", 4, CheckpointStatus.Active),
                    ],
                }
        );

        var point = await ConversationForks.ResolveAsync(store, Source, new(AfterMessageId: "u2"), null);
        _ = await ConversationForks.CreateAsync(store, Fork, point, Seed(Fork));

        var inherited = await CompactionStateProjection.LoadAsync(store, Fork);
        inherited!.ActiveCheckpointId.Should().Be("cp-early", "cp-late's row lies after the fork point");
        inherited.History.Should().ContainSingle().Which.Status.Should().Be(CheckpointStatus.Active);
        inherited.ToolResultsClearedThroughSeq.Should().Be(3, "the fork's own results are never pre-cleared");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Delete_HidesAConversationForksStillRead_AndErasesItWithItsLastFork(string kind)
    {
        var store = _harness.Open(kind);
        await store.SaveMetadataAsync(Source, Seed(Source));
        await AppendAsync(store, Source, ("u1", "run-1", "User"), ("a1", "run-1", "Assistant"));
        var point = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), null);
        _ = await ConversationForks.CreateAsync(store, Fork, point, Seed(Fork));

        (await ConversationForks.DeleteAsync(store, Source)).Should().Be(ForkDeleteOutcome.Hidden);
        ConversationLineage.Read(await store.LoadMetadataAsync(Source))!.DeletedAt.Should().NotBeNull();
        (await store.LoadMessagesAsync(Fork))
            .Select(m => m.Id)
            .Should()
            .Equal(["u1", "a1"], "the fork still reads the deleted original's messages");

        (await ConversationForks.DeleteAsync(store, Fork)).Should().Be(ForkDeleteOutcome.Deleted);
        (await store.LoadMetadataAsync(Fork)).Should().BeNull();
        (await store.LoadMetadataAsync(Source)).Should().BeNull("its last reader is gone");
        (await store.LoadMessagesAsync(Source)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task List_LeavesOutDeletedOriginals_WhenAsked_BeforeItPages(string kind)
    {
        // The deleted original is the newest row. Filtering after the page was cut would return an
        // empty first page and skip a real conversation.
        var store = _harness.Open(kind);
        await store.SaveMetadataAsync("thread-old", Seed("thread-old") with { LastUpdated = 100 });
        await store.SaveMetadataAsync(Fork, Seed(Fork) with { LastUpdated = 200 });
        await store.SaveMetadataAsync(
            Source,
            ConversationLineage.Write(
                Seed(Source) with
                {
                    LastUpdated = 300,
                },
                new ConversationLineage { DeletedAt = 1 }
            )
        );
        var hide = new ConversationListOptions { ExcludeDeleted = true };
        var scope = ConversationListScope.ForTenantIncludingUntenanted("tenant-a");

        foreach (
            var list in new Func<int, ConversationListOptions?, Task<IReadOnlyList<ThreadMetadata>>>[]
            {
                (offset, options) => store.ListThreadsAsync(1, offset, options),
                (offset, options) => store.ListThreadsAsync(scope, 1, offset, options),
            }
        )
        {
            (await list(0, hide)).Select(m => m.ThreadId).Should().Equal(Fork);
            (await list(1, hide)).Select(m => m.ThreadId).Should().Equal("thread-old");
            (await list(0, null)).Select(m => m.ThreadId).Should().Equal([Source], "by default every row is listed");
        }
    }

    [Fact]
    public async Task Delete_OfAForkWithSiblings_LeavesTheHiddenOriginalForThem()
    {
        var store = await TwoTurnSourceAsync();
        var point = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), null);
        _ = await ConversationForks.CreateAsync(store, Fork, point, Seed(Fork));
        _ = await ConversationForks.CreateAsync(store, ForkOfFork, point, Seed(ForkOfFork));
        _ = await ConversationForks.DeleteAsync(store, Source);

        (await ConversationForks.DeleteAsync(store, Fork)).Should().Be(ForkDeleteOutcome.Deleted);

        (await store.LoadMetadataAsync(Source)).Should().NotBeNull();
        (await store.LoadMessagesAsync(ForkOfFork)).Select(m => m.Id).Should().Equal("u1", "a1");
    }

    [Fact]
    public async Task Create_RacingADeleteThatFoundNoForks_IsRefused_AndLeavesNoDanglingFork()
    {
        // The delete has scanned for forks and found none. A fork created before the delete erases
        // the source would point at messages that are about to vanish, so it must be refused.
        var store = new PauseFirstListStore(await TwoTurnSourceAsync());
        var point = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), null);

        var delete = ConversationForks.DeleteAsync(store, Source);
        await store.ListReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var create = ConversationForks.CreateAsync(store, Fork, point, Seed(Fork));
        store.Resume.SetResult();

        (await delete).Should().Be(ForkDeleteOutcome.Deleted);
        (await create).Should().BeNull("its source was erased");
        (await store.LoadMetadataAsync(Fork)).Should().BeNull();
    }

    [Fact]
    public async Task Create_OfADeletedConversation_IsRefused()
    {
        var store = await TwoTurnSourceAsync();
        var point = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-1"), null);
        _ = await ConversationForks.CreateAsync(store, Fork, point, Seed(Fork));
        (await ConversationForks.DeleteAsync(store, Source)).Should().Be(ForkDeleteOutcome.Hidden);

        (await ConversationForks.CreateAsync(store, ForkOfFork, point, Seed(ForkOfFork))).Should().BeNull();
        (await store.LoadMetadataAsync(ForkOfFork)).Should().BeNull();
    }

    private async Task<IConversationStore> TwoTurnSourceAsync()
    {
        var store = _harness.Open("memory");
        await store.SaveMetadataAsync(Source, Seed(Source));
        await AppendAsync(
            store,
            Source,
            ("u1", "run-1", "User"),
            ("a1", "run-1", "Assistant"),
            ("u2", "run-2", "User"),
            ("a2", "run-2", "Assistant")
        );
        return store;
    }

    private static async Task AppendAsync(
        IConversationStore store,
        string threadId,
        params (string Id, string RunId, string Role)[] rows
    )
    {
        // One append per row so each row's Seq is its position, whatever its timestamp.
        foreach (var (id, runId, role) in rows)
        {
            await store.AppendMessagesAsync(
                threadId,
                [ConversationStoreHarness.Row(threadId, id, 100, runId: runId, role: role)]
            );
        }
    }

    private static ThreadMetadata Seed(string threadId) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>.Empty.Add("title", "seeded"),
        };

    private static PersistedMessage DeferredResult(string threadId, string runId) =>
        MessagePersistenceConverter.ToPersistedMessage(
            new ToolCallResultMessage
            {
                ToolCallId = "tc-wait",
                ToolName = "wait_for_human",
                Result = string.Empty,
                IsDeferred = true,
                DeferredAt = 1_700_000_000_000,
                Role = Role.User,
                RunId = runId,
            },
            threadId,
            runId
        );

    /// <summary>Holds the first catalog listing - a delete's fork scan - until <see cref="Resume"/> is set.</summary>
    private sealed class PauseFirstListStore(IConversationStore inner) : IConversationStore
    {
        private int _lists;

        public TaskCompletionSource ListReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        )
        {
            var listed = await inner.ListThreadsAsync(limit, offset, options, ct);
            if (Interlocked.Increment(ref _lists) == 1)
            {
                ListReached.SetResult();
                await Resume.Task.WaitAsync(ct);
            }

            return listed;
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
    }

    private static CheckpointEntry Checkpoint(string id, long rowSeq, CheckpointStatus status) =>
        new()
        {
            CheckpointId = id,
            Status = status,
            BoundarySeq = rowSeq - 1,
            WatermarkAtPrepare = rowSeq - 1,
            RowSeq = rowSeq,
            Trigger = CompactionTrigger.Preemptive,
            At = DateTimeOffset.UnixEpoch,
        };
}
