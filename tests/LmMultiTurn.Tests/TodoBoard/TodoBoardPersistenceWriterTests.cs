using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.TodoBoard;

/// <summary>
///     The change-driven durability half of PR 2 (#583, and #586's review finding F-005): every board
///     change schedules a coalesced save of the LATEST board, disposal flushes it (the evict/swap capture
///     point), an empty capture never clobbers a persisted non-empty board, and a thread with no metadata
///     row never gets one minted by the projection writer.
/// </summary>
public class TodoBoardPersistenceWriterTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static TodoBoardSnapshot Board(string title, int minutesAfterNoon = 0)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = "conv-1",
            CapturedAtUtc = Noon.AddMinutes(minutesAfterNoon),
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = title,
                },
            ],
        };
    }

    private static TodoBoardSnapshot EmptyBoard(int minutesAfterNoon = 0)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = "conv-1",
            CapturedAtUtc = Noon.AddMinutes(minutesAfterNoon),
            Tasks = [],
        };
    }

    private static Task SeedMetadataRowAsync(IConversationStore store, string threadId = "conv-1")
    {
        return store.UpdateMetadataAsync(
            threadId,
            existing => existing ?? new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 }
        );
    }

    /// <summary>
    ///     Wraps the in-memory store in a mock that counts durable writes and can block them, so a test
    ///     can hold a write in flight while more schedules pile up behind it.
    /// </summary>
    private static Mock<IConversationStore> WrapStore(
        InMemoryConversationStore inner,
        Action onWriteEntered,
        Func<Task>? writeGate = null
    )
    {
        var mock = new Mock<IConversationStore>();
        _ = mock.Setup(s => s.LoadMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken ct) => inner.LoadMetadataAsync(id, ct));
        _ = mock.Setup(s =>
                s.UpdateMetadataAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<ThreadMetadata?, ThreadMetadata>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                async (string id, Func<ThreadMetadata?, ThreadMetadata> update, CancellationToken ct) =>
                {
                    onWriteEntered();
                    if (writeGate is not null)
                    {
                        await writeGate();
                    }

                    await inner.UpdateMetadataAsync(id, update, ct);
                }
            );
        return mock;
    }

    [Fact]
    public async Task Burst_CoalescesIntoInFlightPlusOneTrailingWrite_AndTheTrailingWriteCarriesTheLatestBoard()
    {
        // Mutation that must go red: persisting per Schedule() call instead of coalescing (six writes),
        // or capturing the board at SCHEDULE time (trailing write carries an intermediate title).
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);

        var writes = 0;
        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new SemaphoreSlim(0);
        var store = WrapStore(
            inner,
            onWriteEntered: () =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    _ = firstWriteEntered.TrySetResult();
                }
            },
            writeGate: () => gate.WaitAsync()
        );

        var current = Board("v1");
        await using var writer = new TodoBoardPersistenceWriter(store.Object, "conv-1", () => current);

        writer.Schedule();
        await firstWriteEntered.Task.WaitAsync(WaitBudget);

        // Five more changes land while the first write is stuck in the store.
        for (var i = 2; i <= 5; i++)
        {
            current = Board($"v{i}", minutesAfterNoon: i);
            writer.Schedule();
        }

        current = Board("final", minutesAfterNoon: 10);
        writer.Schedule();

        gate.Release(10);
        var durable = await writer.FlushAsync().WaitAsync(WaitBudget);

        durable.Should().BeTrue();
        writes.Should().Be(2, "a burst collapses into the in-flight write plus one trailing write");

        var persisted = await ConversationTodoProjection.LoadAsync(inner, "conv-1");
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("final");
    }

    [Fact]
    public async Task EmptyCapture_IsNeverPersisted_OverANonEmptyBoard()
    {
        // From the writer's seat, "empty" and "this process has not seen the board yet" look identical,
        // so persisting it could clear a board another process wrote. Mutation that must go red:
        // dropping the IsEmpty guard.
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);
        await ConversationTodoProjection.SaveAsync(inner, Board("already durable"));

        var writes = 0;
        var store = WrapStore(inner, onWriteEntered: () => Interlocked.Increment(ref writes));

        await using var writer = new TodoBoardPersistenceWriter(
            store.Object,
            "conv-1",
            () => EmptyBoard(minutesAfterNoon: 30)
        );

        writer.Schedule();
        var durable = await writer.FlushAsync().WaitAsync(WaitBudget);

        // The skip is policy, not failure: the boundary is clean and nothing was written.
        durable.Should().BeTrue();
        writes.Should().Be(0);

        var persisted = await ConversationTodoProjection.LoadAsync(inner, "conv-1");
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("already durable");
    }

    [Fact]
    public async Task ThreadWithNoMetadataRow_IsSkipped_NeverMinted()
    {
        // Metadata rows are created (and ownership-stamped) by the conversation lifecycle; a projection
        // writer minting one would bring an unstamped row into existence (#586's ownership-stamp
        // hazard). The guard MECHANISM lives in SaveAsync (silent pre-probe skip, decline-by-throw in
        // the callback — pinned by the projection's own tests); this test pins the INVARIANT end-to-end
        // through the writer path: schedule against a rowless thread, clean boundary, no row after.
        var inner = new InMemoryConversationStore();
        await using var writer = new TodoBoardPersistenceWriter(inner, "conv-1", () => Board("orphan"));

        writer.Schedule();
        var durable = await writer.FlushAsync().WaitAsync(WaitBudget);

        durable.Should().BeTrue();
        (await inner.LoadMetadataAsync("conv-1")).Should().BeNull("no metadata row may be minted");
    }

    [Fact]
    public async Task Dispose_WaitsForThePendingWrite_SoEvictionCannotOutrunDurability()
    {
        // The pool disposes an entry's owned resources on eviction/swap/shutdown; this flush is the
        // capture point that makes the last change durable before the entry disappears. Mutation that
        // must go red: removing the FlushAsync await from DisposeAsync (disposal would complete while
        // the write is still stuck in the store).
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);

        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new SemaphoreSlim(0);
        var store = WrapStore(
            inner,
            onWriteEntered: () => _ = firstWriteEntered.TrySetResult(),
            writeGate: () => gate.WaitAsync()
        );

        var writer = new TodoBoardPersistenceWriter(store.Object, "conv-1", () => Board("last change"));

        writer.Schedule();
        await firstWriteEntered.Task.WaitAsync(WaitBudget);

        var disposal = writer.DisposeAsync().AsTask();
        await Task.Delay(100);
        disposal.IsCompleted.Should().BeFalse("disposal must wait for the in-flight write to land");

        gate.Release(10);
        await disposal.WaitAsync(WaitBudget);

        var persisted = await ConversationTodoProjection.LoadAsync(inner, "conv-1");
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("last change");
    }

    [Fact]
    public async Task ScheduleAfterDispose_IsInert()
    {
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);

        var writes = 0;
        var store = WrapStore(inner, onWriteEntered: () => Interlocked.Increment(ref writes));
        var writer = new TodoBoardPersistenceWriter(store.Object, "conv-1", () => Board("late"));
        await writer.DisposeAsync();

        writer.Schedule();
        var durable = await writer.FlushAsync().WaitAsync(WaitBudget);

        durable.Should().BeTrue();
        writes.Should().Be(0, "a disposed writer must not resurrect and write");
    }

    [Fact]
    public async Task SnapshotIsReStampedWithTheWritersThreadId_SoTheSaveLandsOnTheRootRow()
    {
        // Sub-agents mutate the shared board, so a capture can arrive stamped with the acting agent's
        // own id; SaveAsync keys the row off snapshot.ThreadId. Mutation that must go red: dropping the
        // re-stamp in PersistLatestAsync — the save lands under the sub-agent's own (deliberately
        // seeded) row and "conv-1" stays bare, failing both board assertions below.
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner, "conv-1");
        await SeedMetadataRowAsync(inner, "subagent-abc");

        await using var writer = new TodoBoardPersistenceWriter(
            inner,
            "conv-1",
            () => Board("from a sub-agent") with { ThreadId = "subagent-abc" }
        );

        writer.Schedule();
        (await writer.FlushAsync().WaitAsync(WaitBudget)).Should().BeTrue();

        var rootBoard = await ConversationTodoProjection.LoadAsync(inner, "conv-1");
        rootBoard.Should().NotBeNull();
        rootBoard!.ThreadId.Should().Be("conv-1");
        rootBoard.Tasks.Should().ContainSingle().Which.Title.Should().Be("from a sub-agent");
        (await ConversationTodoProjection.LoadAsync(inner, "subagent-abc")).Should().BeNull();
    }

    [Fact]
    public async Task DeclinedWrite_IsFinalNotTransient_SoItIsNotRetriedForever()
    {
        // SaveAsync declines a write for a deleted conversation by THROWING TodoBoardDeclinedException
        // from the update callback (there is no no-op value a callback can return — every store
        // persists what it gets back). A deleted conversation is a permanent decline, not a transient
        // fault: treating it as one would retry forever on the background drain.
        //
        // The decline path itself must execute (#590 review F-003a): the mock seeds the metadata row so
        // SaveAsync's pre-probe passes, then hands the update callback `existing: null` — the
        // delete-between-probe-and-write race — so the projection's own `if (existing is null) throw`
        // runs for real. Mutations that must go red: removing the TodoBoardDeclinedException catch in
        // PersistLatestAsync, AND deleting the projection's decline throw itself.
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);

        var writes = 0;
        var store = new Mock<IConversationStore>();
        _ = store
            .Setup(s => s.LoadMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken ct) => inner.LoadMetadataAsync(id, ct));
        _ = store
            .Setup(s =>
                s.UpdateMetadataAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<ThreadMetadata?, ThreadMetadata>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (string _, Func<ThreadMetadata?, ThreadMetadata> update, CancellationToken _) =>
                {
                    _ = Interlocked.Increment(ref writes);
                    // The row vanished between the probe and the write: the store's serialized
                    // transform sees no existing metadata. Whatever the callback returns would be
                    // persisted — only its throw prevents the write.
                    var written = update(null);
                    return inner.UpdateMetadataAsync("conv-1", _ => written);
                }
            );

        await using var writer = new TodoBoardPersistenceWriter(store.Object, "conv-1", () => Board("orphaned"));

        writer.Schedule();
        var durable = await writer.FlushAsync().WaitAsync(WaitBudget);

        durable.Should().BeTrue("a decline settles the schedule; only a transient failure stays pending");
        writes.Should().Be(1);

        // Nothing retries it on a second boundary, and — decisively — nothing was written.
        (await writer.FlushAsync().WaitAsync(WaitBudget))
            .Should()
            .BeTrue();
        writes.Should().Be(1);
        (await ConversationTodoProjection.LoadAsync(inner, "conv-1")).Should().BeNull();
    }

    [Fact]
    public async Task StoreFault_EvenAnInvalidOperationSubtype_StaysPendingAndFailsTheBoundary()
    {
        // #590 review F-003b: the decline signal rides an InvalidOperationException SUBTYPE, and the
        // store infrastructure throws InvalidOperationException subtypes of its own —
        // ObjectDisposedException from SqliteConnectionFactory.GetConnectionAsync derives from it. A
        // catch keyed on the base type would swallow such a store fault as a satisfied write: FlushAsync
        // would report a clean boundary and the disposal warning would never fire, with a false
        // "conversation no longer exists" record as the only trace. Mutation that must go red: widening
        // the writer's catch back to InvalidOperationException.
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);

        var writes = 0;
        var storeDisposed = true;
        var store = WrapStore(
            inner,
            onWriteEntered: () => Interlocked.Increment(ref writes),
            writeGate: () =>
                storeDisposed ? throw new ObjectDisposedException("SqliteConnectionFactory") : Task.CompletedTask
        );

        await using var writer = new TodoBoardPersistenceWriter(store.Object, "conv-1", () => Board("must land"));

        writer.Schedule();
        var firstBoundary = await writer.FlushAsync().WaitAsync(WaitBudget);
        firstBoundary.Should().BeFalse("a store fault is not a decline and must not be reported as durable");

        storeDisposed = false;
        var secondBoundary = await writer.FlushAsync().WaitAsync(WaitBudget);
        secondBoundary.Should().BeTrue();
        writes.Should().BeGreaterThanOrEqualTo(2, "the faulted write must have been retried");

        var persisted = await ConversationTodoProjection.LoadAsync(inner, "conv-1");
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("must land");
    }

    [Fact]
    public async Task FailedWrite_StaysPending_AndTheNextFlushRetriesIt()
    {
        // Inherited from the coalescing engine, asserted here because durability is THIS writer's whole
        // reason to exist: a failed save must report a dirty boundary and be retried, not vanish.
        var inner = new InMemoryConversationStore();
        await SeedMetadataRowAsync(inner);

        var writes = 0;
        var storeOffline = true;
        var store = WrapStore(
            inner,
            onWriteEntered: () => Interlocked.Increment(ref writes),
            writeGate: () =>
                // Test-controlled, not first-write-only: a flush racing the failed drain may retry
                // immediately, and that retry must ALSO fail or the first boundary check is flaky.
                storeOffline ? throw new IOException("store offline") : Task.CompletedTask
        );

        await using var writer = new TodoBoardPersistenceWriter(store.Object, "conv-1", () => Board("must land"));

        writer.Schedule();
        var firstBoundary = await writer.FlushAsync().WaitAsync(WaitBudget);
        firstBoundary.Should().BeFalse("a failed write must not be reported as a clean boundary");

        storeOffline = false;
        var secondBoundary = await writer.FlushAsync().WaitAsync(WaitBudget);
        secondBoundary.Should().BeTrue();
        writes.Should().BeGreaterThanOrEqualTo(2, "the failed write must have been retried");

        var persisted = await ConversationTodoProjection.LoadAsync(inner, "conv-1");
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("must land");
    }
}
