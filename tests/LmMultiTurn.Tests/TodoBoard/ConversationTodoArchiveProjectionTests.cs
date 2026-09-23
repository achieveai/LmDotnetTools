using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.TodoBoard;

/// <summary>
///     Bug 19: a <c>bulk-initialize</c> clear used to be unrecoverable. The board the clear dropped is
///     now appended here, under its own metadata key, bounded so a run of clears cannot grow the
///     conversation row without limit.
/// </summary>
public class ConversationTodoArchiveProjectionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static TodoBoardSnapshot Board(int ordinal, DateTimeOffset? capturedAt = null) =>
        new()
        {
            ThreadId = "conv-1",
            CapturedAtUtc = capturedAt ?? Noon.AddMinutes(ordinal),
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = $"Board {ordinal}",
                    Notes = ["waiting on schema"],
                    Artifacts = ["docs/spec.md"],
                    SubTasks =
                    [
                        new TodoTaskNode
                        {
                            Id = "1.1",
                            Status = TodoTaskStatus.Completed,
                            Title = "Add the map",
                        },
                    ],
                },
            ],
        };

    private static Task SeedConversationAsync(IConversationStore store, string threadId = "conv-1") =>
        store.UpdateMetadataAsync(
            threadId,
            existing =>
                existing
                ?? new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = 0,
                    TenantId = "tenant-1",
                }
        );

    [Fact]
    public async Task AppendAsync_RoundTripsTheArchivedBoard()
    {
        // Mutation that must go red: dropping SubTasks, Notes or Artifacts from what is serialized.
        // An archive that loses the completed rows is not a recovery of the board that was wiped.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);

        await ConversationTodoArchiveProjection.AppendAsync(store, Board(1));

        var archive = await ConversationTodoArchiveProjection.LoadAsync(store, "conv-1");
        archive.Should().NotBeNull();
        var entry = archive!.Entries.Should().ContainSingle().Subject;
        entry.ThreadId.Should().Be("conv-1");
        entry.CapturedAtUtc.Should().Be(Noon.AddMinutes(1));
        entry.Tasks.Should().ContainSingle();
        entry.Tasks[0].Title.Should().Be("Board 1");
        entry.Tasks[0].Notes.Should().ContainSingle();
        entry.Tasks[0].Artifacts.Should().ContainSingle();
        entry.Tasks[0].SubTasks.Should().ContainSingle().Which.Status.Should().Be(TodoTaskStatus.Completed);
    }

    [Fact]
    public async Task AppendAsync_KeepsTheFiveNewestEntries_NewestFirst()
    {
        // The bound is the whole reason this is a separate key rather than a growing list on the board:
        // a model that clears repeatedly must not grow the conversation's metadata row without limit.
        // Mutation that must go red: appending to the tail, or raising/removing the cap.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);

        for (var i = 1; i <= 7; i++)
        {
            await ConversationTodoArchiveProjection.AppendAsync(store, Board(i));
        }

        var archive = await ConversationTodoArchiveProjection.LoadAsync(store, "conv-1");
        archive!.Entries.Should().HaveCount(ConversationTodoArchiveProjection.MaxEntries);
        archive
            .Entries.Select(e => e.Tasks[0].Title)
            .Should()
            .Equal("Board 7", "Board 6", "Board 5", "Board 4", "Board 3");
    }

    [Fact]
    public async Task AppendAsync_WithNoMetadataRow_PersistsNothingAndMintsNoRow()
    {
        // The same no-mint policy ConversationTodoProjection.SaveAsync documents: a row minted here
        // would carry no TenantId and would be unreadable by everyone, including its owner.
        var store = new InMemoryConversationStore();

        await ConversationTodoArchiveProjection.AppendAsync(store, Board(1));

        (await store.LoadMetadataAsync("conv-1")).Should().BeNull();
        (await ConversationTodoArchiveProjection.LoadAsync(store, "conv-1")).Should().BeNull();
    }

    [Fact]
    public void FromMetadata_NewerSchemaVersion_ReadsAsAbsent()
    {
        var metadata = WithRawArchive("{\"SchemaVersion\":99,\"Entries\":[]}");

        ConversationTodoArchiveProjection.FromMetadata(metadata).Should().BeNull();
    }

    [Fact]
    public void FromMetadata_CorruptBlob_ReadsAsAbsent()
    {
        // An archive is a convenience: a bad blob must read as "nothing archived", never as a 500 on
        // every subsequent read of the conversation.
        ConversationTodoArchiveProjection.FromMetadata(WithRawArchive("{ not json")).Should().BeNull();
        ConversationTodoArchiveProjection.FromMetadata(null).Should().BeNull();
    }

    private static ThreadMetadata WithRawArchive(string json) =>
        new()
        {
            ThreadId = "conv-1",
            LastUpdated = 0,
            TenantId = "tenant-1",
            Properties = ImmutableDictionary<string, object>.Empty.Add(
                ConversationTodoArchiveProjection.PropertyKey,
                json
            ),
        };
}
