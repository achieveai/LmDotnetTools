using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Direct unit coverage for <see cref="SubAgentScanCoverageCache"/>'s owner/generation keying and
/// bounded-retention policy (PR #245 review — HIGH/MEDIUM findings on the cache introduced for
/// PRRT_kwDOOPysWM6V1mjj). <see cref="AgentHierarchyServiceTests"/> covers these same guarantees
/// end-to-end through real <c>MultiTurnAgentLoop</c>/<c>SubAgentManager</c> resets; this file isolates
/// the cache's own bookkeeping (owner-mismatch miss, delete eviction, capacity eviction) so those
/// invariants have a fast, precise, non-integration proof independent of the loop plumbing.
/// </summary>
public sealed class SubAgentScanCoverageCacheTests
{
    private static IReadOnlyList<SubAgentSummary> Rows(params string[] agentIds) =>
        [.. agentIds.Select(id => new SubAgentSummary
        {
            AgentId = id,
            Template = "worker",
            Task = "task",
            Status = "completed",
            ThreadId = $"subagent-{id}",
        })];

    [Fact]
    public void TryGetRecovered_ReturnsFalse_WhenNoEntryWasEverRecorded()
    {
        var cache = new SubAgentScanCoverageCache();

        var hit = cache.TryGetRecovered("thread-1", owner: new object(), out var rows);

        hit.Should().BeFalse();
        rows.Should().BeEmpty();
    }

    [Fact]
    public void TryGetRecovered_ReturnsTrue_ForTheSameThreadAndOwner_ThatRecordRecoveredWasCalledWith()
    {
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();

        cache.RecordRecovered("thread-1", owner, Rows("child-a"));

        var hit = cache.TryGetRecovered("thread-1", owner, out var rows);

        hit.Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["child-a"]);
    }

    [Fact]
    public void TryGetRecovered_ReturnsFalse_WhenTheOwnerDiffers_EvenThoughTheThreadIdMatches()
    {
        // This is the core PR #245 fix: a manager reset (mode switch, provider switch, restart,
        // pool eviction+reopen) constructs a brand-new SubAgentManager, so the caller resolves a
        // brand-new owner reference for the SAME threadId. The old entry must not be served to it.
        var cache = new SubAgentScanCoverageCache();
        var originalOwner = new object();
        var ownerAfterReset = new object();

        cache.RecordRecovered("thread-1", originalOwner, Rows("child-a"));

        var hitWithNewOwner = cache.TryGetRecovered("thread-1", ownerAfterReset, out var rows);

        hitWithNewOwner.Should().BeFalse(
            "a different owner reference means the live manager was reset since the entry was recorded");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void RecordRecovered_OverwritesThePriorOwner_SoOnlyTheNewestGenerationIsServed_AcrossMultipleResets()
    {
        // Simulates several consecutive manager-reset cycles against the same thread id (mode switch,
        // then provider switch, then a later restart) — every RecordRecovered call after a reset must
        // fully replace the previous generation's entry, not accumulate alongside it.
        var cache = new SubAgentScanCoverageCache();
        var generation1 = new object();
        var generation2 = new object();
        var generation3 = new object();

        cache.RecordRecovered("thread-1", generation1, Rows("gen1-child"));
        cache.RecordRecovered("thread-1", generation2, Rows("gen1-child", "gen2-child"));
        cache.RecordRecovered("thread-1", generation3, Rows("gen1-child", "gen2-child", "gen3-child"));

        cache.TryGetRecovered("thread-1", generation1, out _).Should().BeFalse(
            "generation 1's owner reference no longer matches the recorded entry after two more resets");
        cache.TryGetRecovered("thread-1", generation2, out _).Should().BeFalse(
            "generation 2's owner reference no longer matches the recorded entry after the third reset");

        var hit = cache.TryGetRecovered("thread-1", generation3, out var rows);
        hit.Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["gen1-child", "gen2-child", "gen3-child"]);
    }

    [Fact]
    public void RecordRecovered_ThenTryGetRecovered_StillHits_WhenCalledRepeatedlyWithTheSameOwner()
    {
        // The same-manager empty-to-populated transition (PRRT_kwDOOPysWM6V1mjj) must keep working: as
        // long as the owner does not change, repeated polls (even with a differently-shaped rows list
        // recorded later, e.g. after the live manager gained a child) must not spuriously miss.
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();

        cache.RecordRecovered("thread-1", owner, []);
        cache.TryGetRecovered("thread-1", owner, out var emptyRows).Should().BeTrue();
        emptyRows.Should().BeEmpty();

        cache.RecordRecovered("thread-1", owner, Rows("child-a"));
        var hit = cache.TryGetRecovered("thread-1", owner, out var rows);

        hit.Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["child-a"]);
    }

    [Fact]
    public void Forget_RemovesTheEntry_RegardlessOfOwner_SoAReusedThreadIdAlwaysMisses()
    {
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();
        cache.RecordRecovered("thread-1", owner, Rows("child-a"));

        cache.Forget("thread-1");

        cache.TryGetRecovered("thread-1", owner, out var rows).Should().BeFalse(
            "Forget must evict the entry outright, not merely require a different owner");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void Forget_OnAnUnknownThreadId_IsANoOp()
    {
        var cache = new SubAgentScanCoverageCache();

        var act = () => cache.Forget("never-recorded");

        act.Should().NotThrow();
    }

    [Fact]
    public void RecordRecovered_EvictsTheOldestEntryByLastWrite_OnceCapacityIsExceeded()
    {
        var cache = new SubAgentScanCoverageCache(capacity: 3);
        var owner1 = new object();
        var owner2 = new object();
        var owner3 = new object();
        var owner4 = new object();

        cache.RecordRecovered("thread-1", owner1, Rows("a"));
        cache.RecordRecovered("thread-2", owner2, Rows("b"));
        cache.RecordRecovered("thread-3", owner3, Rows("c"));

        // Fourth distinct thread pushes the tracked-thread count past capacity — thread-1 (the oldest
        // by last write) must be evicted first, deterministically, not thread-2 or thread-3.
        cache.RecordRecovered("thread-4", owner4, Rows("d"));

        cache.TryGetRecovered("thread-1", owner1, out _).Should().BeFalse(
            "thread-1 was the oldest entry by last write and must be evicted once capacity is exceeded");
        cache.TryGetRecovered("thread-2", owner2, out var rows2).Should().BeTrue();
        rows2.Select(r => r.AgentId).Should().BeEquivalentTo(["b"]);
        cache.TryGetRecovered("thread-3", owner3, out var rows3).Should().BeTrue();
        rows3.Select(r => r.AgentId).Should().BeEquivalentTo(["c"]);
        cache.TryGetRecovered("thread-4", owner4, out var rows4).Should().BeTrue();
        rows4.Select(r => r.AgentId).Should().BeEquivalentTo(["d"]);
    }

    [Fact]
    public void RecordRecovered_EvictsDeterministically_ByLastWriteOrder_NotInsertionOrderOfSurvivors()
    {
        var cache = new SubAgentScanCoverageCache(capacity: 2);
        var owner1 = new object();
        var owner2 = new object();
        var owner3 = new object();

        cache.RecordRecovered("thread-1", owner1, Rows("a"));
        cache.RecordRecovered("thread-2", owner2, Rows("b"));

        // Re-writing thread-1 (same owner, e.g. the live manager gained a new child) bumps it to
        // most-recently-written, so thread-2 — now the oldest — is the one evicted next, not thread-1.
        cache.RecordRecovered("thread-1", owner1, Rows("a", "a2"));
        cache.RecordRecovered("thread-3", owner3, Rows("c"));

        cache.TryGetRecovered("thread-1", owner1, out var rows1).Should().BeTrue(
            "thread-1 was re-written after thread-2, so it should survive the eviction thread-2 triggers");
        rows1.Select(r => r.AgentId).Should().BeEquivalentTo(["a", "a2"]);

        cache.TryGetRecovered("thread-2", owner2, out _).Should().BeFalse(
            "thread-2 became the oldest entry by last write once thread-1 was re-recorded, so capacity "
                + "eviction removes it, not thread-1");

        cache.TryGetRecovered("thread-3", owner3, out var rows3).Should().BeTrue();
        rows3.Select(r => r.AgentId).Should().BeEquivalentTo(["c"]);
    }

    [Fact]
    public void Constructor_Throws_WhenCapacityIsLessThanOne()
    {
        var act = () => new SubAgentScanCoverageCache(capacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NoLiveManager_IsASingleSharedInstance_SoRepeatedColdPollsForTheSameThreadKeepHitting()
    {
        var cache = new SubAgentScanCoverageCache();

        cache.RecordRecovered("thread-1", SubAgentScanCoverageCache.NoLiveManager, Rows("child-a"));

        var hit = cache.TryGetRecovered("thread-1", SubAgentScanCoverageCache.NoLiveManager, out var rows);

        hit.Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["child-a"]);
    }
}
