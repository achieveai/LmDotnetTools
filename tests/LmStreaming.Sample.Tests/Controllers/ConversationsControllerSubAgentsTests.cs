using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests for the read-only <c>GET /api/conversations/{threadId}/subagents</c> endpoint (WI #194,
/// Task 3). The action is presentation-only: it projects <c>SubAgentManager.ListAgents()</c>
/// snapshots into <see cref="SubAgentSummary"/> DTOs and never touches sub-agent execution.
/// </summary>
public sealed class ConversationsControllerSubAgentsTests
{
    private static ConversationsController CreateController(MultiTurnAgentPool pool)
    {
        return CreateController(pool, new WorkflowRunRegistry());
    }

    private static ConversationsController CreateController(
        MultiTurnAgentPool pool,
        WorkflowRunRegistry workflowRunRegistry)
    {
        return CreateController(pool, workflowRunRegistry, Mock.Of<IConversationStore>());
    }

    private static ConversationsController CreateController(
        MultiTurnAgentPool pool,
        WorkflowRunRegistry workflowRunRegistry,
        IConversationStore store)
    {
        return new ConversationsController(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(Mock.Of<IConversationStore>(), new InMemoryConversationStore()),
            TimeProvider.System,
            workflowRunRegistry,
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance);
    }

    private static MultiTurnAgentPool CreateFakeAgentPool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent)
    {
        return new MultiTurnAgentPool(
            (_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    [Fact]
    public async Task ListSubAgents_Returns404_ForUnknownParentThread()
    {
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool);

        var result = await controller.ListSubAgents("does-not-exist");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        payload.Should().Contain("unknown_thread");
        payload.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task ListSubAgents_ReturnsEmptyArray_ForKnownIdleThreadWithNoSubAgents()
    {
        // A persisted conversation that is NOT live in the pool and never spawned a sub-agent or
        // workflow (e.g. a plain chat the user reopened). It must answer 200 with an empty list — not
        // 404 — so the client's 3s sub-agent poll doesn't spuriously log "Failed to list sub-agents".
        var threadId = "thread-known-idle";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 });

        await using var pool = CreateFakeAgentPool(); // no live loop for this thread
        var controller = CreateController(pool, new WorkflowRunRegistry(), store);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value);
        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSubAgents_ReturnsEmptyArray_WhenAgentHasNoSubAgentManager()
    {
        await using var pool = CreateFakeAgentPool();
        var threadId = "thread-no-subagents";
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(threadId, mode);

        var controller = CreateController(pool);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value);
        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSubAgents_ReturnsSnapshots_ForSpawnedChildren()
    {
        var threadId = "thread-with-subagents";

        var registry = new FunctionRegistry();
        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    // Blocking provider keeps each spawned child in the Running state deterministically.
                    AgentFactory = () => BlockingProvider(),
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        await using var loop = new MultiTurnAgentLoop(
            BlockingProvider(),
            registry,
            threadId: threadId,
            subAgentOptions: subAgentOptions);

        await using var pool = CreatePoolReturning(loop);
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(threadId, mode);

        var alphaJson = await loop.SubAgentManager!.SpawnAsync(
            "worker", "first task", name: "alpha", runInBackground: true);
        var betaJson = await loop.SubAgentManager!.SpawnAsync(
            "worker", "second task", name: "beta", runInBackground: true);

        var alphaId = ParseAgentId(alphaJson);
        var betaId = ParseAgentId(betaJson);

        var controller = CreateController(pool);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        summaries.Should().HaveCount(2);

        var alpha = summaries.Single(s => s.AgentId == alphaId);
        alpha.Name.Should().Be("alpha");
        alpha.Template.Should().Be("worker");
        alpha.Task.Should().Be("first task");
        alpha.Status.Should().Be("running");
        alpha.ThreadId.Should().Be($"subagent-{alphaId}");
        alpha.GetType().GetProperty("EffectiveModelId")
            .Should().NotBeNull("the presentation DTO must expose the child model actually selected at spawn");
        alpha.GetType().GetProperty("ModelSelectionSource")
            .Should().NotBeNull("the UI must distinguish effective routing from raw controller arguments");

        var beta = summaries.Single(s => s.AgentId == betaId);
        beta.Name.Should().Be("beta");
        beta.Template.Should().Be("worker");
        beta.Task.Should().Be("second task");
        beta.Status.Should().Be("running");
        beta.ThreadId.Should().Be($"subagent-{betaId}");
    }

    [Fact]
    public async Task ListSubAgents_ReturnsPersistedWorkflowTabs_ForNonLiveConversation_SurvivingRestart()
    {
        // A conversation whose live loop was evicted by a server restart, but whose workflow + delegate
        // tabs were written through to the durable index. The endpoint must surface them (200) instead of
        // 404 — that's what makes workflow tabs survive a restart.
        var indexDir = Path.Combine(Path.GetTempPath(), "wf-index-ctrl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new WorkflowRunRegistry(indexDir);
            var threadId = "thread-restarted";
            registry.PersistTabs(
                threadId,
                [
                    new SubAgentSummary
                    {
                        AgentId = "wf-1",
                        Kind = "workflow",
                        Name = "Review PR",
                        Template = "workflow",
                        Task = "Review PR",
                        Status = "completed",
                        ThreadId = "workflow-wf-1",
                    },
                    new SubAgentSummary
                    {
                        AgentId = "del-1",
                        Kind = "subagent",
                        Name = "read:task",
                        Template = "general-purpose",
                        Task = "read the file",
                        Status = "completed",
                        ThreadId = "subagent-del-1",
                    },
                ]);

            // Pool has NO live loop for this thread (restart evicted it) — TryGet returns false.
            await using var pool = CreateFakeAgentPool();
            var controller = CreateController(pool, registry);

            var result = await controller.ListSubAgents(threadId);

            var ok = Assert.IsType<OkObjectResult>(result);
            var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
            summaries.Select(s => s.AgentId).Should().BeEquivalentTo(["wf-1", "del-1"]);
            summaries.Single(s => s.AgentId == "del-1").ThreadId.Should().Be("subagent-del-1");
        }
        finally
        {
            try
            {
                Directory.Delete(indexDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task ListSubAgents_ReconstructsOrdinaryChildren_FromPersistedProvenance_AfterPoolEviction()
    {
        // Collaboration is OFF (the checked-in default), so an Agent-tool child never enters the
        // WorkflowRunRegistry tab index (the write-through in AgentHierarchyService.BuildAsync only
        // persists there once collaboration is enabled). Its only durable trace is the
        // SubAgentProvenance stamp on its OWN persisted thread metadata — what
        // Program.ApplyDefaultSubAgentStore/NonOwningConversationStore write in production. This is the
        // pre-#244 flat-listing contract (ConversationsController.ListSubAgents used to reconstruct it
        // via a direct bounded scan); #244's replacement dropped it when it started delegating fully to
        // AgentHierarchyService.BuildAsync.
        var threadId = "thread-restarted-plain";
        const string childId = "evicted-child";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            $"subagent-{childId}",
            new ThreadMetadata
            {
                ThreadId = $"subagent-{childId}",
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    threadId,
                    new SubAgentSnapshot(
                        childId,
                        Name: "alpha",
                        TemplateName: "worker",
                        Task: "alpha's task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: $"subagent-{childId}",
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow)),
            });

        // Pool has NO live loop for this thread (restart evicted it) — TryGet returns false, so
        // BuildAsync's only route to this child is the persisted-provenance scan.
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, new WorkflowRunRegistry(), store);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        var child = summaries.Should().ContainSingle(s => s.AgentId == childId).Which;
        child.Name.Should().Be("alpha");
        child.Template.Should().Be("worker");
        child.Status.Should().Be("completed");
        child.ThreadId.Should().Be($"subagent-{childId}");
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>
    /// A provider whose stream never yields and only unwinds on cancellation — keeps a spawned
    /// child's run in progress (Running) without any timing dependence.
    /// </summary>
    private static IStreamingAgent BlockingProvider()
    {
        var provider = new Mock<IStreamingAgent>();
        provider
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                Task.FromResult(BlockingStream(ct)));
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }
}
