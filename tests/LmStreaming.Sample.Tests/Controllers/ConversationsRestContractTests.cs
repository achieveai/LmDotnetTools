using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// End-to-end REST contract tests against a REAL <see cref="MultiTurnAgentBase"/>-derived agent
/// (not <see cref="TestDoubles.FakeMultiTurnAgent"/>) and a real <see cref="InMemoryConversationStore"/>,
/// covering provision → send → queued-send → poll-by-inputId, including a restart variant that
/// proves an orphaned accepted-input synthesizes an <see cref="ConversationRunStatus.Interrupted"/>
/// row when a fresh process/agent reconciles the ledger.
/// </summary>
public class ConversationsRestContractTests
{
    [Fact]
    public async Task ProvisionSendPoll_HappyPath_ResolvesToCompletedWithResponseText()
    {
        var store = new InMemoryConversationStore();
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        await using var pool = CreateRealAgentPool(registry, store);
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace("ws-1"));

        var controller = CreateController(
            store,
            pool,
            ModeStoreResolvingSystemModes(),
            workspaceStore: workspaceStore.Object,
            providerRegistry: registry.ToReal());

        var provisionResult = await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "ws-1",
                ProviderId = "test",
                ModeId = SystemChatModes.DefaultModeId,
            },
            CancellationToken.None);
        var threadId = Assert.IsType<ProvisionConversationResponse>(
            Assert.IsType<OkObjectResult>(provisionResult).Value).ThreadId;

        var sendResult = await controller.SendMessage(
            threadId,
            new SendMessageRequest { Text = "hello" },
            CancellationToken.None);
        var sendResponse = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(sendResult).Value);
        sendResponse.Queued.Should().BeTrue();

        ConversationStatusResponse? status = null;
        (await WaitUntilAsync(
            async () =>
            {
                var statusResult = await controller.GetStatus(
                    threadId, runId: null, inputId: sendResponse.InputId, CancellationToken.None);
                status = Assert.IsType<OkObjectResult>(statusResult).Value as ConversationStatusResponse;
                return status?.Status == nameof(ConversationRunStatus.Completed) && status.Response != null;
            },
            TimeSpan.FromSeconds(5))).Should().BeTrue();

        status!.RunId.Should().NotBeNullOrEmpty();
        JsonSerializer.Serialize(status.Response).Should().Contain("Echo: hello");
    }

    [Fact]
    public async Task SendMessage_WhileRunInProgress_QueuesAndResolvesIndependently()
    {
        var store = new InMemoryConversationStore();
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = CreateRealAgentPool(registry, store, beforeComplete: () => gate.Task);
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace("ws-1"));

        var controller = CreateController(
            store,
            pool,
            ModeStoreResolvingSystemModes(),
            workspaceStore: workspaceStore.Object,
            providerRegistry: registry.ToReal());

        var provisionResult = await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "ws-1",
                ProviderId = "test",
                ModeId = SystemChatModes.DefaultModeId,
            },
            CancellationToken.None);
        var threadId = Assert.IsType<ProvisionConversationResponse>(
            Assert.IsType<OkObjectResult>(provisionResult).Value).ThreadId;

        var send1 = await controller.SendMessage(
            threadId, new SendMessageRequest { Text = "first" }, CancellationToken.None);
        var inputId1 = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(send1).Value).InputId;

        // Wait until run #1 is actually in progress (StartRunAsync ran; the agent is now blocked
        // on the gate) before sending the second message, so both inputs can never land in the
        // same drained batch.
        (await WaitUntilAsync(
            async () =>
            {
                var statusResult = await controller.GetStatus(
                    threadId, runId: null, inputId: inputId1, CancellationToken.None);
                var value = Assert.IsType<OkObjectResult>(statusResult).Value as ConversationStatusResponse;
                return value?.Status == nameof(ConversationRunStatus.InProgress);
            },
            TimeSpan.FromSeconds(5))).Should().BeTrue();

        var send2 = await controller.SendMessage(
            threadId, new SendMessageRequest { Text = "second" }, CancellationToken.None);
        var inputId2 = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(send2).Value).InputId;

        // Input #2 was durably accepted but not yet drained into a run — the loop is still
        // blocked completing run #1.
        var status2Result = await controller.GetStatus(
            threadId, runId: null, inputId: inputId2, CancellationToken.None);
        var status2 = Assert.IsType<OkObjectResult>(status2Result).Value as ConversationStatusResponse;
        status2!.Status.Should().Be(nameof(ConversationRunStatus.NotStarted));
        status2.RunId.Should().BeNull();

        // Release the gate: run #1 completes, and the loop continues to drain input #2 into run #2.
        gate.SetResult();

        ConversationStatusResponse? finalStatus1 = null;
        (await WaitUntilAsync(
            async () =>
            {
                var statusResult = await controller.GetStatus(
                    threadId, runId: null, inputId: inputId1, CancellationToken.None);
                finalStatus1 = Assert.IsType<OkObjectResult>(statusResult).Value as ConversationStatusResponse;
                return finalStatus1?.Status == nameof(ConversationRunStatus.Completed) && finalStatus1.Response != null;
            },
            TimeSpan.FromSeconds(5))).Should().BeTrue();

        ConversationStatusResponse? finalStatus2 = null;
        (await WaitUntilAsync(
            async () =>
            {
                var statusResult = await controller.GetStatus(
                    threadId, runId: null, inputId: inputId2, CancellationToken.None);
                finalStatus2 = Assert.IsType<OkObjectResult>(statusResult).Value as ConversationStatusResponse;
                return finalStatus2?.Status == nameof(ConversationRunStatus.Completed) && finalStatus2.Response != null;
            },
            TimeSpan.FromSeconds(5))).Should().BeTrue();

        finalStatus1!.RunId.Should().NotBe(finalStatus2!.RunId);
        JsonSerializer.Serialize(finalStatus1.Response).Should().Contain("Echo: first");
        JsonSerializer.Serialize(finalStatus2.Response).Should().Contain("Echo: second");
    }

    [Fact]
    public async Task Restart_BetweenAcceptedInputAndDrain_ResolvesToInterrupted()
    {
        var store = new InMemoryConversationStore();
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var workspaceStore = new Mock<IWorkspaceStore>();
        workspaceStore.Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace("ws-1"));

        string threadId;
        string inputId;

        // Simulate the process that accepted the input but crashed before its agent's run loop
        // ever drained it: the agent never drains, so disposing this pool tears down a
        // permanently-blocked RunLoopAsync (StopAsync swallows the resulting OperationCanceledException).
        await using (var crashedPool = CreateRealAgentPool(registry, store, neverDrains: true))
        {
            var controller = CreateController(
                store,
                crashedPool,
                ModeStoreResolvingSystemModes(),
                workspaceStore: workspaceStore.Object,
                providerRegistry: registry.ToReal());

            var provisionResult = await controller.Provision(
                new ProvisionConversationRequest
                {
                    WorkspaceId = "ws-1",
                    ProviderId = "test",
                    ModeId = SystemChatModes.DefaultModeId,
                },
                CancellationToken.None);
            threadId = Assert.IsType<ProvisionConversationResponse>(
                Assert.IsType<OkObjectResult>(provisionResult).Value).ThreadId;

            var sendResult = await controller.SendMessage(
                threadId, new SendMessageRequest { Text = "hello" }, CancellationToken.None);
            inputId = Assert.IsType<SendMessageResponse>(Assert.IsType<AcceptedResult>(sendResult).Value).InputId;
        }

        // A fresh pool/agent for the same thread + durable store represents the restarted process.
        // RunAsync fully awaits ledger reconciliation before its loop task is even created, so the
        // orphaned accepted-input synthesizes an Interrupted row shortly after creation.
        await using var restartedPool = CreateRealAgentPool(registry, store);
        var restartedController = CreateController(
            store,
            restartedPool,
            ModeStoreResolvingSystemModes(),
            workspaceStore: workspaceStore.Object,
            providerRegistry: registry.ToReal());

        _ = restartedPool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        ConversationStatusResponse? status = null;
        (await WaitUntilAsync(
            async () =>
            {
                var statusResult = await restartedController.GetStatus(
                    threadId, runId: null, inputId: inputId, CancellationToken.None);
                status = Assert.IsType<OkObjectResult>(statusResult).Value as ConversationStatusResponse;
                return status?.Status == nameof(ConversationRunStatus.Interrupted);
            },
            TimeSpan.FromSeconds(5))).Should().BeTrue();

        status!.RunId.Should().NotBeNullOrEmpty();
    }

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

    private static IChatModeStore ModeStoreResolvingSystemModes()
    {
        var modeStore = new Mock<IChatModeStore>();
        modeStore.Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modeId, CancellationToken _) => SystemChatModes.GetById(modeId));
        return modeStore.Object;
    }

    private static Workspace TestWorkspace(string id) =>
        new() { Id = id, Name = id, DirectoryRelPath = id };

    private static MultiTurnAgentPool CreateRealAgentPool(
        FakeProviderRegistry registry,
        InMemoryConversationStore store,
        bool neverDrains = false,
        Func<Task>? beforeComplete = null)
    {
        return new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(
                new RestContractTestAgent(context.ThreadId, store, neverDrains, beforeComplete)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> that echoes the last user text back as its
    /// response. When <c>neverDrains</c> is true it blocks forever instead of draining its input
    /// channel, simulating a process that accepted an input but crashed before its loop ever
    /// processed it. When <c>beforeComplete</c> is supplied, it is awaited after
    /// <see cref="MultiTurnAgentBase.StartRunAsync"/> and before the run completes, letting a test
    /// hold a run open deterministically (instead of racing a fixed delay) to exercise a second
    /// message being queued while the first is still in progress.
    /// </summary>
    private sealed class RestContractTestAgent : MultiTurnAgentBase
    {
        private readonly bool _neverDrains;
        private readonly Func<Task>? _beforeComplete;

        public RestContractTestAgent(
            string threadId,
            IConversationStore store,
            bool neverDrains = false,
            Func<Task>? beforeComplete = null)
            : base(threadId, store: store, persistRunLedger: true)
        {
            _neverDrains = neverDrains;
            _beforeComplete = beforeComplete;
        }

        protected override TimeSpan FallbackGracePeriod => TimeSpan.FromMilliseconds(100);

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            if (_neverDrains)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break;
                }

                if (!TryDrainInputs(out var batch) || batch.Count == 0)
                {
                    continue;
                }

                var lastText = batch
                    .SelectMany(item => item.Input.Messages)
                    .OfType<TextMessage>()
                    .LastOrDefault()?.Text ?? string.Empty;

                var assignment = await StartRunAsync(batch, ct: ct);

                if (_beforeComplete != null)
                {
                    await _beforeComplete();
                }

                AddToHistory(new TextMessage { Text = $"Echo: {lastText}", Role = Role.Assistant });
                await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, 0, ct: ct);
            }
        }
    }
}
