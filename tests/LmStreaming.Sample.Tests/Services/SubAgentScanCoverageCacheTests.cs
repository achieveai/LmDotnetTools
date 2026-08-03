using System.Collections.Concurrent;
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

    // --- PR #245 review: scan/delete generation protocol -------------------------------------------
    // These tests isolate the cache's own generation bookkeeping without needing a real scan or store
    // — the race AgentHierarchyService guards against is entirely reproducible by sequencing
    // CaptureGeneration / Forget / RecordRecovered calls directly, since the cache has no notion of
    // "a scan is running": it only ever sees the generation token the caller captured before starting
    // one.

    [Fact]
    public void CaptureGeneration_ReturnsZero_ForAThreadThatHasNeverBeenRecordedOrForgotten()
    {
        var cache = new SubAgentScanCoverageCache();

        cache.CaptureGeneration("never-seen").Should().Be(0L);
    }

    [Fact]
    public void RecordRecovered_WithMatchingGeneration_Commits_AndReturnsTrue()
    {
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();

        var generation = cache.CaptureGeneration("thread-1");
        var committed = cache.RecordRecovered("thread-1", owner, Rows("child-a"), generation);

        committed.Should().BeTrue("no Forget ran between the capture and the write, so it must commit");
        cache.TryGetRecovered("thread-1", owner, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["child-a"]);
    }

    [Fact]
    public void RecordRecovered_RejectsALateWriteback_WhenForgetRanAfterTheGenerationWasCaptured()
    {
        // The exact race from PRRT_kwDOOPysWM6V39Ux: a caller misses the cache, captures the
        // generation, and starts a (here: simulated) scan. Before that scan's result is recorded,
        // ConversationsController.Delete calls Forget for the SAME thread. The scan's writeback must
        // be rejected — not resurrect the deleted thread's roster — even though the caller's rows are
        // a perfectly good answer from BEFORE the delete.
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();

        // Caller A misses the cache and captures the generation right before starting its "scan".
        var generationBeforeScan = cache.CaptureGeneration("thread-1");

        // The scan is slow; meanwhile the thread is deleted.
        cache.Forget("thread-1");

        // Caller A's scan finally completes and tries to record what it found — this must be rejected.
        var committed = cache.RecordRecovered("thread-1", owner, Rows("stale-child"), generationBeforeScan);

        committed.Should().BeFalse(
            "a Forget landed between CaptureGeneration and RecordRecovered, so this write is stale and "
                + "must not resurrect the deleted thread's roster");
        cache.TryGetRecovered("thread-1", owner, out var rows).Should().BeFalse(
            "the rejected write must not have replaced the tombstone Forget left behind");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void RecordRecovered_StaleGeneration_IsRejected_EvenUnderADifferentOwner()
    {
        // The generation guard must apply BEFORE any owner-based overwrite logic — a late writeback
        // under a brand-new owner (e.g. a reset that also raced in) is just as stale as one under the
        // original owner, and must be rejected the same way.
        var cache = new SubAgentScanCoverageCache();
        var originalOwner = new object();
        var ownerAfterReset = new object();

        var generationBeforeScan = cache.CaptureGeneration("thread-1");
        cache.Forget("thread-1");

        var committed = cache.RecordRecovered(
            "thread-1", ownerAfterReset, Rows("stale-child"), generationBeforeScan);

        committed.Should().BeFalse();
        cache.TryGetRecovered("thread-1", ownerAfterReset, out _).Should().BeFalse();
        cache.TryGetRecovered("thread-1", originalOwner, out _).Should().BeFalse();
    }

    [Fact]
    public void Forget_ThenFreshCaptureAndRecord_Commits_SoAReusedThreadIdRescansSuccessfully()
    {
        // Thread reuse: once a thread id is deleted and later reused (a fresh conversation created
        // with the same client-supplied id), a NEW scan that captures the generation AFTER the Forget
        // must be able to record its result normally — the generation guard must not permanently wedge
        // the thread id.
        var cache = new SubAgentScanCoverageCache();
        var deletedOwner = new object();
        var reusedOwner = new object();

        cache.RecordRecovered("thread-1", deletedOwner, Rows("old-child"));
        cache.Forget("thread-1");

        // The reused conversation's caller captures the generation AFTER the delete this time.
        var freshGeneration = cache.CaptureGeneration("thread-1");
        var committed = cache.RecordRecovered("thread-1", reusedOwner, Rows("new-child"), freshGeneration);

        committed.Should().BeTrue("a scan that captured its generation AFTER the Forget is not stale");
        cache.TryGetRecovered("thread-1", reusedOwner, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["new-child"]);
    }

    [Fact]
    public void RecordRecovered_ForAnOwnerReset_StillCommits_WhenNoForgetRanInBetween()
    {
        // Composes with owner-keyed reset invalidation: a manager reset (mode/provider switch, pool
        // eviction+reopen, restart) does not call Forget and must not bump the generation, so a scan
        // racing a reset (not a delete) keeps resolving purely through the owner check, unaffected by
        // this guard.
        var cache = new SubAgentScanCoverageCache();
        var originalOwner = new object();
        var ownerAfterReset = new object();

        cache.RecordRecovered("thread-1", originalOwner, Rows("gen1-child"));

        var generation = cache.CaptureGeneration("thread-1");
        var committed = cache.RecordRecovered("thread-1", ownerAfterReset, Rows("gen2-child"), generation);

        committed.Should().BeTrue("an owner reset with no intervening Forget does not bump the generation");
        cache.TryGetRecovered("thread-1", ownerAfterReset, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["gen2-child"]);
    }

    [Fact]
    public void Forget_TombstoneEntry_IsBoundByCapacity_LikeAnyOtherEntry_SoADeletedThreadDoesNotLeakForever()
    {
        // "No unbounded generation memory": a Forget tombstone occupies one slot in the SAME bounded
        // structure as a real entry, so recording enough OTHER distinct threads evicts it exactly like
        // it would evict a real entry — the deleted thread id does not pin memory forever.
        var cache = new SubAgentScanCoverageCache(capacity: 2);
        var owner = new object();
        cache.RecordRecovered("thread-1", owner, Rows("child-a"));

        cache.Forget("thread-1");
        cache.RecordRecovered("thread-2", new object(), Rows("child-b"));
        cache.RecordRecovered("thread-3", new object(), Rows("child-c"));

        // thread-1's tombstone (recorded most-recently at the time of Forget, then aged by two more
        // writes) should now be evicted, resetting its generation back to the 0 baseline.
        cache.CaptureGeneration("thread-1").Should().Be(
            0L, "the tombstone must have been evicted by capacity pressure, same as a real entry would be");
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

    [Fact]
    public async Task RecordRecovered_ConcurrentDistinctThreads_EvictsToExactlyCapacity_WithNoCrossContamination()
    {
        // PR #245 review (MEDIUM): the single-writer eviction tests above prove the bookkeeping is
        // correct for sequential calls; this test proves the SAME invariants hold when N > capacity
        // distinct (threadId, owner, rows) triples are recorded from many threads racing the same
        // `_gate` lock at once — the scenario the sequential tests cannot exercise. It intentionally
        // does NOT assert which specific threads survive (that is a genuine race, since all writes are
        // released simultaneously) — only that the ceiling is respected and every surviving entry is
        // internally consistent (no reader ever observes one thread's owner paired with another
        // thread's rows).
        const int capacity = 5;
        const int distinctThreads = 20;
        var cache = new SubAgentScanCoverageCache(capacity: capacity);

        var owners = new object[distinctThreads];
        var expectedRows = new IReadOnlyList<SubAgentSummary>[distinctThreads];
        for (var i = 0; i < distinctThreads; i++)
        {
            owners[i] = new object();
            expectedRows[i] = Rows($"child-{i}");
        }

        // Release every writer at (as close to) the same instant as possible, rather than letting
        // Task.Run trickle them in one at a time — the point is to actually contend the lock.
        using var ready = new CountdownEvent(distinctThreads);
        using var start = new ManualResetEventSlim(initialState: false);

        var writeTasks = Enumerable.Range(0, distinctThreads)
            .Select(i => Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                cache.RecordRecovered($"thread-{i}", owners[i], expectedRows[i]);
            }))
            .ToArray();

        ready.Wait();
        start.Set();

        var writeException = await Record.ExceptionAsync(() => Task.WhenAll(writeTasks));
        writeException.Should().BeNull("concurrent RecordRecovered calls for distinct threads must never throw");

        // Read back concurrently too, and collect results instead of asserting inside the task bodies —
        // an xUnit assertion failure thrown on a background Task can be swallowed by Task.WhenAll.
        var observations = new ConcurrentBag<(int Index, bool Hit, IReadOnlyList<SubAgentSummary> Rows)>();
        var readTasks = Enumerable.Range(0, distinctThreads)
            .Select(i => Task.Run(() =>
            {
                var hit = cache.TryGetRecovered($"thread-{i}", owners[i], out var rows);
                observations.Add((i, hit, rows));
            }))
            .ToArray();

        var readException = await Record.ExceptionAsync(() => Task.WhenAll(readTasks));
        readException.Should().BeNull("concurrent TryGetRecovered calls must never throw");

        observations.Should().HaveCount(distinctThreads);
        var hits = observations.Where(o => o.Hit).ToList();

        // The ceiling: exactly `capacity` distinct threads may survive concurrent eviction — never more
        // (eviction runs under the same lock as the write that triggers it) and never fewer (every write
        // that lands is a real, retrievable entry until IT is the one evicted). Deliberately not
        // asserting WHICH indices survive — that outcome is a genuine, non-deterministic race.
        hits.Should().HaveCount(
            capacity,
            "exactly `capacity` distinct threads must survive concurrent eviction, regardless of which ones");

        // No cross-contamination: every surviving hit must return precisely the rows recorded under its
        // OWN owner, never another thread's rows (which a shared-state race on the dictionary/list would
        // produce).
        foreach (var (index, _, rows) in hits)
        {
            rows.Select(r => r.AgentId).Should().BeEquivalentTo(
                expectedRows[index].Select(r => r.AgentId),
                $"thread-{index}'s surviving entry must return exactly its own recorded rows, never another thread's");
        }
    }
}
