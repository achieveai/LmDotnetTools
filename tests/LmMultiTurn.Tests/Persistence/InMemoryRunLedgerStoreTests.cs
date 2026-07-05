using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for the IRunLedgerStore implementation on InMemoryConversationStore.
/// </summary>
public class InMemoryRunLedgerStoreTests
{
    #region UpsertRunLedgerAsync / LoadRunLedgerAsync Tests

    [Fact]
    public async Task UpsertThenLoad_ReturnsSameEntry()
    {
        var store = new InMemoryConversationStore();
        var entry = CreateEntry("thread-1", "run-1", inputIds: ["input-1", "input-2"]);

        await store.UpsertRunLedgerAsync(entry);
        var loaded = await store.LoadRunLedgerAsync("run-1");

        loaded.Should().Be(entry);
        loaded!.InputIds.Should().BeEquivalentTo(["input-1", "input-2"], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task UpsertTwiceWithSameRunId_UpdatesInPlace()
    {
        var store = new InMemoryConversationStore();
        var original = CreateEntry("thread-1", "run-1", status: RunStatus.Queued);
        var updated = original with { Status = RunStatus.Completed };

        await store.UpsertRunLedgerAsync(original);
        await store.UpsertRunLedgerAsync(updated);

        var loaded = await store.LoadRunLedgerAsync("run-1");
        loaded!.Status.Should().Be(RunStatus.Completed);

        var listed = await store.ListRunLedgerAsync("thread-1");
        listed.Should().ContainSingle(e => e.RunId == "run-1");
    }

    [Fact]
    public async Task ListRunLedgerAsync_OrdersByCreatedAtDescending()
    {
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow;

        var oldest = CreateEntry("thread-1", "run-oldest", createdAt: now - TimeSpan.FromMinutes(2));
        var newest = CreateEntry("thread-1", "run-newest", createdAt: now);
        var middle = CreateEntry("thread-1", "run-middle", createdAt: now - TimeSpan.FromMinutes(1));

        await store.UpsertRunLedgerAsync(oldest);
        await store.UpsertRunLedgerAsync(newest);
        await store.UpsertRunLedgerAsync(middle);

        var listed = await store.ListRunLedgerAsync("thread-1");

        listed.Should().HaveCount(3);
        listed[0].RunId.Should().Be("run-newest");
        listed[1].RunId.Should().Be("run-middle");
        listed[2].RunId.Should().Be("run-oldest");
    }

    [Fact]
    public async Task LoadRunLedgerAsync_ReturnsNullForUnknownRunId()
    {
        var store = new InMemoryConversationStore();

        var loaded = await store.LoadRunLedgerAsync("unknown-run");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ListRunLedgerAsync_ReturnsEmptyForUnknownThread()
    {
        var store = new InMemoryConversationStore();

        var listed = await store.ListRunLedgerAsync("unknown-thread");

        listed.Should().BeEmpty();
    }

    #endregion

    #region Accepted-Input Tests

    [Fact]
    public async Task RecordAcceptedInput_ThenList_ContainsInputId()
    {
        var store = new InMemoryConversationStore();

        await store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);
        var accepted = await store.ListAcceptedInputIdsAsync("thread-1");

        accepted.Should().Contain("input-1");
    }

    [Fact]
    public async Task RemoveAcceptedInput_RemovesFromList()
    {
        var store = new InMemoryConversationStore();
        await store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);

        await store.RemoveAcceptedInputAsync("thread-1", "input-1");
        var accepted = await store.ListAcceptedInputIdsAsync("thread-1");

        accepted.Should().NotContain("input-1");
    }

    [Fact]
    public async Task RemoveAcceptedInput_OnNeverRecordedInputId_IsNoOp()
    {
        var store = new InMemoryConversationStore();

        var act = () => store.RemoveAcceptedInputAsync("thread-1", "never-recorded");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListAcceptedInputIdsAsync_ReturnsEmptySetForThreadWithNothingAccepted()
    {
        var store = new InMemoryConversationStore();

        var accepted = await store.ListAcceptedInputIdsAsync("thread-with-nothing-accepted");

        accepted.Should().NotBeNull();
        accepted.Should().BeEmpty();
    }

    #endregion

    #region DeleteThreadAsync / Clear Cleanup Tests

    [Fact]
    public async Task DeleteThreadAsync_RemovesRunLedgerAndAcceptedInputs()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(CreateEntry("thread-1", "run-1"));
        await store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);

        await store.DeleteThreadAsync("thread-1");

        (await store.ListRunLedgerAsync("thread-1")).Should().BeEmpty();
        (await store.LoadRunLedgerAsync("run-1")).Should().BeNull();
        (await store.ListAcceptedInputIdsAsync("thread-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_RemovesRunLedgerAndAcceptedInputs()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(CreateEntry("thread-1", "run-1"));
        await store.RecordAcceptedInputAsync("thread-1", "input-1", DateTimeOffset.UtcNow);

        store.Clear();

        (await store.ListRunLedgerAsync("thread-1")).Should().BeEmpty();
        (await store.ListAcceptedInputIdsAsync("thread-1")).Should().BeEmpty();
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
        var timestamp = createdAt ?? DateTimeOffset.UtcNow;
        return new RunLedgerEntry(
            ThreadId: threadId,
            RunId: runId,
            Status: status,
            InputIds: inputIds ?? [runId + "-input"],
            CreatedAt: timestamp,
            UpdatedAt: timestamp);
    }

    #endregion
}
