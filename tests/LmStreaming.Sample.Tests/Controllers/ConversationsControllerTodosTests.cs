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

    /// <summary>
    ///     Creates the conversation's metadata row, stamped with a tenant, the way
    ///     <c>MultiTurnAgentPool.PersistThreadBindingsIfNeededAsync</c> already does for every pooled
    ///     agent. The projection deliberately never creates a row, so any test that expects a board to be
    ///     PERSISTED has to start from a conversation that exists — as production always does.
    /// </summary>
    private static Task SeedConversationAsync(IConversationStore store)
    {
        return store.UpdateMetadataAsync(
            ThreadId,
            existing =>
                existing
                ?? new ThreadMetadata
                {
                    ThreadId = ThreadId,
                    LastUpdated = 0,
                    TenantId = "tenant-1",
                }
        );
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
        await SeedConversationAsync(store);
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
        await SeedConversationAsync(store);
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
        await SeedConversationAsync(store);
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
        await SeedConversationAsync(store);
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
    public async Task GetTodos_CreatesNoMetadataRow_WhenTheConversationHasNone()
    {
        // A GET is a read. Persisting the live board must never be the thing that brings a conversation
        // into existence: the row would have no TenantId, and ConversationAuthorizer reads a null
        // TenantId as conversation_not_found — nobody could ever read it back, owner included. The
        // caller still gets its board; only the write is declined.
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith("live only")));
        Pool(pool);

        var result = await ControllerFor(store, pool).GetTodos(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<TodoBoardSnapshot>(ok.Value).Tasks.Should().ContainSingle().Which.Title.Should().Be("live only");
        (await store.LoadMetadataAsync(ThreadId)).Should().BeNull("a read must not conjure a conversation");
    }

    [Fact]
    public async Task GetTodos_StillReturnsTheLiveBoard_WhenPersistingItFails()
    {
        // The write-through is a cache warm, not the response. A store that is down, full, or racing a
        // delete must cost the panel nothing — the board the caller asked for is already in hand.
        var store = new Mock<IConversationStore>();
        store
            .Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 0 });
        store
            .Setup(s =>
                s.UpdateMetadataAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<ThreadMetadata?, ThreadMetadata>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new IOException("the metadata store is unavailable"));
        await using var pool = CreatePoolWithBoard(new StubBoard(BoardWith("unpersistable")));
        Pool(pool);

        var result = await ControllerFor(store.Object, pool).GetTodos(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert
            .IsType<TodoBoardSnapshot>(ok.Value)
            .Tasks.Should()
            .ContainSingle()
            .Which.Title.Should()
            .Be("unpersistable");
    }

    [Fact]
    public async Task GetTodos_SerializesToTheWireShapeTheClientAndThePushFrameShare()
    {
        // Pins the contract two other in-flight PRs are being written against: the PR 2 push frame
        // carries this same payload, and the PR 3 panel parses it. A rename here is invisible to every
        // test that inspects the CLR object — and breaks both of them on merge. camelCase comes from
        // ASP.NET's JsonSerializerDefaults.Web (nothing in Program.cs overrides it); the enum renders by
        // NAME because JsonStringEnumConverter sits on the enum type itself, so the frame inherits it.
        var store = new InMemoryConversationStore();
        var board = BoardWith("Wire the SSE endpoint") with
        {
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
            ],
        };
        await using var pool = CreatePoolWithBoard(new StubBoard(board));
        Pool(pool);

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(store, pool).GetTodos(ThreadId));
        var json = JsonSerializer.Serialize(
            Assert.IsType<TodoBoardSnapshot>(ok.Value),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("threadId").GetString().Should().Be(ThreadId);
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.TryGetProperty("capturedAtUtc", out _).Should().BeTrue();
        root.TryGetProperty("isEmpty", out _).Should().BeFalse("IsEmpty is a server-side convenience");

        var task = root.GetProperty("tasks")[0];
        task.GetProperty("id").GetString().Should().Be("1");
        task.GetProperty("title").GetString().Should().Be("Wire the SSE endpoint");
        task.GetProperty("status").GetString().Should().Be("InProgress");
        task.GetProperty("notes")[0].GetString().Should().Be("waiting on schema");
        task.GetProperty("subTasks")[0].GetProperty("status").GetString().Should().Be("Completed");
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
