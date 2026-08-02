using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Security and behaviour tests for the two readers of an agent's transcript (#244):
/// <c>GET /api/conversations/{threadId}/agents/{agentId}/transcript</c> and the in-agent
/// <c>GetAgentTranscript</c> tool.
/// </summary>
/// <remarks>
/// This is the only place the sample hands one agent's transcript to a different agent, so the cases
/// below are deliberately adversarial: on the route the <c>viewer</c> is a caller-supplied string, and
/// in the tool it is the model that names the target. Both must derive the answer from the trusted
/// directory through <see cref="AgentHierarchyProjection"/> and never from the request, must return only
/// a content-free denial code, and must never disclose reasoning even on a permitted read. They are
/// tested together because the danger is not that one of them is wrong — it is that they DISAGREE.
/// </remarks>
public sealed class AgentTranscriptAccessTests
{
    private const string RootThread = "thread-root";

    /// <summary>The same options the controller normalizes persisted messages with.</summary>
    private static readonly JsonSerializerOptions MessageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new IMessageJsonConverter() },
    };

    [Fact]
    public async Task Returns404_ForAnUnknownThread()
    {
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, new WorkflowRunRegistry(), new InMemoryConversationStore());

        var result = await controller.GetAgentTranscript("does-not-exist", "a-1");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("unknown_thread");
    }

    [Fact]
    public async Task Returns404_WhenTheHostNeverEnabledCollaboration()
    {
        await using var loop = CreateLoop(collaboration: null);
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var agentId = await SpawnAsync(loop, "alpha", collaborating: false);
        var controller = CreateController(pool, new WorkflowRunRegistry(), new InMemoryConversationStore());

        var result = await controller.GetAgentTranscript(RootThread, agentId);

        // The tab exists and is listed as it always was; only the cross-agent read is unavailable, and
        // saying so is what keeps the legacy surface unchanged rather than silently half-enabled.
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("collaboration_unavailable");
    }

    [Fact]
    public async Task ToolAndRoute_ReportTheSameCode_ForAConversationWithNoHierarchy()
    {
        // The "there is nothing here to read" outcomes are part of the same contract as the refusals, and
        // the two surfaces used to answer them differently (the tool said hierarchy_unavailable where the
        // route said unknown_thread/collaboration_unavailable). One vocabulary, or neither side's answer
        // can be documented — or trusted — as meaning anything.
        await using var pool = CreateFakeAgentPool();
        var registry = new WorkflowRunRegistry();
        var store = new InMemoryConversationStore();

        var routeResult = await CreateController(pool, registry, store).GetAgentTranscript(RootThread, "a-1");
        var toolResult = await InvokeToolAsync(
            pool, registry, store, RootThread, JsonSerializer.Serialize(new { agent_id = "a-1" }));

        JsonSerializer.Serialize(Assert.IsType<NotFoundObjectResult>(routeResult).Value)
            .Should().Contain(AgentTranscriptReasons.UnknownThread);
        toolResult.Payload.IsError.Should().BeTrue();
        toolResult.Payload.ErrorCode.Should().Be(AgentTranscriptReasons.UnknownThread);
    }

    [Fact]
    public async Task ToolAndRoute_ReportTheSameCode_WhenTheHostNeverEnabledCollaboration()
    {
        await using var loop = CreateLoop(collaboration: null);
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var agentId = await SpawnAsync(loop, "alpha", collaborating: false);
        var registry = new WorkflowRunRegistry();
        var store = new InMemoryConversationStore();

        var routeResult = await CreateController(pool, registry, store).GetAgentTranscript(RootThread, agentId);
        var toolResult = await InvokeToolAsync(
            pool, registry, store, RootThread, JsonSerializer.Serialize(new { agent_id = agentId }));

        JsonSerializer.Serialize(Assert.IsType<NotFoundObjectResult>(routeResult).Value)
            .Should().Contain(AgentTranscriptReasons.CollaborationUnavailable);
        toolResult.Payload.ErrorCode.Should().Be(AgentTranscriptReasons.CollaborationUnavailable);
        toolResult.Payload.Text.Should().NotContain(
            agentId, "an unavailable hierarchy says nothing about who was asked for");
    }

    [Fact]
    public async Task Returns403_ForAnAgentTheHierarchyDoesNotKnow()
    {
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var controller = CreateController(pool, new WorkflowRunRegistry(), new InMemoryConversationStore());

        var result = await controller.GetAgentTranscript(RootThread, "agent-that-never-existed");

        AssertDenied(result, TranscriptAccessReasons.UnknownTarget);
    }

    [Fact]
    public async Task Returns403_WhenOneSubAgentAsksForItsSibling()
    {
        // The bypass attempt this route exists to stop: a caller naming itself as a legitimate agent and
        // then asking for a peer's transcript. Under the default Ancestors visibility a sibling is not
        // above the target, so the honest answer is no — regardless of what the query string claims.
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(loop, "alpha");
        var betaId = await SpawnAsync(loop, "beta");
        var controller = CreateController(pool, new WorkflowRunRegistry(), new InMemoryConversationStore());

        var result = await controller.GetAgentTranscript(RootThread, betaId, viewer: alphaId);

        AssertDenied(result, TranscriptAccessReasons.NotAnAncestor);
    }

    [Fact]
    public async Task Returns403_ForAViewerFromOutsideTheCollaboration()
    {
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(loop, "alpha");
        var controller = CreateController(pool, new WorkflowRunRegistry(), new InMemoryConversationStore());

        var result = await controller.GetAgentTranscript(RootThread, alphaId, viewer: "someone-elses-agent");

        AssertDenied(result, TranscriptAccessReasons.UnknownReader);
    }

    [Fact]
    public async Task ListSubAgents_ReportsTheSameVerdictTheTranscriptRouteEnforces()
    {
        // The listing's isReadable flag is what the client renders an "open transcript" affordance from.
        // If it could disagree with the route, the UI would offer a read that then 403s.
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(loop, "alpha");
        var betaId = await SpawnAsync(loop, "beta");
        var controller = CreateController(pool, new WorkflowRunRegistry(), new InMemoryConversationStore());

        var listed = Assert.IsType<OkObjectResult>(await controller.ListSubAgents(RootThread, viewer: alphaId));
        var rows = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(listed.Value).ToList();

        rows.Single(r => r.AgentId == alphaId).IsCurrent.Should().BeTrue();
        rows.Single(r => r.AgentId == alphaId).IsReadable.Should().BeTrue();
        rows.Single(r => r.AgentId == betaId).IsReadable.Should().BeFalse();
        rows.Should().OnlyContain(r => r.ParentAgentId == RootThread,
            "both children hang off the root the loop registered itself as");
    }

    [Fact]
    public async Task Returns200WithoutReasoning_WhenAnAncestorReads()
    {
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(loop, "alpha");

        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(
            $"subagent-{alphaId}",
            [
                Persisted("m1", new ReasoningMessage { Reasoning = "private deliberation" }),
                Persisted("m2", new TextMessage { Text = "the finding", Role = Role.Assistant }),
                Persisted("m3", new ReasoningUpdateMessage { Reasoning = "more deliberation" }),
            ]);

        var controller = CreateController(pool, new WorkflowRunRegistry(), store);

        // No viewer: the request is the root's own, and the root is above every agent it spawned.
        var ok = Assert.IsType<OkObjectResult>(await controller.GetAgentTranscript(RootThread, alphaId));
        var messages = Assert.IsAssignableFrom<IReadOnlyCollection<PersistedMessage>>(ok.Value).ToList();

        messages.Select(m => m.Id).Should().Equal(
            ["m2"],
            "reasoning is excluded from every cross-agent read, in both its finalized and delta forms");
        JsonSerializer.Serialize(messages).Should().NotContain("deliberation");
    }

    [Theory]
    // The pairs that matter: reading yourself, reading down, and the sibling read the policy exists to
    // stop. Whatever the answer is, the route and the tool must give the SAME one — a client that shows
    // an "open transcript" affordance from the listing, and a model that then calls the tool, are looking
    // at one decision, and a split between them is a bypass waiting to be found.
    [InlineData("alpha", "alpha", true)]
    [InlineData(null, "alpha", true)]
    [InlineData("alpha", "beta", false)]
    public async Task ToolAndRoute_AgreeForTheSameViewerAndTarget(
        string? viewerName, string targetName, bool expectAllowed)
    {
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var ids = new Dictionary<string, string>
        {
            ["alpha"] = await SpawnAsync(loop, "alpha"),
            ["beta"] = await SpawnAsync(loop, "beta"),
        };
        var viewer = viewerName is null ? null : ids[viewerName];
        var target = ids[targetName];

        var registry = new WorkflowRunRegistry();
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(
            $"subagent-{target}", [Persisted("m1", new TextMessage { Text = "the finding", Role = Role.Assistant })]);

        var routeResult = await CreateController(pool, registry, store)
            .GetAgentTranscript(RootThread, target, viewer);
        var toolResult = await InvokeToolAsync(
            pool, registry, store, viewer ?? RootThread,
            JsonSerializer.Serialize(new { agent_id = target }));

        if (expectAllowed)
        {
            Assert.IsType<OkObjectResult>(routeResult);
            toolResult.Payload.IsError.Should().BeFalse();
            toolResult.Payload.Text.Should().Contain("the finding");
        }
        else
        {
            var denied = Assert.IsType<ObjectResult>(routeResult);
            denied.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            toolResult.Payload.IsError.Should().BeTrue();
            toolResult.Payload.ErrorCode.Should().Be(TranscriptAccessReasons.NotAnAncestor,
                "the tool reports the very code the route puts in its 403 body");
            JsonSerializer.Serialize(denied.Value).Should().Contain(toolResult.Payload.ErrorCode);
        }
    }

    [Fact]
    public async Task Tool_ReadsAsItsOwnAgent_NotAsWhicheverReaderTheModelNames()
    {
        // The escalation the tool's shape is designed to make impossible: there is no viewer parameter,
        // so a model that invents one is simply ignored. If this ever regresses into reading a caller-
        // supplied reader, one compromised prompt reads the whole hierarchy.
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(loop, "alpha");
        var betaId = await SpawnAsync(loop, "beta");

        var result = await InvokeToolAsync(
            pool,
            new WorkflowRunRegistry(),
            new InMemoryConversationStore(),
            viewerAgentId: alphaId,
            argsJson: JsonSerializer.Serialize(new { agent_id = betaId, viewer = RootThread }));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(TranscriptAccessReasons.NotAnAncestor);
        result.Payload.Text.Should().NotContain(betaId, "a refusal must not confirm the target exists");
    }

    [Fact]
    public async Task Tool_ReturnsTheMostRecentMessagesWithoutReasoning()
    {
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(loop, "alpha");
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(
            $"subagent-{alphaId}",
            [
                Persisted("m1", new TextMessage { Text = "the early finding", Role = Role.Assistant }),
                Persisted("m2", new ReasoningMessage { Reasoning = "private deliberation" }),
                Persisted("m3", new TextMessage { Text = "the late finding", Role = Role.Assistant }),
            ]);

        var result = await InvokeToolAsync(
            pool,
            new WorkflowRunRegistry(),
            store,
            viewerAgentId: RootThread,
            argsJson: JsonSerializer.Serialize(new { agent_id = alphaId, limit = 1 }));

        result.Payload.IsError.Should().BeFalse();
        result.Payload.Text.Should().Contain("the late finding");
        result.Payload.Text.Should().NotContain("the early finding", "limit keeps only the recent tail");
        result.Payload.Text.Should().NotContain("deliberation", "reasoning is excluded from every read");
        result.Payload.Text.Should().Contain("omitted_older_messages",
            "a truncated read says so, so the reader knows there is more");
    }

    [Fact]
    public async Task Tool_RejectsACallWithNoTarget()
    {
        await using var loop = CreateLoop(CreateRootCollaboration());
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var result = await InvokeToolAsync(
            pool, new WorkflowRunRegistry(), new InMemoryConversationStore(), RootThread, argsJson: "{}");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
    }

    [Fact]
    public async Task Wiring_RegistersTheToolAndKeepsItOutOfSubAgentInheritance()
    {
        // Registering without excluding is the escalation this helper exists to make impossible: the
        // provider is bound to one reader, so an inherited copy hands every descendant that reader's
        // reach over the whole hierarchy. Both halves are asserted here because the host does them in
        // one call — if that ever splits back into two statements, this fails.
        await using var pool = CreateFakeAgentPool();
        var registry = new FunctionRegistry();

        var options = global::Program.RegisterAgentTranscriptTool(
            registry,
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>(),
                NonInheritedToolNames = ["SomethingTheHostAlreadyExcluded"],
            },
            new AgentTranscriptToolProvider(
                new AgentHierarchyService(pool, new WorkflowRunRegistry(), new InMemoryConversationStore()),
                RootThread,
                RootThread));

        registry.BuildContracts().Select(c => c.Name).Should()
            .Contain(AgentTranscriptToolProvider.GetAgentTranscriptToolName);
        options!.NonInheritedToolNames.Should().Contain(
            AgentTranscriptToolProvider.GetAgentTranscriptToolName);
        options.NonInheritedToolNames.Should().Contain(
            "SomethingTheHostAlreadyExcluded",
            "existing exclusions are unioned, never replaced");
    }

    [Fact]
    public async Task Wiring_RegistersTheToolForAConversationThatSpawnsNoSubAgents()
    {
        await using var pool = CreateFakeAgentPool();
        var registry = new FunctionRegistry();

        var options = global::Program.RegisterAgentTranscriptTool(
            registry,
            subAgentOptions: null,
            new AgentTranscriptToolProvider(
                new AgentHierarchyService(pool, new WorkflowRunRegistry(), new InMemoryConversationStore()),
                RootThread,
                RootThread));

        options.Should().BeNull("a conversation with no sub-agent options has nothing to exclude from");
        registry.BuildContracts().Select(c => c.Name).Should()
            .Contain(AgentTranscriptToolProvider.GetAgentTranscriptToolName);
    }

    /// <summary>Runs the tool exactly as the loop would: one handler, one args string, one reader.</summary>
    private static async Task<ToolHandlerResult.Resolved> InvokeToolAsync(
        MultiTurnAgentPool pool,
        WorkflowRunRegistry registry,
        IConversationStore store,
        string viewerAgentId,
        string argsJson)
    {
        var provider = new AgentTranscriptToolProvider(
            new AgentHierarchyService(pool, registry, store), RootThread, viewerAgentId);

        var descriptor = provider.GetFunctions().Single();
        descriptor.Contract.Name.Should().Be(AgentTranscriptToolProvider.GetAgentTranscriptToolName);

        var result = await descriptor.Handler(argsJson, new ToolCallContext(), CancellationToken.None);
        return Assert.IsType<ToolHandlerResult.Resolved>(result);
    }

    private static void AssertDenied(IActionResult result, string expectedReason)
    {
        var denied = Assert.IsType<ObjectResult>(result);
        denied.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var payload = JsonSerializer.Serialize(denied.Value);
        payload.Should().Contain(expectedReason);
        payload.Should().NotContain("subagent-", "a denial must not leak the target's thread");
    }

    private static PersistedMessage Persisted(string id, IMessage message) =>
        new()
        {
            Id = id,
            ThreadId = "ignored-by-the-store",
            RunId = "run-1",
            Timestamp = 0,
            MessageType = message.GetType().Name,
            Role = "assistant",
            MessageJson = JsonSerializer.Serialize(message, message.GetType(), MessageJson),
        };

    private static AgentCollaborationSetup CreateRootCollaboration() =>
        AgentCollaborationSetup.CreateRoot(
            new AgentCollaborationOptions(),
            collaborationId: RootThread,
            agentId: RootThread,
            name: "root");

    private static MultiTurnAgentLoop CreateLoop(AgentCollaborationSetup? collaboration) =>
        new(
            BlockingProvider(),
            new FunctionRegistry(),
            threadId: RootThread,
            subAgentOptions: new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["worker"] = new SubAgentTemplate
                    {
                        Name = "worker",
                        SystemPrompt = "You are a worker.",
                        // Blocking provider keeps each spawned child Running deterministically.
                        AgentFactory = () => BlockingProvider(),
                    },
                },
                MaxConcurrentSubAgents = 5,
            },
            collaboration: collaboration);

    private static async Task<string> SpawnAsync(
        MultiTurnAgentLoop loop, string name, bool collaborating = true)
    {
        var json = await loop.SubAgentManager!.SpawnAsync(
            "worker",
            $"{name}'s task",
            name: name,
            runInBackground: true,
            role: collaborating ? $"{name}'s role" : null,
            description: collaborating ? $"contact {name} about its role" : null);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static ConversationsController CreateController(
        MultiTurnAgentPool pool,
        WorkflowRunRegistry workflowRunRegistry,
        IConversationStore store) =>
        new(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(Mock.Of<IConversationStore>(), new InMemoryConversationStore()),
            TimeProvider.System,
            workflowRunRegistry,
            NullLogger<ConversationsController>.Instance);

    private static MultiTurnAgentPool CreateFakeAgentPool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent) =>
        new((_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent), NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>
    /// A provider whose stream never yields and only unwinds on cancellation — keeps a spawned child's
    /// run in progress without any timing dependence.
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
