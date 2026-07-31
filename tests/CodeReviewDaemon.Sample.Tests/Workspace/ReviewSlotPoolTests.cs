using CodeReviewDaemon.Sample.Agents;
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

    private ReviewSlotPool CreatePool(int maxSlots) =>
        new(maxSlots, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance);

    [Fact]
    public async Task LeaseAsync_FirstLease_AllocatesSlotAddressWithoutCreatingStore()
    {
        var pool = CreatePool(maxSlots: 2);

        var slot = await pool.LeaseAsync(default);

        slot.Index.Should().Be(0);
        slot.HostPath.Should().Be(Path.Combine(_hostRoot, "slot-0"));
        slot.StorePath.Should().Be(Path.Combine(slot.HostPath, "store"));
        slot.ScratchPath.Should().Be(Path.Combine(slot.HostPath, "scratch"));
        Directory.Exists(slot.HostPath).Should().BeTrue();
        Directory.Exists(slot.ScratchPath).Should().BeTrue();
        Directory.Exists(slot.StorePath).Should().BeFalse(
            "repository ownership starts only after the slot is mounted through SandboxClient");
    }

    [Fact]
    public async Task LeaseAsync_AfterReturn_ReusesTheAddressWithoutInspectingStore()
    {
        var pool = CreatePool(maxSlots: 1);
        var first = await pool.LeaseAsync(default);
        Directory.CreateDirectory(first.StorePath);
        File.WriteAllText(Path.Combine(first.StorePath, "partial"), "handled by SDK preparation");

        await pool.ReturnAsync(first, default);
        var second = await pool.LeaseAsync(default);

        second.Should().Be(first);
        File.Exists(Path.Combine(second.StorePath, "partial")).Should().BeTrue(
            "the pool does not classify or repair repository state");
    }

    [Fact]
    public async Task LeaseAsync_WhenPoolExhausted_BlocksUntilSlotIsReturned()
    {
        var pool = CreatePool(maxSlots: 1);
        var firstSlot = await pool.LeaseAsync(default);

        var secondLeaseTask = pool.LeaseAsync(default);
        secondLeaseTask.IsCompleted.Should().BeFalse();

        await pool.ReturnAsync(firstSlot, default);
        var secondSlot = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(10));

        secondSlot.Index.Should().Be(firstSlot.Index);
    }

    [Fact]
    public void Ctor_WithZeroMaxSlots_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new ReviewSlotPool(0, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task LeaseAsync_WithACustomSlotPrefix_NamesTheSlotDirWithIt()
    {
        var pool = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            slotDirPrefix: "review-slot-");

        var slot = await pool.LeaseAsync(default);

        pool.SlotDirectoryName(0).Should().Be("review-slot-0");
        slot.HostPath.Should().Be(Path.Combine(_hostRoot, "review-slot-0"));
        slot.StorePath.Should().Be(Path.Combine(slot.HostPath, "store"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public void SlotDirectoryName_OnTheS2SPath_SurvivesLmStreamingsSanitizerUnchanged(int index)
    {
        var pool = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            slotDirPrefix: "review-slot-");

        var name = pool.SlotDirectoryName(index);

        S2SReviewWorkspacePreparer.SanitizeLeaf(name).Should().Be(name);
    }

    [Fact]
    public void Ctor_WithABlankSlotPrefix_ThrowsArgumentException()
    {
        var act = () => new ReviewSlotPool(
            1, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance, slotDirPrefix: "  ");

        act.Should().Throw<ArgumentException>();
    }
}
