using System.Collections.Immutable;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

public class ConversationsControllerTests
{
    /// <summary>
    /// Builds a controller. <c>SwitchMode</c>/<c>SwitchProvider</c> tests don't touch
    /// workspace/provider-registry/status-resolver, so stand-ins suffice there; tests exercising
    /// <c>Provision</c>/<c>SendMessage</c>/<c>GetStatus</c> pass the real pieces they need. When
    /// <paramref name="store"/> also implements <see cref="IRunLedgerStore"/> (e.g. a real
    /// <see cref="InMemoryConversationStore"/>), the default status resolver is wired to it so a
    /// test can seed ledger/accepted-input state through the same <paramref name="store"/> instance
    /// it hands the controller.
    /// </summary>
    private static ConversationsController CreateController(
        IConversationStore store,
        MultiTurnAgentPool pool,
        IChatModeStore modeStore,
        IWorkspaceStore? workspaceStore = null,
        ProviderRegistry? providerRegistry = null,
        ConversationStatusResolver? statusResolver = null)
    {
        return new ConversationsController(
            store,
            pool,
            modeStore,
            workspaceStore ?? Mock.Of<IWorkspaceStore>(),
            providerRegistry ?? new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            statusResolver ?? new ConversationStatusResolver(store, store as IRunLedgerStore ?? new InMemoryConversationStore()),
            NullLogger<ConversationsController>.Instance);
    }

    /// <summary>Resolves any real system mode id (default mode, math-helper, etc.) — for tests that
    /// need mode resolution to just work without stubbing one specific mode id.</summary>
    private static IChatModeStore ModeStoreResolvingSystemModes()
    {
        var modeStore = new Mock<IChatModeStore>();
        modeStore
            .Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modeId, CancellationToken _) => SystemChatModes.GetById(modeId));
        return modeStore.Object;
    }

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

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, modeStore.Object);

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

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, modeStore.Object);

        var result = await controller.SwitchMode(
            threadId,
            new SwitchModeRequest { ModeId = "math-helper" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        payload.Should().Contain("\"modeId\":\"math-helper\"");
        // No Wait is armed on the FakeMultiTurnAgent (HasArmedWaitAsync degrades to false for a
        // non-loop agent), so a clean switch must carry no warning.
        Assert.IsType<SwitchModeResponse>(ok.Value).Warning.Should().BeNull();
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

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, modeStore.Object);

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

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, modeStore.Object);

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

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, Mock.Of<IChatModeStore>());

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

        var controller = CreateController(store, pool, Mock.Of<IChatModeStore>());

        var result = await controller.SwitchProvider(
            threadId,
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        JsonSerializer.Serialize(ok.Value).Should().Contain("\"providerId\":\"openai\"");
        // No armed Wait on the FakeMultiTurnAgent → the successful switch reports no warning.
        Assert.IsType<SwitchProviderResponse>(ok.Value).Warning.Should().BeNull();
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

        var controller = CreateController(store, pool, Mock.Of<IChatModeStore>());

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

        var controller = CreateController(store, pool, Mock.Of<IChatModeStore>());

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
        var controller = CreateController(store, pool, modeStore.Object);

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
        var controller = CreateController(store, pool, modeStore.Object);

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

    private static Workspace TestWorkspace(string id) =>
        new() { Id = id, Name = id, DirectoryRelPath = id };

    [Fact]
    public async Task Provision_ReturnsOk_AndPersistsMetadata()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace("ws-1"));
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal();

        var controller = CreateController(
            store,
            pool,
            ModeStoreResolvingSystemModes(),
            workspaceStore: workspaceStore.Object,
            providerRegistry: registry);

        var result = await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "ws-1",
                ProviderId = "test",
                ModeId = SystemChatModes.DefaultModeId,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ProvisionConversationResponse>(ok.Value);
        response.ThreadId.Should().StartWith("thread-");

        var metadata = await store.LoadMetadataAsync(response.ThreadId, CancellationToken.None);
        metadata.Should().NotBeNull();
        metadata!.Properties![MultiTurnAgentPool.ProviderPropertyKey].Should().Be("test");
        metadata.Properties[MultiTurnAgentPool.WorkspacePropertyKey].Should().Be("ws-1");
        metadata.Properties[MultiTurnAgentPool.ModePropertyKey].Should().Be(SystemChatModes.DefaultModeId);
    }

    [Fact]
    public async Task Provision_ReturnsNotFound_WhenWorkspaceMissing()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("missing-ws", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Workspace?)null);

        var controller = CreateController(
            store,
            pool,
            ModeStoreResolvingSystemModes(),
            workspaceStore: workspaceStore.Object);

        var result = await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "missing-ws",
                ProviderId = "test",
                ModeId = SystemChatModes.DefaultModeId,
            },
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("missing-ws");
    }

    [Fact]
    public async Task Provision_ReturnsNotFound_WhenModeMissing()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace("ws-1"));
        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("missing-mode", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatMode?)null);

        var controller = CreateController(
            store,
            pool,
            modeStore.Object,
            workspaceStore: workspaceStore.Object);

        var result = await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "ws-1",
                ProviderId = "test",
                ModeId = "missing-mode",
            },
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("missing-mode");
    }

    [Fact]
    public async Task Provision_Returns503_WhenProviderUnavailable()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace("ws-1"));
        // "openai" is not in the registry's available set → provider_unavailable, and no thread is minted.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal();

        var controller = CreateController(
            store,
            pool,
            ModeStoreResolvingSystemModes(),
            workspaceStore: workspaceStore.Object,
            providerRegistry: registry);

        var result = await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "ws-1",
                ProviderId = "openai",
                ModeId = SystemChatModes.DefaultModeId,
            },
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        obj.StatusCode.Should().Be(503);
        var payload = JsonSerializer.Serialize(obj.Value);
        payload.Should().Contain("provider_unavailable");
        payload.Should().Contain("openai");
        (await store.ListThreadsAsync(50, 0, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessage_ReturnsNotFound_WhenThreadUnprovisioned()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            "thread-send-missing",
            new SendMessageRequest { Text = "hello" },
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("unknown_thread");
    }

    [Fact]
    public async Task SendMessage_ReturnsAccepted_WithInputIdAndNoRunId()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-send-ok";
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId),
            });

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "hello" },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var response = Assert.IsType<SendMessageResponse>(accepted.Value);
        response.InputId.Should().NotBeNullOrEmpty();
        response.Queued.Should().BeTrue();

        // The DTO has no RunId member at all — belt-and-suspenders check on the wire shape too.
        JsonSerializer.Serialize(accepted.Value).Should().NotContain("runId");
    }

    [Fact]
    public async Task SendMessage_Returns503_WhenQueueFull()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-send-full";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.RejectAsQueueFull = true;

        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId),
            });

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "hello" },
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        obj.StatusCode.Should().Be(503);
        JsonSerializer.Serialize(obj.Value).Should().Contain("queue_full");
    }

    [Fact]
    public async Task SendMessage_Throws_WhenDurableWriteFails()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-send-fail";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.ThrowOnTrySend = true;

        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId),
            });

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        Func<Task> act = () => controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "hello" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendMessage_Returns503_WhenPersistedProviderUnavailable()
    {
        // Persisting an unavailable provider id is enough to trigger the 503 — GetOrCreateAgent
        // resolves the persisted provider before ever looking at the requested (null) one.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await using var pool = CreatePoolWithRegistry(registry, store);

        var threadId = "thread-send-prov-503";
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId)
                    .SetItem(MultiTurnAgentPool.ProviderPropertyKey, "openai"),
            });

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "hello" },
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        obj.StatusCode.Should().Be(503);
        var payload = JsonSerializer.Serialize(obj.Value);
        payload.Should().Contain("provider_unavailable");
        payload.Should().Contain("openai");
    }

    [Fact]
    public async Task GetStatus_ReturnsBadRequest_WhenNeitherIdProvided()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus("thread-x", runId: null, inputId: null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        JsonSerializer.Serialize(badRequest.Value).Should().Contain("Exactly one of");
    }

    [Fact]
    public async Task GetStatus_ReturnsBadRequest_WhenBothIdsProvided()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus("thread-x", runId: "run-1", inputId: "input-1", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        JsonSerializer.Serialize(badRequest.Value).Should().Contain("Exactly one of");
    }

    [Fact]
    public async Task GetStatus_ReturnsNotFound_WhenThreadUnprovisioned()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus("thread-status-missing", runId: "run-1", inputId: null, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("unknown_thread");
    }

    [Fact]
    public async Task GetStatus_ReturnsNotFound_WhenRunIdUnknown()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-status-runid-404";
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = 1, Properties = ImmutableDictionary<string, object>.Empty });

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus(threadId, runId: "run-unknown", inputId: null, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        payload.Should().Contain("unknown_runId");
        payload.Should().Contain("run-unknown");
    }

    [Fact]
    public async Task GetStatus_ReturnsNotFound_WhenInputIdUnknown()
    {
        // Distinct 404 from the unprovisioned-thread case: this thread IS provisioned, but the
        // inputId was never accepted nor folded into any run ledger entry.
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-status-inputid-404";
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = 1, Properties = ImmutableDictionary<string, object>.Empty });

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus(threadId, runId: null, inputId: "input-unknown", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        payload.Should().Contain("unknown_inputId");
        payload.Should().Contain("input-unknown");
    }

    [Fact]
    public async Task GetStatus_ReturnsOk_ResolvingByRunId()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-status-runid-ok";
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = 1, Properties = ImmutableDictionary<string, object>.Empty });

        var now = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(
            new RunLedgerEntry(threadId, "run-ok", RunStatus.InProgress, ["input-ok"], now, now));

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus(threadId, runId: "run-ok", inputId: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ConversationStatusResponse>(ok.Value);
        response.ThreadId.Should().Be(threadId);
        response.RunId.Should().Be("run-ok");
        response.Status.Should().Be(nameof(ConversationRunStatus.InProgress));
    }

    [Fact]
    public async Task GetStatus_ReturnsOk_NotStarted_ForAcceptedButUnledgeredInputId()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-status-inputid-notstarted";
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = 1, Properties = ImmutableDictionary<string, object>.Empty });

        await store.RecordAcceptedInputAsync(threadId, "input-queued", DateTimeOffset.UtcNow);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetStatus(threadId, runId: null, inputId: "input-queued", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ConversationStatusResponse>(ok.Value);
        response.ThreadId.Should().Be(threadId);
        response.RunId.Should().BeNull();
        response.Status.Should().Be(nameof(ConversationRunStatus.NotStarted));
    }
}
