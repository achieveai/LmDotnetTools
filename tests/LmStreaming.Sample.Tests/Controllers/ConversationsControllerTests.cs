using System.Collections.Immutable;
using LmStreaming.Sample.Tests.Agents;
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
    public async Task SwitchMode_ReturnsOk_WhenRunStateIsStale()
    {
        await using var pool = CreatePool();
        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("math-helper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("math-helper"));

        var threadId = "thread-stale";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.CurrentRunId = "run-stale";
        agent.IsRunning = false;

        var controller = new ConversationsController(
            Mock.Of<IConversationStore>(),
            pool,
            modeStore.Object,
            NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchMode(
            threadId,
            new SwitchModeRequest { ModeId = "math-helper" },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
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
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static MultiTurnAgentPool CreatePoolWithRegistry(
        FakeProviderRegistry registry,
        InMemoryConversationStore store)
    {
        return new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    [Fact]
    public async Task SwitchProvider_ReturnsConflict_WhenRunIsInProgress()
    {
        await using var pool = CreatePool();
        var threadId = "thread-prov-conflict";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.CurrentRunId = "run-active";

        var controller = new ConversationsController(
            Mock.Of<IConversationStore>(),
            pool,
            Mock.Of<IChatModeStore>(),
            NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchProvider(
            threadId,
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);
        var payload = JsonSerializer.Serialize(conflict.Value);
        payload.Should().Contain("provider_switch_while_streaming");
        payload.Should().Contain(threadId);

        // No recreate happened — the agent (and its mode) is still pooled.
        pool.GetAgentMode(threadId)!.Id.Should().Be(SystemChatModes.DefaultModeId);
    }

    [Fact]
    public async Task SwitchProvider_ReturnsOk_AndPersistsProvider_WhenRunIsIdle()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithRegistry(registry, store);

        var threadId = "thread-prov-idle";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = (FakeMultiTurnAgent)pool.GetOrCreateAgent(
            threadId, currentMode, requestedProviderId: "test", requestResponseDumpFileName: null);

        var controller = new ConversationsController(
            store, pool, Mock.Of<IChatModeStore>(), NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchProvider(
            threadId,
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        JsonSerializer.Serialize(ok.Value).Should().Contain("\"providerId\":\"openai\"");
        pool.GetEffectiveProviderId(threadId, null).Should().Be("openai"); // persisted overwrite
        pool.GetAgentMode(threadId)!.Id.Should().Be(SystemChatModes.DefaultModeId); // mode preserved
    }

    [Fact]
    public async Task SwitchProvider_Returns503_WhenProviderUnavailable()
    {
        // "openai" is NOT in the registry's available set → RecreateAgentWithProviderAsync throws
        // ProviderUnavailableException → the controller maps it to a clean 503.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithRegistry(registry, store);

        var threadId = "thread-prov-503";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = (FakeMultiTurnAgent)pool.GetOrCreateAgent(
            threadId, currentMode, requestedProviderId: "test", requestResponseDumpFileName: null);

        var controller = new ConversationsController(
            store, pool, Mock.Of<IChatModeStore>(), NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchProvider(
            threadId,
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        obj.StatusCode.Should().Be(503);
        var payload = JsonSerializer.Serialize(obj.Value);
        payload.Should().Contain("provider_unavailable");
        payload.Should().Contain("openai");
        pool.GetEffectiveProviderId(threadId, null).Should().Be("test"); // untouched
    }

    [Fact]
    public async Task SwitchProvider_ReturnsOk_WhenRunStateIsStale()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithRegistry(registry, store);

        var threadId = "thread-prov-stale";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(
            threadId, currentMode, requestedProviderId: "test", requestResponseDumpFileName: null);
        agent.CurrentRunId = "run-stale";
        agent.IsRunning = false;

        var controller = new ConversationsController(
            store, pool, Mock.Of<IChatModeStore>(), NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchProvider(
            threadId,
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        pool.GetEffectiveProviderId(threadId, null).Should().Be("openai");
    }

    [Fact]
    public async Task SwitchProvider_ReturnsOk_ViaPersistedModeFallback_WhenAgentNotPooled()
    {
        // The real-world switch-after-refresh path: the agent was evicted from the pool
        // (GetAgentMode == null), but the thread's mode was persisted. The controller must recover the
        // mode from metadata + the mode store and preserve it across the provider swap. This exercises
        // the fallback chain (metadata → mode store) that the live-agent tests never reach.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-prov-refresh",
            new ThreadMetadata
            {
                ThreadId = "thread-prov-refresh",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, "math-helper")
                    .SetItem(MultiTurnAgentPool.ProviderPropertyKey, "test"),
            });
        await using var pool = CreatePoolWithRegistry(registry, store);

        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("math-helper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("math-helper"));

        // No live agent for this thread → forces the persisted-mode fallback chain in the controller.
        var controller = new ConversationsController(
            store, pool, modeStore.Object, NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchProvider(
            "thread-prov-refresh",
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        JsonSerializer.Serialize(ok.Value).Should().Contain("\"providerId\":\"openai\"");
        // Provider switched AND the recovered mode was preserved on the recreated agent.
        pool.GetEffectiveProviderId("thread-prov-refresh", null).Should().Be("openai");
        pool.GetAgentMode("thread-prov-refresh")!.Id.Should().Be("math-helper");
    }

    [Fact]
    public async Task SwitchProvider_Returns500_WhenNoModeCanBeResolved()
    {
        // Agent evicted from the pool (GetAgentMode == null) AND the mode store resolves nothing —
        // neither a persisted mode nor the system default. The controller cannot preserve a mode across
        // the swap, so it answers a clean 500 rather than recreating the agent with an unknown mode.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithRegistry(registry, store);

        var modeStore = new Mock<IChatModeStore>();
        // GetById on an unknown id returns null (typed ChatMode?), so every resolution attempt fails.
        modeStore.Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("__no_such_mode__"));

        // No agent is created for this thread and no metadata is persisted → GetAgentMode is null and
        // the fallback chain resolves nothing.
        var controller = new ConversationsController(
            store, pool, modeStore.Object, NullLogger<ConversationsController>.Instance);

        var result = await controller.SwitchProvider(
            "thread-prov-nomode",
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        obj.StatusCode.Should().Be(500);
        JsonSerializer.Serialize(obj.Value)
            .Should().Contain("Could not resolve the conversation");
        // The failed switch left the thread's persisted provider untouched.
        pool.GetEffectiveProviderId("thread-prov-nomode", null).Should().Be("test");
    }
}
