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
            new AgentHierarchyService(
                pool,
                new WorkflowRunRegistry(),
                new InMemoryConversationStore(),
                NullLogger<AgentHierarchyService>.Instance,
                new SubAgentScanCoverageCache()),
            RootThread,
            RootThread);

        registry.BuildContracts().Select(c => c.Name).Should()
            .Contain(AgentTranscriptToolProvider.GetAgentTranscriptToolName);
        options!.NonInheritedToolNames.Should().Contain(
            AgentTranscriptToolProvider.GetAgentTranscriptToolName);
        options.NonInheritedToolNames.Should().Contain(
            "SomethingTheHostAlreadyExcluded",
            "existing exclusions are unioned, never replaced");

        // Excluding it from inheritance would otherwise leave every deeper agent with no transcript tool
        // at all, so the same call must also say how a deeper agent gets its OWN instance.
        options.ChildToolProviderFactory.Should().NotBeNull(
            "the exclusion is only safe because each participant is handed a fresh, self-bound instance");
        options.ChildToolProviderFactory!("a-child").Should().BeOfType<AgentTranscriptToolProvider>()
            .Which.GetFunctions().Select(f => f.Contract.Name).Should()
            .Equal([AgentTranscriptToolProvider.GetAgentTranscriptToolName]);
    }

    [Fact]
    public async Task Wiring_RegistersTheToolForAConversationThatSpawnsNoSubAgents()
    {
        await using var pool = CreateFakeAgentPool();
        var registry = new FunctionRegistry();

        var options = global::Program.RegisterAgentTranscriptTool(
            registry,
            subAgentOptions: null,
            new AgentHierarchyService(
                pool,
                new WorkflowRunRegistry(),
                new InMemoryConversationStore(),
                NullLogger<AgentHierarchyService>.Instance,
                new SubAgentScanCoverageCache()),
            RootThread,
            RootThread);

        options.Should().BeNull("a conversation with no sub-agent options has nothing to exclude from");
        registry.BuildContracts().Select(c => c.Name).Should()
            .Contain(AgentTranscriptToolProvider.GetAgentTranscriptToolName);
    }

    [Fact]
    public async Task ADeeperAgent_ReadsItsOwnChild_ButStillNotASibling()
    {
        // The gap this closes: the tool was registered on the ROOT's registry only, and excluded from
        // inheritance (rightly — it is bound to one reader). So an agent at depth 1 that spawned children
        // of its own could not read them, and the Ancestors policy it is entitled to was unreachable for
        // everyone but the root. Both halves are asserted through the deeper agent's OWN registered
        // handler, because a test that builds its own provider proves nothing about the wiring.
        var store = new InMemoryConversationStore();

        // The pool resolves the loop, the loop's options come from the registration, and the registration
        // needs the pool — so the pool is handed a closure over the loop that is assigned just below.
        MultiTurnAgentLoop? root = null;
        await using var pool = new MultiTurnAgentPool(
            (_, _, _) => new MultiTurnAgentPool.AgentCreationResult(root!),
            NullLogger<MultiTurnAgentPool>.Instance);

        var registry = new FunctionRegistry();
        var options = global::Program.RegisterAgentTranscriptTool(
            registry,
            WorkerOptions(),
            new AgentHierarchyService(
                pool,
                new WorkflowRunRegistry(),
                store,
                NullLogger<AgentHierarchyService>.Instance,
                new SubAgentScanCoverageCache()),
            RootThread,
            RootThread);

        root = new MultiTurnAgentLoop(
            BlockingProvider(),
            registry,
            threadId: RootThread,
            subAgentOptions: options,
            collaboration: CreateRootCollaboration(new AgentCollaborationOptions { MaxDelegationDepth = 2 }));
        await using var rootLifetime = root;
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var alphaId = await SpawnAsync(root, "alpha");
        var betaId = await SpawnAsync(root, "beta");
        root.SubAgentManager!.TryGetAgent(alphaId, out var spawned).Should().BeTrue();
        var alphaLoop = spawned.Should().BeOfType<MultiTurnAgentLoop>().Subject;

        var alphasChildId = await SpawnAsync(alphaLoop, "alpha-child");
        await store.AppendMessagesAsync(
            $"subagent-{alphasChildId}",
            [Persisted("m1", new TextMessage { Text = "the deep finding", Role = Role.Assistant })]);

        alphaLoop.RegisteredToolNames.Should().Contain(
            AgentTranscriptToolProvider.GetAgentTranscriptToolName,
            "a participant that can spawn must be able to read what it spawned");

        // The handler map is what the loop actually resolves a tool call against, so invoking through it
        // exercises the instance the host bound to alpha — not one this test chose.
        var snapshot = alphaLoop.SubAgentManager!.GetInheritableToolSnapshot();
        snapshot.Contracts.Select(c => c.Name).Should().NotContain(
            AgentTranscriptToolProvider.GetAgentTranscriptToolName,
            "alpha's own instance is still never handed down; its child is given a fresh one instead");

        var handler = snapshot.Handlers[AgentTranscriptToolProvider.GetAgentTranscriptToolName];

        var allowed = Assert.IsType<ToolHandlerResult.Resolved>(await handler(
            JsonSerializer.Serialize(new { agent_id = alphasChildId }),
            new ToolCallContext(),
            CancellationToken.None));
        allowed.Payload.IsError.Should().BeFalse(allowed.Payload.Text);
        allowed.Payload.Text.Should().Contain("the deep finding");

        var denied = Assert.IsType<ToolHandlerResult.Resolved>(await handler(
            JsonSerializer.Serialize(new { agent_id = betaId }),
            new ToolCallContext(),
            CancellationToken.None));
        denied.Payload.IsError.Should().BeTrue("reaching deeper never widens who a reader may look at");
        denied.Payload.ErrorCode.Should().Be(TranscriptAccessReasons.NotAnAncestor);
    }

    [Fact]
    public async Task ReadTranscript_SucceedsForPersistedCollaboratingChild_AfterRestartWithFreshDirectory()
    {
        // BuildAsync -> persist -> restart -> transcript read. Regression for AgentHierarchyProjection.
        // Enrich() now stamping a row's collaboration hierarchy metadata (AgentNodeId/AgentKind/
        // ParentAgentId/CollaborationId) BEFORE it is written to the durable index. Without that, a server
        // restart rebuilds the loop's collaboration directory from scratch (root only, no memory of the
        // prior child), the retained/persisted child row carries none of that metadata, ToNodeRecord()
        // returns null for it, and the transcript route fails closed with unknown_target for its own
        // legitimate root ancestor even though the child's transcript is still on disk.
        var indexDir = Path.Combine(Path.GetTempPath(), "wf-index-transcript-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemoryConversationStore();

            // --- Before restart: spawn a collaborating child and let BuildAsync write it through. ---
            var registryBeforeRestart = new WorkflowRunRegistry(indexDir);
            await using var loopBeforeRestart = CreateLoop(CreateRootCollaboration());
            await using var poolBeforeRestart = CreatePoolReturning(loopBeforeRestart);
            _ = poolBeforeRestart.GetOrCreateAgent(
                RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

            var alphaId = await SpawnAsync(loopBeforeRestart, "alpha");
            await store.AppendMessagesAsync(
                $"subagent-{alphaId}",
                [Persisted("m1", new TextMessage { Text = "alpha's finding", Role = Role.Assistant })]);

            var serviceBeforeRestart = new AgentHierarchyService(
                poolBeforeRestart,
                registryBeforeRestart,
                store,
                NullLogger<AgentHierarchyService>.Instance,
                new SubAgentScanCoverageCache());
            _ = await serviceBeforeRestart.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

            // --- Restart: a brand-new loop with a FRESH collaboration directory (root only, no memory of
            // alpha), a brand-new WorkflowRunRegistry instance pointed at the SAME on-disk index, and a
            // pool that never rehydrated the prior loop — exactly what a server restart leaves behind.
            var registryAfterRestart = new WorkflowRunRegistry(indexDir);
            await using var loopAfterRestart = CreateLoop(CreateRootCollaboration());
            await using var poolAfterRestart = CreatePoolReturning(loopAfterRestart);
            _ = poolAfterRestart.GetOrCreateAgent(
                RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

            var controller = CreateController(poolAfterRestart, registryAfterRestart, store);
            var result = await controller.GetAgentTranscript(RootThread, alphaId);

            var ok = Assert.IsType<OkObjectResult>(result);
            var messages = Assert.IsAssignableFrom<IReadOnlyCollection<PersistedMessage>>(ok.Value).ToList();
            messages.Select(m => m.Id).Should().Equal(["m1"]);
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
    public async Task ReadTranscript_SucceedsForARetainedChild_AfterTheLoopLeftThePool()
    {
        // The daemon's actual failure mode, and the one the restart test above does NOT cover: it keeps a
        // live loop on the far side of the restart, so a collaboration is still in hand. Here NOTHING is
        // live — the conversation was evicted from the pool (or the host restarted and nobody reopened
        // it), which is the state every reader is in AFTER a review terminates. BuildAsync returns
        // loop?.Collaboration, so the collaboration is null purely because the loop is gone, and the route
        // used to answer collaboration_unavailable for a hierarchy that is sitting on disk, fully
        // persisted, right next to the transcript it is refusing.
        await WithRetainedRootAsync(async (store, registry, alphaId) =>
        {
            await using var coldPool = CreateFakeAgentPool();
            var result = await CreateController(coldPool, registry, store)
                .GetAgentTranscript(RootThread, alphaId);

            var ok = Assert.IsType<OkObjectResult>(result);
            var messages = Assert.IsAssignableFrom<IReadOnlyCollection<PersistedMessage>>(ok.Value).ToList();

            messages.Select(m => m.Id).Should().Equal(
                ["m2"],
                "a retained read is the same read, so reasoning is excluded exactly as it is when live");
            JsonSerializer.Serialize(messages).Should().NotContain("deliberation");
        });
    }

    [Fact]
    public async Task ReadTranscript_StillRefusesARetainedChild_ForANamedViewer()
    {
        // The retained path answers only for the conversation root, whose verdict is knowable without the
        // live bundle: root-reads-descendant resolves to Ancestor, which is allowed under BOTH visibility
        // modes, so the cold answer is the answer the live path would have given. A NAMED reader is a
        // genuinely different question — cross-collaboration, ancestry and the configured mode all matter,
        // and the mode is not persisted — so it must keep saying the hierarchy is unavailable rather than
        // guess. This is also what keeps the in-agent tool, which always names its reader, untouched.
        await WithRetainedRootAsync(async (store, registry, alphaId) =>
        {
            await using var coldPool = CreateFakeAgentPool();
            var result = await CreateController(coldPool, registry, store)
                .GetAgentTranscript(RootThread, alphaId, viewer: "some-other-agent");

            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            JsonSerializer.Serialize(notFound.Value).Should()
                .Contain(AgentTranscriptReasons.CollaborationUnavailable);
        });
    }

    [Fact]
    public async Task ReadTranscript_DoesNotServeARetainedRowThatCarriesNoCollaborationIdentity()
    {
        // The gate that keeps this from becoming a new door on a host that never enabled collaboration.
        // Such a host persists workflow tabs unenriched, so the row carries no CollaborationId/AgentKind
        // and ToNodeRecord() returns null for it — there is no hierarchy that ever authorized this agent,
        // and the retained path must not invent one just because the row survived on disk.
        var indexDir = NewIndexDir();
        try
        {
            var registry = new WorkflowRunRegistry(indexDir);
            registry.PersistTabs(
                RootThread,
                [
                    new SubAgentSummary
                    {
                        AgentId = "plain-1",
                        Template = "worker",
                        Task = "a task",
                        Status = "completed",
                        ThreadId = "subagent-plain-1",
                    },
                ]);

            var store = new InMemoryConversationStore();
            await store.AppendMessagesAsync(
                "subagent-plain-1",
                [Persisted("m1", new TextMessage { Text = "the finding", Role = Role.Assistant })]);

            await using var coldPool = CreateFakeAgentPool();
            var result = await CreateController(coldPool, registry, store)
                .GetAgentTranscript(RootThread, "plain-1");

            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            JsonSerializer.Serialize(notFound.Value).Should()
                .Contain(AgentTranscriptReasons.CollaborationUnavailable);
        }
        finally
        {
            DeleteIndexDir(indexDir);
        }
    }

    [Fact]
    public async Task ReadTranscript_ReportsAnUnknownTargetOnTheRetainedPath()
    {
        // A retained conversation that does have a hierarchy still owes the same content-free answer for
        // an agent it has never heard of — the retained path must not become the one place a 404/403 split
        // tells a caller which agent ids are real.
        await WithRetainedRootAsync(async (store, registry, _) =>
        {
            await using var coldPool = CreateFakeAgentPool();
            var result = await CreateController(coldPool, registry, store)
                .GetAgentTranscript(RootThread, "agent-that-never-existed");

            AssertDenied(result, TranscriptAccessReasons.UnknownTarget);
        });
    }

    /// <summary>
    /// Drives one conversation to the state every retained read starts from: a collaborating child was
    /// spawned, <see cref="AgentHierarchyService.BuildAsync"/> wrote its enriched row through to the
    /// durable index, its transcript was persisted, and then everything live went away. The callback is
    /// handed the store, a registry over the same on-disk index, and the child's id — with no live agent
    /// anywhere, exactly as the daemon finds the host after a review terminates.
    /// </summary>
    private static async Task WithRetainedRootAsync(
        Func<IConversationStore, WorkflowRunRegistry, string, Task> assert)
    {
        var indexDir = NewIndexDir();
        try
        {
            var store = new InMemoryConversationStore();
            string alphaId;

            var registryWhileLive = new WorkflowRunRegistry(indexDir);
            await using (var loop = CreateLoop(CreateRootCollaboration()))
            await using (var pool = CreatePoolReturning(loop))
            {
                _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);
                alphaId = await SpawnAsync(loop, "alpha");
                await store.AppendMessagesAsync(
                    $"subagent-{alphaId}",
                    [
                        Persisted("m1", new ReasoningMessage { Reasoning = "private deliberation" }),
                        Persisted("m2", new TextMessage { Text = "the finding", Role = Role.Assistant }),
                    ]);

                _ = await new AgentHierarchyService(
                        pool,
                        registryWhileLive,
                        store,
                        NullLogger<AgentHierarchyService>.Instance,
                        new SubAgentScanCoverageCache())
                    .BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);
            }

            // A fresh registry over the same index, and below this the caller brings a pool that never
            // held this conversation — no loop, no directory, no collaboration. Only what is on disk.
            await assert(store, new WorkflowRunRegistry(indexDir), alphaId);
        }
        finally
        {
            DeleteIndexDir(indexDir);
        }
    }

    private static string NewIndexDir() =>
        Path.Combine(Path.GetTempPath(), "wf-index-transcript-" + Guid.NewGuid().ToString("N"));

    private static void DeleteIndexDir(string indexDir)
    {
        try
        {
            Directory.Delete(indexDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a directory that was never written throws here too.
        }
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
            new AgentHierarchyService(
                pool, registry, store, NullLogger<AgentHierarchyService>.Instance, new SubAgentScanCoverageCache()),
            RootThread,
            viewerAgentId);

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

    private static AgentCollaborationSetup CreateRootCollaboration(
        AgentCollaborationOptions? options = null) =>
        AgentCollaborationSetup.CreateRoot(
            options ?? new AgentCollaborationOptions(),
            collaborationId: RootThread,
            agentId: RootThread,
            name: "root");

    /// <summary>
    /// The one sub-agent template every test here spawns from. Its provider blocks, so a spawned child
    /// stays Running deterministically instead of racing the assertions to completion.
    /// </summary>
    private static SubAgentOptions WorkerOptions() =>
        new()
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

    private static MultiTurnAgentLoop CreateLoop(AgentCollaborationSetup? collaboration) =>
        new(
            BlockingProvider(),
            new FunctionRegistry(),
            threadId: RootThread,
            subAgentOptions: WorkerOptions(),
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
        IConversationStore store,
        SubAgentScanCoverageCache? scanCoverageCache = null) =>
        new(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(Mock.Of<IConversationStore>(), new InMemoryConversationStore()),
            TimeProvider.System,
            workflowRunRegistry,
            TestAuthorizers.Disabled(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            scanCoverageCache ?? new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance));

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
