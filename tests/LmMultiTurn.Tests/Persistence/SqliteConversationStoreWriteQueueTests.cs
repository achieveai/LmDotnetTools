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
        await using var store = new SqliteConversationStore(_databasePath, maxConnections: 3);
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

            await Task.Delay(Settle);

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

    private static ThreadMetadata Metadata(string threadId, string writer) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = System.Collections.Immutable.ImmutableDictionary<string, object>.Empty.Add("writer", writer),
        };
}
