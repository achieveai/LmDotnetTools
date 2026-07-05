using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for FileConversationStore's IRunLedgerStore implementation.
/// </summary>
public class FileRunLedgerStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FileConversationStore _store;

    public FileRunLedgerStoreTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FileRunLedgerStoreTests_{Guid.NewGuid()}");
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

    #region Run Ledger Tests

    [Fact]
    public async Task UpsertRunLedgerAsync_ThenLoad_RoundTripsEntry()
    {
        // Arrange
        var entry = CreateEntry("thread-1", "run-1", RunStatus.Queued, ["input-1", "input-2"]);

        // Act
        await _store.UpsertRunLedgerAsync(entry);
        var loaded = await _store.LoadRunLedgerAsync("run-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
        loaded.RunId.Should().Be("run-1");
        loaded.Status.Should().Be(RunStatus.Queued);
        loaded.InputIds.Should().BeEquivalentTo(["input-1", "input-2"]);
    }

    [Fact]
    public async Task UpsertRunLedgerAsync_CalledTwiceWithSameRunId_UpdatesInPlace()
    {
        // Arrange
        var initial = CreateEntry("thread-1", "run-1", RunStatus.Queued, ["input-1"]);
        var updated = initial with { Status = RunStatus.Completed, UpdatedAt = initial.UpdatedAt.AddMinutes(1) };

        // Act
        await _store.UpsertRunLedgerAsync(initial);
        await _store.UpsertRunLedgerAsync(updated);

        // Assert
        var all = await _store.ListRunLedgerAsync("thread-1");
        all.Should().HaveCount(1);
        all[0].Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task ListRunLedgerAsync_ReturnsEntriesOrderedByCreatedAtDescending()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var oldest = CreateEntry("thread-1", "run-oldest", RunStatus.Completed, ["i1"], now.AddMinutes(-10));
        var middle = CreateEntry("thread-1", "run-middle", RunStatus.Completed, ["i2"], now.AddMinutes(-5));
        var newest = CreateEntry("thread-1", "run-newest", RunStatus.InProgress, ["i3"], now);

        await _store.UpsertRunLedgerAsync(oldest);
        await _store.UpsertRunLedgerAsync(middle);
        await _store.UpsertRunLedgerAsync(newest);

        // Act
        var result = await _store.ListRunLedgerAsync("thread-1");

        // Assert
        result.Should().HaveCount(3);
        result[0].RunId.Should().Be("run-newest");
        result[1].RunId.Should().Be("run-middle");
        result[2].RunId.Should().Be("run-oldest");
    }

    [Fact]
    public async Task LoadRunLedgerAsync_ReturnsNullForUnknownRunId()
    {
        // Act
        var result = await _store.LoadRunLedgerAsync("unknown-run");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Accepted Input Tests

    [Fact]
    public async Task RecordAcceptedInputAsync_ThenList_RoundTripsInputId()
    {
        // Act
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");

        // Assert
        ids.Should().BeEquivalentTo(new HashSet<string> { "input-1" });
    }

    [Fact]
    public async Task RemoveAcceptedInputAsync_RemovesEntry()
    {
        // Arrange
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);
        await _store.RecordAcceptedInputAsync("thread-1", "input-2", DateTimeOffset.UtcNow);

        // Act
        await _store.RemoveAcceptedInputAsync("thread-1", "input-1");
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");

        // Assert
        ids.Should().BeEquivalentTo(new HashSet<string> { "input-2" });
    }

    [Fact]
    public async Task RemoveAcceptedInputAsync_OfAbsentEntry_IsNoOp()
    {
        // Act
        var act = async () => await _store.RemoveAcceptedInputAsync("thread-1", "never-recorded");

        // Assert
        await act.Should().NotThrowAsync();
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");
        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAcceptedInputIdsAsync_ReturnsEmptySetForUnknownThread()
    {
        // Act
        var ids = await _store.ListAcceptedInputIdsAsync("nonexistent-thread");

        // Assert
        ids.Should().BeEmpty();
    }

    #endregion

    #region Cross-Instance Persistence Tests

    [Fact]
    public async Task RunLedger_PersistsAcrossStoreInstances()
    {
        // Arrange
        var entry = CreateEntry("thread-1", "run-1", RunStatus.Completed, ["input-1"]);
        await _store.UpsertRunLedgerAsync(entry);

        // Act - fresh instance pointed at the same directory
        var freshStore = new FileConversationStore(_testDirectory);
        var list = await freshStore.ListRunLedgerAsync("thread-1");
        var loaded = await freshStore.LoadRunLedgerAsync("run-1");

        // Assert
        list.Should().HaveCount(1);
        loaded.Should().NotBeNull();
        loaded!.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task AcceptedInputs_PersistAcrossStoreInstances()
    {
        // Arrange
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);

        // Act - fresh instance pointed at the same directory
        var freshStore = new FileConversationStore(_testDirectory);
        var ids = await freshStore.ListAcceptedInputIdsAsync("thread-1");

        // Assert
        ids.Should().BeEquivalentTo(new HashSet<string> { "input-1" });
    }

    #endregion

    #region Test Helpers

    private static RunLedgerEntry CreateEntry(
        string threadId,
        string runId,
        RunStatus status,
        IReadOnlyList<string> inputIds,
        DateTimeOffset? createdAt = null)
    {
        var timestamp = createdAt ?? DateTimeOffset.UtcNow;
        return new RunLedgerEntry(threadId, runId, status, inputIds, timestamp, timestamp);
    }

    #endregion
}
