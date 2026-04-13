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

        // Clear the connection pool to release file handles
        SqliteConnection.ClearAllPools();

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
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("threadId");
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullMessages()
    {
        // Act
        var act = async () => await _store.AppendMessagesAsync("thread-1", null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("messages");
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
            Properties = ImmutableDictionary<string, object>.Empty
                .Add("key1", "value1")
                .Add("key2", 42),
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
            tasks.Add(Task.Run(async () =>
            {
                var messages = CreateTestMessages("thread-1", runId, 10);
                await _store.AppendMessagesAsync("thread-1", messages);
            }));
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
            tasks.Add(Task.Run(async () =>
            {
                var messages = CreateTestMessages(threadId, "run-1", 5);
                await _store.AppendMessagesAsync(threadId, messages);
            }));
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
            .. Enumerable.Range(0, count)
                .Select(i => CreateMessage(threadId, runId, $"msg-{runId}-{uniqueId}-{i}", now + i, messageOrderIdx: i))
        ];
    }

    private static PersistedMessage CreateMessage(
        string threadId,
        string runId,
        string id,
        long timestamp,
        int? messageOrderIdx = null)
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
