using System.Collections.Immutable;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Pins the <see cref="ConversationSummary.Workspace"/> mapping in the conversation-list action:
/// it surfaces the persisted <c>workspace</c> thread property
/// (<see cref="MultiTurnAgentPool.WorkspacePropertyKey"/>), and stays null for legacy threads that
/// were never locked to a workspace.
/// </summary>
public class ConversationsControllerWorkspaceTests
{
    [Fact]
    public async Task List_MapsWorkspaceProperty_ToSummaryWorkspace()
    {
        var thread = ThreadWithProperties(
            "thread-ws",
            ImmutableDictionary<string, object>.Empty.SetItem(MultiTurnAgentPool.WorkspacePropertyKey, "ws-7"));

        await using var pool = CreatePool();
        var controller = CreateController(pool, thread);

        var summaries = await ListSummariesAsync(controller);

        var summary = summaries.Single(s => s.ThreadId == "thread-ws");
        summary.Workspace.Should().Be("ws-7");
    }

    [Fact]
    public async Task List_LegacyThreadWithoutWorkspaceProperty_YieldsNullWorkspace()
    {
        var thread = ThreadWithProperties(
            "thread-legacy",
            ImmutableDictionary<string, object>.Empty.SetItem("title", "Legacy chat"));

        await using var pool = CreatePool();
        var controller = CreateController(pool, thread);

        var summaries = await ListSummariesAsync(controller);

        var summary = summaries.Single(s => s.ThreadId == "thread-legacy");
        summary.Workspace.Should().BeNull();
    }

    private static async Task<IReadOnlyList<ConversationSummary>> ListSummariesAsync(ConversationsController controller)
    {
        var result = await controller.List(ct: CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        return [.. Assert.IsAssignableFrom<IEnumerable<ConversationSummary>>(ok.Value!)];
    }

    private static ThreadMetadata ThreadWithProperties(string threadId, ImmutableDictionary<string, object> properties) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = properties,
        };

    private static ConversationsController CreateController(MultiTurnAgentPool pool, params ThreadMetadata[] threads)
    {
        var store = new Mock<IConversationStore>();
        store
            .Setup(s => s.ListThreadsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(threads);

        return new ConversationsController(
            store.Object,
            pool,
            Mock.Of<IChatModeStore>(),
            NullLogger<ConversationsController>.Instance);
    }

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new TestDoubles.FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
    }
}
