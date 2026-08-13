using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
            TimeProvider.System,
            new WorkflowRunRegistry(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance));
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
    public async Task SwitchMode_ReturnsConflict_WhenAskUserQuestionIsPending()
    {
        // #4 (mode-switch hard-block guard): FakeMultiTurnAgent always degrades HasPendingAskUserQuestionAsync
        // to false, so this guard can only be proven true with a REAL MultiTurnAgentLoop parked on a
        // deferred AskUserQuestion — not the run-in-progress conflict above, which is a different guard.
        const string threadId = "thread-mode-pending-question";
        const string toolCallId = "tc_mode_switch";
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId);

        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("math-helper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("math-helper"));

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, modeStore.Object);

        var result = await controller.SwitchMode(
            threadId,
            new SwitchModeRequest { ModeId = "math-helper" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);
        var payload = JsonSerializer.Serialize(conflict.Value);
        payload.Should().Contain("mode_switch_blocked_by_pending_ask_user_question");
        payload.Should().Contain(threadId);

        // No recreate happened — the agent (and its mode) is still pooled and the deferred call is
        // still pending, untouched by the blocked switch attempt.
        pool.GetAgentMode(threadId)!.Id.Should().Be(SystemChatModes.DefaultModeId);
        (await pool.HasPendingAskUserQuestionAsync(threadId)).Should().BeTrue();
    }

    [Fact]
    public async Task SwitchMode_ReturnsConflict_WhenDescendantAskUserQuestionIsPending()
    {
        // #246: the guard must also fire when the pending question belongs to a live DIRECT CHILD,
        // not the primary itself — recreating the primary on a mode switch disposes the whole
        // descendant tree, which would otherwise silently orphan the child's unanswered question.
        const string threadId = "thread-mode-descendant-pending-question";
        const string toolCallId = "tc_child_mode_switch";
        await using var pool = await CreatePoolWithParkedChildAskUserQuestionAsync(threadId, toolCallId);

        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync("math-helper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById("math-helper"));

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, modeStore.Object);

        var result = await controller.SwitchMode(
            threadId,
            new SwitchModeRequest { ModeId = "math-helper" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);
        var payload2 = JsonSerializer.Serialize(conflict.Value);
        payload2.Should().Contain("mode_switch_blocked_by_pending_ask_user_question");
        payload2.Should().Contain(threadId);

        // No recreate happened — the primary (and its mode) is still pooled, so the child it owns
        // was never disposed out from under its still-pending question.
        pool.GetAgentMode(threadId)!.Id.Should().Be(SystemChatModes.DefaultModeId);
    }

    /// <summary>
    /// Issue #246 test-review finding: unlike <c>SwitchMode</c>/<c>SwitchProvider</c> (which hard-block
    /// on a pending <see cref="AskUserQuestionToolProvider"/> question via
    /// <c>HasPendingAskUserQuestionAsync</c>), <see cref="ConversationsController.Delete"/> has NO such
    /// guard — it unconditionally calls <c>MultiTurnAgentPool.RemoveAgentAsync</c>, which disposes the
    /// agent regardless of any deferred tool call awaiting an answer. This is intentional "delete-as-cancel"
    /// semantics: deleting a conversation implicitly abandons any pending question rather than blocking
    /// the delete or synthesizing a resolution on the client's behalf. This test locks in that existing
    /// behavior as a deliberate contract (not an oversight): a pending question must not prevent Delete
    /// from succeeding, and once deleted the agent is gone from the pool entirely — so a
    /// <c>client_tool_result</c> that later arrives for that <c>toolCallId</c> hits the disposed-agent
    /// <c>not_found</c> path added to <c>ChatWebSocketManager</c>, not a resolvable agent.
    /// </summary>
    [Fact]
    public async Task Delete_WithPendingAskUserQuestion_RemovesAgentUnconditionally_PreservingDeleteAsCancelSemantics()
    {
        const string threadId = "thread-delete-pending-question";
        const string toolCallId = "tc_delete_pending";
        var store = new InMemoryConversationStore();
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId);

        // Precondition: the question really is pending before Delete runs.
        (await pool.HasPendingAskUserQuestionAsync(threadId)).Should().BeTrue();

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.Delete(threadId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        // Delete neither blocked on nor attempted to resolve the pending question — the agent (and its
        // deferred call) is simply gone.
        pool.TryGet(threadId, out _).Should().BeFalse();
        pool.GetAgentMode(threadId).Should().BeNull();
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
        await store.SaveMetadataAsync(
            "workflow-wf1-thread-normal",
            new ThreadMetadata { ThreadId = "workflow-wf1-thread-normal", LastUpdated = 1, Properties = ImmutableDictionary<string, object>.Empty });

        await using var pool = CreatePool();
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.List() as OkObjectResult;
        result.Should().NotBeNull();
        var summaries = (result!.Value as IEnumerable<ConversationSummary>)!.ToList();

        summaries.Select(s => s.ThreadId).Should().Contain("thread-normal");
        summaries.Select(s => s.ThreadId).Should().NotContain(id =>
            id.StartsWith("subagent-", StringComparison.Ordinal)
            || id.StartsWith("workflow-", StringComparison.Ordinal));
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

    /// <summary>
    /// Builds a pool-registered REAL <see cref="MultiTurnAgentLoop"/> (not <see cref="FakeMultiTurnAgent"/>,
    /// which always degrades <c>HasPendingAskUserQuestionAsync</c> to false) whose mock LLM emits a
    /// single <c>AskUserQuestion</c> tool call, parking a deferred placeholder and ending the run.
    /// Patterned on <c>ChatWebSocketManagerClientToolResultTests.CreatePoolWithParkedAskUserQuestionAsync</c>.
    /// </summary>
    private static async Task<MultiTurnAgentPool> CreatePoolWithParkedAskUserQuestionAsync(
        string threadId, string toolCallId)
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(new
            {
                context = "Need to know which color to use.",
                questions = new[]
                {
                    new { prompt = "Which color?", options = new object[] { new { label = "Red" }, new { label = "Blue" } } },
                },
            }),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(ToAsyncEnumerable(toolCall)));

        var pool = new MultiTurnAgentPool(
            (tid, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                new MultiTurnAgentLoop(
                    mockAgent.Object,
                    new FunctionRegistry(),
                    tid,
                    logger: NullLogger<MultiTurnAgentLoop>.Instance)),
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent(threadId, mode);

        var userInput = new UserInput([new TextMessage { Text = "Which color should I use?", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(userInput))
        {
            // drain until the run parks on the deferred AskUserQuestion
        }

        return pool;
    }

    /// <summary>
    /// Builds a pool-registered REAL <see cref="MultiTurnAgentLoop"/> primary whose own mock LLM never
    /// emits anything (no pending question of its own) but which spawns ONE background sub-agent
    /// child whose mock LLM emits a single <c>AskUserQuestion</c> tool call, parking the CHILD on a
    /// deferred placeholder. Proves the #246 guard walks into the live descendant tree rather than
    /// only checking the primary's own deferred calls.
    /// </summary>
    private static async Task<MultiTurnAgentPool> CreatePoolWithParkedChildAskUserQuestionAsync(
        string threadId, string toolCallId)
    {
        const string templateName = "asker";

        var childAskCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(new
            {
                context = "Need to know which color to use.",
                questions = new[]
                {
                    new { prompt = "Which color?", options = new object[] { new { label = "Red" }, new { label = "Blue" } } },
                },
            }),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

        var childAgent = new Mock<IStreamingAgent>();
        childAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(ToAsyncEnumerable(childAskCall)));

        var parentAgent = new Mock<IStreamingAgent>();
        parentAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(ToAsyncEnumerable()));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [templateName] = new SubAgentTemplate
                {
                    Name = templateName,
                    SystemPrompt = "You ask the user a clarifying question.",
                    AgentFactory = () => childAgent.Object,
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        var pool = new MultiTurnAgentPool(
            (tid, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                new MultiTurnAgentLoop(
                    parentAgent.Object,
                    new FunctionRegistry(),
                    tid,
                    subAgentOptions: subAgentOptions,
                    logger: NullLogger<MultiTurnAgentLoop>.Instance)),
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent(threadId, mode);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
            templateName, "ask the user", name: "asker", runInBackground: true);
        using var doc = JsonDocument.Parse(spawnJson);
        var agentId = doc.RootElement.GetProperty("agent_id").GetString()!;

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilChildAwaitingQuestionAsync(loop.SubAgentManager!, agentId, ct.Token);

        return pool;
    }

    /// <summary>
    /// Waits for a spawned sub-agent to park on its own <c>AskUserQuestion</c> call. Since the
    /// <c>SubAgentManager</c> fix that keeps a parked AskUserQuestion non-terminal, <c>state.Completion</c>
    /// (what <see cref="SubAgentManager.ObserveCompletionAsync"/> awaits) is deliberately never resolved
    /// while parked — only the answer-triggered run performs the one true final completion. So tests that
    /// need the child parked (not finished) must instead poll the child loop's own deferred-call registry
    /// directly, mirroring the production-side <c>HasPendingAskUserQuestionAsync</c> check, rather than
    /// waiting on a completion that will never come.
    /// </summary>
    private static async Task WaitUntilChildAwaitingQuestionAsync(
        SubAgentManager subAgentManager, string agentId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (subAgentManager.TryGetAgent(agentId, out var childAgent)
                && childAgent is MultiTurnAgentLoop childLoop)
            {
                var deferred = await childLoop.GetDeferredToolCallsAsync(ct);
                if (deferred.Count > 0)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(params IMessage[] messages)
    {
        foreach (var msg in messages)
        {
            yield return msg;
            await Task.Yield();
        }
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
    public async Task SwitchProvider_ReturnsConflict_WhenAskUserQuestionIsPending()
    {
        // #4 (provider-switch hard-block guard), mirroring the SwitchMode test above: needs a REAL
        // MultiTurnAgentLoop parked on a deferred AskUserQuestion since FakeMultiTurnAgent always
        // degrades HasPendingAskUserQuestionAsync to false.
        const string threadId = "thread-prov-pending-question";
        const string toolCallId = "tc_provider_switch";
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId);

        var controller = CreateController(Mock.Of<IConversationStore>(), pool, Mock.Of<IChatModeStore>());

        var result = await controller.SwitchProvider(
            threadId,
            new SwitchProviderRequest { ProviderId = "openai" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);
        var payload = JsonSerializer.Serialize(conflict.Value);
        payload.Should().Contain("provider_switch_blocked_by_pending_ask_user_question");
        payload.Should().Contain(threadId);

        // No recreate happened — the agent (and its mode) is still pooled and the deferred call is
        // still pending, untouched by the blocked switch attempt.
        pool.GetAgentMode(threadId)!.Id.Should().Be(SystemChatModes.DefaultModeId);
        (await pool.HasPendingAskUserQuestionAsync(threadId)).Should().BeTrue();
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
    public async Task SendMessage_DispatchesToRefreshedAgent_WhenSandboxSessionWasReplaced()
    {
        // A workspace plugin-selection migration replaces the sandbox session underneath a pooled
        // agent. GetOrCreateAgent hands back whatever is pooled without examining session liveness,
        // so the REST/S2S turn must be dispatched off EnsureCurrentAgentAsync — exactly as the
        // WebSocket setup path does — or it lands in a destroyed session.
        //
        // This cannot be built on CreatePool(): that helper wires no liveSessionResolver, so the
        // refresh always short-circuits to Current and the test would pass against either
        // implementation. The pool below supplies the two things the pool's liveness check needs —
        // a staged binding and a resolver — so a replacement genuinely occurs.
        var store = new InMemoryConversationStore();
        var threadId = "thread-send-refresh";
        var credential = new SandboxCredential("owner", "key");
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                // The first entry is bound to a session the registry no longer serves; every later
                // entry is built against the live one. The refresh therefore fires exactly once, and
                // the replacement it produces is itself current.
                var sessionId = created.Count == 0 ? "sess-stale" : "sess-live";
                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add(agent);
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        credential,
                        credential,
                        sessionId),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            liveSessionResolver: (_, _) => Task.FromResult(
                new SandboxSession("workspace-1", "sess-live", "workspace", "/workspace")));

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

        Assert.IsType<AcceptedResult>(result);

        // Identity, not just a status code: a 202 alone is returned on both sides of this behaviour.
        // Two creations prove the stale entry was rebuilt, and SendCount proves the turn was handed
        // to the REBUILT agent rather than the stale one GetOrCreateAgent returned.
        created.Should().HaveCount(2, "the stale entry must be rebuilt before the turn is dispatched");
        created[0].SendCount.Should().Be(0, "the turn must not reach the agent bound to the replaced session");
        created[1].SendCount.Should().Be(1, "the refreshed agent is the one that must receive the turn");
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
