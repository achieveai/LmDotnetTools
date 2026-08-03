using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
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
