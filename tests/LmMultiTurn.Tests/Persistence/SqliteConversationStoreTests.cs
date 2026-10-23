using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
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

        // Clear this file's connection pool to release its handles; the raw connections below are unpooled
        SqliteConnectionFactory.ClearPool(_databasePath);

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

        await using (var seed = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
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
        await using var verify = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
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

    #endregion

    #region ListThreadsAsync Sort-Order Parity Tests

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

    #region Disposal Tests

    // The host disposes the store while the agent pool still drains, so a metadata writer can be
    // queued on the store's lock at that moment. With a non-owned factory the connection is still live.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_WhileAWriterIsQueuedOnTheMetadataLock_FailsThatWriterInsteadOfOrphaningIt(
        bool ownsFactory
    )
    {
        var factory = new SqliteConnectionFactory(_databasePath);
        var store = new SqliteConversationStore(factory, ownsFactory);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? holder = null;
        try
        {
            holder = Task.Run(() =>
                store.UpdateMetadataAsync(
                    "thread-a",
                    _ =>
                    {
                        entered.SetResult();
                        release.Wait();
                        return CreateTestMetadata("thread-a");
                    }
                )
            );
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var queued = store.UpdateMetadataAsync("thread-b", _ => CreateTestMetadata("thread-b"));
            queued.IsCompleted.Should().BeFalse("the holder still has the metadata lock");

            await store.DisposeAsync();
            release.Set();

            var queuedError = await Record.ExceptionAsync(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            (queuedError as TimeoutException).Should().BeNull("disposal must not orphan a writer queued on the lock");
            queuedError
                .Should()
                .BeOfType<ObjectDisposedException>("a disposed store refuses work whether or not it owns its factory");
        }
        finally
        {
            release.Set();
            if (holder is not null)
            {
                var holderError = await Record.ExceptionAsync(() => holder.WaitAsync(TimeSpan.FromSeconds(5)));
                // The holder may fail (owned factory, now disposed) or succeed (live factory); it must not hang.
                (holderError as TimeoutException)
                    .Should()
                    .BeNull("the lock holder must finish once released");
            }

            await factory.DisposeAsync();
        }
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

    #endregion
}
