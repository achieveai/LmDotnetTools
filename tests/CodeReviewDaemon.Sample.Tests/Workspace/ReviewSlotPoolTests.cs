using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class ReviewSlotPoolTests : IDisposable
{
    private readonly string _hostRoot = Path.Combine(Path.GetTempPath(), "crd-pool-" + Guid.NewGuid().ToString("N"));

    /// <summary>Where a planted link points. Outside the pool, so following one is visible as an escape.</summary>
    private readonly string _outsideRoot = Path.Combine(
        Path.GetTempPath(), "crd-outside-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var root in new[] { _hostRoot, _outsideRoot })
        {
            DirectoryLink.UnlinkAllUnder(root);
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // Best-effort cleanup only; leaving a stray temp dir must never fail the test.
            }
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

    [Fact]
    public async Task LeaseAsync_WhenTheSlotDirIsRedirected_RefusesAndCreatesNothingThroughIt()
    {
        Directory.CreateDirectory(_hostRoot);
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0"), _outsideRoot);
        var pool = CreatePool(maxSlots: 1);

        var act = async () => await pool.LeaseAsync(default);

        await act.Should().ThrowAsync<SlotHostPathRefusedException>();
        Directory.Exists(Path.Combine(_outsideRoot, "scratch")).Should().BeFalse(
            "CreateDirectory succeeds through a junction, so an unguarded lease builds the slot outside the pool");
    }

    [Fact]
    public async Task LeaseAsync_WhenOnlyTheStoreIsRedirected_StillRefuses()
    {
        Directory.CreateDirectory(Path.Combine(_hostRoot, "slot-0"));
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0", "store"), _outsideRoot);
        var pool = CreatePool(maxSlots: 1);

        var act = async () => await pool.LeaseAsync(default);

        await act.Should().ThrowAsync<SlotHostPathRefusedException>()
            .WithMessage("*store*", "the store is the path the clone and the wipe both write to");
    }

    [Fact]
    public async Task LeaseAsync_WhenOnlyTheScratchDirIsRedirected_StillRefuses()
    {
        Directory.CreateDirectory(Path.Combine(_hostRoot, "slot-0"));
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0", "scratch"), _outsideRoot);
        var pool = CreatePool(maxSlots: 1);

        var act = async () => await pool.LeaseAsync(default);

        await act.Should().ThrowAsync<SlotHostPathRefusedException>()
            .WithMessage("*scratch*", "the lease creates the scratch dir, and the preparer later clears it");
    }

    [Fact]
    public async Task LeaseAsync_WhenALinkSitsBeneathTheRedirectedSlotDir_NamesTheSlotDirAndNotTheFarEnd()
    {
        Directory.CreateDirectory(_hostRoot);
        Directory.CreateDirectory(Path.Combine(_outsideRoot, "elsewhere"));
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0"), _outsideRoot);
        DirectoryLink.Create(Path.Combine(_outsideRoot, "store"), Path.Combine(_outsideRoot, "elsewhere"));
        var pool = CreatePool(maxSlots: 1);

        var act = async () => await pool.LeaseAsync(default);

        var refusal = await act.Should().ThrowAsync<SlotHostPathRefusedException>();
        refusal.Which.Message.Should()
            .Contain($"'{Path.Combine(_hostRoot, "slot-0")}'")
            .And.NotContain(
                Path.Combine(_hostRoot, "slot-0", "store"),
                "checking a child resolves THROUGH the slot dir, so a child-first order reports an entry the "
                    + "operator will never find at the address the message gives");
    }

    [Fact]
    public async Task LeaseAsync_AfterARefusal_HandsOutAFreshAddressInsteadOfTheRefusedOne()
    {
        Directory.CreateDirectory(_hostRoot);
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0"), _outsideRoot);
        var pool = CreatePool(maxSlots: 1);
        var refused = async () => await pool.LeaseAsync(default);
        await refused.Should().ThrowAsync<SlotHostPathRefusedException>();

        var next = await pool.LeaseAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        next.Index.Should().Be(1, "the free list is a stack, so recycling a refused index refuses every later lease");
        next.HostPath.Should().Be(Path.Combine(_hostRoot, "slot-1"));
        Directory.Exists(next.ScratchPath).Should().BeTrue("the pool is still serving leases at full concurrency");
    }
}
