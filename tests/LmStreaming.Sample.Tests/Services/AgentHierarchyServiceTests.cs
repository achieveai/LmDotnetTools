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
            pool,
            new WorkflowRunRegistry(),
            countingStore,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache());

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
            pool, new WorkflowRunRegistry(), store, NullLogger<AgentHierarchyService>.Instance, new SubAgentScanCoverageCache());

        var (rows, isKnown, _) = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown.Should().BeTrue();
        var child = rows.Should().ContainSingle(s => s.AgentId == childId).Which;
        child.Name.Should().Be("alpha");
        child.Template.Should().Be("worker");
    }

    /// <summary>
    /// The twin of
    /// <c>ConversationDescendantScannerTests.ScanAsync_ListsEveryChild_WhenAThreadIsTouchedWhileTheScanIsRunning</c>,
    /// against the flat cold-path scan. A child touched while the scan runs must still be listed:
    /// <see cref="IConversationStore.ListThreadsAsync(int, int, CancellationToken)"/> orders by a MUTABLE column, so an offset-paged
    /// scan lets a thread slide forward past an offset it has already stepped over. Here the loss is
    /// permanent by construction — <see cref="SubAgentScanCoverageCache"/> records what the scan
    /// recovered and keeps it for the process lifetime, so the skipped child is never reconsidered.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ListsEveryPersistedChild_WhenAThreadIsTouchedWhileTheScanIsRunning()
    {
        // More children than the pre-fix 200-row page, so the scan has a page boundary at all.
        const int childCount = 250;
        // A child that a paged scan has NOT read yet when the touch happens (it sorts into page 2).
        const string touchedAgentId = "child-010";
        var touchedThreadId = $"subagent-{touchedAgentId}";

        var store = new InMemoryConversationStore();
        for (var i = 0; i < childCount; i++)
        {
            var agentId = $"child-{i:D3}";
            var childThreadId = $"subagent-{agentId}";
            await store.SaveMetadataAsync(
                childThreadId,
                new ThreadMetadata
                {
                    ThreadId = childThreadId,
                    // Distinct stamps so "ordered by last updated descending" is unambiguous.
                    LastUpdated = i + 1,
                    Properties = SubAgentProvenance.Build(
                        RootThread,
                        new SubAgentSnapshot(
                            agentId,
                            Name: agentId,
                            TemplateName: "worker",
                            Task: $"task for {agentId}",
                            Status: SubAgentStatus.Completed,
                            ThreadId: childThreadId,
                            LastActivityUtc: DateTimeOffset.UtcNow,
                            TerminalAtUtc: DateTimeOffset.UtcNow)),
                });
        }

        // No live loop for RootThread, so the cold-path persisted scan is the only route to these children.
        await using var pool = CreateFakeAgentPool();
        var service = new AgentHierarchyService(
            pool,
            new WorkflowRunRegistry(),
            new TouchingConversationStore(store, touchedThreadId),
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache());

        var (rows, _, _) = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        rows.Select(r => r.AgentId)
            .Should()
            .Contain(
                touchedAgentId,
                "a child that was merely touched mid-scan must still be listed — what this scan recovers "
                    + "is recorded in the coverage cache for the process lifetime, so a skip is permanent");
        rows.Should().HaveCount(childCount);
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
            pool,
            new WorkflowRunRegistry(),
            countingStore,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache());

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
            pool, new WorkflowRunRegistry(), store, NullLogger<AgentHierarchyService>.Instance, new SubAgentScanCoverageCache());

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
        // Seeds ONE MORE thread than the scan's cap, which is exactly what truncation is: the scan asks for
        // cap + 1 and gets it back full, so it knows a thread it will not look at exists. Without the
        // warning an operator has no signal that the sub-agent listing for a very long-lived store became
        // incomplete — and the incomplete roster is then cached for the process lifetime.
        const int scanCap = 2000;
        var store = new InMemoryConversationStore();
        for (var i = 0; i <= scanCap; i++)
        {
            var id = $"thread-cap-{i}";
            await store.SaveMetadataAsync(id, new ThreadMetadata { ThreadId = id, LastUpdated = i });
        }

        // No live loop for RootThread — the cold-path scan is what runs here.
        await using var pool = CreateFakeAgentPool();
        var logger = new CapturingLogger<AgentHierarchyService>();
        var service = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), store, logger, new SubAgentScanCoverageCache());

        _ = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning
                && e.Message.Contains(RootThread, StringComparison.Ordinal)
                && e.Message.Contains(scanCap.ToString(), StringComparison.Ordinal),
            "hitting the scan cap must be observable, not a silent truncation");
    }

    [Fact]
    public async Task ScanPersistedSubAgentChildren_DoesNotWarnWhenTheStoreHoldsExactlyTheCap()
    {
        // The other side of the boundary, and the reason the scan asks for cap + 1 rather than cap: a store
        // holding exactly the cap is read COMPLETELY, so warning about it would be a false alarm — and a
        // truncation warning that fires when nothing was truncated is one an operator learns to ignore.
        const int scanCap = 2000;
        var store = new InMemoryConversationStore();
        for (var i = 0; i < scanCap; i++)
        {
            var id = $"thread-cap-{i}";
            await store.SaveMetadataAsync(id, new ThreadMetadata { ThreadId = id, LastUpdated = i });
        }

        await using var pool = CreateFakeAgentPool();
        var logger = new CapturingLogger<AgentHierarchyService>();
        var service = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), store, logger, new SubAgentScanCoverageCache());

        _ = await service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("cap", StringComparison.Ordinal),
            "the store was read in full, so there is nothing to warn about");
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
                pool1, registry1, store, NullLogger<AgentHierarchyService>.Instance, new SubAgentScanCoverageCache());

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
                pool2, registry2, store, NullLogger<AgentHierarchyService>.Instance, new SubAgentScanCoverageCache());

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

    [Fact]
    public async Task BuildAsync_ForAGenuinelyChildlessRehydratedCollaborationOffLoop_ScansAtMostOnce_AndReusesEmptyResult()
    {
        // PRRT_kwDOOPysWM6V1mjj: before SubAgentScanCoverageCache existed, a genuinely childless
        // collaboration-off loop's empty SubAgentManager re-triggered ScanPersistedSubAgentChildrenAsync
        // on EVERY request — the old gate was "is the manager empty right now", not "have I already
        // covered this thread" — so a conversation that will never have anything to show still paid for
        // a bounded-but-still-expensive up-to-2000-thread store scan on every single hierarchy poll.
        var countingStore = new CountingConversationStore(new InMemoryConversationStore());
        var sharedCache = new SubAgentScanCoverageCache();

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
        await using var loop = new MultiTurnAgentLoop(
            BlockingProvider(), new FunctionRegistry(), threadId: RootThread, subAgentOptions: subAgentOptions);
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        // Each poll gets its OWN AgentHierarchyService instance — mirrors production, where the service
        // is constructed fresh per HTTP request/tool call rather than resolved from DI — sharing only the
        // cache/pool/store, exactly like two real requests would.
        var service1 = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows1, isKnown1, _) = await service1.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown1.Should().BeTrue();
        rows1.Should().BeEmpty();
        countingStore.ListThreadsCallCount.Should().Be(
            1, "the first poll must scan once to confirm there is truly nothing persisted for this thread");

        var service2 = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows2, isKnown2, _) = await service2.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown2.Should().BeTrue();
        rows2.Should().BeEmpty();
        countingStore.ListThreadsCallCount.Should().Be(
            1,
            "a repeated poll against a genuinely childless loop must reuse the cached empty result "
                + "instead of rescanning the store");
    }

    [Fact]
    public async Task BuildAsync_UnionsTheRecoveredRoster_WithANewlySpawnedLiveChild_AfterTheEmptyToPopulatedTransition()
    {
        // PRRT_kwDOOPysWM6V1mjj's core reopened complaint: a rehydrated collaboration-off loop recovers
        // an old persisted child on its first (empty-manager) poll. Before this fix, the moment it
        // spawned ONE new child, SubAgentManager.ListAgents().Count became nonempty, the cold-path scan
        // was skipped on every later poll (the old gate reasoned "the live manager already covers
        // everything now"), and the OLD recovered child vanished from the response — only the new live
        // child remained visible, even though ordinary collaboration-off rows are never write-through-
        // persisted into WorkflowRunRegistry. The cache must retain the recovered roster across that
        // transition and union it with the live rows.
        const string oldChildId = "old-recovered-child";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            $"subagent-{oldChildId}",
            new ThreadMetadata
            {
                ThreadId = $"subagent-{oldChildId}",
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    RootThread,
                    new SubAgentSnapshot(
                        oldChildId,
                        Name: "old-child",
                        TemplateName: "worker",
                        Task: "old child's task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: $"subagent-{oldChildId}",
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow)),
            });
        var countingStore = new CountingConversationStore(store);
        var sharedCache = new SubAgentScanCoverageCache();

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
            BlockingProvider(), new FunctionRegistry(), threadId: RootThread, subAgentOptions: subAgentOptions);
        await using var pool = CreatePoolReturning(rehydratedLoop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        // First poll: empty manager, recovers the old child via the persisted-provenance cold scan.
        var service1 = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows1, _, _) = await service1.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);
        rows1.Select(r => r.AgentId).Should().BeEquivalentTo([oldChildId]);
        countingStore.ListThreadsCallCount.Should().Be(1);

        // The SAME live loop now spawns a brand-new child — SubAgentManager.ListAgents() becomes
        // nonempty for the first time in this process.
        var newChildId = await SpawnAndResolveIdAsync(rehydratedLoop.SubAgentTools!, "new-child");

        // Second poll: a fresh AgentHierarchyService instance (a fresh HTTP request), sharing only the
        // same cache/pool/store.
        var service2 = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows2, isKnown2, _) = await service2.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown2.Should().BeTrue();
        rows2.Select(r => r.AgentId).Should().BeEquivalentTo(
            [oldChildId, newChildId],
            "both the cached recovered roster AND the newly spawned live child must appear together");
        countingStore.ListThreadsCallCount.Should().Be(
            1,
            "the manager becoming nonempty must not trigger a rescan — the recovered roster is served "
                + "from the cache, and the new child comes from the live SubAgentManager snapshot");
    }

    [Fact]
    public async Task BuildAsync_RecoversPersistedChildren_AcrossTwoConsecutiveManagerResetCycles_WithSpawnAndPersistBetweenEach()
    {
        // PR #245 review (HIGH): owner-keyed invalidation must not just handle a SINGLE reset — a mode
        // switch followed later by a provider switch (or a restart, or a pool eviction+reopen) is a
        // SEQUENCE of resets, and a child spawned+persisted in an EARLIER generation must still be
        // recoverable via the cold-path scan after MULTIPLE subsequent resets, not just the first one.
        // Three generations (two resets) of a brand-new MultiTurnAgentLoop/SubAgentManager simulate that
        // sequence; a real child is spawned and its provenance persisted in each of the first two
        // generations (mirroring what the production NonOwningConversationStore decorator would do,
        // which this unit test wires manually since it isn't part of this test's store).
        var store = new InMemoryConversationStore();
        var countingStore = new CountingConversationStore(store);
        var sharedCache = new SubAgentScanCoverageCache();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
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

        // --- Generation 1: a live root loop with its own fresh SubAgentManager. ---
        await using var loop1 = new MultiTurnAgentLoop(
            BlockingProvider(), new FunctionRegistry(), threadId: RootThread, subAgentOptions: subAgentOptions);
        await using var pool1 = CreatePoolReturning(loop1);
        _ = pool1.GetOrCreateAgent(RootThread, mode);

        var gen1ChildId = await SpawnAndResolveIdAsync(loop1.SubAgentTools!, "gen1-child");
        await PersistProvenanceAsync(store, gen1ChildId, "gen1-child");

        var service1 = new AgentHierarchyService(
            pool1, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows1, isKnown1, _) = await service1.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown1.Should().BeTrue();
        rows1.Select(r => r.AgentId).Should().Contain(gen1ChildId);
        countingStore.ListThreadsCallCount.Should().Be(
            1, "the first-ever poll for generation 1's owner must scan once");

        // --- Reset 1 (e.g. a mode switch): a brand-new loop/SubAgentManager for the SAME threadId. ---
        await using var loop2 = new MultiTurnAgentLoop(
            BlockingProvider(), new FunctionRegistry(), threadId: RootThread, subAgentOptions: subAgentOptions);
        await using var pool2 = CreatePoolReturning(loop2);
        _ = pool2.GetOrCreateAgent(RootThread, mode);

        var service2 = new AgentHierarchyService(
            pool2, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows2, isKnown2, _) = await service2.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown2.Should().BeTrue();
        rows2.Select(r => r.AgentId).Should().BeEquivalentTo(
            [gen1ChildId],
            "generation 1's persisted child must surface via the cold-path scan after the FIRST reset, "
                + "since generation 2's fresh manager never spawned it itself");
        countingStore.ListThreadsCallCount.Should().Be(
            2,
            "generation 2's owner has never been seen before, so the FIRST reset forces exactly one "
                + "fresh rescan rather than reusing generation 1's cached entry");

        // Spawn+persist a second child under generation 2's own (still-live) manager.
        var gen2ChildId = await SpawnAndResolveIdAsync(loop2.SubAgentTools!, "gen2-child");
        await PersistProvenanceAsync(store, gen2ChildId, "gen2-child");

        var service2b = new AgentHierarchyService(
            pool2, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows2b, _, _) = await service2b.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        rows2b.Select(r => r.AgentId).Should().BeEquivalentTo(
            [gen1ChildId, gen2ChildId],
            "a repeated poll under the SAME (still-live) generation-2 owner must union the cached "
                + "recovered roster with the newly spawned live child");
        countingStore.ListThreadsCallCount.Should().Be(
            2, "a repeated poll under the SAME owner must not trigger another rescan");

        // --- Reset 2 (e.g. a later restart): a SECOND independent reset — another brand-new manager
        // instance, distinct from BOTH generation 1 and generation 2. ---
        await using var loop3 = new MultiTurnAgentLoop(
            BlockingProvider(), new FunctionRegistry(), threadId: RootThread, subAgentOptions: subAgentOptions);
        await using var pool3 = CreatePoolReturning(loop3);
        _ = pool3.GetOrCreateAgent(RootThread, mode);

        var service3 = new AgentHierarchyService(
            pool3, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        var (rows3, isKnown3, _) = await service3.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown3.Should().BeTrue();
        rows3.Select(r => r.AgentId).Should().BeEquivalentTo(
            [gen1ChildId, gen2ChildId],
            "both earlier generations' persisted children must survive the SECOND reset cycle too — "
                + "owner-keyed invalidation must keep working across repeated resets, not just the first one");
        countingStore.ListThreadsCallCount.Should().Be(
            3, "the SECOND reset's brand-new owner again forces exactly one fresh rescan");
    }

    [Fact]
    public async Task BuildAsync_DoesNotPoisonTheCache_WhenTheScanFails_SoARetryCanStillRecoverTheChild()
    {
        // Requirement: cancellation/failure of the underlying scan must not leave the thread's coverage
        // permanently (and incorrectly) marked as "covered with nothing". GetOrScanPersistedSubAgentChildrenAsync
        // only calls RecordRecovered AFTER ScanPersistedSubAgentChildrenAsync completes successfully, so a
        // throwing/cancelled attempt must leave the thread uncached for the next caller to retry.
        const string childId = "recovered-after-retry";
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
                        Name: "child",
                        TemplateName: "worker",
                        Task: "task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: $"subagent-{childId}",
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow)),
            });
        var flakyStore = new FlakyConversationStore(store) { FailNextCall = true };
        var sharedCache = new SubAgentScanCoverageCache();

        await using var pool = CreateFakeAgentPool();

        var service1 = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), flakyStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service1.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None));

        flakyStore.ListThreadsCallCount.Should().Be(1);

        // Retry via a fresh service instance sharing the same cache — must NOT be poisoned by the failed
        // attempt: it must scan again and recover the persisted child this time.
        var service2 = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), flakyStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);

        var (rows, isKnown, _) = await service2.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        isKnown.Should().BeTrue();
        rows.Should().ContainSingle(r => r.AgentId == childId);
        flakyStore.ListThreadsCallCount.Should().Be(
            2, "the retry after a failed scan must actually rescan, not reuse a poisoned/missing cache entry");
    }

    [Fact]
    public async Task BuildAsync_SupportsConcurrentCallsForTheSameThread_WithoutCorruptingTheCache()
    {
        // Requirement: the cache must be concurrency-safe for simultaneous BuildAsync calls. Redundant
        // concurrent scans racing before the first one records its result are tolerated (harmless
        // duplicate work) rather than requiring exactly-once semantics, but the cache must still settle:
        // once any of the racing scans has recorded a result, every later call must reuse it.
        var countingStore = new CountingConversationStore(new InMemoryConversationStore());
        var sharedCache = new SubAgentScanCoverageCache();

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
        await using var loop = new MultiTurnAgentLoop(
            BlockingProvider(), new FunctionRegistry(), threadId: RootThread, subAgentOptions: subAgentOptions);
        await using var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(RootThread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        // Ten concurrent "requests", each its own fresh AgentHierarchyService instance sharing the same
        // cache/pool/store — mirrors ten simultaneous HTTP polls against the same live conversation.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ =>
            {
                var service = new AgentHierarchyService(
                    pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
                return service.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsKnown && r.Rows.Count == 0);

        var settledCallCount = countingStore.ListThreadsCallCount;
        settledCallCount.Should().BeGreaterThan(0);

        var followUpService = new AgentHierarchyService(
            pool, new WorkflowRunRegistry(), countingStore, NullLogger<AgentHierarchyService>.Instance, sharedCache);
        _ = await followUpService.BuildAsync(RootThread, viewerAgentId: null, CancellationToken.None);

        countingStore.ListThreadsCallCount.Should().Be(
            settledCallCount, "once any concurrent scan has recorded a result, the cache must be settled and reused");
    }

    /// <summary>
    /// A forwarding <see cref="IConversationStore"/> whose <see cref="ListThreadsAsync"/> throws on the
    /// next call when <see cref="FailNextCall"/> is set — simulates a transient scan failure so a test
    /// can prove <see cref="SubAgentScanCoverageCache"/> is not poisoned by it.
    /// </summary>
    private sealed class FlakyConversationStore(IConversationStore inner) : IConversationStore
    {
        private int _listThreadsCalls;

        public bool FailNextCall { get; set; }

        public int ListThreadsCallCount => Volatile.Read(ref _listThreadsCalls);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(
            string threadId,
            ThreadMetadata metadata,
            CancellationToken ct = default) => inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default) => inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listThreadsCalls);
            if (FailNextCall)
            {
                FailNextCall = false;
                throw new InvalidOperationException("Simulated transient scan failure.");
            }

            return inner.ListThreadsAsync(limit, offset, ct);
        }
    }

    /// <summary>
    /// Persists <paramref name="agentId"/>'s <see cref="SubAgentProvenance"/> directly to
    /// <paramref name="store"/> under <see cref="RootThread"/> — what the production
    /// <c>NonOwningConversationStore</c> decorator stamps onto a spawned child's own metadata writes
    /// automatically, reproduced by hand here since these unit tests spawn children through a plain
    /// <see cref="InMemoryConversationStore"/> with no such decorator wired.
    /// </summary>
    private static Task PersistProvenanceAsync(IConversationStore store, string agentId, string name) =>
        store.SaveMetadataAsync(
            $"subagent-{agentId}",
            new ThreadMetadata
            {
                ThreadId = $"subagent-{agentId}",
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    RootThread,
                    new SubAgentSnapshot(
                        agentId,
                        Name: name,
                        TemplateName: "worker",
                        Task: $"{name}'s task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: $"subagent-{agentId}",
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow)),
            });

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
