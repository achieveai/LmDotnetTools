using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
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

    [Fact]
    public async Task List_ExcludesSubAgentThreads_FromTheConversationSidebar()
    {
        // Sub-agent conversations use the reserved "subagent-{agentId}" thread id and are surfaced
        // only through the sub-agent panel; they must never leak into the primary conversation list.
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-normal",
            new ThreadMetadata { ThreadId = "thread-normal", LastUpdated = 2, Properties = ImmutableDictionary<string, object>.Empty });
        await store.SaveMetadataAsync(
            "subagent-abc123",
            new ThreadMetadata { ThreadId = "subagent-abc123", LastUpdated = 1, Properties = ImmutableDictionary<string, object>.Empty });

        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.List() as OkObjectResult;
        result.Should().NotBeNull();
        var summaries = (result!.Value as IEnumerable<ConversationSummary>)!.ToList();

        summaries.Select(s => s.ThreadId).Should().Contain("thread-normal");
        summaries.Select(s => s.ThreadId).Should().NotContain(id => id.StartsWith("subagent-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetUsage_ReturnsPersistedAggregate_IncludingTotals()
    {
        var store = new InMemoryConversationStore();
        var ledger = new UsageLedger("usage-thread");
        ledger.UpsertAttempt(new UsageRecord
        {
            LogicalCallId = "a1",
            ProviderAttemptId = "a1",
            RootConversationId = "usage-thread",
            RequestedModel = "model-A",
            InputTokens = 100,
            OutputTokens = 40,
        });
        await ConversationUsageProjection.SaveAsync(store, ledger.Snapshot());

        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetUsage("usage-thread");

        var ok = Assert.IsType<OkObjectResult>(result);
        var aggregate = Assert.IsType<ConversationUsageAggregate>(ok.Value);
        aggregate.TotalTokens.Should().Be(140);
        aggregate.PerModel.Should().ContainSingle(m => m.ModelId == "model-A");
    }

    [Fact]
    public async Task GetUsage_ReturnsNotFound_WhenNoUsageRecorded()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.GetUsage("no-usage-thread");

        Assert.IsType<NotFoundResult>(result);
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
        metadata.Properties.Should().NotContainKey(
            SystemPromptAugmenter.AppendixPropertyKey,
            "an interactive provision carries no caller instructions and must not seed an empty appendix");
    }

    [Theory]
    [InlineData("Review the PR and dispatch the code-reviewer:* sub-agents.")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Provision_PersistsTheSystemPromptAppendix_OnlyWhenTheCallerSuppliedOne(string? appendix)
    {
        // The appendix is persisted on the THREAD rather than held in memory so it survives a process
        // restart and any later mode/provider recreation — the hosted agent is rebuilt from metadata on
        // every entry, and a headless review that lost its methodology mid-run would silently degrade to a
        // generic workspace agent.
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
                SystemPromptAppendix = appendix,
            },
            CancellationToken.None);

        var threadId = Assert.IsType<ProvisionConversationResponse>(
            Assert.IsType<OkObjectResult>(result).Value).ThreadId;
        var metadata = await store.LoadMetadataAsync(threadId, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(appendix))
        {
            metadata!.Properties.Should().NotContainKey(SystemPromptAugmenter.AppendixPropertyKey);
        }
        else
        {
            metadata!.Properties![SystemPromptAugmenter.AppendixPropertyKey].Should().Be(appendix);
        }
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

    /// <summary>
    /// Fail CLOSED: a caller asking for a guarantee the thread's agent cannot make must be refused, not
    /// quietly served an unsuppressed turn. This is also what keeps the wire contract version-safe in the
    /// other direction — the caller can tell "refused" from "accepted".
    /// </summary>
    [Fact]
    public async Task SendMessage_ReturnsBadRequest_WhenSuppressionRequestedButAgentCannotEnforceIt()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool();
        var threadId = "thread-send-suppress-unsupported";
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", SuppressSubAgentSpawning = true },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        JsonSerializer.Serialize(badRequest.Value).Should().Contain("spawn_suppression_unsupported");
    }

    /// <summary>
    /// Declaring <c>ISpawnSuppressingAgent</c> only proves the agent can CARRY the flag. An implementation
    /// that satisfies the signature and cannot keep the promise must be refused BEFORE the enqueue: once the
    /// message is in the run's channel, a receipt saying "not suppressed" is too late — the caller's turn
    /// runs unsuppressed regardless of what the response says.
    /// </summary>
    [Fact]
    public async Task SendMessage_RejectsBeforeQueueing_WhenTheAgentDeclaresSuppressionButCannotEnforceIt()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-suppress-incapable";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.EnforcesSpawnSuppression = false;
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", SuppressSubAgentSpawning = true },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        JsonSerializer.Serialize(badRequest.Value).Should().Contain("spawn_suppression_unsupported");
        agent.SendCount.Should().Be(0, "a refused request must leave nothing queued to run unsuppressed");
        agent.LastInput.Should().BeNull("the input never reached the agent at all");
    }

    /// <summary>
    /// A capable agent gets the flag on the actual input (not just a friendly echo), and the response
    /// acknowledges the guarantee so the caller can verify it was made.
    /// </summary>
    [Fact]
    public async Task SendMessage_ForwardsSuppression_AndAcknowledgesIt_WhenAgentCanEnforceIt()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-suppress-ok";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", SuppressSubAgentSpawning = true },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var response = Assert.IsType<SendMessageResponse>(accepted.Value);
        response.SpawningSuppressed.Should().BeTrue("the host must acknowledge the guarantee it made");
        agent.LastInput.Should().NotBeNull();
        agent.LastInput!.SuppressSubAgentSpawning.Should().BeTrue(
            "the flag has to reach the run, not just the response");
        agent.LastInput.InputId.Should().Be(response.InputId);
    }

    /// <summary>
    /// Task 5 (fix round 3) — the acknowledgement must come from ENFORCEMENT, never from the request. An
    /// agent that declares the capability but whose receipt does not confirm it (an implementation that
    /// accepts the flag and ignores it) must not be able to make the host promise a guarantee: echoing the
    /// request would hand the caller a suppression it never got, and the caller has no other way to tell.
    /// </summary>
    [Fact]
    public async Task SendMessage_DoesNotClaimSuppression_WhenTheAgentsReceiptDoesNotConfirmIt()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-suppress-unenforced";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.ConfirmsSuppressionOnReceipt = false;
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", SuppressSubAgentSpawning = true },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.IsType<SendMessageResponse>(accepted.Value).SpawningSuppressed.Should().BeFalse(
            "the host relays the agent's enforcement, not the caller's request");
        agent.LastInput!.SuppressSubAgentSpawning.Should().BeTrue(
            "the request still reached the agent — what changed is only what the host claims about it");
    }

    /// <summary>An ordinary send is unaffected: no suppression asked for, none claimed.</summary>
    [Fact]
    public async Task SendMessage_DoesNotClaimSuppression_WhenNotRequested()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-suppress-off";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "review" },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.IsType<SendMessageResponse>(accepted.Value).SpawningSuppressed.Should().BeFalse();
        agent.LastInput!.SuppressSubAgentSpawning.Should().BeFalse();
    }

    /// <summary>
    /// The point of the whole contract: a caller that repeats a send because it never saw the first response
    /// gets the input the host ALREADY took. Without this, the daemon's synthesis retry would put a second
    /// minutes-long, sub-agent-fanning turn onto the same conversation and double-post the review.
    /// </summary>
    [Fact]
    public async Task SendMessage_WithARepeatedIdempotencyKey_EnqueuesOnce_AndReturnsTheSameInputId()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-repeat";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        var request = new SendMessageRequest { Text = "synthesize", IdempotencyKey = "review-run-7:2" };

        var first = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);
        var second = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);

        first.Queued.Should().BeTrue();
        first.IdempotencyKeyHonored.Should().BeTrue("the caller may only retry once the host says it can");
        second.InputId.Should().Be(first.InputId, "the retry must recover the first input, not mint a new one");
        second.Queued.Should().BeFalse("nothing new was accepted — the caller is being handed the old input");
        second.IdempotencyKeyHonored.Should().BeTrue();
        agent.SendCount.Should().Be(1, "exactly one turn may reach the agent");
    }

    /// <summary>
    /// The retry that matters most crosses a PROCESS boundary — the daemon died between the host accepting
    /// and the artifact being written locally. Reconciliation therefore has to work off durable state alone,
    /// including after the input was drained out of the accepted set into a run.
    /// </summary>
    [Fact]
    public async Task SendMessage_WithARepeatedIdempotencyKey_ReconcilesAgainstAnInputAlreadyDrainedIntoARun()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-drained";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);
        // What a previous process left behind: the input is no longer "accepted, pending" — it is running.
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId,
            "run-earlier",
            RunStatus.InProgress,
            ["idem:0:review-run-7:2"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", IdempotencyKey = "review-run-7:2" },
            CancellationToken.None);

        var response = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(result).Value);
        response.InputId.Should().Be("idem:0:review-run-7:2");
        response.Queued.Should().BeFalse();
        agent.SendCount.Should().Be(0, "the turn this key names is already running — re-sending would duplicate it");
    }

    /// <summary>
    /// A key identifies one turn INCLUDING what it does. A repeat that asks for something different (here:
    /// suppression flipped on) is a different operation, so reconciling it to the earlier input would quietly
    /// serve the caller a turn without the guarantee it just asked for.
    /// </summary>
    [Fact]
    public async Task SendMessage_TreatsAFlippedSuppressionRequestAsADifferentOperation()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-options";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var plain = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(
            await controller.SendMessage(
                threadId,
                new SendMessageRequest { Text = "review", IdempotencyKey = "review-run-7:2" },
                default)).Value);
        var suppressed = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(
            await controller.SendMessage(
                threadId,
                new SendMessageRequest
                {
                    Text = "synthesize",
                    IdempotencyKey = "review-run-7:2",
                    SuppressSubAgentSpawning = true,
                },
                default)).Value);

        suppressed.InputId.Should().NotBe(plain.InputId);
        suppressed.Queued.Should().BeTrue();
        suppressed.SpawningSuppressed.Should().BeTrue("the second turn really did run suppressed");
        agent.SendCount.Should().Be(2, "two different operations, two turns");
    }

    /// <summary>
    /// Order matters: reconciliation must not become a side door around the capability gate. A repeat asking
    /// for a guarantee this thread's agent cannot make is still refused, even though an input under that
    /// derived id already exists.
    /// </summary>
    [Fact]
    public async Task SendMessage_StillRefusesAnUnenforceableSuppression_EvenWhenTheKeyWasAlreadyAccepted()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-gate-order";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.EnforcesSpawnSuppression = false;
        await SeedDefaultModeThreadAsync(store, threadId);
        await store.RecordAcceptedInputAsync(
            threadId, "idem:1:review-run-7:2", DateTimeOffset.UtcNow);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest
            {
                Text = "synthesize",
                IdempotencyKey = "review-run-7:2",
                SuppressSubAgentSpawning = true,
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        JsonSerializer.Serialize(badRequest.Value).Should().Contain("spawn_suppression_unsupported");
    }

    /// <summary>
    /// A key the host cannot turn into a durable, unambiguous id is refused rather than read as "absent".
    /// Blank is the shape of a caller whose key derivation produced nothing; a control character survives
    /// JSON but not the ids, logs and store round-trips the reservation lives in. Absent means "no protection
    /// asked for" — but a caller that SENT a key believes its retry is safe, so it has to be told it is not.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("review-run-7\u00002")]
    [InlineData("review-run-7\n2")]
    public async Task SendMessage_ReturnsBadRequest_WhenTheIdempotencyKeyCannotBecomeADurableId(string key) =>
        await AssertKeyRefusedBeforeEnqueueAsync(key);

    /// <summary>
    /// The cap bounds what a caller can push into every accepted-input record and run row the derived id
    /// appears in, and is enforced the same way: before anything is queued.
    /// </summary>
    [Fact]
    public async Task SendMessage_ReturnsBadRequest_WhenTheIdempotencyKeyExceedsTheLengthCap() =>
        await AssertKeyRefusedBeforeEnqueueAsync(new string('k', 201));

    private static async Task AssertKeyRefusedBeforeEnqueueAsync(string key)
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-invalid";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", IdempotencyKey = key },
            CancellationToken.None);

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value)
            .Should().Contain("invalid_idempotency_key");
        agent.SendCount.Should().Be(0, "the refusal has to land before anything is queued");
    }

    /// <summary>
    /// The other half of the version handshake: a send WITHOUT a key claims nothing. This is what a caller
    /// talking to a host that predates the field also sees, which is what lets it fail closed rather than
    /// retry into a duplicate review.
    /// </summary>
    [Fact]
    public async Task SendMessage_ClaimsNoIdempotency_WhenNoKeyWasSupplied()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-absent";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        var request = new SendMessageRequest { Text = "review" };

        var first = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);
        var second = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);

        first.IdempotencyKeyHonored.Should().BeFalse();
        second.InputId.Should().NotBe(first.InputId, "without a key each send is its own turn");
        agent.SendCount.Should().Be(2);
    }

    /// <summary>
    /// Two keys that differ only in a marker one of them CONTAINS must not derive the same id. A derivation
    /// that appended the suppression marker made exactly that collision reachable from the wire: a caller
    /// whose key legitimately ends in the marker would dedupe against an unrelated suppressed turn, which is
    /// the failure the key exists to prevent, only harder to see.
    /// </summary>
    [Fact]
    public async Task SendMessage_DerivesDistinctInputIds_ForKeysThatDifferOnlyByTheSuppressionMarker()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-collision";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var suppressed = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(
            await controller.SendMessage(
                threadId,
                new SendMessageRequest
                {
                    Text = "synthesize",
                    IdempotencyKey = "review-run-7:2",
                    SuppressSubAgentSpawning = true,
                },
                default)).Value);
        var lookalike = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(
            await controller.SendMessage(
                threadId,
                new SendMessageRequest { Text = "review", IdempotencyKey = "review-run-7:2:spawn-suppressed" },
                default)).Value);

        lookalike.InputId.Should().NotBe(suppressed.InputId);
        lookalike.Queued.Should().BeTrue("an unrelated key must not reconcile to someone else's turn");
        agent.SendCount.Should().Be(2, "two different operations, two turns");
    }

    /// <summary>
    /// Ids the host mints for keyless sends live in their own namespace, so no caller key can name one. A
    /// shared namespace would let a caller that echoed back an observed inputId as its key reconcile onto a
    /// turn it never sent — and never get the turn it asked for.
    /// </summary>
    [Fact]
    public async Task SendMessage_DoesNotLetACallerKeyNameAServerMintedInput()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-namespace";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        var minted = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(
            await controller.SendMessage(threadId, new SendMessageRequest { Text = "review" }, default)).Value);
        // That first turn is now running, which is the state a repeat would reconcile against.
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId,
            "run-minted",
            RunStatus.InProgress,
            [minted.InputId],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var echoed = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(
            await controller.SendMessage(
                threadId,
                new SendMessageRequest { Text = "synthesize", IdempotencyKey = minted.InputId },
                default)).Value);

        echoed.InputId.Should().NotBe(minted.InputId);
        echoed.Queued.Should().BeTrue("the caller asked for a new turn, not for the host's earlier one");
        agent.SendCount.Should().Be(2);
    }

    /// <summary>
    /// The honored claim is a promise about DURABLE state, so a host whose store cannot record a reservation
    /// has to refuse before it queues anything. Queueing first and then reporting "not honored" would already
    /// have created the duplicate the key exists to prevent.
    /// </summary>
    [Fact]
    public async Task SendMessage_RefusesAKey_WhenTheStoreCannotDurablyReserveTheInput()
    {
        var threadId = "thread-send-idem-no-ledger";
        var store = new Mock<IConversationStore>();
        store
            .Setup(s => s.LoadMetadataAsync(threadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultModeMetadata(threadId));
        await using var pool = CreateSuppressionCapablePool();
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);

        var controller = CreateController(store.Object, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "synthesize", IdempotencyKey = "review-run-7:2" },
            CancellationToken.None);

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value)
            .Should().Contain("idempotency_unsupported");
        agent.SendCount.Should().Be(0, "nothing may be queued under a promise the host cannot keep");
    }

    /// <summary>
    /// The race the reservation exists for: the daemon's retry can overlap the send it is retrying (the first
    /// response was lost, not the request). Both sends are held inside the accepted-input lookup until each
    /// has been told the input is not there, so a check-then-write acceptance would let BOTH enqueue — two
    /// minutes-long, sub-agent-fanning turns on one conversation, and a double-posted review.
    /// </summary>
    [Fact]
    public async Task SendMessage_WithTwoConcurrentSendsOfOneKey_EnqueuesExactlyOnce()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-concurrent";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        // One rendezvous shared by both controllers: neither may leave the lookup until both have arrived.
        var rendezvous = new RendezvousLedgerStore(store, participants: 2);
        ConversationsController Controller() => CreateController(
            store,
            pool,
            ModeStoreResolvingSystemModes(),
            statusResolver: new ConversationStatusResolver(store, rendezvous));
        var request = new SendMessageRequest { Text = "synthesize", IdempotencyKey = "review-run-7:2" };

        var first = Controller().SendMessage(threadId, request, default);
        var second = Controller().SendMessage(threadId, request, default);
        var responses = (await Task.WhenAll(first, second))
            .Select(r => Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(r).Value))
            .ToList();

        responses.Select(r => r.InputId).Distinct().Should().ContainSingle(
            "both callers named the same turn, so both must be told the same input id");
        responses.Count(r => r.Queued).Should().Be(1, "exactly one of the two may become queued work");
        responses.Should().OnlyContain(
            r => r.IdempotencyKeyHonored, "both sends were covered by the same durable reservation");
        agent.SendCount.Should().Be(1, "the loser must not put a second turn onto the conversation");
    }

    /// <summary>
    /// A reservation whose send never became queued work has to go back. Left behind, it wedges that key
    /// permanently: every later retry reconciles to a turn that does not exist and the caller waits for a
    /// status that will never arrive. Covers both ways a send can fail after the claim — a full queue (503)
    /// and a throwing send (surfaced as a 500).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendMessage_ReleasesTheReservation_WhenTheSendNeverBecomesQueuedWork(bool sendThrows)
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-compensate";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.ThrowOnTrySend = sendThrows;
        agent.RejectAsQueueFull = !sendThrows;
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        var request = new SendMessageRequest { Text = "synthesize", IdempotencyKey = "review-run-7:2" };

        var failing = async () => await controller.SendMessage(threadId, request, default);
        if (sendThrows)
        {
            await failing.Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            Assert.IsType<ObjectResult>(await failing()).StatusCode.Should().Be(503);
        }

        agent.ThrowOnTrySend = false;
        agent.RejectAsQueueFull = false;

        var retry = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);

        retry.Queued.Should().BeTrue("the failed attempt queued nothing, so the retry still has to");
        agent.SendCount.Should().Be(1);
    }

    /// <summary>
    /// The suppression outcome a repeat is told comes from the stored record, not from the repeat's own
    /// request — otherwise the host would be confirming a guarantee by reading back the very claim it is
    /// meant to verify.
    /// </summary>
    [Fact]
    public async Task SendMessage_ReportsTheRecordedSuppressionOutcome_OnAReconciledRepeat()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-suppress-repeat";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        var request = new SendMessageRequest
        {
            Text = "synthesize",
            IdempotencyKey = "review-run-7:2",
            SuppressSubAgentSpawning = true,
        };

        _ = Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default));
        var repeat = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);

        repeat.Queued.Should().BeFalse();
        repeat.SpawningSuppressed.Should().BeTrue("the recovered turn really is the suppressed one");
        repeat.IdempotencyKeyHonored.Should().BeTrue();
        agent.SendCount.Should().Be(1);
    }

    /// <summary>
    /// When the receipt refuses the guarantee the record would have claimed, the host keeps the two honest
    /// together: it drops the reservation, tells the caller its key was NOT honored, and lets the retry queue
    /// a real turn — instead of leaving behind a record that would later promise a suppression that never held.
    /// </summary>
    [Fact]
    public async Task SendMessage_DoesNotHonorAKey_WhenTheReceiptContradictsTheRecordedSuppression()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateSuppressionCapablePool();
        var threadId = "thread-send-idem-suppress-unconfirmed";
        var currentMode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (SpawnSuppressingFakeAgent)pool.GetOrCreateAgent(threadId, currentMode);
        agent.ConfirmsSuppressionOnReceipt = false;
        await SeedDefaultModeThreadAsync(store, threadId);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        var request = new SendMessageRequest
        {
            Text = "synthesize",
            IdempotencyKey = "review-run-7:2",
            SuppressSubAgentSpawning = true,
        };

        var first = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);
        var repeat = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(threadId, request, default)).Value);

        first.IdempotencyKeyHonored.Should().BeFalse("no record survives that could dedupe a repeat");
        repeat.Queued.Should().BeTrue("the caller was told plainly that its first send was not protected");
        agent.SendCount.Should().Be(2);
    }

    /// <summary>
    /// The preflight a caller checks before its first send. It reports the same durable fact the send path
    /// reserves against, so the two can never disagree: a host whose store cannot reserve must not advertise
    /// message idempotency, or a caller would fail closed only after its turn was already queued.
    /// </summary>
    [Fact]
    public async Task GetCapabilities_ReportsMessageIdempotency_OnlyWhenTheStoreCanReserve()
    {
        await using var pool = CreateSuppressionCapablePool();

        var reserving = Assert.IsType<ConversationCapabilitiesResponse>(Assert.IsType<OkObjectResult>(
            CreateController(new InMemoryConversationStore(), pool, ModeStoreResolvingSystemModes())
                .GetCapabilities()).Value);
        var nonReserving = Assert.IsType<ConversationCapabilitiesResponse>(Assert.IsType<OkObjectResult>(
            CreateController(Mock.Of<IConversationStore>(), pool, ModeStoreResolvingSystemModes())
                .GetCapabilities()).Value);

        reserving.MessageIdempotency.Should().BeTrue();
        reserving.SpawnSuppression.Should().BeTrue();
        nonReserving.MessageIdempotency.Should().BeFalse(
            "advertising a guarantee this store cannot record is what makes a caller fail late");
    }

    private static MultiTurnAgentPool CreateSuppressionCapablePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(new SpawnSuppressingFakeAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static Task SeedDefaultModeThreadAsync(InMemoryConversationStore store, string threadId) =>
        store.SaveMetadataAsync(threadId, DefaultModeMetadata(threadId));

    /// <summary>Metadata for a thread pinned to the default mode — what <c>SendMessage</c> needs to resolve
    /// an agent, whether it is seeded into a real store or stubbed on a mock one.</summary>
    private static ThreadMetadata DefaultModeMetadata(string threadId) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>.Empty
                .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId),
        };

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
