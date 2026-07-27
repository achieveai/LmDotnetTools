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
/// It answers from the live manager UNION the children reconstructed from persisted
/// <see cref="SubAgentProvenance"/>, so a link to a conversation whose run has ended still lists
/// its sub-agents.
/// </summary>
public sealed class ConversationsControllerSubAgentsTests
{
    private static ConversationsController CreateController(
        MultiTurnAgentPool pool,
        IConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        return new ConversationsController(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(Mock.Of<IConversationStore>(), new InMemoryConversationStore()),
            NullLogger<ConversationsController>.Instance);
    }

    /// <summary>
    /// Persists thread metadata carrying the provenance a spawned child's store would have stamped.
    /// </summary>
    private static async Task SeedPersistedChildAsync(
        IConversationStore store,
        string parentThreadId,
        string agentId,
        string name,
        string template,
        string task,
        long lastUpdated)
    {
        var childThreadId = $"{SubAgentProvenance.ThreadIdPrefix}{agentId}";
        await store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = lastUpdated,
                Properties = SubAgentProvenance.Build(
                    parentThreadId,
                    new SubAgentSnapshot(
                        AgentId: agentId,
                        Name: name,
                        TemplateName: template,
                        Task: task,
                        Status: SubAgentStatus.Completed,
                        ThreadId: childThreadId,
                        LastActivityUtc: DateTimeOffset.FromUnixTimeMilliseconds(lastUpdated))),
            });
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

        var beta = summaries.Single(s => s.AgentId == betaId);
        beta.Name.Should().Be("beta");
        beta.Template.Should().Be("worker");
        beta.Task.Should().Be("second task");
        beta.Status.Should().Be("running");
        beta.ThreadId.Should().Be($"subagent-{betaId}");
    }

    /// <summary>
    /// The deep-link case: the review run has ended and the parent agent is gone from the pool, but a
    /// human following the posted link must still see which sub-agents ran. Nothing is live here — the
    /// whole roster has to come back from the store.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_ReturnsPersistedChildren_WhenParentIsNotInThePool()
    {
        const string parentThreadId = "thread-finished-review";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            parentThreadId,
            new ThreadMetadata { ThreadId = parentThreadId, LastUpdated = 1_000 });
        await SeedPersistedChildAsync(
            store, parentThreadId, "aaa", "alpha", "code-reviewer:security", "check auth", 2_000);
        await SeedPersistedChildAsync(
            store, parentThreadId, "bbb", "beta", "code-reviewer:performance", "check hot path", 3_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(parentThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        summaries.Should().HaveCount(2);

        // Newest activity first.
        summaries[0].AgentId.Should().Be("bbb");
        summaries[0].Template.Should().Be("code-reviewer:performance");
        summaries[0].Task.Should().Be("check hot path");
        summaries[0].ThreadId.Should().Be("subagent-bbb");
        summaries[0].Status.Should().Be(SubAgentProvenance.PersistedStatus,
            "lifecycle status died with the manager; a reconstructed child must not claim one");

        summaries[1].AgentId.Should().Be("aaa");
        summaries[1].Name.Should().Be("alpha");
    }

    [Fact]
    public async Task ListSubAgents_ExcludesChildrenOfOtherConversations()
    {
        const string parentThreadId = "thread-mine";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            parentThreadId,
            new ThreadMetadata { ThreadId = parentThreadId, LastUpdated = 1_000 });
        await SeedPersistedChildAsync(
            store, parentThreadId, "mine", "mine", "worker", "my task", 2_000);
        await SeedPersistedChildAsync(
            store, "thread-someone-else", "theirs", "theirs", "worker", "their task", 3_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(parentThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        summaries.Should().ContainSingle().Which.AgentId.Should().Be("mine");
    }

    /// <summary>
    /// A child that is both live and persisted appears ONCE, described by the live manager — it is the
    /// authority while it exists and is the only source of real lifecycle status.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_PrefersLiveSnapshot_OverPersistedCopyOfSameChild()
    {
        const string threadId = "thread-live-and-persisted";

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () => BlockingProvider(),
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        await using var loop = new MultiTurnAgentLoop(
            BlockingProvider(),
            new FunctionRegistry(),
            threadId: threadId,
            subAgentOptions: subAgentOptions);

        await using var pool = CreatePoolReturning(loop);
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(threadId, mode);

        var agentId = ParseAgentId(await loop.SubAgentManager!.SpawnAsync(
            "worker", "live task", name: "live", runInBackground: true));

        var store = new InMemoryConversationStore();
        await SeedPersistedChildAsync(
            store, threadId, agentId, "stale", "worker", "stale task", 9_000);

        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summary = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value)
            .Should().ContainSingle().Subject;
        summary.AgentId.Should().Be(agentId);
        summary.Name.Should().Be("live");
        summary.Task.Should().Be("live task");
        summary.Status.Should().Be("running");
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
