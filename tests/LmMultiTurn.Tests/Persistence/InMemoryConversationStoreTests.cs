using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for InMemoryConversationStore.
/// </summary>
public class InMemoryConversationStoreTests
{
    #region AppendMessagesAsync Tests

    [Fact]
    public async Task AppendMessagesAsync_AddsMessagesToNewThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 3);

        // Act
        await store.AppendMessagesAsync("thread-1", messages);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(3);
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var batch1 = CreateTestMessages("thread-1", "run-1", 2);
        var batch2 = CreateTestMessages("thread-1", "run-2", 3);

        await store.AppendMessagesAsync("thread-1", batch1);

        // Act
        await store.AppendMessagesAsync("thread-1", batch2);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(5);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_DoesNothing()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = new List<PersistedMessage>();

        // Act
        await store.AppendMessagesAsync("thread-1", messages);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(0);
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullThreadId()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 1);

        // Act
        var act = async () => await store.AppendMessagesAsync(null!, messages);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("threadId");
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullMessages()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var act = async () => await store.AppendMessagesAsync("thread-1", null!);

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
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now + 200),
            CreateMessage("thread-1", "run-1", "msg-1", now),
            CreateMessage("thread-1", "run-1", "msg-2", now + 100),
        };

        await store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await store.LoadMessagesAsync("thread-1");

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
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now, messageOrderIdx: 2),
            CreateMessage("thread-1", "run-1", "msg-1", now, messageOrderIdx: 0),
            CreateMessage("thread-1", "run-1", "msg-2", now, messageOrderIdx: 1),
        };

        await store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be("msg-1");
        loaded[1].Id.Should().Be("msg-2");
        loaded[2].Id.Should().Be("msg-3");
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsEmptyListForNonexistentThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var loaded = await store.LoadMessagesAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsCopyOfMessages()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 2);
        await store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded1 = await store.LoadMessagesAsync("thread-1");
        var loaded2 = await store.LoadMessagesAsync("thread-1");

        // Assert
        loaded1.Should().NotBeSameAs(loaded2);
    }

    #endregion

    #region SaveMetadataAsync / LoadMetadataAsync Tests

    [Fact]
    public async Task SaveMetadataAsync_StoresMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var metadata = CreateTestMetadata("thread-1");

        // Act
        await store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
    }

    [Fact]
    public async Task SaveMetadataAsync_OverwritesExistingMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var metadata1 = CreateTestMetadata("thread-1", currentRunId: "run-1");
        var metadata2 = CreateTestMetadata("thread-1", currentRunId: "run-2");

        await store.SaveMetadataAsync("thread-1", metadata1);

        // Act
        await store.SaveMetadataAsync("thread-1", metadata2);
        var loaded = await store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.CurrentRunId.Should().Be("run-2");
    }

    [Fact]
    public async Task LoadMetadataAsync_ReturnsNullForNonexistentThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var loaded = await store.LoadMetadataAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeNull();
    }

    #endregion

    #region DeleteThreadAsync Tests

    [Fact]
    public async Task DeleteThreadAsync_RemovesMessagesAndMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 3);
        var metadata = CreateTestMetadata("thread-1");

        await store.AppendMessagesAsync("thread-1", messages);
        await store.SaveMetadataAsync("thread-1", metadata);

        // Act
        await store.DeleteThreadAsync("thread-1");

        // Assert
        store.GetMessageCount("thread-1").Should().Be(0);
        (await store.LoadMetadataAsync("thread-1")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteThreadAsync_DoesNotThrowForNonexistentThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var act = async () => await store.DeleteThreadAsync("nonexistent-thread");

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task Store_IsThreadSafe_ForConcurrentAppends()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var tasks = new List<Task>();

        // Act - Append messages from multiple threads
        for (var i = 0; i < 10; i++)
        {
            var runId = $"run-{i}";
            tasks.Add(Task.Run(async () =>
            {
                var messages = CreateTestMessages("thread-1", runId, 10);
                await store.AppendMessagesAsync("thread-1", messages);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(100);
    }

    #endregion

    #region Helper Methods

    [Fact]
    public async Task GetAllThreadIds_ReturnsAllThreadIds()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 1));
        await store.AppendMessagesAsync("thread-2", CreateTestMessages("thread-2", "run-1", 1));
        await store.SaveMetadataAsync("thread-3", CreateTestMetadata("thread-3"));

        // Act
        var threadIds = store.GetAllThreadIds();

        // Assert
        threadIds.Should().Contain("thread-1");
        threadIds.Should().Contain("thread-2");
        threadIds.Should().Contain("thread-3");
    }

    [Fact]
    public async Task Clear_RemovesAllData()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 1));
        await store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));

        // Act
        store.Clear();

        // Assert
        store.GetMessageCount("thread-1").Should().Be(0);
        store.GetAllThreadIds().Should().BeEmpty();
    }

    #endregion

    #region ListThreadsAsync Tests

    [Fact]
    public async Task ListThreadsAsync_ReturnsEmptyWhenNoThreads()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListThreadsAsync_ReturnsAllThreadsWithMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));
        await store.SaveMetadataAsync("thread-2", CreateTestMetadata("thread-2"));
        await store.SaveMetadataAsync("thread-3", CreateTestMetadata("thread-3"));

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Select(m => m.ThreadId).Should().Contain(["thread-1", "thread-2", "thread-3"]);
    }

    [Fact]
    public async Task ListThreadsAsync_ReturnsSortedByLastUpdatedDescending()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await store.SaveMetadataAsync("thread-oldest", new ThreadMetadata
        {
            ThreadId = "thread-oldest",
            LastUpdated = now - 2000,
        });
        await store.SaveMetadataAsync("thread-newest", new ThreadMetadata
        {
            ThreadId = "thread-newest",
            LastUpdated = now,
        });
        await store.SaveMetadataAsync("thread-middle", new ThreadMetadata
        {
            ThreadId = "thread-middle",
            LastUpdated = now - 1000,
        });

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(3);
        result[0].ThreadId.Should().Be("thread-newest");
        result[1].ThreadId.Should().Be("thread-middle");
        result[2].ThreadId.Should().Be("thread-oldest");
    }

    [Fact]
    public async Task ListThreadsAsync_RespectsLimitParameter()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveMetadataAsync($"thread-{i}", new ThreadMetadata
            {
                ThreadId = $"thread-{i}",
                LastUpdated = now - (i * 1000), // thread-0 is newest
            });
        }

        // Act
        var result = await store.ListThreadsAsync(limit: 3);

        // Assert
        result.Should().HaveCount(3);
        result[0].ThreadId.Should().Be("thread-0"); // newest
        result[1].ThreadId.Should().Be("thread-1");
        result[2].ThreadId.Should().Be("thread-2");
    }

    [Fact]
    public async Task ListThreadsAsync_RespectsOffsetParameter()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveMetadataAsync($"thread-{i}", new ThreadMetadata
            {
                ThreadId = $"thread-{i}",
                LastUpdated = now - (i * 1000), // thread-0 is newest
            });
        }

        // Act
        var result = await store.ListThreadsAsync(limit: 2, offset: 2);

        // Assert
        result.Should().HaveCount(2);
        result[0].ThreadId.Should().Be("thread-2");
        result[1].ThreadId.Should().Be("thread-3");
    }

    [Fact]
    public async Task ListThreadsAsync_PreservesPropertiesInMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-with-props",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = new Dictionary<string, object>
            {
                ["title"] = "My Conversation Title",
                ["preview"] = "First message preview...",
            }.ToImmutableDictionary(),
        };
        await store.SaveMetadataAsync("thread-with-props", metadata);

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Properties.Should().NotBeNull();
        result[0].Properties!["title"].ToString().Should().Be("My Conversation Title");
        result[0].Properties!["preview"].ToString().Should().Be("First message preview...");
    }

    [Fact]
    public async Task ListThreadsAsync_CreatesMinimalMetadataForThreadsWithoutExplicitMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Add messages to a thread without explicit metadata
        await store.AppendMessagesAsync("thread-no-metadata", CreateTestMessages("thread-no-metadata", "run-1", 1));

        // Add a thread with metadata
        await store.SaveMetadataAsync("thread-with-metadata", CreateTestMetadata("thread-with-metadata"));

        // Act
        var result = await store.ListThreadsAsync();

        // Assert - Should include both threads (one with full metadata, one with minimal)
        result.Should().HaveCount(2);
        result.Select(m => m.ThreadId).Should().Contain("thread-with-metadata");
        result.Select(m => m.ThreadId).Should().Contain("thread-no-metadata");
    }

    #endregion

    #region Test Helpers

    private static List<PersistedMessage> CreateTestMessages(string threadId, string runId, int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return
        [
            .. Enumerable.Range(0, count)
                .Select(i => CreateMessage(threadId, runId, $"msg-{runId}-{i}", now + i, messageOrderIdx: i))
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
            Role = "user",
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
