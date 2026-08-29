using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
///     <c>GET /api/conversations/{threadId}/todos</c> — the board read path (#583, PR 1).
/// </summary>
public class ConversationsControllerTodosTests
{
    private const string ThreadId = "todo-thread";

    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A board whose contents the test controls directly, independent of the task tool.</summary>
    private sealed class StubBoard(TodoBoardSnapshot snapshot) : ITodoBoardSource
    {
        public int CaptureCount { get; private set; }

        public TodoBoardSnapshot GetTodoBoardSnapshot(string threadId)
        {
            CaptureCount++;
            return snapshot with { ThreadId = threadId };
        }
    }

    private static TodoBoardSnapshot BoardWith(params string[] titles)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = ThreadId,
            CapturedAtUtc = Noon,
            Tasks =
            [
                .. titles.Select(
                    (title, index) =>
                        new TodoTaskNode
                        {
                            Id = (index + 1).ToString(),
                            Status = TodoTaskStatus.NotStarted,
                            Title = title,
                        }
                ),
            ],
        };
    }

    private static MultiTurnAgentPool CreatePoolWithBoard(ITodoBoardSource? board)
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)) { TodoBoard = board },
            NullLogger<MultiTurnAgentPool>.Instance
        );
    }

    private static ConversationsController ControllerFor(IConversationStore store, MultiTurnAgentPool pool)
    {
        return ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes()
        );
    }

    /// <summary>Registers a live agent for <see cref="ThreadId" />, so the pool lookup finds its board.</summary>
    private static void Pool(MultiTurnAgentPool pool)
    {
        _ = pool.GetOrCreateAgent(ThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);
    }

    [Fact]
    public async Task GetTodos_ReturnsTheLiveBoard_WhenTheAgentIsPooled()
    {
        var store = new InMemoryConversationStore();
        var board = new StubBoard(BoardWith("Wire the SSE endpoint", "Vitest coverage"));
        await using var pool = CreatePoolWithBoard(board);
        Pool(pool);

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var snapshot = Assert.IsType<TodoBoardSnapshot>(ok.Value);
        snapshot.ThreadId.Should().Be(ThreadId);
        snapshot.Tasks.Select(t => t.Title).Should().Equal("Wire the SSE endpoint", "Vitest coverage");
        board.CaptureCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTodos_PersistsTheLiveBoard_SoItSurvivesTheAgentBeingEvicted()
    {
        // The read is the only writer in PR 1 (the change-driven save arrives with the push frame in
        // PR 2). Without the write-through, the projection branch below could never be reached in
        // production and the board would vanish on restart.
        var store = new InMemoryConversationStore();
        await using (var pool = CreatePoolWithBoard(new StubBoard(BoardWith("survives"))))
        {
            Pool(pool);
            _ = await ControllerFor(store, pool).GetTodos(ThreadId);
        }

        var persisted = await ConversationTodoProjection.LoadAsync(store, ThreadId);
        persisted.Should().NotBeNull();
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("survives");
    }

    [Fact]
    public async Task GetTodos_ReturnsTheProjection_WhenNoAgentIsPooled()
    {
        var store = new InMemoryConversationStore();
        await ConversationTodoProjection.SaveAsync(store, BoardWith("from the projection"));
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith("never read")));
        // Deliberately NOT pooled: the reload / post-restart case.

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var snapshot = Assert.IsType<TodoBoardSnapshot>(ok.Value);
        snapshot.Tasks.Should().ContainSingle().Which.Title.Should().Be("from the projection");
    }

    [Fact]
    public async Task GetTodos_PrefersTheProjection_OverAPooledButUntouchedBoard()
    {
        // After a restart (or a mode/provider swap) the conversation gets a FRESH task tool with an
        // empty board while the persisted one still holds real rows. Returning the live board because
        // it merely exists would blank the panel — the exact "stale UI is worse than no UI" failure
        // inverted. An empty live board carries no information; a persisted board does.
        var store = new InMemoryConversationStore();
        await ConversationTodoProjection.SaveAsync(store, BoardWith("written before the restart"));
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith()));
        Pool(pool);

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var snapshot = Assert.IsType<TodoBoardSnapshot>(ok.Value);
        snapshot.Tasks.Should().ContainSingle().Which.Title.Should().Be("written before the restart");
    }

    [Fact]
    public async Task GetTodos_DoesNotWipeTheProjection_WithAnEmptyLiveBoard()
    {
        // The companion to the test above: preferring the projection would be worthless if the same
        // request wrote the empty board over it on the way out.
        var store = new InMemoryConversationStore();
        await ConversationTodoProjection.SaveAsync(store, BoardWith("must survive the read"));
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith()));
        Pool(pool);

        _ = await ControllerFor(store, pool).GetTodos(ThreadId);

        var persisted = await ConversationTodoProjection.LoadAsync(store, ThreadId);
        persisted!.Tasks.Should().ContainSingle().Which.Title.Should().Be("must survive the read");
    }

    [Fact]
    public async Task GetTodos_ReturnsNotFound_WhenNothingIsRecordedAnywhere()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith()));
        Pool(pool);

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetTodos_ReturnsNotFound_ForAnAgentThatShipsNoTaskTooling()
    {
        // The CLI-backed providers (codex/claude/copilot) return before the task registration and bring
        // their own tracking. A 404 is what tells the client to render NOTHING rather than an empty
        // board, so absence stays distinguishable from "the board is empty".
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithBoard(board: null);
        Pool(pool);

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetTodos_ReturnsNotFound_ForAnUnknownThread()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith("elsewhere")));

        var result = await ControllerFor(store, pool).GetTodos("never-heard-of-it");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetTodos_ProjectsTheRealTaskTool_IncludingNestingNotesAndStatus()
    {
        // The other cases stub the board. This one drives the ACTUAL TaskManager through its tools, so
        // the endpoint is proven against the shape the agent really produces, not a hand-built mirror.
        var taskManager = new TaskManager();
        _ = taskManager.AddTask("Renderer registry");
        _ = taskManager.AddTask("Add the map", parentId: "1");
        _ = taskManager.AddNote("1", noteText: "waiting on schema");
        _ = taskManager.UpdateTask("1", "in progress");
        _ = taskManager.UpdateTask("1.1", "completed");

        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithBoard(taskManager);
        Pool(pool);

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var snapshot = Assert.IsType<TodoBoardSnapshot>(ok.Value);
        var root = snapshot.Tasks.Should().ContainSingle().Which;
        root.Id.Should().Be("1");
        root.Title.Should().Be("Renderer registry");
        root.Status.Should().Be(TodoTaskStatus.InProgress);
        root.Notes.Should().ContainSingle().Which.Should().Be("waiting on schema");
        var child = root.SubTasks.Should().ContainSingle().Which;
        child.Id.Should().Be("1.1");
        child.Status.Should().Be(TodoTaskStatus.Completed);
    }
}
