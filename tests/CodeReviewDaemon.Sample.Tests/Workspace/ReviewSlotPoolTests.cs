using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class ReviewSlotPoolTests : IDisposable
{
    private readonly string _hostRoot = Path.Combine(Path.GetTempPath(), "crd-pool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hostRoot, true);
        }
        catch
        {
            // Best-effort cleanup only; leaving a stray temp dir must never fail the test.
        }
    }

    private ReviewSlotPool CreatePool(int maxSlots, Func<ReviewSlot, CancellationToken, Task> ensureStoreClonedAsync) =>
        new(maxSlots, _hostRoot, "scratch", ensureStoreClonedAsync, NullLogger<ReviewSlotPool>.Instance);

    private static Func<ReviewSlot, CancellationToken, Task> CountingCloneCallback(out Func<int> callCount)
    {
        var count = 0;
        callCount = () => count;
        return (slot, _) =>
        {
            Interlocked.Increment(ref count);
            // A real `git clone` leaves StorePath non-empty; write a marker file so the pool's
            // "already cloned" check (which also treats an empty StorePath as not-yet-cloned) sees
            // a genuinely populated store, matching production behavior.
            Directory.CreateDirectory(slot.StorePath);
            File.WriteAllText(Path.Combine(slot.StorePath, ".cloned"), "");
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task LeaseAsync_FirstLease_AllocatesSlotZeroAndClonesStore()
    {
        var clone = CountingCloneCallback(out var callCount);
        var pool = CreatePool(maxSlots: 2, clone);

        var slot = await pool.LeaseAsync(default);

        slot.Index.Should().Be(0);
        slot.HostPath.Should().Be(Path.Combine(_hostRoot, "slot-0"));
        slot.StorePath.Should().Be(Path.Combine(slot.HostPath, "store"));
        slot.ScratchPath.Should().Be(Path.Combine(slot.HostPath, "scratch"));
        callCount().Should().Be(1);
        Directory.Exists(slot.HostPath).Should().BeTrue();
        Directory.Exists(slot.ScratchPath).Should().BeTrue();
        Directory.Exists(slot.StorePath).Should().BeTrue();
    }

    [Fact]
    public async Task LeaseAsync_AfterReturn_ReusesSlotWithoutRecloning()
    {
        var clone = CountingCloneCallback(out var callCount);
        var pool = CreatePool(maxSlots: 1, clone);

        var first = await pool.LeaseAsync(default);
        await pool.ReturnAsync(first, default);
        var second = await pool.LeaseAsync(default);

        second.Index.Should().Be(first.Index);
        second.StorePath.Should().Be(first.StorePath);
        callCount().Should().Be(1);
    }

    [Fact]
    public async Task LeaseAsync_WhenPoolExhausted_BlocksUntilSlotIsReturned()
    {
        var clone = CountingCloneCallback(out _);
        var pool = CreatePool(maxSlots: 1, clone);

        var firstSlot = await pool.LeaseAsync(default);

        // The gate has zero permits left, so SemaphoreSlim.WaitAsync deterministically returns an
        // incomplete task here — no race, no sleep needed to observe this.
        var secondLeaseTask = pool.LeaseAsync(default);
        secondLeaseTask.IsCompleted.Should().BeFalse("only one slot exists and it has not been returned yet");

        await pool.ReturnAsync(firstSlot, default);

        var secondSlot = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(10));
        secondSlot.Index.Should().Be(firstSlot.Index);
    }

    [Fact]
    public void Ctor_WithZeroMaxSlots_ThrowsArgumentOutOfRangeException()
    {
        var clone = CountingCloneCallback(out _);

        var act = () => new ReviewSlotPool(0, _hostRoot, "scratch", clone, NullLogger<ReviewSlotPool>.Instance);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task LeaseAsync_WhenSetupThrows_ReleasesPermitAndRecyclesIndex()
    {
        var attempts = 0;
        var pool = CreatePool(maxSlots: 1, (slot, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("transient clone failure");
            }

            Directory.CreateDirectory(slot.StorePath);
            File.WriteAllText(Path.Combine(slot.StorePath, ".cloned"), "");
            return Task.CompletedTask;
        });

        // First lease fails inside setup, AFTER the single permit + index were taken.
        var failingLease = async () => await pool.LeaseAsync(default);
        await failingLease.Should().ThrowAsync<InvalidOperationException>();

        // A leaked permit would make this second lease block forever; the recycle path releases the
        // permit and returns the index, so it completes promptly and reuses slot 0.
        var slot = await pool.LeaseAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        slot.Index.Should().Be(0);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task RecloneStoreAsync_WipesTheWarmStoreAndReclonesFromScratch()
    {
        var clone = CountingCloneCallback(out var callCount);
        var pool = CreatePool(maxSlots: 1, clone);

        var slot = await pool.LeaseAsync(default);
        callCount().Should().Be(1);
        // Simulate a corrupt warm store: a stray/wedged file the recovery ladder must not carry forward.
        File.WriteAllText(Path.Combine(slot.StorePath, "corrupt-marker.txt"), "wedged");

        await pool.RecloneStoreAsync(slot, default);

        callCount().Should().Be(2, "the corrupt store is re-cloned from scratch");
        File.Exists(Path.Combine(slot.StorePath, "corrupt-marker.txt"))
            .Should().BeFalse("the corrupt store is wiped before re-cloning");
        File.Exists(Path.Combine(slot.StorePath, ".cloned")).Should().BeTrue("the re-clone produced a fresh store");
    }

    [Fact]
    public async Task QuarantineAsync_RetiresIndexAndReleasesPermit_SoNextLeaseGetsAFreshSlot()
    {
        var clone = CountingCloneCallback(out var callCount);
        var pool = CreatePool(maxSlots: 1, clone); // single permit — proves the permit is released, not leaked

        var first = await pool.LeaseAsync(default);
        first.Index.Should().Be(0);

        await pool.QuarantineAsync(first, default);

        // A durable tombstone is dropped so the quarantine survives a restart.
        File.Exists(Path.Combine(first.HostPath, ".quarantined")).Should().BeTrue("a quarantine tombstone is written");

        // The permit was released (else this lease blocks forever at maxSlots=1) AND the quarantined index is
        // retired: the next lease allocates a FRESH index with its own clone rather than recycling slot-0's
        // possibly-live store.
        var second = await pool.LeaseAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        second.Index.Should().Be(1, "the quarantined index 0 is retired, never recycled");
        callCount().Should().Be(2, "the fresh slot is cloned from scratch");
    }

    [Fact]
    public async Task NewPoolOverSameHostRoot_ReapsQuarantinedSlot_SoRestartNeverReusesTheTaintedStore()
    {
        var cloneA = CountingCloneCallback(out _);
        var poolA = CreatePool(maxSlots: 1, cloneA);

        var slot = await poolA.LeaseAsync(default);
        // Taint the warm store so the restart can be proven NOT to reuse it.
        File.WriteAllText(Path.Combine(slot.StorePath, "tainted.txt"), "live-mount-residue");
        await poolA.QuarantineAsync(slot, default);
        File.Exists(Path.Combine(slot.HostPath, ".quarantined")).Should().BeTrue();

        // Simulate a daemon RESTART: a brand-new pool over the SAME host root. Its ctor reaps the tombstoned
        // slot dir (safe — a fresh process holds no mounts), so index 0 no longer has a warm store and the
        // first lease re-clones from scratch. Without the reap, _nextIndex=0 would treat the non-empty
        // slot-0/store as warm and hand back the tainted store (the exact review #180 finding).
        var cloneB = CountingCloneCallback(out var callCountB);
        var poolB = CreatePool(maxSlots: 1, cloneB);

        var reused = await poolB.LeaseAsync(default);

        reused.Index.Should().Be(0);
        callCountB().Should().Be(1, "the quarantined store was reaped at startup, so the restart re-clones it");
        File.Exists(Path.Combine(reused.StorePath, "tainted.txt"))
            .Should().BeFalse("a quarantined store must never be reused after a restart");
    }
}
