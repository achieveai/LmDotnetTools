using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.TodoBoard;

public class ConversationTodoProjectionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static TodoBoardSnapshot SampleBoard(DateTimeOffset? capturedAt = null)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = "conv-1",
            CapturedAtUtc = capturedAt ?? Noon,
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = "Wire the SSE endpoint",
                    Notes = ["waiting on schema"],
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
                new TodoTaskNode
                {
                    Id = "2",
                    Status = TodoTaskStatus.NotStarted,
                    Title = "Vitest coverage",
                },
            ],
        };
    }

    private static async Task AssertRoundTripsAsync(IConversationStore store)
    {
        var original = SampleBoard();

        await ConversationTodoProjection.SaveAsync(store, original);
        var loaded = await ConversationTodoProjection.LoadAsync(store, "conv-1");

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task RoundTrips_ThroughInMemoryStore()
    {
        await AssertRoundTripsAsync(new InMemoryConversationStore());
    }

    [Fact]
    public async Task RoundTrips_ThroughFileStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"todo_proj_{Guid.NewGuid():N}");

        await AssertRoundTripsAsync(new FileConversationStore(dir));

        // #477: detach-then-delete rather than recursive-delete in place. Deliberately NOT in a finally —
        // a throw from a finally REPLACES the assertion failure unwinding through it.
        DetachedStoreTeardown.Purge(dir);
    }

    [Fact]
    public async Task RoundTrips_ThroughSqliteStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"todo_proj_{Guid.NewGuid():N}.db");
        var store = new SqliteConversationStore(dbPath);
        try
        {
            await AssertRoundTripsAsync(store);
        }
        finally
        {
            await store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public async Task Save_PreservesNestedRowsAndNotes_NotJustTheTopLevel()
    {
        // The board is a tree; a projection that flattened it would still round-trip the row COUNT.
        var store = new InMemoryConversationStore();

        await ConversationTodoProjection.SaveAsync(store, SampleBoard());
        var loaded = await ConversationTodoProjection.LoadAsync(store, "conv-1");

        loaded!.Tasks.Should().HaveCount(2);
        loaded.Tasks[0].SubTasks.Should().ContainSingle();
        loaded.Tasks[0].SubTasks[0].Id.Should().Be("1.1");
        loaded.Tasks[0].SubTasks[0].Status.Should().Be(TodoTaskStatus.Completed);
        loaded.Tasks[0].Notes.Should().ContainSingle().Which.Should().Be("waiting on schema");
        loaded.Tasks[1].Status.Should().Be(TodoTaskStatus.NotStarted);
    }

    [Fact]
    public void FromMetadata_ReturnsNull_WhenNoProjectionPresent()
    {
        ConversationTodoProjection.FromMetadata(null).Should().BeNull();

        var empty = new ThreadMetadata { ThreadId = "conv-1", LastUpdated = 0 };
        ConversationTodoProjection.FromMetadata(empty).Should().BeNull();
    }

    [Fact]
    public async Task FromMetadata_ReturnsNull_WhenBlobIsCorrupt()
    {
        // A corrupt blob must read as "no board", never throw: the endpoint behind this would turn a
        // single bad write into a 500 on every subsequent read of that conversation.
        var store = new InMemoryConversationStore();
        await SetRawPropertyAsync(store, "conv-1", "{ not valid json");

        var metadata = await store.LoadMetadataAsync("conv-1");
        ConversationTodoProjection.FromMetadata(metadata).Should().BeNull();
        (await ConversationTodoProjection.LoadAsync(store, "conv-1")).Should().BeNull();
    }

    [Fact]
    public async Task FromMetadata_ReturnsNull_WhenBlobIsFromANewerSchema()
    {
        var store = new InMemoryConversationStore();
        var future = JsonSerializer.Serialize(SampleBoard() with { SchemaVersion = 99 });
        await SetRawPropertyAsync(store, "conv-1", future);

        (await ConversationTodoProjection.LoadAsync(store, "conv-1")).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_DoesNotOverwrite_NewerSchemaProjection()
    {
        // Rollback / mixed-version deployment: an older build must preserve forward-compatible data
        // rather than clobbering it with a shape the newer build can no longer read.
        var store = new InMemoryConversationStore();
        var futureJson = JsonSerializer.Serialize(
            SampleBoard() with
            {
                SchemaVersion = 99,
                Tasks =
                [
                    new TodoTaskNode
                    {
                        Id = "9",
                        Status = TodoTaskStatus.Completed,
                        Title = "from the future",
                    },
                ],
            }
        );
        await SetRawPropertyAsync(store, "conv-1", futureJson);

        await ConversationTodoProjection.SaveAsync(store, SampleBoard(Noon.AddHours(1)));

        var metadata = await store.LoadMetadataAsync("conv-1");
        var raw = (string)metadata!.Properties![ConversationTodoProjection.PropertyKey];
        using var document = JsonDocument.Parse(raw);
        document.RootElement.GetProperty("SchemaVersion").GetInt32().Should().Be(99);
        document.RootElement.GetProperty("Tasks").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_DoesNotReplaceNewerBoard_WithAnOlderCapture()
    {
        // Two writers observe the same board at different instants (a slow write racing a fresh one, or
        // a post-restart writer). The later capture is the board; the earlier one must not regress it.
        var store = new InMemoryConversationStore();
        var fresh = SampleBoard(Noon.AddMinutes(10)) with
        {
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.Completed,
                    Title = "done",
                },
            ],
        };
        var stale = SampleBoard(Noon) with
        {
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.NotStarted,
                    Title = "done",
                },
            ],
        };

        await ConversationTodoProjection.SaveAsync(store, fresh);
        await ConversationTodoProjection.SaveAsync(store, stale);

        var loaded = await ConversationTodoProjection.LoadAsync(store, "conv-1");
        loaded!.Tasks[0].Status.Should().Be(TodoTaskStatus.Completed);
        loaded.CapturedAtUtc.Should().Be(Noon.AddMinutes(10));
    }

    [Fact]
    public async Task SaveAsync_AcceptsARecaptureAtTheSameInstant()
    {
        // Equal capture times are the common case at coarse clock resolution; treating them as stale
        // would silently drop every write that lands inside one tick of the previous one.
        var store = new InMemoryConversationStore();
        await ConversationTodoProjection.SaveAsync(store, SampleBoard());

        await ConversationTodoProjection.SaveAsync(
            store,
            SampleBoard() with
            {
                Tasks =
                [
                    new TodoTaskNode
                    {
                        Id = "1",
                        Status = TodoTaskStatus.Completed,
                        Title = "later",
                    },
                ],
            }
        );

        var loaded = await ConversationTodoProjection.LoadAsync(store, "conv-1");
        loaded!.Tasks.Should().ContainSingle().Which.Title.Should().Be("later");
    }

    [Fact]
    public async Task SaveAsync_PreservesOtherPropertiesInTheBag()
    {
        // The bag is shared with the usage projection, the mode binding, and more. A save that replaced
        // Properties wholesale would silently delete every one of them.
        var store = new InMemoryConversationStore();
        await store.UpdateMetadataAsync(
            "conv-1",
            _ => new ThreadMetadata
            {
                ThreadId = "conv-1",
                LastUpdated = 0,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem("unrelated.key", "keep me"),
            }
        );

        await ConversationTodoProjection.SaveAsync(store, SampleBoard());

        var metadata = await store.LoadMetadataAsync("conv-1");
        metadata!.Properties!["unrelated.key"].Should().Be("keep me");
        ConversationTodoProjection.FromMetadata(metadata).Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_PersistsAnEmptyBoard_WhenThatIsTheLatestCapture()
    {
        // The projection persists what it is given: "the agent cleared the board" is a real state. The
        // policy about WHEN an empty board may be written belongs at the call site, which knows whether
        // emptiness means "cleared" or merely "this process has not seen the board yet".
        var store = new InMemoryConversationStore();
        await ConversationTodoProjection.SaveAsync(store, SampleBoard());

        await ConversationTodoProjection.SaveAsync(store, SampleBoard(Noon.AddMinutes(5)) with { Tasks = [] });

        var loaded = await ConversationTodoProjection.LoadAsync(store, "conv-1");
        loaded.Should().NotBeNull();
        loaded!.Tasks.Should().BeEmpty();
    }

    private static Task SetRawPropertyAsync(IConversationStore store, string threadId, string rawJson)
    {
        return store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                    ConversationTodoProjection.PropertyKey,
                    rawJson
                );
                return existing is not null
                    ? existing with
                    {
                        Properties = properties,
                    }
                    : new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = 0,
                        Properties = properties,
                    };
            }
        );
    }

    private static void TryDelete(string path)
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
            // best-effort cleanup
        }
    }
}
