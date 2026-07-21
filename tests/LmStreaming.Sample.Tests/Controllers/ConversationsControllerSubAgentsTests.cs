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
        return new ConversationsController(
            Mock.Of<IConversationStore>(),
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(Mock.Of<IConversationStore>(), new InMemoryConversationStore()),
            NullLogger<ConversationsController>.Instance);
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

        var result = controller.ListSubAgents("does-not-exist");

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

        var result = controller.ListSubAgents(threadId);

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

        var result = controller.ListSubAgents(threadId);

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
