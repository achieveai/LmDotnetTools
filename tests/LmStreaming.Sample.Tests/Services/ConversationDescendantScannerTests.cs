using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Tests for <see cref="ConversationDescendantScanner"/> — the persisted descendant-graph reader lifted
/// out of <c>ConversationsController.BuildDescendantTreeAsync</c> (issue #251) so the transcript writer
/// and the recursive HTTP listing share one traversal, and so the writer can poll without paying for a
/// full store scan every flush.
/// </summary>
public sealed class ConversationDescendantScannerTests
{
    private static ConversationDescendantScanner CreateScanner(IConversationStore store, int? capacity = null) =>
        capacity is null
            ? new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance)
            : new ConversationDescendantScanner(
                store,
                NullLogger<ConversationDescendantScanner>.Instance,
                capacity.Value);

    /// <summary>Seeds one persisted sub-agent thread stamped as <paramref name="parentThreadId"/>'s child.</summary>
    private static Task SeedChildAsync(
        IConversationStore store,
        string parentThreadId,
        string agentId,
        string childThreadId) =>
        store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    parentThreadId,
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

    private static MultiTurnAgentPool CreateFakeAgentPool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>
    /// The traversal contract lifted verbatim from the controller: transitive descendants only (the root
    /// is never a node), each stamped with its BFS depth, ordered by depth then parent then thread id.
    /// This is the regression anchor for "the extraction did not change behaviour".
    /// </summary>
    [Fact]
    public async Task ScanAsync_ReturnsTheTransitiveGraph_WithDepthsAndOrdering()
    {
        const string root = "thread-root";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(root, new ThreadMetadata { ThreadId = root, LastUpdated = 0 });
        await SeedChildAsync(store, root, "child-b", "subagent-b");
        await SeedChildAsync(store, root, "child-a", "subagent-a");
        await SeedChildAsync(store, "subagent-a", "grandchild", "subagent-a1");
        await SeedChildAsync(store, "subagent-a1", "great-grandchild", "subagent-a1x");
        // An unrelated tree that must not leak into this root's answer.
        await SeedChildAsync(store, "thread-other-root", "stranger", "subagent-stranger");

        var nodes = await CreateScanner(store).ScanAsync(root);

        nodes.Select(n => n.ThreadId).Should().Equal("subagent-a", "subagent-b", "subagent-a1", "subagent-a1x");
        nodes.Select(n => n.Depth).Should().Equal(1, 1, 2, 3);
        nodes.Should().NotContain(n => n.ThreadId == root);
    }

    /// <summary>A cycle in the persisted parent stamps must be cut, not looped on.</summary>
    [Fact]
    public async Task ScanAsync_CutsARepeatVisit_WhenPersistedParentStampsFormACycle()
    {
        const string root = "thread-cyclic-root";
        var store = new InMemoryConversationStore();
        await SeedChildAsync(store, root, "one", "subagent-one");
        // The root is itself stamped as its own descendant's child — the BFS must cut the repeat visit
        // (the root is seeded into the visited set) instead of looping forever.
        await SeedChildAsync(store, "subagent-one", "root-again", root);

        var nodes = await CreateScanner(store).ScanAsync(root);

        nodes.Select(n => n.ThreadId).Should().Equal("subagent-one");
        nodes.Select(n => n.Depth).Should().Equal(1);
    }

    /// <summary>
    /// The recursive HTTP listing must still answer exactly what the scanner computes — the controller is
    /// now only response shaping around this class.
    /// </summary>
    [Fact]
    public async Task ScanAsync_MatchesTheTreeTheRecursiveEndpointReturns()
    {
        const string root = "thread-parity-root";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(root, new ThreadMetadata { ThreadId = root, LastUpdated = 0 });
        await SeedChildAsync(store, root, "p-child", "subagent-p1");
        await SeedChildAsync(store, "subagent-p1", "p-grandchild", "subagent-p2");

        await using var pool = CreateFakeAgentPool();
        var scanner = CreateScanner(store);
        var controller = CreateController(store, pool, scanner);

        var scanned = await scanner.ScanAsync(root);
        var response = Assert.IsType<SubAgentTreeResponse>(
            Assert.IsType<OkObjectResult>(await controller.ListSubAgents(root, recursive: true)).Value);

        response.SchemaVersion.Should().Be(1);
        response.Nodes.Should().BeEquivalentTo(scanned, o => o.WithStrictOrdering());
    }

    /// <summary>An unknown root still 404s through the recursive branch after the extraction.</summary>
    [Fact]
    public async Task RecursiveEndpoint_StillReturns404_ForAnUnknownThread()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool, CreateScanner(store));

        var result = await controller.ListSubAgents("thread-never-existed", recursive: true);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetOrScanAsync_SecondCall_ReadsTheStoreOnlyOnce()
    {
        const string root = "thread-cached-root";
        var store = new InMemoryConversationStore();
        await SeedChildAsync(store, root, "cached-child", "subagent-cached");
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting);

        var first = await scanner.GetOrScanAsync(root);
        var callsAfterFirst = counting.ListThreadsCallCount;
        var second = await scanner.GetOrScanAsync(root);

        callsAfterFirst.Should().BeGreaterThan(0, "the cold scan must actually read the store");
        counting.ListThreadsCallCount.Should().Be(
            callsAfterFirst,
            "a cache hit must not touch the store at all");
        second.Should().BeEquivalentTo(first, o => o.WithStrictOrdering());
    }

    /// <summary>
    /// An empty answer is cached like any other. This is the exact behaviour that makes reusing
    /// <see cref="SubAgentScanCoverageCache"/> unsafe (it would ONLY ever cache, never refresh); here it
    /// is safe because <see cref="ConversationDescendantScanner.NoteAgentActivity"/> exists.
    /// </summary>
    [Fact]
    public async Task GetOrScanAsync_CachesAnEmptyGraph_AndDoesNotRescanUntilActivityIsNoted()
    {
        const string root = "thread-childless-root";
        var store = new InMemoryConversationStore();
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting);

        (await scanner.GetOrScanAsync(root)).Should().BeEmpty();
        var callsAfterFirst = counting.ListThreadsCallCount;

        // A child is spawned and persisted, but nobody told the scanner — the cached answer stands.
        await SeedChildAsync(store, root, "late-child", "subagent-late");
        (await scanner.GetOrScanAsync(root)).Should().BeEmpty();
        counting.ListThreadsCallCount.Should().Be(callsAfterFirst);

        scanner.NoteAgentActivity(root);
        var refreshed = await scanner.GetOrScanAsync(root);

        refreshed.Select(n => n.ThreadId).Should().Equal("subagent-late");
        counting.ListThreadsCallCount.Should().BeGreaterThan(callsAfterFirst);
    }

    [Fact]
    public async Task NoteAgentActivity_RefreshesOnlyTheNotedRoot()
    {
        const string notedRoot = "thread-noted-root";
        const string quietRoot = "thread-quiet-root";
        var store = new InMemoryConversationStore();
        await SeedChildAsync(store, notedRoot, "noted-child", "subagent-noted");
        await SeedChildAsync(store, quietRoot, "quiet-child", "subagent-quiet");
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting);

        _ = await scanner.GetOrScanAsync(notedRoot);
        _ = await scanner.GetOrScanAsync(quietRoot);
        var callsAfterCold = counting.ListThreadsCallCount;

        scanner.NoteAgentActivity(notedRoot);
        _ = await scanner.GetOrScanAsync(quietRoot);
        counting.ListThreadsCallCount.Should().Be(
            callsAfterCold,
            "activity on one root must not invalidate another root's graph");

        _ = await scanner.GetOrScanAsync(notedRoot);
        counting.ListThreadsCallCount.Should().BeGreaterThan(callsAfterCold);
    }

    /// <summary>
    /// A refresh signalled WHILE a scan is in flight must not be swallowed by that scan's own write —
    /// otherwise the writer's "I just saw a new agent" notice is lost and the new child stays invisible.
    /// </summary>
    [Fact]
    public async Task NoteAgentActivity_DuringAnInFlightScan_StillForcesTheNextCallToRescan()
    {
        const string root = "thread-inflight-root";
        var store = new InMemoryConversationStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = CreateScanner(new BlockingListThreadsStore(store, gate.Task));

        var inFlight = scanner.GetOrScanAsync(root);
        scanner.NoteAgentActivity(root);
        gate.SetResult();
        (await inFlight).Should().BeEmpty();

        // The in-flight scan's answer predates the notice, so it must not have been recorded as fresh:
        // the next call has to rescan and see the child that arrived with that activity.
        await SeedChildAsync(store, root, "raced-child", "subagent-raced");

        var afterNotice = await scanner.GetOrScanAsync(root);

        afterNotice.Select(n => n.ThreadId).Should().Equal("subagent-raced");
    }

    [Fact]
    public async Task Forget_DropsTheRootsGraph_SoTheNextCallRescans()
    {
        const string root = "thread-forgotten-root";
        var store = new InMemoryConversationStore();
        await SeedChildAsync(store, root, "forgotten-child", "subagent-forgotten");
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting);

        _ = await scanner.GetOrScanAsync(root);
        var callsAfterCold = counting.ListThreadsCallCount;

        scanner.Forget(root);
        _ = await scanner.GetOrScanAsync(root);

        counting.ListThreadsCallCount.Should().BeGreaterThan(callsAfterCold);
    }

    /// <summary>
    /// The composition root wires <c>MultiTurnAgentPool.ThreadRemoved</c> to <c>Forget</c> (the pool is
    /// deliberately NOT a constructor dependency). This proves that wiring actually invalidates, and that
    /// a reused thread id cannot inherit the removed conversation's descendants.
    /// </summary>
    [Fact]
    public async Task ThreadRemoved_InvalidatesTheCachedGraph_WhenWiredTheWayTheHostWiresIt()
    {
        const string root = "thread-removed-root";
        var store = new InMemoryConversationStore();
        await SeedChildAsync(store, root, "removed-child", "subagent-removed");
        var scanner = CreateScanner(store);

        await using var pool = CreateFakeAgentPool();
        pool.ThreadRemoved += scanner.Forget;
        _ = pool.GetOrCreateAgent(root, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        (await scanner.GetOrScanAsync(root)).Should().ContainSingle(n => n.ThreadId == "subagent-removed");

        // The conversation goes away, its persisted child with it, and the id is later reused.
        await store.DeleteThreadAsync("subagent-removed", CancellationToken.None);
        await pool.RemoveAgentAsync(root);

        (await scanner.GetOrScanAsync(root)).Should().BeEmpty(
            "ThreadRemoved must drop the cached graph so a reused thread id cannot inherit it");
    }

    [Fact]
    public async Task Cache_EvictsTheLeastRecentlyWrittenRoot_WhenCapacityIsExceeded()
    {
        var store = new InMemoryConversationStore();
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting, capacity: 2);

        _ = await scanner.GetOrScanAsync("root-1");
        _ = await scanner.GetOrScanAsync("root-2");
        _ = await scanner.GetOrScanAsync("root-3");
        var callsAfterThree = counting.ListThreadsCallCount;

        // root-2 and root-3 are still resident; root-1 was evicted and must rescan.
        _ = await scanner.GetOrScanAsync("root-3");
        _ = await scanner.GetOrScanAsync("root-2");
        counting.ListThreadsCallCount.Should().Be(callsAfterThree);

        _ = await scanner.GetOrScanAsync("root-1");
        counting.ListThreadsCallCount.Should().BeGreaterThan(callsAfterThree);
    }

    /// <summary>
    /// Pins the retention policy the class documents: LEAST RECENTLY USED, not insertion order. The two
    /// differ exactly when an early root is still being used — under insertion order it is evicted anyway,
    /// so the roots that are actually mirroring are the ones thrown out while an idle conversation that
    /// merely happens to be newer survives, and the eviction lands where it costs the most. Losing an entry
    /// only costs one extra scan, which is why this is worth an O(1) splice and not more.
    /// </summary>
    [Fact]
    public async Task Cache_RenewsARootOnAccess_SoAnActiveEarlyRootOutlivesAnIdleLaterOne()
    {
        var store = new InMemoryConversationStore();
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting, capacity: 2);

        _ = await scanner.GetOrScanAsync("root-1");
        _ = await scanner.GetOrScanAsync("root-2");

        // root-1 is used again, which makes root-2 the least recently used of the two.
        _ = await scanner.GetOrScanAsync("root-1");
        _ = await scanner.GetOrScanAsync("root-3");
        var callsAfterThree = counting.ListThreadsCallCount;

        // The renewed root survived and serves from cache...
        _ = await scanner.GetOrScanAsync("root-1");
        counting.ListThreadsCallCount.Should().Be(callsAfterThree);

        // ...and the one that went untouched longest is the one that had to be rescanned.
        _ = await scanner.GetOrScanAsync("root-2");
        counting.ListThreadsCallCount.Should().BeGreaterThan(callsAfterThree);
    }

    /// <summary>
    /// The same renewal, applied consistently: reported agent activity is USE. It is the strongest possible
    /// signal that a conversation is live — the caller only raises it when it actually saw an Agent tool
    /// run — so a root that is spawning sub-agents right now must not be the next one evicted just because
    /// its last read was a while ago. The invalidation still costs the next reader one scan; what it must
    /// not cost is the slot itself.
    /// </summary>
    [Fact]
    public async Task Cache_RenewsARootOnReportedActivity_SoALiveRootIsNotEvictedForAnIdleOne()
    {
        var store = new InMemoryConversationStore();
        var counting = new CountingConversationStore(store);
        var scanner = CreateScanner(counting, capacity: 2);

        _ = await scanner.GetOrScanAsync("root-1");
        _ = await scanner.GetOrScanAsync("root-2");

        scanner.NoteAgentActivity("root-1");
        _ = await scanner.GetOrScanAsync("root-3");

        // root-1 was renewed by the activity report, so root-2 is the one that went.
        var callsBefore = counting.ListThreadsCallCount;
        _ = await scanner.GetOrScanAsync("root-2");
        counting.ListThreadsCallCount.Should().BeGreaterThan(callsBefore);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCapacity()
    {
        var act = () => CreateScanner(new InMemoryConversationStore(), capacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ConversationsController CreateController(
        IConversationStore store,
        MultiTurnAgentPool pool,
        ConversationDescendantScanner scanner) =>
        new(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(store, new InMemoryConversationStore()),
            TimeProvider.System,
            new WorkflowRunRegistry(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            scanner);

    /// <summary>Holds the first <c>ListThreadsAsync</c> open until a gate completes, so a test can signal
    /// a refresh while a scan is genuinely in flight.</summary>
    private sealed class BlockingListThreadsStore(IConversationStore inner, Task gate) : IConversationStore
    {
        private int _calls;

        public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await gate;
            }

            return await inner.ListThreadsAsync(limit, offset, ct);
        }

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
    }
}
