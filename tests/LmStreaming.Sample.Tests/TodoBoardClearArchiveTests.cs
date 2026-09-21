using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Bug 19 end to end, wired the way Program.cs wires it: a <c>bulk-initialize</c> clear hands the
///     doomed board to <see cref="TaskManager.OnCleared" />, the host persists it through
///     <see cref="ConversationTodoArchiveProjection" />, and the live board keeps going through
///     <see cref="TodoBoardPersistenceWriter" /> on the OTHER key. Lives here for the same reason
///     <see cref="TodoBoardRecreateSurvivalTests" /> does: it needs Misc's board and LmMultiTurn's
///     projections in one assembly, and only the sample references both.
/// </summary>
public class TodoBoardClearArchiveTests
{
    private const string ThreadId = "conv-1";

    private static Task SeedMetadataRowAsync(IConversationStore store) =>
        store.UpdateMetadataAsync(
            ThreadId,
            existing => existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 0 }
        );

    /// <summary>
    ///     The host's two board hooks, wired exactly as Program.cs does — the archive on
    ///     <c>OnCleared</c>, the live board's coalescing writer on <c>OnChanged</c>. The archive append
    ///     is awaited here rather than fired and forgotten, so the assertion is about the wiring and not
    ///     about a race.
    /// </summary>
    private static TodoBoardPersistenceWriter WireHost(IConversationStore store, TaskManager board)
    {
        var writer = new TodoBoardPersistenceWriter(store, ThreadId, () => board.GetTodoBoardSnapshot(ThreadId));
        board.ThreadId = ThreadId;
        board.OnChanged += writer.Schedule;
        board.OnCleared += cleared =>
            ConversationTodoArchiveProjection
                .AppendAsync(store, cleared with { ThreadId = ThreadId })
                .GetAwaiter()
                .GetResult();
        return writer;
    }

    /// <summary>
    ///     Builds up a real board through the tools. Called AFTER the host hooks are wired, because the
    ///     live board only becomes durable through <c>OnChanged</c> — a board built before the wiring
    ///     would never have been persisted in the first place, and the assertions about what survived a
    ///     clear would be reading an absence they created themselves.
    /// </summary>
    private static void AddWork(TaskManager board)
    {
        _ = board.AddTask("Wire the SSE endpoint"); // 1
        _ = board.AddTask("Add the map", "1"); // 1.1
        _ = board.AddNote("1", noteText: "waiting on schema");
        _ = board.ClaimTask("1.1", "agent-a");
        _ = board.UpdateTask("1.1", "completed", agent: "agent-a");
    }

    [Fact]
    public async Task RootClear_ArchivesTheWipedBoard_WithoutDisturbingTheLiveBoardKey()
    {
        // The recovery path bug 19 lacked: after the wipe the rows are still somewhere a human can get
        // them back from. Mutation that must go red: dropping the OnCleared wiring in Program.cs, or
        // archiving under the live board's own key where the next write overwrites it.
        var store = new InMemoryConversationStore();
        await SeedMetadataRowAsync(store);

        var board = new TaskManager();
        await using (var writer = WireHost(store, board))
        {
            AddWork(board);
            _ = board.BulkInitialize([new TaskManager.BulkTaskItem { Task = "Start over" }], clearExisting: true);
            (await writer.FlushAsync()).Should().BeTrue();
        }

        var archive = await ConversationTodoArchiveProjection.LoadAsync(store, ThreadId);
        var wiped = archive!.Entries.Should().ContainSingle().Subject;
        wiped.Tasks.Should().ContainSingle().Which.Title.Should().Be("Wire the SSE endpoint");
        wiped.Tasks[0].Notes.Should().ContainSingle();
        wiped.Tasks[0].SubTasks.Should().ContainSingle().Which.Title.Should().Be("Add the map");

        // The live board moved on independently, on its own key.
        var live = await ConversationTodoProjection.LoadAsync(store, ThreadId);
        live!.Tasks.Should().ContainSingle().Which.Title.Should().Be("Start over");
    }

    [Fact]
    public async Task SubAgentClear_IsRefused_SoNothingIsArchivedAndNothingIsLost()
    {
        // The incident itself: agent-7 asked for a fresh start on the shared board. Nothing to archive
        // because nothing was destroyed.
        var store = new InMemoryConversationStore();
        await SeedMetadataRowAsync(store);

        var board = new TaskManager();
        await using (var writer = WireHost(store, board))
        {
            AddWork(board);
            using (AgentActorScope.Begin("agent-7", "researcher"))
            {
                var refusal = board.BulkInitialize(
                    [new TaskManager.BulkTaskItem { Task = "Start over" }],
                    clearExisting: true
                );

                refusal.ErrorCode.Should().Be("board_clear_not_permitted");
            }

            (await writer.FlushAsync()).Should().BeTrue();
        }

        (await ConversationTodoArchiveProjection.LoadAsync(store, ThreadId)).Should().BeNull();

        var live = await ConversationTodoProjection.LoadAsync(store, ThreadId);
        live!.Tasks.Should().ContainSingle().Which.Title.Should().Be("Wire the SSE endpoint");
    }
}
