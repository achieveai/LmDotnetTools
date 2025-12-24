using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for FileConversationStore.
/// </summary>
public class FileConversationStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FileConversationStore _store;

    public FileConversationStoreTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FileConversationStoreTests_{Guid.NewGuid()}");
        _store = new FileConversationStore(_testDirectory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_CreatesBaseDirectory()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"FileStoreTest_{Guid.NewGuid()}");

        try
        {
            // Act
            _ = new FileConversationStore(tempDir);

            // Assert
            Directory.Exists(tempDir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_ThrowsOnNullDirectory()
    {
        // Act
        var act = () => new FileConversationStore(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region AppendMessagesAsync Tests

    [Fact]
    public async Task AppendMessagesAsync_CreatesThreadDirectory()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 2);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var threadDir = Path.Combine(_testDirectory, "thread-1");
        Directory.Exists(threadDir).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMessagesAsync_CreatesMessagesFile()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 2);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var messagesFile = Path.Combine(_testDirectory, "thread-1", "messages.json");
        File.Exists(messagesFile).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingMessages()
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
    public async Task AppendMessagesAsync_WithEmptyList_DoesNotCreateFile()
    {
        // Arrange
        var messages = new List<PersistedMessage>();

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var threadDir = Path.Combine(_testDirectory, "thread-1");
        Directory.Exists(threadDir).Should().BeFalse();
    }

    [Fact]
    public async Task AppendMessagesAsync_SanitizesThreadId()
    {
        // Arrange
        var threadIdWithInvalidChars = "thread:with<invalid>chars";
        var messages = CreateTestMessages(threadIdWithInvalidChars, "run-1", 1);

        // Act
        await _store.AppendMessagesAsync(threadIdWithInvalidChars, messages);

        // Assert - Should not throw and should create a sanitized directory
        var loaded = await _store.LoadMessagesAsync(threadIdWithInvalidChars);
        loaded.Should().HaveCount(1);
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
    public async Task LoadMessagesAsync_ReturnsEmptyListForNonexistentThread()
    {
        // Act
        var loaded = await _store.LoadMessagesAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMessagesAsync_HandlesCorruptedFile()
    {
        // Arrange
        var threadDir = Path.Combine(_testDirectory, "corrupted-thread");
        Directory.CreateDirectory(threadDir);
        await File.WriteAllTextAsync(Path.Combine(threadDir, "messages.json"), "invalid json content");

        // Act
        var loaded = await _store.LoadMessagesAsync("corrupted-thread");

        // Assert - Should return empty list instead of throwing
        loaded.Should().BeEmpty();
    }

    #endregion

    #region SaveMetadataAsync / LoadMetadataAsync Tests

    [Fact]
    public async Task SaveMetadataAsync_CreatesMetadataFile()
    {
        // Arrange
        var metadata = CreateTestMetadata("thread-1");

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);

        // Assert
        var metadataFile = Path.Combine(_testDirectory, "thread-1", "metadata.json");
        File.Exists(metadataFile).Should().BeTrue();
    }

    [Fact]
    public async Task SaveMetadataAsync_RoundTripsMetadata()
    {
        // Arrange
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-1",
            CurrentRunId = "run-1",
            LatestRunId = "run-2",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionMappings = new Dictionary<string, string>
            {
                ["session-1"] = "external-id-1",
                ["session-2"] = "external-id-2",
            },
        };

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
        loaded.CurrentRunId.Should().Be("run-1");
        loaded.LatestRunId.Should().Be("run-2");
        loaded.SessionMappings.Should().ContainKey("session-1");
        loaded.SessionMappings!["session-1"].Should().Be("external-id-1");
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
    public async Task LoadMetadataAsync_HandlesCorruptedFile()
    {
        // Arrange
        var threadDir = Path.Combine(_testDirectory, "corrupted-metadata");
        Directory.CreateDirectory(threadDir);
        await File.WriteAllTextAsync(Path.Combine(threadDir, "metadata.json"), "invalid json");

        // Act
        var loaded = await _store.LoadMetadataAsync("corrupted-metadata");

        // Assert - Should return null instead of throwing
        loaded.Should().BeNull();
    }

    #endregion

    #region DeleteThreadAsync Tests

    [Fact]
    public async Task DeleteThreadAsync_RemovesThreadDirectory()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 2);
        var metadata = CreateTestMetadata("thread-1");

        await _store.AppendMessagesAsync("thread-1", messages);
        await _store.SaveMetadataAsync("thread-1", metadata);

        var threadDir = Path.Combine(_testDirectory, "thread-1");
        Directory.Exists(threadDir).Should().BeTrue();

        // Act
        await _store.DeleteThreadAsync("thread-1");

        // Assert
        Directory.Exists(threadDir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteThreadAsync_DoesNotThrowForNonexistentThread()
    {
        // Act
        var act = async () => await _store.DeleteThreadAsync("nonexistent-thread");

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Atomic Write Tests

    [Fact]
    public async Task AppendMessagesAsync_WritesAtomically()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 5);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert - No temp file should remain
        var threadDir = Path.Combine(_testDirectory, "thread-1");
        var tempFiles = Directory.GetFiles(threadDir, "*.tmp");
        tempFiles.Should().BeEmpty();
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

    private static ThreadMetadata CreateTestMetadata(string threadId)
    {
        return new ThreadMetadata
        {
            ThreadId = threadId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    #endregion
}
