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
}
