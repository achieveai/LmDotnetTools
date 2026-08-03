using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Coverage for two <see cref="AgentHierarchyService"/> hot/cold-path contracts that
/// <see cref="AgentHierarchyProjectionTests"/> and <see cref="AgentTranscriptAccessTests"/> do not
/// exercise directly:
/// <list type="bullet">
/// <item>
/// the persisted <c>SubAgentProvenance</c> scan (<c>ScanPersistedSubAgentChildrenAsync</c>) must never
/// run for a LIVE conversation — every 3-second sub-agent poll and transcript read on a live loop would
/// otherwise pay for a bounded but still expensive multi-page store scan;
/// </item>
/// <item>
/// hitting the scan's 2000-thread cap must not fail silently — it warns, naming the conversation and
/// the cap, so an operator can see the listing became incomplete instead of it just quietly happening.
/// </item>
/// </list>
/// </summary>
public sealed class AgentHierarchyServiceTests
{
    private const string RootThread = "thread-root";

    [Fact]
    public async Task BuildAsync_ForALiveConversation_NeverCallsListThreadsAsync()
    {
        // A conversation with a live MultiTurnAgentLoop in the pool. The cold-path reconstruction of
        // ordinary Agent-tool children from persisted SubAgentProvenance metadata exists ONLY to cover a
        // restart/eviction gap; a live loop already accounts for every child that matters (via its own
        // SubAgentManager snapshot, or the enriched persisted WorkflowRunRegistry tabs), so the scan must
        // be skipped entirely here.
        var countingStore = new CountingConversationStore(new InMemoryConversationStore());

        await using var loop = new MultiTurnAgentLoop(BlockingProvider(), new FunctionRegistry(), threadId: RootThread);
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var service = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance);

        _ = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        countingStore.ListThreadsCallCount.Should().Be(
            0,
            "a live conversation's hot path (3s poll / transcript read) must never pay for the bounded "
                + "persisted-thread scan — that scan exists only for the cold/restart-recovery case");
    }

    [Fact]
    public async Task ListSubAgents_ReconstructsOrdinaryChildren_FromPersistedProvenance_AfterPoolEviction_StillWorks()
    {
        // Companion to the test above: proves the gate is scoped to "there is a live loop", not "the
        // scan never runs at all" — an idle/evicted conversation must still reconstruct its persisted
        // children through the cold-path scan. (Mirrors
        // ConversationsControllerSubAgentsTests.ListSubAgents_ReconstructsOrdinaryChildren_FromPersistedProvenance_AfterPoolEviction,
        // exercised here directly against the service rather than through the controller.)
        const string childId = "evicted-child";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            $"subagent-{childId}",
            new ThreadMetadata
            {
                ThreadId = $"subagent-{childId}",
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    RootThread,
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

        // No live loop registered for RootThread — TryGet returns false, so BuildAsync's only route to
        // this child is the persisted-provenance cold-path scan.
        await using var pool = CreateFakeAgentPool();
        var service = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), store, NullLogger<AgentHierarchyService>.Instance);

        var (rows, isKnown, _) = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown.Should().BeTrue();
        var child = rows.Should().ContainSingle(s => s.AgentId == childId).Which;
        child.Name.Should().Be("alpha");
        child.Template.Should().Be("worker");
    }

    [Fact]
    public async Task BuildAsync_ForALiveNonMultiTurnAgentLoop_NeverCallsListThreadsAsync()
    {
        // PRRT_kwDOOPysWM6V1mjj: `loop is null` used to stand in for "no live coverage", but a live
        // Codex/Copilot CLI pool entry (or any other non-MultiTurnAgentLoop IMultiTurnAgent) also makes
        // `loop` null — so it paid for the same bounded-but-expensive multi-page store scan on every
        // 3-second hot poll, even though it can never own an Agent-tool SubAgentManager roster to begin
        // with. A persisted provenance child is seeded to prove the scan is skipped, not merely empty.
        const string childId = "cli-sibling-child";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            $"subagent-{childId}",
            new ThreadMetadata
            {
                ThreadId = $"subagent-{childId}",
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    RootThread,
                    new SubAgentSnapshot(
                        childId,
                        Name: "cli-child",
                        TemplateName: "worker",
                        Task: "cli child's task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: $"subagent-{childId}",
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow)),
            });
        var countingStore = new CountingConversationStore(store);

        // A live, non-MultiTurnAgentLoop agent — stands in for a Codex/Copilot CLI pool entry. It has no
        // SubAgentManager/Collaboration at all, so `agent as MultiTurnAgentLoop` is null exactly like the
        // pre-fix "loop is null" gate saw it, but it is fully live (isLive is true).
        await using var cliAgent = new FakeMultiTurnAgent(RootThread);
        await using var pool = CreatePoolReturning(cliAgent);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var service = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance);

        _ = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        countingStore.ListThreadsCallCount.Should().Be(
            0,
            "a live CLI/non-owning agent can never have an Agent-tool SubAgentManager roster, so the "
                + "persisted-provenance scan must be skipped for it just like a live MultiTurnAgentLoop");
    }

    [Fact]
    public async Task ListSubAgents_ReconstructsOrdinaryChildren_FromPersistedProvenance_ForRehydratedCollaborationOffLoop_WithEmptyManager()
    {
        // PRRT_kwDOOPysWM6V1mjj, the opposite-direction regression: after a restart/eviction, a fresh
        // collaboration-off MultiTurnAgentLoop is re-created with an empty SubAgentManager — `loop is
        // null` used to be false here (the loop IS live), so the cold-path scan was skipped and a
        // persisted ordinary child became invisible the moment the parent conversation became live again.
        const string childId = "rehydrated-child";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            $"subagent-{childId}",
            new ThreadMetadata
            {
                ThreadId = $"subagent-{childId}",
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    RootThread,
                    new SubAgentSnapshot(
                        childId,
                        Name: "beta",
                        TemplateName: "worker",
                        Task: "beta's task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: $"subagent-{childId}",
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow)),
            });

        // A collaboration-off MultiTurnAgentLoop that DOES own a SubAgentManager (subAgentOptions is
        // supplied), but is a brand-new instance that never spawned anything in this process — exactly
        // what GetOrCreateAgent rebuilds after a pool eviction/restart. Its SubAgentManager.ListAgents()
        // is empty, and Collaboration is null (the checked-in default), which is the one combination
        // live state cannot answer for on its own.
        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a worker.",
                    Description = "Does work.",
                    AgentFactory = BlockingProvider,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
        await using var rehydratedLoop = new MultiTurnAgentLoop(
            BlockingProvider(),
            new FunctionRegistry(),
            threadId: RootThread,
            subAgentOptions: subAgentOptions);
        await using var pool = CreatePoolReturning(rehydratedLoop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var service = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), store, NullLogger<AgentHierarchyService>.Instance);

        var (rows, isKnown, _) = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown.Should().BeTrue();
        var child = rows.Should().ContainSingle(s => s.AgentId == childId,
            "a rehydrated collaboration-off loop's empty SubAgentManager does not cover this persisted "
                + "child, so BuildAsync must still fall back to the cold-path provenance scan").Which;
        child.Name.Should().Be("beta");
        child.Template.Should().Be("worker");
    }

    [Fact]
    public async Task ScanPersistedSubAgentChildren_WarnsWhenTheThreadCapIsReached()
    {
        // Seeds exactly as many threads as the scan's cap so every page comes back full (200) and the
        // loop exhausts scanned == cap without a short final page ever triggering an early return —
        // the one path that reaches the "stopped at the cap" warning rather than silently returning
        // whatever it found. Without the warning an operator has no signal that the sub-agent listing
        // for a very long-lived store became incomplete.
        const int scanCap = 2000;
        var store = new InMemoryConversationStore();
        for (var i = 0; i < scanCap; i++)
        {
            var id = $"thread-cap-{i}";
            await store.SaveMetadataAsync(id, new ThreadMetadata { ThreadId = id, LastUpdated = i });
        }

        // No live loop for RootThread — the cold-path scan is what runs here.
        await using var pool = CreateFakeAgentPool();
        var logger = new CapturingLogger<AgentHierarchyService>();
        var service = new AgentHierarchyService(pool, new WorkflowRunRegistry(), store, logger);

        _ = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning
                && e.Message.Contains(RootThread, StringComparison.Ordinal)
                && e.Message.Contains(scanCap.ToString(), StringComparison.Ordinal),
            "hitting the scan cap must be observable, not a silent truncation");
    }

    [Fact]
    public async Task BuildAsync_PersistsAnUnmatchedGrandchild_SoItSurvivesARestart_AndItsTranscriptStaysReadable()
    {
        // Reproduces the #244 review finding at PRRT_kwDOOPysWM6V1ACd: a grandchild owned by a
        // CHILD's own SubAgentManager (not this conversation's own SubAgentManager) is invisible to
        // BuildAsync's own `summaries`/`workflowTabs` — only the shared collaboration directory knows
        // about it. Before the fix, the write-through to WorkflowRunRegistry only ever ran
        // AgentHierarchyProjection.Enrich() over those two lists, so the grandchild's row never made
        // it to disk: fine while the root loop stayed live (Project()'s own unmatched-node pass still
        // surfaced it), but gone the moment a restart replaced the root loop and its collaboration
        // directory with a fresh one containing only the root — exactly what phase 2 below simulates.
        const string childName = "child";
        const string grandchildName = "grandchild";
        var indexDirectory = Path.Combine(Path.GetTempPath(), "AgentHierarchyServiceTests-" + Guid.NewGuid().ToString("N"));

        var collaborationOptions = new AgentCollaborationOptions { MaxDelegationDepth = 2 };
        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a worker.",
                    Description = "Does work.",
                    AgentFactory = BlockingProvider,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
        var store = new InMemoryConversationStore();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

        string childId;
        string grandchildId;
        try
        {
            // Phase 1: a live root with a real spawned child, whose OWN manager spawns a real
            // grandchild — the exact shape the review describes. BuildAsync's write-through is what
            // is under test, so its return value here is only a sanity check that the LIVE path
            // already sees the grandchild (already covered by AgentHierarchyProjectionTests); the
            // real assertions are in phase 2, after persistence is the only thing left standing.
            var rootCollaboration = AgentCollaborationSetup.CreateRoot(
                collaborationOptions, collaborationId: "collab-1", agentId: "root-agent");
            await using var rootLoop = new MultiTurnAgentLoop(
                BlockingProvider(),
                new FunctionRegistry(),
                threadId: RootThread,
                subAgentOptions: subAgentOptions,
                collaboration: rootCollaboration);
            await using var pool1 = CreatePoolReturning(rootLoop);
            _ = pool1.GetOrCreateAgent(RootThread, mode);

            var registry1 = new WorkflowRunRegistry(indexDirectory);
            var service1 = new AgentHierarchyService(
                pool1, registry1, store, NullLogger<AgentHierarchyService>.Instance);

            childId = await SpawnAndResolveIdAsync(rootLoop.SubAgentTools!, childName);
            var childLoop = ChildLoop(rootLoop.SubAgentManager!, childId);
            grandchildId = await SpawnAndResolveIdAsync(childLoop.SubAgentTools!, grandchildName);

            var (rows1, _, _) = await service1.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);
            rows1.Should().Contain(
                r => r.AgentId == grandchildId,
                "the live directory already surfaces the grandchild for today's answer (Project's own "
                    + "unmatched-node pass) — this is not what's broken");

            // Phase 2: simulate a restart. A brand-new WorkflowRunRegistry instance re-reads the SAME
            // on-disk index, and a brand-new root loop gets a brand-new, otherwise-empty collaboration
            // directory (same ids, but nothing spawned in THIS process) — the root is the only node it
            // self-registers. Any answer about the grandchild from here on can only come from what
            // phase 1 persisted to disk.
            var rootCollaboration2 = AgentCollaborationSetup.CreateRoot(
                collaborationOptions, collaborationId: "collab-1", agentId: "root-agent");
            await using var rootLoop2 = new MultiTurnAgentLoop(
                BlockingProvider(),
                new FunctionRegistry(),
                threadId: RootThread,
                subAgentOptions: subAgentOptions,
                collaboration: rootCollaboration2);
            await using var pool2 = CreatePoolReturning(rootLoop2);
            _ = pool2.GetOrCreateAgent(RootThread, mode);

            var registry2 = new WorkflowRunRegistry(indexDirectory);
            var service2 = new AgentHierarchyService(
                pool2, registry2, store, NullLogger<AgentHierarchyService>.Instance);

            var (rows2, isKnown2, _) = await service2.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

            isKnown2.Should().BeTrue();
            var grandchildRow = rows2.Should().ContainSingle(r => r.AgentId == grandchildId,
                "the grandchild must have been written through to the durable index BEFORE the restart, "
                    + "not reconstructed from a live directory the fresh root never populated").Which;
            grandchildRow.ParentAgentId.Should().Be(childId);
            grandchildRow.AncestorAgentIds.Should().Equal("root-agent", childId);
            grandchildRow.StructuralDepth.Should().Be(2);
            grandchildRow.IsReadable.Should().BeTrue(
                "the root is a genuine ancestor of the persisted grandchild row, even though the fresh "
                    + "in-memory directory never heard of either the child or the grandchild");

            var transcript = await service2.ReadTranscriptAsync(
                RootThread, grandchildId, viewerAgentId: null, CancellationToken.None);
            transcript.Outcome.Should().Be(
                AgentTranscriptOutcome.Allowed,
                "a root transcript read for the grandchild must succeed after restart, not fail closed "
                    + "with unknown_target");
        }
        finally
        {
            if (Directory.Exists(indexDirectory))
            {
                Directory.Delete(indexDirectory, recursive: true);
            }
        }
    }

    private static object NewSpawn(string name, string subagentType = "worker") => new
    {
        subagent_type = subagentType,
        prompt = "work",
        role = "worker role",
        description = "Does a unit of work.",
        name,
        run_in_background = true,
    };

    private static async Task<string> SpawnAndResolveIdAsync(
        SubAgentToolProvider provider,
        string name,
        string subagentType = "worker")
    {
        var payload = await InvokeAsync(provider, "Agent", NewSpawn(name, subagentType));
        payload.IsError.Should().BeFalse(payload.Text);

        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>
    /// The loop the REAL spawn path built for <paramref name="agentId"/> — the only place the child
    /// actually runs, and (when collaboration is on) the only place its OWN SubAgentManager/SubAgentTools
    /// live, since every collaborating child is automatically given its own.
    /// </summary>
    private static MultiTurnAgentLoop ChildLoop(SubAgentManager manager, string agentId)
    {
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();
        return agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
    }

    private static async Task<ToolHandlerResultPayload> InvokeAsync(
        SubAgentToolProvider provider,
        string toolName,
        object args,
        CancellationToken ct = default)
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == toolName).Handler;
        var result = await handler(JsonSerializer.Serialize(args), new ToolCallContext(), ct);

        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private static MultiTurnAgentPool CreateFakeAgentPool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent) =>
        new((_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent), NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>
    /// A provider whose stream never yields and only unwinds on cancellation — keeps the loop's own
    /// implicit "run" state inert; this test never starts a run, but MultiTurnAgentLoop's constructor
    /// still requires a provider.
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
