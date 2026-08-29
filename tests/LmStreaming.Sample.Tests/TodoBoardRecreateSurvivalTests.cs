using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     The full durability chain across a pool-entry recreate (#583 PR 2, review F-002), wired exactly
///     the way Program.cs wires it: TaskManager mutations fire OnChanged, OnChanged schedules the
///     <see cref="TodoBoardPersistenceWriter" />, the writer persists through
///     <see cref="ConversationTodoProjection" /> — and a RECREATED entry hydrates its manager from the
///     persisted board via <see cref="TaskManager.FromSnapshot(AchieveAi.LmDotnetTools.LmCore.Models.TodoBoardSnapshot)" />
///     before its first mutation. This test lives here because it needs Misc's TaskManager and
///     LmMultiTurn's projection in one assembly, and only the sample references both.
/// </summary>
public class TodoBoardRecreateSurvivalTests
{
    private static Task SeedMetadataRowAsync(IConversationStore store, string threadId = "conv-1")
    {
        return store.UpdateMetadataAsync(
            threadId,
            existing => existing ?? new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 }
        );
    }

    private static TodoBoardPersistenceWriter WireWriter(IConversationStore store, TaskManager manager)
    {
        var writer = new TodoBoardPersistenceWriter(store, "conv-1", () => manager.GetTodoBoardSnapshot("conv-1"));
        manager.OnChanged = writer.Schedule;
        return writer;
    }

    [Fact]
    public async Task RecreatedEntry_FirstMutation_ExtendsThePersistedBoard_InsteadOfOverwritingIt()
    {
        // The F-002 failure mode: a recreated entry starting from `new TaskManager()` captures a
        // one-row board on its first mutation, the writer's IsEmpty guard passes (one row is not
        // empty), the projection's monotonic guard passes (the capture is genuinely newer), and the
        // persisted board is silently truncated. Mutation that must go red: hydrating with
        // `new TaskManager()` below instead of FromSnapshot.
        var store = new InMemoryConversationStore();
        await SeedMetadataRowAsync(store);

        // --- First life of the pool entry: build up a real board and let it become durable. ---
        var firstManager = new TaskManager();
        await using (var firstWriter = WireWriter(store, firstManager))
        {
            _ = firstManager.AddTask("Wire the SSE endpoint"); // 1
            _ = firstManager.AddTask("Add the map", "1"); // 1.1
            _ = firstManager.AddTask("Vitest coverage"); // 2
            _ = firstManager.AddNote("1", noteText: "waiting on schema");
            _ = firstManager.ClaimTask("1", "agent-a");

            (await firstWriter.FlushAsync()).Should().BeTrue();
        }

        var persisted = await ConversationTodoProjection.LoadAsync(store, "conv-1");
        persisted.Should().NotBeNull();
        persisted!.Tasks.Should().HaveCount(2);

        // --- Recreate (eviction / provider swap / restart): hydrate exactly as Program.cs does. ---
        var rehydratedBoard = await ConversationTodoProjection.LoadAsync(store, "conv-1");
        var secondManager = rehydratedBoard is { IsEmpty: false }
            ? TaskManager.FromSnapshot(rehydratedBoard)
            : new TaskManager();

        await using (var secondWriter = WireWriter(store, secondManager))
        {
            _ = secondManager.AddTask("First task after the recreate"); // the would-be truncating write

            (await secondWriter.FlushAsync()).Should().BeTrue();
        }

        var final = await ConversationTodoProjection.LoadAsync(store, "conv-1");
        final.Should().NotBeNull();
        final!.Tasks.Should().HaveCount(3, "the recreate's first write must extend the board, not replace it");
        final.Tasks.Select(t => t.Title).Should().Contain(["Wire the SSE endpoint", "Vitest coverage"]);
        var survivor = final.Tasks.Single(t => t.Id == "1");
        survivor.Status.Should().Be(AchieveAi.LmDotnetTools.LmCore.Models.TodoTaskStatus.InProgress);
        survivor.Notes.Should().ContainSingle().Which.Should().Be("waiting on schema");
        survivor.SubTasks.Should().ContainSingle().Which.Title.Should().Be("Add the map");
        final.Tasks.Single(t => t.Title == "First task after the recreate").Id.Should().Be("3");
    }
}
