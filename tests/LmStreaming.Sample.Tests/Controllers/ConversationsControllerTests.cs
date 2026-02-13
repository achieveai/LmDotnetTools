using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

public class ConversationsControllerTests
{
    [Fact]
    public async Task SwitchMode_ReturnsConflict_WhenRunIsInProgress()
    {
        await using var pool = CreatePool();
        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("math-helper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("math-helper"));

        var threadId = "thread-conflict";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.CurrentRunId = "run-active";

        var controller = new ConversationsController(
            Mock.Of<IConversationStore>(),
            pool,
            modeStore.Object,
            NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchMode(
            threadId,
            new SwitchModeRequest { ModeId = "math-helper" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);

        var payload = JsonSerializer.Serialize(conflict.Value);
        payload.Should().Contain("mode_switch_while_streaming");
        payload.Should().Contain(threadId);

        pool.GetAgentMode(threadId)!.Id.Should().Be(SystemChatModes.DefaultModeId);
    }

    [Fact]
    public async Task SwitchMode_ReturnsOk_WhenRunIsIdle()
    {
        await using var pool = CreatePool();
        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("math-helper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("math-helper"));

        var threadId = "thread-idle";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = (FakeMultiTurnAgent)pool.GetOrCreateAgent(threadId, currentMode);

        var controller = new ConversationsController(
            Mock.Of<IConversationStore>(),
            pool,
            modeStore.Object,
            NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchMode(
            threadId,
            new SwitchModeRequest { ModeId = "math-helper" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        payload.Should().Contain("\"modeId\":\"math-helper\"");
        pool.GetAgentMode(threadId)!.Id.Should().Be("math-helper");
    }

    [Fact]
    public async Task SwitchMode_ReturnsNotFound_WhenModeDoesNotExist()
    {
        await using var pool = CreatePool();
        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("missing-mode", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatMode?)null);

        var controller = new ConversationsController(
            Mock.Of<IConversationStore>(),
            pool,
            modeStore.Object,
            NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchMode(
            "thread-404",
            new SwitchModeRequest { ModeId = "missing-mode" },
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        payload.Should().Contain("missing-mode");
    }

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new FakeMultiTurnAgent(threadId),
            NullLogger<MultiTurnAgentPool>.Instance);
    }
}
