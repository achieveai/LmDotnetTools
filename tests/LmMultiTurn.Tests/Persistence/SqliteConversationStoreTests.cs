using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for SqliteConversationStore.
/// </summary>
public class SqliteConversationStoreTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private SqliteConversationStore _store = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _store = new SqliteConversationStore(_databasePath);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        // Clear only this database's pool: ClearAllPools also disposes connections that tests in other classes
        // are using in parallel (ObjectDisposedException on SQLitePCL.sqlite3).
        SqliteConnectionFactory.ClearPool(_databasePath);

        // The tests' own "Data Source=<path>" connections pool under a different key. Left open, they hold the
        // file, and Windows refuses to delete the -shm file (UnauthorizedAccessException).
        await using (var raw = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(raw);
        }

        // Give time for pool to release
        await Task.Delay(50);

        // Clean up the database file
        TryDeleteFile(_databasePath);
        TryDeleteFile(_databasePath + "-wal");
        TryDeleteFile(_databasePath + "-shm");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore - file may still be locked in some edge cases
        }
    }

    #region AppendMessagesAsync Tests

    [Fact]
    public async Task AppendMessagesAsync_AddsMessagesToNewThread()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 3);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var loaded = await _store.LoadMessagesAsync("thread-1");
        loaded.Should().HaveCount(3);
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingThread()
    {
        // Arrange
        var batch1 = CreateTestMessages("thread-1", "run-1", 2);
        var batch2 = CreateTestMessages("thread-1", "run-2", 3);

        await _store.AppendMessagesAsync("thread-1", batch1);

        // Act
        await _store.AppendMessagesAsync("thread-1", batch2);

        // Assert
        var loaded = await _store.LoadMessagesAsync("thread-1");
        loaded.Should().HaveCount(5);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_DoesNothing()
    {
        // Arrange
        var messages = new List<PersistedMessage>();

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var loaded = await _store.LoadMessagesAsync("thread-1");
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullThreadId()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 1);

        // Act
        var act = async () => await _store.AppendMessagesAsync(null!, messages);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("threadId");
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullMessages()
    {
        // Act
        var act = async () => await _store.AppendMessagesAsync("thread-1", null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("messages");
    }

    #endregion

    #region LoadMessagesAsync Tests

    [Fact]
    public async Task LoadMessagesAsync_ReturnsMessagesOrderedByTimestamp()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now + 200),
            CreateMessage("thread-1", "run-1", "msg-1", now),
            CreateMessage("thread-1", "run-1", "msg-2", now + 100),
        };

        await _store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await _store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be("msg-1");
        loaded[1].Id.Should().Be("msg-2");
        loaded[2].Id.Should().Be("msg-3");
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsMessagesOrderedByMessageOrderIdxWithinSameTimestamp()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now, messageOrderIdx: 2),
            CreateMessage("thread-1", "run-1", "msg-1", now, messageOrderIdx: 0),
            CreateMessage("thread-1", "run-1", "msg-2", now, messageOrderIdx: 1),
        };

        await _store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await _store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be("msg-1");
        loaded[1].Id.Should().Be("msg-2");
        loaded[2].Id.Should().Be("msg-3");
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsEmptyListForNonexistentThread()
    {
        // Act
        var loaded = await _store.LoadMessagesAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMessagesAsync_PreservesAllMessageFields()
    {
        // Arrange
        var message = new PersistedMessage
        {
            Id = "msg-1",
            ThreadId = "thread-1",
            RunId = "run-1",
            ParentRunId = "parent-run-0",
            GenerationId = "gen-123",
            MessageOrderIdx = 5,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageType = "TextMessage",
            Role = "User",
            FromAgent = "user-agent",
            MessageJson = "{\"text\": \"Hello, world!\"}",
        };

        await _store.AppendMessagesAsync("thread-1", [message]);

        // Act
        var loaded = await _store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(1);
        var loadedMessage = loaded[0];
        loadedMessage.Id.Should().Be(message.Id);
        loadedMessage.ThreadId.Should().Be(message.ThreadId);
        loadedMessage.RunId.Should().Be(message.RunId);
        loadedMessage.ParentRunId.Should().Be(message.ParentRunId);
        loadedMessage.GenerationId.Should().Be(message.GenerationId);
        loadedMessage.MessageOrderIdx.Should().Be(message.MessageOrderIdx);
        loadedMessage.Timestamp.Should().Be(message.Timestamp);
        loadedMessage.MessageType.Should().Be(message.MessageType);
        loadedMessage.Role.Should().Be(message.Role);
        loadedMessage.FromAgent.Should().Be(message.FromAgent);
        loadedMessage.MessageJson.Should().Be(message.MessageJson);
    }

    #endregion

    #region SaveMetadataAsync / LoadMetadataAsync Tests

    [Fact]
    public async Task SaveMetadataAsync_StoresMetadata()
    {
        // Arrange
        var metadata = CreateTestMetadata("thread-1");

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
    }

    [Fact]
    public async Task SaveMetadataAsync_OverwritesExistingMetadata()
    {
        // Arrange
        var metadata1 = CreateTestMetadata("thread-1", currentRunId: "run-1");
        var metadata2 = CreateTestMetadata("thread-1", currentRunId: "run-2");

        await _store.SaveMetadataAsync("thread-1", metadata1);

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata2);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.CurrentRunId.Should().Be("run-2");
    }

    [Fact]
    public async Task LoadMetadataAsync_ReturnsNullForNonexistentThread()
    {
        // Act
        var loaded = await _store.LoadMetadataAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveMetadataAsync_PreservesSessionMappings()
    {
        // Arrange
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-1",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionMappings = new Dictionary<string, string>
            {
                ["claude-sdk:sess_abc123"] = "run-1",
                ["openai:thread_xyz"] = "run-2",
            },
        };

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.SessionMappings.Should().NotBeNull();
        loaded.SessionMappings!["claude-sdk:sess_abc123"].Should().Be("run-1");
        loaded.SessionMappings["openai:thread_xyz"].Should().Be("run-2");
    }

    [Fact]
    public async Task SaveMetadataAsync_PreservesLatestRunId()
    {
        // Arrange
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-1",
            CurrentRunId = "run-current",
            LatestRunId = "run-latest",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.CurrentRunId.Should().Be("run-current");
        loaded.LatestRunId.Should().Be("run-latest");
    }

    [Fact]
    public async Task SaveMetadataAsync_PreservesProperties()
    {
        // Arrange
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-1",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = ImmutableDictionary<string, object>.Empty.Add("key1", "value1").Add("key2", 42),
        };

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Properties.Should().NotBeNull();
        loaded.Properties!["key1"].ToString().Should().Be("value1");
    }

    [Fact]
    public async Task MetadataWrites_KeepListingProjectionColumnsInSync()
    {
        var initial = ConversationLineage.Write(
            new ThreadMetadata
            {
                ThreadId = "thread-1",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.Add(
                    ConversationRelationKeys.SubAgentParentThreadId,
                    "parent-a"
                ),
            },
            new ConversationLineage
            {
                DeletedAt = 10,
                ForkedFrom = new ForkPoint
                {
                    ThreadId = "source-a",
                    MessageId = "message-a",
                    Seq = 1,
                },
            }
        );
        await _store.SaveMetadataAsync("thread-1", initial);

        (await ReadListingProjectionAsync("thread-1")).Should().Be((10L, "source-a", "parent-a"));

        await _store.UpdateMetadataAsync(
            "thread-1",
            existing =>
                ConversationLineage.Write(
                    existing! with
                    {
                        LastUpdated = 2,
                        Properties = existing.Properties!.SetItem(
                            ConversationRelationKeys.SubAgentParentThreadId,
                            "parent-b"
                        ),
                    },
                    new ConversationLineage
                    {
                        ForkedFrom = new ForkPoint
                        {
                            ThreadId = "source-b",
                            MessageId = "message-b",
                            Seq = 2,
                        },
                    }
                )
        );

        (await ReadListingProjectionAsync("thread-1")).Should().Be((null, "source-b", "parent-b"));
    }

    #endregion

    #region DeleteThreadAsync Tests

    [Fact]
    public async Task DeleteThreadAsync_RemovesMessagesAndMetadata()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 3);
        var metadata = CreateTestMetadata("thread-1");

        await _store.AppendMessagesAsync("thread-1", messages);
        await _store.SaveMetadataAsync("thread-1", metadata);

        // Act
        await _store.DeleteThreadAsync("thread-1");

        // Assert
        var loadedMessages = await _store.LoadMessagesAsync("thread-1");
        loadedMessages.Should().BeEmpty();

        var loadedMetadata = await _store.LoadMetadataAsync("thread-1");
        loadedMetadata.Should().BeNull();
    }

    [Fact]
    public async Task DeleteThreadAsync_RemovesEveryThreadScopedTable_AndKeepsOtherThreads()
    {
        // Arrange: ensure the schema exists, then seed every thread-scoped table for two threads.
        // Before the cascade fix, DeleteThreadAsync cleared only messages + thread_metadata, so a
        // recreated thread id replaying an idempotency key saw a stale accepted_inputs row and was
        // refused (queued:false). This proves the deleted thread leaves no orphaned run/admission rows.
        await _store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 1));
        await _store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));
        await _store.AppendMessagesAsync("thread-keep", CreateTestMessages("thread-keep", "run-2", 1));
        await _store.SaveMetadataAsync("thread-keep", CreateTestMetadata("thread-keep"));

        string[] threadScopedTables =
        [
            "messages",
            "thread_metadata",
            "run_ledger",
            "run_lifecycle",
            "run_deferred_calls",
            "accepted_inputs",
            "input_acceptances",
            "notify_waits",
        ];

        await using (var seed = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await seed.OpenAsync();
            foreach (var threadId in new[] { "thread-1", "thread-keep" })
            {
                await ExecAsync(
                    seed,
                    "INSERT INTO run_ledger (run_id, thread_id, status, input_ids, created_at, updated_at) "
                        + $"VALUES ('run-{threadId}', '{threadId}', 'completed', '[]', 0, 0);"
                );
                await ExecAsync(
                    seed,
                    "INSERT INTO run_lifecycle (run_id, thread_id, generation_id, cause_kind, phase, turn_count, started_at, updated_at) "
                        + $"VALUES ('run-{threadId}', '{threadId}', 'gen', 'user', 'completed', 0, 0, 0);"
                );
                await ExecAsync(
                    seed,
                    "INSERT INTO run_deferred_calls (thread_id, tool_call_id, run_id, tool_name, ordinal, deferred_at) "
                        + $"VALUES ('{threadId}', 'tc-{threadId}', 'run-{threadId}', 'Bash', 0, 0);"
                );
                await ExecAsync(
                    seed,
                    $"INSERT INTO accepted_inputs (thread_id, input_id, accepted_at) VALUES ('{threadId}', 'in-1', 0);"
                );
                await ExecAsync(
                    seed,
                    "INSERT INTO input_acceptances (thread_id, input_id, accepted_at, state, spawning_suppressed, idempotency_honored, reservation_id) "
                        + $"VALUES ('{threadId}', 'in-1', 0, 'accepted', 0, 0, 'res-{threadId}');"
                );
                await ExecAsync(
                    seed,
                    "INSERT INTO notify_waits (wait_id, thread_id, kind, args, fires_so_far, timeout_at, armed_at, status) "
                        + $"VALUES ('w-{threadId}', '{threadId}', 'notify', '{{}}', 0, 0, 0, 'armed');"
                );
            }
        }

        // Act
        await _store.DeleteThreadAsync("thread-1");

        // Assert: every thread-scoped table is empty for thread-1, and thread-keep is untouched.
        await using var verify = new SqliteConnection($"Data Source={_databasePath}");
        await verify.OpenAsync();
        foreach (var table in threadScopedTables)
        {
            (await CountAsync(verify, table, "thread-1"))
                .Should()
                .Be(0, $"{table} rows for the deleted thread must be gone");
            (await CountAsync(verify, table, "thread-keep"))
                .Should()
                .Be(1, $"{table} rows for other threads must survive");
        }
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, string threadId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE thread_id = $thread_id;";
        command.Parameters.AddWithValue("$thread_id", threadId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task DeleteThreadAsync_DoesNotThrowForNonexistentThread()
    {
        // Act
        var act = async () => await _store.DeleteThreadAsync("nonexistent-thread");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteThreadAsync_OnlyDeletesSpecifiedThread()
    {
        // Arrange
        await _store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 2));
        await _store.AppendMessagesAsync("thread-2", CreateTestMessages("thread-2", "run-1", 3));

        // Act
        await _store.DeleteThreadAsync("thread-1");

        // Assert
        var thread1Messages = await _store.LoadMessagesAsync("thread-1");
        var thread2Messages = await _store.LoadMessagesAsync("thread-2");

        thread1Messages.Should().BeEmpty();
        thread2Messages.Should().HaveCount(3);
    }

    [Fact]
    public async Task DeleteThreadAsync_RemovesUsageStateAndRecords_WithoutChangingAnotherThread()
    {
        var deleted = UsageAttempt("thread-delete", "delete-attempt", revision: 1, inputTokens: 10);
        var kept = UsageAttempt("thread-keep", "keep-attempt", revision: 2, inputTokens: 20);
        await _store.AppendUsageAsync(ConversationUsageAggregate.Fold("thread-delete", [deleted], 1), [deleted]);
        await _store.AppendUsageAsync(ConversationUsageAggregate.Fold("thread-keep", [kept], 2), [kept]);
        var keptBefore = await _store.LoadUsageAsync("thread-keep");

        await _store.DeleteThreadAsync("thread-delete");

        (await _store.LoadUsageAsync("thread-delete")).Should().BeNull();
        (await _store.LoadUsageAsync("thread-keep")).Should().BeEquivalentTo(keptBefore);
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task Store_IsThreadSafe_ForConcurrentAppends()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act - Append messages from multiple threads
        for (var i = 0; i < 10; i++)
        {
            var runId = $"run-{i}";
            tasks.Add(
                Task.Run(async () =>
                {
                    var messages = CreateTestMessages("thread-1", runId, 10);
                    await _store.AppendMessagesAsync("thread-1", messages);
                })
            );
        }

        await Task.WhenAll(tasks);

        // Assert
        var loaded = await _store.LoadMessagesAsync("thread-1");
        loaded.Should().HaveCount(100);
    }

    [Fact]
    public async Task Store_HandlesMultipleThreadsConcurrently()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act - Different threads operating on different thread IDs
        for (var i = 0; i < 5; i++)
        {
            var threadId = $"thread-{i}";
            tasks.Add(
                Task.Run(async () =>
                {
                    var messages = CreateTestMessages(threadId, "run-1", 5);
                    await _store.AppendMessagesAsync(threadId, messages);
                })
            );
        }

        await Task.WhenAll(tasks);

        // Assert
        for (var i = 0; i < 5; i++)
        {
            var loaded = await _store.LoadMessagesAsync($"thread-{i}");
            loaded.Should().HaveCount(5);
        }
    }

    [Fact]
    public async Task AppendUsageAsync_TwoStoreInstancesMergeConcurrentRecords_WithoutRegressingState()
    {
        await using var secondStore = new SqliteConversationStore(_databasePath);
        var lower = UsageAttempt("usage-thread", "lower", revision: 2, inputTokens: 20);
        var higher = UsageAttempt("usage-thread", "higher", revision: 7, inputTokens: 70);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lowerWrite = Task.Run(async () =>
        {
            await start.Task;
            await _store.AppendUsageAsync(
                ConversationUsageAggregate.Fold(
                    "usage-thread",
                    [lower],
                    foldedRevision: 2,
                    UsageCompleteness.InProgress
                ),
                [lower]
            );
        });
        var higherWrite = Task.Run(async () =>
        {
            await start.Task;
            await secondStore.AppendUsageAsync(
                ConversationUsageAggregate.Fold(
                    "usage-thread",
                    [higher],
                    foldedRevision: 7,
                    UsageCompleteness.Complete
                ),
                [higher]
            );
        });

        start.SetResult();
        await Task.WhenAll(lowerWrite, higherWrite);

        var loaded = await _store.LoadUsageAsync("usage-thread");
        loaded!.Records.Select(record => record.ProviderAttemptId).Should().BeEquivalentTo(["lower", "higher"]);
        loaded.FoldedRevision.Should().Be(7);
        loaded.Completeness.Should().Be(UsageCompleteness.Complete);
    }

    [Fact]
    public async Task AppendUsageAsync_EqualRevisionIncomingRecordWins_WithoutRegressingState()
    {
        var first = UsageAttempt("usage-thread", "attempt", revision: 4, inputTokens: 10);
        var incoming = first with { InputTokens = 99 };
        await _store.AppendUsageAsync(
            ConversationUsageAggregate.Fold("usage-thread", [first], foldedRevision: 10, UsageCompleteness.Complete),
            [first]
        );

        await _store.AppendUsageAsync(
            ConversationUsageAggregate.Fold(
                "usage-thread",
                [incoming],
                foldedRevision: 3,
                UsageCompleteness.InProgress
            ),
            [incoming]
        );

        var loaded = await _store.LoadUsageAsync("usage-thread");
        loaded!.Records.Should().ContainSingle().Which.InputTokens.Should().Be(99);
        loaded.FoldedRevision.Should().Be(10);
        loaded.Completeness.Should().Be(UsageCompleteness.Complete);
    }

    [Fact]
    public async Task AppendUsageAsync_StaleLowerRevisionReplay_KeepsTheHigherRevisionPayloadAndTotals()
    {
        // A replayed lower revision of a finalized attempt (a retried or out-of-order save) must not overwrite
        // its payload, or the folded totals regress to the stale counts and cost.
        var finalized = UsageAttempt("usage-thread", "attempt", revision: 8, inputTokens: 120) with
        {
            OutputTokens = 30,
            EstimatedPublicCostMicros = 900,
            Finalized = true,
        };
        var stale = UsageAttempt("usage-thread", "attempt", revision: 5, inputTokens: 40) with
        {
            OutputTokens = 2,
            EstimatedPublicCostMicros = 100,
        };
        await _store.AppendUsageAsync(
            ConversationUsageAggregate.Fold("usage-thread", [finalized], foldedRevision: 8, UsageCompleteness.Complete),
            [finalized]
        );

        await _store.AppendUsageAsync(
            ConversationUsageAggregate.Fold("usage-thread", [stale], foldedRevision: 5, UsageCompleteness.InProgress),
            [stale]
        );

        var loaded = await _store.LoadUsageAsync("usage-thread");
        loaded!.Records.Should().ContainSingle().Which.Should().BeEquivalentTo(finalized);
        loaded.FoldedRevision.Should().Be(8);
        loaded.Completeness.Should().Be(UsageCompleteness.Complete);
        var totals = await ConversationUsageProjection.LoadAsync(_store, "usage-thread");
        totals!.TotalTokens.Should().Be(150);
        totals.EstimatedPublicCostMicros.Should().Be(900);
    }

    [Fact]
    public async Task AppendUsageAsync_PreservesEarliestKnownOccurrence_AcrossArrivalOrders()
    {
        var early = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        var late = early.AddHours(1);
        (
            string ThreadId,
            DateTimeOffset? First,
            long FirstRevision,
            DateTimeOffset? Second,
            long SecondRevision,
            DateTimeOffset Expected
        )[] cases =
        [
            ("null-then-known", null, 1L, late, 2L, late),
            ("known-then-null", early, 1L, null, 2L, early),
            ("older-then-newer", early, 1L, late, 2L, early),
            ("newer-then-older-equal", late, 2L, early, 2L, early),
        ];

        foreach (var (threadId, firstTime, firstRevision, secondTime, secondRevision, expected) in cases)
        {
            var first = UsageAttempt(threadId, "attempt", firstRevision, 10) with { OccurredAtUtc = firstTime };
            var second = UsageAttempt(threadId, "attempt", secondRevision, 20) with { OccurredAtUtc = secondTime };
            await _store.AppendUsageAsync(ConversationUsageAggregate.Fold(threadId, [first], firstRevision), [first]);
            await _store.AppendUsageAsync(
                ConversationUsageAggregate.Fold(threadId, [second], secondRevision),
                [second]
            );

            (await _store.LoadUsageAsync(threadId))!
                .Records.Should()
                .ContainSingle()
                .Which.OccurredAtUtc.Should()
                .Be(expected);
        }
    }

    #endregion

    #region ListThreadsAsync Sort-Order Parity Tests

    [Fact]
    public async Task ListConversationRowsAsync_FiltersBeforePaging_AndProjectsOnlyRequestedValues()
    {
        const string CustomValue = """{"nested":[1,true]}""";
        var rows = new List<ThreadMetadata>
        {
            ListingRow("subagent-hidden", 1_000, "tenant-a", "user-a"),
            ListingRow("workflow-hidden", 999, "tenant-a", "user-a"),
            ConversationLineage.Write(
                ListingRow("deleted-hidden", 998, "tenant-a", "user-a"),
                new ConversationLineage { DeletedAt = 5 }
            ),
            ListingRow("private-hidden", 997, "tenant-a", "user-b"),
            ListingRow("other-tenant-hidden", 996, "tenant-b", "user-a"),
            ListingRow("root-owned", 500, "tenant-a", "user-a"),
            ListingRow(
                "root-granted",
                400,
                "tenant-a",
                "user-b",
                ImmutableDictionary<string, object>
                    .Empty.Add(ConversationRelationKeys.SubAgentParentThreadId, "root-parent")
                    .Add("custom", CustomValue)
            ),
            ListingRow("root-published", 300, "tenant-a", "user-b", visibility: Visibility.TenantPublished),
            ListingRow("root-tie-z", 200, "tenant-a", "user-a"),
            ListingRow("root-tie-a", 200, "tenant-a", "user-a"),
        };
        foreach (var row in rows)
        {
            await _store.SaveMetadataAsync(row.ThreadId, row);
        }

        var scope = new ConversationListScope
        {
            TenantId = "tenant-a",
            UserId = "user-a",
            GrantedThreadIds = new HashSet<string>(StringComparer.Ordinal) { "root-granted" },
        };
        var options = new ConversationListOptions
        {
            ExcludedThreadIdPrefixes = ["subagent-", "workflow-"],
            ExcludeDeleted = true,
        };
        var propertyKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            ConversationRelationKeys.SubAgentParentThreadId,
            "custom",
        };

        var expected = options.Order(rows.Where(scope.Admits).Where(options.Admits)).Select(row => row.ThreadId);
        var first = await _store.ListConversationRowsAsync(propertyKeys, scope, 2, 0, options);
        var second = await _store.ListConversationRowsAsync(propertyKeys, scope, 2, 2, options);
        var third = await _store.ListConversationRowsAsync(propertyKeys, scope, 2, 4, options);
        var actual = first.Concat(second).Concat(third).ToList();

        actual.Select(row => row.ThreadId).Should().Equal(expected);
        first.Should().HaveCount(2, "excluded or unauthorized rows must not consume the SQL limit");
        var projected = actual.Single(row => row.ThreadId == "root-granted");
        projected
            .Properties.Should()
            .BeEquivalentTo(
                new Dictionary<string, string?>
                {
                    [ConversationRelationKeys.SubAgentParentThreadId] = "root-parent",
                    ["custom"] = CustomValue,
                }
            );
    }

    /// <summary>
    ///     <see cref="ConversationSortOrder.Created"/> orders by the creation time derived from the
    ///     thread id, NOT by <c>LastUpdated</c>. Mirrors the file-store parity test so the SQLite store
    ///     keeps the same <c>created</c> ordering the host already relies on.
    /// </summary>
    /// <remarks>
    ///     The two orderings are seeded to DISAGREE completely - the earliest-created thread carries the
    ///     newest <c>LastUpdated</c>, so the expected sequences are exact reverses of one another. A seed
    ///     in which they agreed could not distinguish the two implementations and would pass against a
    ///     store that quietly ignored <c>Created</c> (the exact regression this test guards).
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_OrdersByDerivedCreationTime_WhenSortOrderIsCreated()
    {
        // Arrange
        await _store.SaveMetadataAsync(
            "thread-1000-aaa",
            new ThreadMetadata { ThreadId = "thread-1000-aaa", LastUpdated = 9_000 }
        );
        await _store.SaveMetadataAsync(
            "thread-2000-bbb",
            new ThreadMetadata { ThreadId = "thread-2000-bbb", LastUpdated = 8_000 }
        );
        await _store.SaveMetadataAsync(
            "thread-3000-ccc",
            new ThreadMetadata { ThreadId = "thread-3000-ccc", LastUpdated = 7_000 }
        );

        // Act
        var byLastUsed = await _store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.LastUsed }
        );
        var byCreated = await _store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.Created }
        );

        // Assert
        byLastUsed.Select(m => m.ThreadId).Should().Equal("thread-1000-aaa", "thread-2000-bbb", "thread-3000-ccc");
        byCreated.Select(m => m.ThreadId).Should().Equal("thread-3000-ccc", "thread-2000-bbb", "thread-1000-aaa");
    }

    /// <summary>
    ///     A thread id that carries no parseable timestamp segment sorts by <c>LastUpdated</c> under
    ///     <see cref="ConversationSortOrder.Created"/> - it is neither dropped nor sorted to position
    ///     zero. Mirrors the file-store fallback parity test.
    /// </summary>
    /// <remarks>
    ///     Both ways an id can lack a timestamp are seeded: a <c>thread-{guid:N}</c> id with no
    ///     delimiter after the prefix, and a <c>thread-notatimestamp-zzz</c> id whose segment does not
    ///     parse. Both fallback rows are placed ABOVE the parseable rows, so an implementation that
    ///     treated an unparseable id as zero would order them last and fail here.
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_FallsBackToLastUpdated_WhenTheThreadIdCarriesNoTimestamp()
    {
        // Arrange
        var provisioned = $"thread-{Guid.NewGuid():N}";
        const string NonNumeric = "thread-notatimestamp-zzz";
        await _store.SaveMetadataAsync(
            "thread-5000-aaa",
            new ThreadMetadata { ThreadId = "thread-5000-aaa", LastUpdated = 1_000 }
        );
        await _store.SaveMetadataAsync(provisioned, new ThreadMetadata { ThreadId = provisioned, LastUpdated = 7_000 });
        await _store.SaveMetadataAsync(NonNumeric, new ThreadMetadata { ThreadId = NonNumeric, LastUpdated = 6_500 });
        await _store.SaveMetadataAsync(
            "thread-6000-bbb",
            new ThreadMetadata { ThreadId = "thread-6000-bbb", LastUpdated = 2_000 }
        );

        // Act
        var byCreated = await _store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.Created }
        );

        // Assert - both fallbacks (7,000 and 6,500) rank above the parsed 6,000 and 5,000.
        byCreated
            .Select(m => m.ThreadId)
            .Should()
            .Equal(provisioned, NonNumeric, "thread-6000-bbb", "thread-5000-aaa");
    }

    /// <summary>
    ///     A Created page is chosen from the id and last_updated alone, then only its rows are loaded.
    ///     Each paged row must still come back whole, in page order, from both listing overloads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListThreadsAsync_CreatedPage_ReturnsWholeRowsInPageOrder(bool scoped)
    {
        foreach (var (id, lastUpdated) in new[] { ("thread-1000-a", 9L), ("thread-2000-b", 8L), ("thread-3000-c", 7L) })
        {
            await _store.SaveMetadataAsync(
                id,
                new ThreadMetadata
                {
                    ThreadId = id,
                    LastUpdated = lastUpdated,
                    LatestRunId = $"run-{id}",
                    TenantId = "tenant-a",
                    Properties = ImmutableDictionary<string, object>.Empty.Add("title", id),
                }
            );
        }

        var options = new ConversationListOptions { SortOrder = ConversationSortOrder.Created };
        var page = scoped
            ? await _store.ListThreadsAsync(
                ConversationListScope.ForTenant("tenant-a"),
                limit: 2,
                offset: 1,
                options: options
            )
            : await _store.ListThreadsAsync(limit: 2, offset: 1, options: options);

        page.Select(m => m.ThreadId).Should().Equal("thread-2000-b", "thread-1000-a");
        page.Should()
            .AllSatisfy(m =>
            {
                m.LatestRunId.Should().Be($"run-{m.ThreadId}");
                m.TenantId.Should().Be("tenant-a");
                m.Properties!["title"].ToString().Should().Be(m.ThreadId);
            });
    }

    #endregion

    #region Relation Read Tests

    [Fact]
    public async Task RelationReads_ReturnExactCompactRows_ThroughTheirCoveringIndexes()
    {
        await _store.SaveMetadataAsync(
            "subagent-b",
            CreateTestMetadata("subagent-b") with
            {
                Properties = ImmutableDictionary<string, object>.Empty.Add("sample.subAgentOf", "root-b"),
            }
        );
        await _store.SaveMetadataAsync(
            "subagent-a",
            CreateTestMetadata("subagent-a") with
            {
                Properties = ImmutableDictionary<string, object>.Empty.Add("sample.subAgentOf", "root-a"),
            }
        );
        await _store.SaveMetadataAsync(
            "subagent-c",
            CreateTestMetadata("subagent-c") with
            {
                Properties = ImmutableDictionary<string, object>.Empty.Add("sample.subAgentOf", "root-a"),
            }
        );
        await _store.SaveMetadataAsync(
            "fork-b",
            ConversationLineage.Write(
                CreateTestMetadata("fork-b"),
                new ConversationLineage
                {
                    ForkedFrom = new ForkPoint { ThreadId = "source", Seq = 1 },
                }
            )
        );
        await _store.SaveMetadataAsync(
            "fork-a",
            ConversationLineage.Write(
                CreateTestMetadata("fork-a"),
                new ConversationLineage
                {
                    ForkedFrom = new ForkPoint { ThreadId = "source", Seq = 1 },
                }
            )
        );
        await _store.SaveMetadataAsync(
            "fork-grandchild",
            ConversationLineage.Write(
                CreateTestMetadata("fork-grandchild"),
                new ConversationLineage
                {
                    ForkedFrom = new ForkPoint { ThreadId = "fork-a", Seq = 1 },
                }
            )
        );
        await _store.SaveMetadataAsync("unrelated", CreateTestMetadata("unrelated"));

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using (var poison = connection.CreateCommand())
        {
            poison.CommandText = "UPDATE thread_metadata SET metadata_json = zeroblob(2000000);";
            _ = await poison.ExecuteNonQueryAsync();
        }

        var links = await _store.ListSubAgentParentLinksAsync();
        var directForks = await _store.ListDirectForkThreadIdsAsync("source");
        var noForks = await _store.ListDirectForkThreadIdsAsync("missing");

        links
            .Should()
            .Equal(
                new ConversationParentLink("subagent-a", "root-a"),
                new ConversationParentLink("subagent-c", "root-a"),
                new ConversationParentLink("subagent-b", "root-b")
            );
        directForks.Should().Equal("fork-a", "fork-b");
        noForks.Should().BeEmpty();

        var parentPlan = await ExplainAsync(
            connection,
            """
            EXPLAIN QUERY PLAN
            SELECT thread_id, subagent_parent_thread_id
            FROM thread_metadata INDEXED BY idx_thread_metadata_subagent_parent
            WHERE subagent_parent_thread_id IS NOT NULL
            ORDER BY subagent_parent_thread_id, thread_id;
            """
        );
        var forkPlan = await ExplainAsync(
            connection,
            """
            EXPLAIN QUERY PLAN
            SELECT thread_id
            FROM thread_metadata INDEXED BY idx_thread_metadata_fork_parent
            WHERE forked_from_thread_id = 'source'
            ORDER BY thread_id;
            """
        );

        parentPlan
            .Should()
            .ContainSingle(detail =>
                detail.Contains("USING COVERING INDEX idx_thread_metadata_subagent_parent", StringComparison.Ordinal)
            );
        forkPlan
            .Should()
            .ContainSingle(detail =>
                detail.Contains("USING COVERING INDEX idx_thread_metadata_fork_parent", StringComparison.Ordinal)
            );
    }

    #endregion

    #region ListThreadIdsByPrefixAsync Tests

    [Fact]
    public async Task ListThreadIdsByPrefixAsync_UsesThePrimaryKeyWithoutReadingMetadata()
    {
        const string Prefix = "subagent-scope-agent-";
        var matching = new[] { $"{Prefix}1", $"{Prefix}10", $"{Prefix}2" };
        foreach (var threadId in matching.Append(Prefix[..^1]))
        {
            await _store.SaveMetadataAsync(threadId, CreateTestMetadata(threadId));
        }

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using (var poison = connection.CreateCommand())
        {
            poison.CommandText = """
                UPDATE thread_metadata
                SET metadata_json = zeroblob(2000000)
                WHERE thread_id >= $prefix AND thread_id < $upper_bound;
                """;
            _ = poison.Parameters.AddWithValue("$prefix", Prefix);
            _ = poison.Parameters.AddWithValue("$upper_bound", "subagent-scope-agent.");
            _ = await poison.ExecuteNonQueryAsync();
        }

        var ids = await _store.ListThreadIdsByPrefixAsync(Prefix);
        var absent = await _store.ListThreadIdsByPrefixAsync("subagent-absent-agent-");

        ids.Should().Equal(matching);
        absent.Should().BeEmpty();

        await using var explain = connection.CreateCommand();
        explain.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT thread_id
            FROM thread_metadata
            WHERE thread_id >= $prefix
              AND thread_id < $upper_bound
            ORDER BY thread_id;
            """;
        _ = explain.Parameters.AddWithValue("$prefix", Prefix);
        _ = explain.Parameters.AddWithValue("$upper_bound", "subagent-scope-agent.");
        var plan = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        plan.Should().ContainSingle(detail => detail.Contains("USING COVERING INDEX", StringComparison.Ordinal));
    }

    #endregion

    #region Schema Initialization Tests

    [Fact]
    public async Task SchemaInitialization_IsIdempotent()
    {
        // Act - Multiple operations should not fail due to schema issues
        await _store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 1));
        await _store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));
        await _store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-2", 1));
        await _store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1", "run-2"));

        // Assert
        var messages = await _store.LoadMessagesAsync("thread-1");
        messages.Should().HaveCount(2);
    }

    #endregion

    #region Test Helpers

    private static List<PersistedMessage> CreateTestMessages(string threadId, string runId, int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        return
        [
            .. Enumerable
                .Range(0, count)
                .Select(i =>
                    CreateMessage(threadId, runId, $"msg-{runId}-{uniqueId}-{i}", now + i, messageOrderIdx: i)
                ),
        ];
    }

    private static PersistedMessage CreateMessage(
        string threadId,
        string runId,
        string id,
        long timestamp,
        int? messageOrderIdx = null
    )
    {
        return new PersistedMessage
        {
            Id = id,
            ThreadId = threadId,
            RunId = runId,
            Timestamp = timestamp,
            MessageOrderIdx = messageOrderIdx,
            MessageType = "TextMessage",
            Role = "User",
            MessageJson = $"{{\"text\": \"Test message {id}\"}}",
        };
    }

    private static ThreadMetadata CreateTestMetadata(string threadId, string? currentRunId = null)
    {
        return new ThreadMetadata
        {
            ThreadId = threadId,
            CurrentRunId = currentRunId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static UsageRecord UsageAttempt(string threadId, string attemptId, long revision, int inputTokens) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = threadId,
            RequestedModel = "model",
            InputTokens = inputTokens,
            OutputTokens = 1,
            Revision = revision,
        };

    private static async Task<IReadOnlyList<string>> ExplainAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        return plan;
    }

    private async Task<(long? DeletedAt, string? ForkedFrom, string? Parent)> ReadListingProjectionAsync(
        string threadId
    )
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT deleted_at, forked_from_thread_id, subagent_parent_thread_id
            FROM thread_metadata
            WHERE thread_id = $thread_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2)
        );
    }

    private static ThreadMetadata ListingRow(
        string threadId,
        long lastUpdated,
        string tenantId,
        string ownerUserId,
        ImmutableDictionary<string, object>? properties = null,
        Visibility visibility = Visibility.Private
    ) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = lastUpdated,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            Visibility = visibility,
            Properties = properties,
        };

    #endregion
}
