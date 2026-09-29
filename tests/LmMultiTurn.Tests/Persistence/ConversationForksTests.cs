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

    [Fact]
    public async Task Create_WritesLineage_TheRootOfTheFamily_AndTheLatestRun_AndRefusesAnExistingThread()
    {
        var store = await TwoTurnSourceAsync();

        var first = await ConversationForks.ResolveAsync(store, Source, new(AfterRunId: "run-2"), null);
        var lineage = await ConversationForks.CreateAsync(store, Fork, first, Seed(Fork));

        lineage.RootThreadId.Should().Be(Source);
        var metadata = await store.LoadMetadataAsync(Fork);
        metadata!.LatestRunId.Should().Be("run-2");
        metadata.Properties!["title"].Should().Be("seeded", "the host's seed is kept");
        ConversationLineage.Read(metadata)!.ForkedFrom.Should().BeEquivalentTo(first.Point);
        (await store.LoadMessagesAsync(Fork)).Select(m => m.Id).Should().Equal("u1", "a1", "u2", "a2");

        await store.AppendMessagesAsync(Fork, [ConversationStoreHarness.Row(Fork, "f1", 900, runId: "run-f")]);
        var second = await ConversationForks.ResolveAsync(store, Fork, new(AfterMessageId: "f1"), null);
        (await ConversationForks.CreateAsync(store, ForkOfFork, second, Seed(ForkOfFork)))
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
