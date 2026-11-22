using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins how <see cref="SqliteConversationStore"/> queues writes. SQLite lets one writer in at a time, and
/// Microsoft.Data.Sqlite waits for a busy write lock by sleeping the calling thread. Writers used to wait that way
/// while holding one of the store's pooled connections, so a burst of them took every connection and a
/// one-millisecond read waited for one (59.6 s on the review host). Writers now queue for one write connection
/// of their own, and reads keep theirs.
/// </summary>
public sealed class SqliteConversationStoreWriteQueueTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"writeq_{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteTestPools.Clear(_databasePath);
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reads_answer_while_writes_queue_behind_a_held_write_lock()
    {
        // Three read connections and twice as many writers: before the fix the writers took all three and slept in
        // SQLite's busy loop, so neither read below got a connection until the lock was released.
        var factory = new WriteSignallingFactory(new SqliteConnectionFactory(_databasePath, maxConnections: 3));
        await using var store = new SqliteConversationStore(factory, ownsFactory: true);
        var startedAt = DateTimeOffset.UtcNow;
        await store.RecordRunStartedAsync(
            new RunLifecycleState
            {
                RunId = "run-read",
                ThreadId = "thread-read",
                StartedAt = startedAt,
            }
        );
        var usage = new UsageRecord
        {
            LogicalCallId = "attempt-1",
            ProviderAttemptId = "attempt-1",
            RootConversationId = "thread-read",
            RequestedModel = "model",
            InputTokens = 10,
            OutputTokens = 1,
            Revision = 1,
        };
        await store.AppendUsageAsync(ConversationUsageAggregate.Fold("thread-read", [usage], 1), [usage]);

        // The write lock, held from outside the store: a slow writer, another process, a backup.
        await using var holder = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()
        );
        await holder.OpenAsync();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            _ = await begin.ExecuteNonQueryAsync();
        }

        var requestedBefore = factory.WritesRequested;
        var openedBefore = factory.WritesOpened;
        var writes = new List<Task>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var entry = new RunLedgerEntry(
                    "thread-write",
                    $"run-write-{i}",
                    RunStatus.Queued,
                    [],
                    startedAt,
                    startedAt
                );

                // Task.Run: a write blocked on the lock sleeps whichever thread calls it.
                writes.Add(Task.Run(() => store.UpsertRunLedgerAsync(entry)));
            }

            // Every writer has asked for the write connection, and one holds it and is waiting on the lock. Only then
            // do the reads run, so a writer that had not started yet cannot make the check below pass.
            await UntilAsync(() => factory.WritesRequested - requestedBefore == 6, "all six writers asked to write");
            await UntilAsync(() => factory.WritesOpened - openedBefore == 1, "one writer holds the write connection");

            var runState = await store.LoadRunLifecycleAsync("run-read").WaitAsync(Bound);
            var usageState = await store.LoadUsageAsync("thread-read").WaitAsync(Bound);

            runState.Should().NotBeNull();
            usageState.Should().NotBeNull();
            writes
                .Should()
                .OnlyContain(w => !w.IsCompleted, "the reads must have answered while every write was still queued");
        }
        finally
        {
            using var rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            _ = await rollback.ExecuteNonQueryAsync();
        }

        // Released inside the writers' busy timeout, every queued write lands.
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10));
        (await store.ListRunLedgerAsync("thread-write")).Should().HaveCount(6);
    }

    [Fact]
    public async Task A_queued_write_can_be_cancelled_and_the_next_write_still_gets_through()
    {
        await using var factory = new SqliteConnectionFactory(_databasePath, maxConnections: 1);
        await using var store = new SqliteConversationStore(factory);
        var now = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry("thread-c", "run-0", RunStatus.Queued, [], now, now));

        RunLedgerEntry Entry(int i) => new("thread-c", $"run-{i}", RunStatus.Queued, [], now, now);

        using var cancel = new CancellationTokenSource();
        Task queued;
        await using (var held = await factory.GetWriteConnectionAsync())
        {
            queued = store.UpsertRunLedgerAsync(Entry(1), cancel.Token);
            await Task.Delay(Settle);
            queued.IsCompleted.Should().BeFalse("the write connection is out, so the write queues for it");

            // The one read connection is still free while the write connection is out.
            (await store.LoadRunLedgerAsync("run-0").WaitAsync(Bound))
                .Should()
                .NotBeNull();

            cancel.Cancel();
            var cancelled = () => queued.WaitAsync(Bound);
            _ = await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }

        // Cancelling in the queue took no permit with it: the next write gets the connection at once.
        await store.UpsertRunLedgerAsync(Entry(2)).WaitAsync(Bound);
        (await store.LoadRunLedgerAsync("run-1")).Should().BeNull();
        (await store.LoadRunLedgerAsync("run-2")).Should().NotBeNull();
    }

    [Fact]
    public async Task Metadata_writes_wait_only_for_writes_to_the_same_thread()
    {
        await using var store = new SqliteConversationStore(_databasePath);
        await store.SaveMetadataAsync("thread-a", Metadata("thread-a", "initial"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var update = Task.Run(() =>
            store.UpdateMetadataAsync(
                "thread-a",
                existing =>
                {
                    entered.SetResult();

                    // The update delegate runs inside thread-a's lock; holding it here holds the lock.
                    _ = release.Wait(TimeSpan.FromSeconds(10));
                    return existing! with { Properties = existing.Properties!.SetItem("writer", "update") };
                }
            )
        );
        await entered.Task.WaitAsync(Bound);

        Task saveA;
        try
        {
            // Another thread's write does not wait for thread-a's lock.
            await store.SaveMetadataAsync("thread-b", Metadata("thread-b", "save")).WaitAsync(Bound);
            (await store.LoadMetadataAsync("thread-b"))!.Properties!["writer"].ToString().Should().Be("save");

            // A write to thread-a waits, and cancelling it while it waits leaves the lock as it was.
            using var cancel = new CancellationTokenSource();
            var cancelled = store.SaveMetadataAsync("thread-a", Metadata("thread-a", "cancelled"), cancel.Token);
            cancel.Cancel();
            var waitCancelled = () => cancelled.WaitAsync(Bound);
            _ = await waitCancelled.Should().ThrowAsync<OperationCanceledException>();

            saveA = store.SaveMetadataAsync("thread-a", Metadata("thread-a", "save"));
            (await Task.WhenAny(saveA, Task.Delay(Settle)))
                .Should()
                .NotBeSameAs(saveA, "a write to the same thread waits for the update holding its lock");
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(update, saveA).WaitAsync(Bound);

        // The save queued behind the update, so it landed last and the update did not overwrite it (#745).
        (await store.LoadMetadataAsync("thread-a"))!
            .Properties!["writer"]
            .ToString()
            .Should()
            .Be("save");
    }

    [Fact]
    public async Task A_message_write_waits_out_a_foreign_write_lock_held_longer_than_five_seconds()
    {
        // Some callers drop a row whose save fails (MultiTurnAgentBase.PersistMessageAsync logs and moves on), so a
        // write must wait out an outside hold as long as it did before writes had their own connection: 30 s.
        var factory = new WriteSignallingFactory(new SqliteConnectionFactory(_databasePath));
        await using var store = new SqliteConversationStore(factory, ownsFactory: true);
        await store.AppendMessagesAsync("thread-m", [Message("thread-m", "before")]);
        var openedBefore = factory.WritesOpened;

        await using var holder = await HoldWriteLockAsync();
        Task write;
        try
        {
            // Task.Run: a write blocked on the lock sleeps whichever thread calls it.
            write = Task.Run(() => store.AppendMessagesAsync("thread-m", [Message("thread-m", "during")]));

            // The six seconds start once the write holds its connection, so they are all spent waiting on the lock.
            await UntilAsync(() => factory.WritesOpened - openedBefore == 1, "the write holds the write connection");
            await Task.Delay(TimeSpan.FromSeconds(6));
            write.IsCompleted.Should().BeFalse("the write is still waiting for the lock, not failed");
        }
        finally
        {
            await RollbackAsync(holder);
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));
        (await store.LoadMessagesAsync("thread-m")).Select(m => m.Id).Should().Equal("before", "during");
    }

    [Fact]
    public async Task Schema_initialization_waits_for_the_write_connection_and_leaves_reads_free()
    {
        await using var factory = new SqliteConnectionFactory(_databasePath, maxConnections: 1);
        await using var store = new SqliteConversationStore(factory);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<ThreadMetadata?> load;
        await using (var held = await factory.GetWriteConnectionAsync(timeout.Token))
        {
            // The first call on a new store creates the schema.
            load = store.LoadMetadataAsync("thread-s", timeout.Token);
            await Task.Delay(Settle);
            load.IsCompleted.Should().BeFalse("creating the schema is a write, so it queues for the write connection");

            // The one read connection is free, and no table exists yet: the schema has not started without the
            // write connection. Checked here because the listing backfill after it waits for that connection too.
            await using var read = await factory.GetConnectionAsync(timeout.Token);
            using var tables = read.CreateCommand();
            tables.CommandText =
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'thread_metadata';";
            Convert.ToInt64(await tables.ExecuteScalarAsync(timeout.Token)).Should().Be(0);
        }

        (await load.WaitAsync(Bound)).Should().BeNull();
    }

    [Fact]
    public async Task Usage_load_reads_the_state_and_its_records_from_one_snapshot()
    {
        await using var store = new SqliteConversationStore(_databasePath);
        var first = Usage("attempt-1");
        var second = Usage("attempt-2");
        await store.AppendUsageAsync(ConversationUsageAggregate.Fold("thread-u", [first], 1), [first]);

        // A write commits between the two reads: a higher folded revision and a second record.
        store.UsageStateReadForTest = () =>
            store
                .AppendUsageAsync(ConversationUsageAggregate.Fold("thread-u", [first, second], 2), [second])
                .WaitAsync(Bound);

        var snapshot = await store.LoadUsageAsync("thread-u").WaitAsync(TimeSpan.FromSeconds(10));
        store.UsageStateReadForTest = null;

        snapshot!.FoldedRevision.Should().Be(1);
        snapshot.Records.Select(r => r.ProviderAttemptId).Should().Equal("attempt-1");

        var after = await store.LoadUsageAsync("thread-u");
        after!.FoldedRevision.Should().Be(2, "the write did commit; only the earlier snapshot leaves it out");
        after.Records.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("update")]
    public async Task A_metadata_write_queued_when_its_store_is_disposed_fails_and_writes_nothing(string kind)
    {
        // The factory outlives the store, as when a host shares one factory between stores.
        await using var factory = new SqliteConnectionFactory(_databasePath);
        var store = new SqliteConversationStore(factory);
        await store.SaveMetadataAsync("thread-d", Metadata("thread-d", "initial"));

        Task write;
        await using (var held = await factory.GetWriteConnectionAsync())
        {
            write =
                kind == "save"
                    ? store.SaveMetadataAsync("thread-d", Metadata("thread-d", "late"))
                    : store.UpdateMetadataAsync(
                        "thread-d",
                        existing => existing! with { Properties = existing.Properties!.SetItem("writer", "late") }
                    );
            await Task.Delay(Settle);
            write.IsCompleted.Should().BeFalse("the write has passed its thread's gate and waits for the connection");

            // A write to another thread does not hold this thread's gate, so disposal does not wait for it.
            await store.DisposeAsync();
        }

        var awaitWrite = () => write.WaitAsync(Bound);
        _ = await awaitWrite.Should().ThrowAsync<ObjectDisposedException>();

        await using var reader = new SqliteConversationStore(factory);
        (await reader.LoadMetadataAsync("thread-d"))!.Properties!["writer"].ToString().Should().Be("initial");
    }

    [Fact]
    public async Task Metadata_gates_leave_with_their_last_writer()
    {
        await using var store = new SqliteConversationStore(_databasePath);
        await store.SaveMetadataAsync("thread-1", Metadata("thread-1", "save"));
        await store.SaveMetadataAsync("thread-2", Metadata("thread-2", "save"));
        store.MetadataGateCount.Should().Be(0, "a thread with no write running keeps no gate");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var update = Task.Run(() =>
            store.UpdateMetadataAsync(
                "thread-1",
                existing =>
                {
                    entered.SetResult();
                    _ = release.Wait(TimeSpan.FromSeconds(10));
                    return existing!;
                }
            )
        );
        try
        {
            await entered.Task.WaitAsync(Bound);

            // A second writer queues on the same gate and gives up; the gate stays for the writer still inside.
            using var cancel = new CancellationTokenSource();
            var cancelled = store.SaveMetadataAsync("thread-1", Metadata("thread-1", "cancelled"), cancel.Token);
            cancel.Cancel();
            var waitCancelled = () => cancelled.WaitAsync(Bound);
            _ = await waitCancelled.Should().ThrowAsync<OperationCanceledException>();
            store.MetadataGateCount.Should().Be(1);
        }
        finally
        {
            release.Set();
        }

        await update.WaitAsync(Bound);
        store.MetadataGateCount.Should().Be(0);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Counts the store's requests for the write connection, and the ones it got, so a test can wait until writers are
    /// queued instead of guessing with a delay.
    /// </summary>
    private sealed class WriteSignallingFactory(ISqliteConnectionFactory inner) : ISqliteConnectionFactory
    {
        private int _requested;
        private int _opened;

        public int WritesRequested => Volatile.Read(ref _requested);

        public int WritesOpened => Volatile.Read(ref _opened);

        public Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default) =>
            inner.GetConnectionAsync(ct);

        public async Task<SqliteConnection> GetWriteConnectionAsync(CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _requested);
            var connection = await inner.GetWriteConnectionAsync(ct).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _opened);
            return connection;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private async Task<SqliteConnection> HoldWriteLockAsync()
    {
        // The write lock, held from outside the store: another process, a backup, a checkpoint.
        var holder = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()
        );
        await holder.OpenAsync();
        using var begin = holder.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        _ = await begin.ExecuteNonQueryAsync();
        return holder;
    }

    private static async Task RollbackAsync(SqliteConnection holder)
    {
        using var rollback = holder.CreateCommand();
        rollback.CommandText = "ROLLBACK;";
        _ = await rollback.ExecuteNonQueryAsync();
    }

    private static PersistedMessage Message(string threadId, string id) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = "run-m",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageType = "TextMessage",
            Role = "User",
            MessageJson = $"{{\"text\": \"{id}\"}}",
        };

    private static UsageRecord Usage(string attemptId) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = "thread-u",
            RequestedModel = "model",
            InputTokens = 10,
            OutputTokens = 1,
            Revision = 1,
        };

    private static ThreadMetadata Metadata(string threadId, string writer) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("writer", writer),
        };
}
