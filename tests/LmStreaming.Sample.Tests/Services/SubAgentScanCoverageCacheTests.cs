using System.Collections.Concurrent;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Direct unit coverage for <see cref="SubAgentScanCoverageCache"/>'s owner/epoch keying and
/// bounded-retention policy (PR #245 review — HIGH/MEDIUM findings on the cache introduced for
/// PRRT_kwDOOPysWM6V1mjj, and a follow-up stress review that found the original per-thread tombstone
/// generation protocol reopened the exact resurrection it was meant to close — see
/// <see cref="SubAgentScanCoverageCache"/>'s remarks for the ABA hazard and the cache-wide forget-epoch
/// fix). <see cref="AgentHierarchyServiceTests"/> covers these same guarantees end-to-end through real
/// <c>MultiTurnAgentLoop</c>/<c>SubAgentManager</c> resets; this file isolates the cache's own bookkeeping
/// (owner-mismatch miss, delete eviction, capacity eviction, forget-epoch races) so those invariants have
/// a fast, precise, non-integration proof independent of the loop plumbing.
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

        cache.RecordRecovered("thread-1", owner, Rows("child-a"), cache.CaptureWriteEpoch());

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

        cache.RecordRecovered("thread-1", originalOwner, Rows("child-a"), cache.CaptureWriteEpoch());

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

        cache.RecordRecovered("thread-1", generation1, Rows("gen1-child"), cache.CaptureWriteEpoch());
        cache.RecordRecovered("thread-1", generation2, Rows("gen1-child", "gen2-child"), cache.CaptureWriteEpoch());
        cache.RecordRecovered(
            "thread-1", generation3, Rows("gen1-child", "gen2-child", "gen3-child"), cache.CaptureWriteEpoch());

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

        cache.RecordRecovered("thread-1", owner, [], cache.CaptureWriteEpoch());
        cache.TryGetRecovered("thread-1", owner, out var emptyRows).Should().BeTrue();
        emptyRows.Should().BeEmpty();

        cache.RecordRecovered("thread-1", owner, Rows("child-a"), cache.CaptureWriteEpoch());
        var hit = cache.TryGetRecovered("thread-1", owner, out var rows);

        hit.Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["child-a"]);
    }

    [Fact]
    public void Forget_RemovesTheEntry_RegardlessOfOwner_SoAReusedThreadIdAlwaysMisses()
    {
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();
        cache.RecordRecovered("thread-1", owner, Rows("child-a"), cache.CaptureWriteEpoch());

        cache.Forget("thread-1");

        cache.TryGetRecovered("thread-1", owner, out var rows).Should().BeFalse(
            "Forget must evict the entry outright, not merely require a different owner");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void Forget_OnAnUnknownThreadId_IsANoOp_ButStillBumpsTheWriteEpoch()
    {
        var cache = new SubAgentScanCoverageCache();
        var epochBefore = cache.CaptureWriteEpoch();

        var act = () => cache.Forget("never-recorded");

        act.Should().NotThrow();
        cache.CaptureWriteEpoch().Should().NotBe(
            epochBefore, "Forget bumps the cache-wide epoch even for a thread id with no recorded entry");
    }

    [Fact]
    public void Forget_CalledRepeatedly_NeverThrows_AndKeepsAdvancingTheEpoch()
    {
        // "Repeated Forget" (PR #245 stress review RED->GREEN list): the cache-wide epoch must keep
        // moving forward monotonically across many consecutive deletes, whether or not each one hits a
        // real entry, and must never throw.
        var cache = new SubAgentScanCoverageCache();

        var act = () =>
        {
            for (var i = 0; i < 25; i++)
            {
                cache.Forget("thread-1");
                cache.Forget($"thread-{i}");
            }
        };

        act.Should().NotThrow();
        cache.CaptureWriteEpoch().Should().Be(50UL, "every Forget call bumps the epoch by exactly one");
    }

    [Fact]
    public void RecordRecovered_EvictsTheOldestEntryByLastWrite_OnceCapacityIsExceeded()
    {
        var cache = new SubAgentScanCoverageCache(capacity: 3);
        var owner1 = new object();
        var owner2 = new object();
        var owner3 = new object();
        var owner4 = new object();

        cache.RecordRecovered("thread-1", owner1, Rows("a"), cache.CaptureWriteEpoch());
        cache.RecordRecovered("thread-2", owner2, Rows("b"), cache.CaptureWriteEpoch());
        cache.RecordRecovered("thread-3", owner3, Rows("c"), cache.CaptureWriteEpoch());

        // Fourth distinct thread pushes the tracked-thread count past capacity — thread-1 (the oldest
        // by last write) must be evicted first, deterministically, not thread-2 or thread-3.
        cache.RecordRecovered("thread-4", owner4, Rows("d"), cache.CaptureWriteEpoch());

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

        cache.RecordRecovered("thread-1", owner1, Rows("a"), cache.CaptureWriteEpoch());
        cache.RecordRecovered("thread-2", owner2, Rows("b"), cache.CaptureWriteEpoch());

        // Re-writing thread-1 (same owner, e.g. the live manager gained a new child) bumps it to
        // most-recently-written, so thread-2 — now the oldest — is the one evicted next, not thread-1.
        cache.RecordRecovered("thread-1", owner1, Rows("a", "a2"), cache.CaptureWriteEpoch());
        cache.RecordRecovered("thread-3", owner3, Rows("c"), cache.CaptureWriteEpoch());

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

    // --- PR #245 stress review: cache-wide forget-epoch protocol -----------------------------------
    // These tests isolate the cache's own epoch bookkeeping without needing a real scan or store — the
    // race AgentHierarchyService guards against is entirely reproducible by sequencing
    // CaptureWriteEpoch / Forget / RecordRecovered calls directly, since the cache has no notion of "a
    // scan is running": it only ever sees the epoch token the caller captured before starting one.

    [Fact]
    public void CaptureWriteEpoch_ReturnsZero_Initially()
    {
        var cache = new SubAgentScanCoverageCache();

        cache.CaptureWriteEpoch().Should().Be(0UL);
    }

    [Fact]
    public void RecordRecovered_WithMatchingEpoch_Commits_AndReturnsTrue()
    {
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();

        var epoch = cache.CaptureWriteEpoch();
        var committed = cache.RecordRecovered("thread-1", owner, Rows("child-a"), epoch);

        committed.Should().BeTrue("no Forget ran between the capture and the write, so it must commit");
        cache.TryGetRecovered("thread-1", owner, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["child-a"]);
    }

    [Fact]
    public void RecordRecovered_RejectsALateWriteback_WhenForgetRanAfterTheEpochWasCaptured_SameThread()
    {
        // The exact race from PRRT_kwDOOPysWM6V39Ux: a caller misses the cache, captures the epoch, and
        // starts a (here: simulated) scan. Before that scan's result is recorded, ConversationsController
        // .Delete calls Forget for the SAME thread. The scan's writeback must be rejected — not
        // resurrect the deleted thread's roster — even though the caller's rows are a perfectly good
        // answer from BEFORE the delete.
        var cache = new SubAgentScanCoverageCache();
        var owner = new object();

        // Caller A misses the cache and captures the epoch right before starting its "scan".
        var epochBeforeScan = cache.CaptureWriteEpoch();

        // The scan is slow; meanwhile the thread is deleted.
        cache.Forget("thread-1");

        // Caller A's scan finally completes and tries to record what it found — this must be rejected.
        var committed = cache.RecordRecovered("thread-1", owner, Rows("stale-child"), epochBeforeScan);

        committed.Should().BeFalse(
            "a Forget landed between CaptureWriteEpoch and RecordRecovered, so this write is stale and "
                + "must not resurrect the deleted thread's roster");
        cache.TryGetRecovered("thread-1", owner, out var rows).Should().BeFalse(
            "the rejected write must not have inserted anything for the forgotten thread");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void RecordRecovered_StaleEpoch_IsRejected_EvenUnderADifferentOwner()
    {
        // The epoch guard must apply BEFORE any owner-based overwrite logic — a late writeback under a
        // brand-new owner (e.g. a reset that also raced in) is just as stale as one under the original
        // owner, and must be rejected the same way.
        var cache = new SubAgentScanCoverageCache();
        var originalOwner = new object();
        var ownerAfterReset = new object();

        var epochBeforeScan = cache.CaptureWriteEpoch();
        cache.Forget("thread-1");

        var committed = cache.RecordRecovered(
            "thread-1", ownerAfterReset, Rows("stale-child"), epochBeforeScan);

        committed.Should().BeFalse();
        cache.TryGetRecovered("thread-1", ownerAfterReset, out _).Should().BeFalse();
        cache.TryGetRecovered("thread-1", originalOwner, out _).Should().BeFalse();
    }

    [Fact]
    public void RecordRecovered_RejectsALateWriteback_WhenForgetRanForADifferentThread_CrossThreadInvalidation()
    {
        // "Cross-thread deletion rejects in-flight scan and later retry caches" (PR #245 stress review
        // RED->GREEN list): the forget-epoch guard is deliberately cache-WIDE, not per-thread — any
        // Forget anywhere invalidates every write epoch captured before it, even for a completely
        // unrelated thread. This is the conservative trade-off the redesign accepts (an occasional extra
        // rescan) in exchange for never letting capacity eviction reset a per-thread counter back to a
        // stale value (the ABA hazard the tombstone design had).
        var cache = new SubAgentScanCoverageCache();
        var ownerB = new object();

        var epochBeforeScanB = cache.CaptureWriteEpoch();

        // An unrelated conversation (thread-A) is deleted while thread-B's scan is still in flight.
        cache.Forget("thread-A");

        var rejected = cache.RecordRecovered("thread-B", ownerB, Rows("stale-b-child"), epochBeforeScanB);

        rejected.Should().BeFalse(
            "any Forget — even for a different thread — invalidates an epoch captured before it");
        cache.TryGetRecovered("thread-B", ownerB, out _).Should().BeFalse();

        // The later retry (a fresh poll, capturing a fresh epoch after the unrelated delete) must still
        // succeed normally — the cross-thread invalidation costs one extra rescan, not a permanent wedge.
        var freshEpoch = cache.CaptureWriteEpoch();
        var committed = cache.RecordRecovered("thread-B", ownerB, Rows("fresh-b-child"), freshEpoch);

        committed.Should().BeTrue("a fresh epoch captured after the unrelated delete is not stale");
        cache.TryGetRecovered("thread-B", ownerB, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["fresh-b-child"]);
    }

    [Fact]
    public void Forget_ThenFreshCaptureAndRecord_Commits_SoAReusedThreadIdRescansSuccessfully()
    {
        // Thread reuse: once a thread id is deleted and later reused (a fresh conversation created
        // with the same client-supplied id), a NEW scan that captures the epoch AFTER the Forget must
        // be able to record its result normally — the epoch guard must not permanently wedge the thread
        // id.
        var cache = new SubAgentScanCoverageCache();
        var deletedOwner = new object();
        var reusedOwner = new object();

        cache.RecordRecovered("thread-1", deletedOwner, Rows("old-child"), cache.CaptureWriteEpoch());
        cache.Forget("thread-1");

        // The reused conversation's caller captures the epoch AFTER the delete this time.
        var freshEpoch = cache.CaptureWriteEpoch();
        var committed = cache.RecordRecovered("thread-1", reusedOwner, Rows("new-child"), freshEpoch);

        committed.Should().BeTrue("a scan that captured its epoch AFTER the Forget is not stale");
        cache.TryGetRecovered("thread-1", reusedOwner, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["new-child"]);
    }

    [Fact]
    public void RecordRecovered_ForAnOwnerReset_StillCommits_WhenNoForgetRanInBetween()
    {
        // Composes with owner-keyed reset invalidation: a manager reset (mode/provider switch, pool
        // eviction+reopen, restart) does not call Forget and must not bump the epoch, so a scan racing
        // a reset (not a delete) keeps resolving purely through the owner check, unaffected by this
        // guard.
        var cache = new SubAgentScanCoverageCache();
        var originalOwner = new object();
        var ownerAfterReset = new object();

        cache.RecordRecovered("thread-1", originalOwner, Rows("gen1-child"), cache.CaptureWriteEpoch());

        var epoch = cache.CaptureWriteEpoch();
        var committed = cache.RecordRecovered("thread-1", ownerAfterReset, Rows("gen2-child"), epoch);

        committed.Should().BeTrue("an owner reset with no intervening Forget does not bump the epoch");
        cache.TryGetRecovered("thread-1", ownerAfterReset, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["gen2-child"]);
    }

    [Fact]
    public void Forget_NeverConsumesACapacitySlot_SoADeletedThreadDoesNotDisplaceOtherEntries()
    {
        // "No tombstone consumes capacity" (PR #245 stress review RED->GREEN list): the redesign removes
        // the tombstone entirely — Forget frees the thread's slot outright instead of replacing it with a
        // stand-in. At capacity: 1, recording a second thread right after a Forget must succeed without
        // needing to evict anything (there is nothing left to evict), and the freed thread must stay gone.
        var cache = new SubAgentScanCoverageCache(capacity: 1);
        var owner1 = new object();
        var owner2 = new object();

        cache.RecordRecovered("thread-1", owner1, Rows("a"), cache.CaptureWriteEpoch());
        cache.Forget("thread-1");

        var committed = cache.RecordRecovered("thread-2", owner2, Rows("b"), cache.CaptureWriteEpoch());

        committed.Should().BeTrue(
            "no tombstone occupies thread-1's freed slot, so recording thread-2 at capacity 1 must succeed "
                + "without contending for room");
        cache.TryGetRecovered("thread-2", owner2, out var rows).Should().BeTrue();
        rows.Select(r => r.AgentId).Should().BeEquivalentTo(["b"]);
        cache.TryGetRecovered("thread-1", owner1, out _).Should().BeFalse();
    }

    [Fact]
    public void CapacityOne_DeletePlusUnrelatedWriteAndEviction_StillRejectsTheStaleScan()
    {
        // The precise ABA reproduction from the PR #245 stress review: at capacity 1, the OLD tombstone
        // design could have the tombstone for thread-1 evicted by unrelated capacity pressure (a single
        // other write is enough at capacity 1), which reset thread-1's per-thread counter back to its 0
        // baseline — the SAME value a stale in-flight scan had captured before the delete — so the guard
        // silently accepted the resurrection. The cache-wide epoch has no per-thread counter to reset:
        // capacity churn on OTHER threads must never let a stale capture for thread-1 slip back through.
        var cache = new SubAgentScanCoverageCache(capacity: 1);

        // Caller A misses the cache for thread-1 and captures the epoch before its scan starts.
        var epochBeforeScan = cache.CaptureWriteEpoch();

        // thread-1 is deleted while caller A's scan is still in flight (thread-1 had no entry yet, so
        // this exercises the "no-op removal, epoch still bumps" path).
        cache.Forget("thread-1");

        // Unrelated capacity churn: two other distinct threads are recorded at capacity 1, each evicting
        // the previous one. In the old tombstone design this is exactly the pressure that would have
        // evicted thread-1's tombstone.
        cache.RecordRecovered("thread-2", new object(), Rows("x"), cache.CaptureWriteEpoch());
        cache.RecordRecovered("thread-3", new object(), Rows("y"), cache.CaptureWriteEpoch());

        // Caller A's scan finally completes and tries to record its PRE-delete (now stale) answer.
        var committed = cache.RecordRecovered("thread-1", new object(), Rows("stale"), epochBeforeScan);

        committed.Should().BeFalse(
            "unrelated capacity eviction must never let a stale epoch captured before a Forget slip back "
                + "through — the cache-wide epoch has no per-thread counter for eviction to reset");
        cache.TryGetRecovered("thread-1", SubAgentScanCoverageCache.NoLiveManager, out _).Should().BeFalse();
    }

    [Fact]
    public void RecordRecovered_EpochParameter_HasNoDefaultValue()
    {
        // "Explicit epoch required (compile)" (PR #245 stress review RED->GREEN list): the predecessor's
        // `generation = 0` default let a caller silently skip CaptureWriteEpoch/RecordRecovered pairing —
        // an unguarded write compiled without any caller ever naming an epoch. Asserting the parameter
        // carries no default value is what makes that regression a build break instead of a silent
        // runtime hazard: if a default were reintroduced, this test fails immediately.
        var method = typeof(SubAgentScanCoverageCache).GetMethod(nameof(SubAgentScanCoverageCache.RecordRecovered));

        method.Should().NotBeNull();
        var epochParameter = method!.GetParameters().Single(p => p.Name == "epoch");

        epochParameter.HasDefaultValue.Should().BeFalse(
            "RecordRecovered must require every caller to name the epoch it captured — no silent default");
        epochParameter.ParameterType.Should().Be(typeof(ulong));
    }

    [Fact]
    public void NoLiveManager_IsASingleSharedInstance_SoRepeatedColdPollsForTheSameThreadKeepHitting()
    {
        var cache = new SubAgentScanCoverageCache();

        cache.RecordRecovered(
            "thread-1", SubAgentScanCoverageCache.NoLiveManager, Rows("child-a"), cache.CaptureWriteEpoch());

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
                cache.RecordRecovered($"thread-{i}", owners[i], expectedRows[i], cache.CaptureWriteEpoch());
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
