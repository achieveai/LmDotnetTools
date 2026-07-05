using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for the <see cref="IRunLedgerStore"/> implementation on <see cref="SqliteConversationStore"/>.
/// </summary>
public class SqliteRunLedgerStoreTests : IAsyncLifetime
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

        SqliteConnection.ClearAllPools();
        await Task.Delay(50);

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

    #region Run Ledger Tests

    [Fact]
    public async Task UpsertRunLedgerAsync_ThenLoad_RoundTripsCorrectly()
    {
        // Arrange
        var entry = CreateEntry("thread-1", "run-1", inputIds: ["input-1", "input-2"]);

        // Act
        await _store.UpsertRunLedgerAsync(entry);
        var loaded = await _store.LoadRunLedgerAsync("run-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be(entry.ThreadId);
        loaded.RunId.Should().Be(entry.RunId);
        loaded.Status.Should().Be(entry.Status);
        loaded.InputIds.Should().Equal(entry.InputIds);
        loaded.CreatedAt.Should().Be(entry.CreatedAt);
        loaded.UpdatedAt.Should().Be(entry.UpdatedAt);
    }

    [Fact]
    public async Task UpsertRunLedgerAsync_WithEmptyInputIds_RoundTripsCorrectly()
    {
        // Arrange
        var entry = CreateEntry("thread-1", "run-1", inputIds: []);

        // Act
        await _store.UpsertRunLedgerAsync(entry);
        var loaded = await _store.LoadRunLedgerAsync("run-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.InputIds.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertRunLedgerAsync_WithMultipleInputIds_RoundTripsLosslessly()
    {
        // Arrange
        var inputIds = new List<string> { "input-a", "input-b", "input-c", "input-d" };
        var entry = CreateEntry("thread-1", "run-1", inputIds: inputIds);

        // Act
        await _store.UpsertRunLedgerAsync(entry);
        var loaded = await _store.LoadRunLedgerAsync("run-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.InputIds.Should().Equal(inputIds);
    }

    [Fact]
    public async Task UpsertRunLedgerAsync_CalledTwiceWithSameRunId_UpdatesInPlace()
    {
        // Arrange
        var original = CreateEntry("thread-1", "run-1", status: RunStatus.Queued, inputIds: ["input-1"]);
        var updated = original with { Status = RunStatus.Completed, InputIds = ["input-1", "input-2"] };

        // Act
        await _store.UpsertRunLedgerAsync(original);
        await _store.UpsertRunLedgerAsync(updated);

        // Assert
        var loaded = await _store.LoadRunLedgerAsync("run-1");
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(RunStatus.Completed);
        loaded.InputIds.Should().Equal("input-1", "input-2");

        var all = await _store.ListRunLedgerAsync("thread-1");
        all.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListRunLedgerAsync_ReturnsEntriesOrderedByCreatedAtDescending()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var oldest = CreateEntry("thread-1", "run-1", createdAt: now);
        var middle = CreateEntry("thread-1", "run-2", createdAt: now.AddMinutes(1));
        var newest = CreateEntry("thread-1", "run-3", createdAt: now.AddMinutes(2));

        await _store.UpsertRunLedgerAsync(oldest);
        await _store.UpsertRunLedgerAsync(newest);
        await _store.UpsertRunLedgerAsync(middle);

        // Act
        var loaded = await _store.ListRunLedgerAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].RunId.Should().Be("run-3");
        loaded[1].RunId.Should().Be("run-2");
        loaded[2].RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task ListRunLedgerAsync_OnlyReturnsEntriesForSpecifiedThread()
    {
        // Arrange
        await _store.UpsertRunLedgerAsync(CreateEntry("thread-1", "run-1"));
        await _store.UpsertRunLedgerAsync(CreateEntry("thread-2", "run-2"));

        // Act
        var loaded = await _store.ListRunLedgerAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(1);
        loaded[0].RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task ListRunLedgerAsync_ReturnsEmptyListForUnknownThread()
    {
        // Act
        var loaded = await _store.ListRunLedgerAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadRunLedgerAsync_ForUnknownRunId_ReturnsNull()
    {
        // Act
        var loaded = await _store.LoadRunLedgerAsync("nonexistent-run");

        // Assert
        loaded.Should().BeNull();
    }

    #endregion

    #region Accepted Input Tests

    [Fact]
    public async Task RecordAcceptedInputAsync_ThenList_RoundTripsCorrectly()
    {
        // Arrange
        var acceptedAt = DateTimeOffset.UtcNow;

        // Act
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", acceptedAt);
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");

        // Assert
        ids.Should().BeEquivalentTo(["input-1"]);
    }

    [Fact]
    public async Task ListAcceptedInputIdsAsync_ForUnknownThread_ReturnsEmptySet()
    {
        // Act
        var ids = await _store.ListAcceptedInputIdsAsync("nonexistent-thread");

        // Assert
        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAcceptedInputAsync_RemovesEntry()
    {
        // Arrange
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);

        // Act
        await _store.RemoveAcceptedInputAsync("thread-1", "input-1");
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");

        // Assert
        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAcceptedInputAsync_OfAbsentEntry_IsNoOp()
    {
        // Act
        var act = async () => await _store.RemoveAcceptedInputAsync("thread-1", "nonexistent-input");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAcceptedInputAsync_CalledTwiceForSamePair_IsIdempotent()
    {
        // Arrange
        var firstAcceptedAt = DateTimeOffset.UtcNow;
        var secondAcceptedAt = firstAcceptedAt.AddMinutes(5);

        // Act
        var act = async () =>
        {
            await _store.RecordAcceptedInputAsync("thread-1", "input-1", firstAcceptedAt);
            await _store.RecordAcceptedInputAsync("thread-1", "input-1", secondAcceptedAt);
        };

        // Assert
        await act.Should().NotThrowAsync();
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");
        ids.Should().BeEquivalentTo(["input-1"]);
    }

    [Fact]
    public async Task ListAcceptedInputIdsAsync_OnlyReturnsIdsForSpecifiedThread()
    {
        // Arrange
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);
        await _store.RecordAcceptedInputAsync("thread-2", "input-2", DateTimeOffset.UtcNow);

        // Act
        var ids = await _store.ListAcceptedInputIdsAsync("thread-1");

        // Assert
        ids.Should().BeEquivalentTo(["input-1"]);
    }

    #endregion

    #region Schema Initialization Tests

    [Fact]
    public async Task SchemaInitialization_IsIdempotentForRunLedgerTables()
    {
        // Act - Multiple operations across both new tables should not fail due to schema issues
        await _store.UpsertRunLedgerAsync(CreateEntry("thread-1", "run-1"));
        await _store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);
        await _store.UpsertRunLedgerAsync(CreateEntry("thread-1", "run-2"));
        await _store.RecordAcceptedInputAsync("thread-1", "input-2", DateTimeOffset.UtcNow);

        // Assert
        var runs = await _store.ListRunLedgerAsync("thread-1");
        runs.Should().HaveCount(2);

        var inputIds = await _store.ListAcceptedInputIdsAsync("thread-1");
        inputIds.Should().HaveCount(2);
    }

    #endregion

    #region Test Helpers

    private static RunLedgerEntry CreateEntry(
        string threadId,
        string runId,
        RunStatus status = RunStatus.Queued,
        IReadOnlyList<string>? inputIds = null,
        DateTimeOffset? createdAt = null)
    {
        // Truncate to millisecond precision - the store round-trips via Unix milliseconds.
        var created = DateTimeOffset.FromUnixTimeMilliseconds(
            (createdAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        return new RunLedgerEntry(
            threadId,
            runId,
            status,
            inputIds ?? ["input-1"],
            created,
            created);
    }

    #endregion
}
