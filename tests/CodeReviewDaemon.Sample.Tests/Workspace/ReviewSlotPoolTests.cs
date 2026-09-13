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
        Path.GetTempPath(),
        "crd-outside-" + Guid.NewGuid().ToString("N")
    );

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
    public async Task RecoverLease_reserves_exact_address_without_cleaning_and_advances_fresh_indices()
    {
        var original = CreatePool(2);
        var assigned = await original.LeaseAsync(default);
        Directory.CreateDirectory(assigned.StorePath);
        var evidence = Path.Combine(assigned.StorePath, "unfinished.txt");
        await File.WriteAllTextAsync(evidence, "retain this interrupted work");
        var restarted = CreatePool(2);
        var recovered = await restarted.RecoverLeaseAsync(assigned, default);
        recovered.Should().Be(assigned);
        (await File.ReadAllTextAsync(evidence)).Should().Be("retain this interrupted work");
        var fresh = await restarted.LeaseAsync(default);
        fresh.Index.Should().BeGreaterThan(assigned.Index);
        await restarted.ReturnAsync(fresh, default);
        await restarted.ReturnAsync(recovered, default);
    }

    [Fact]
    public async Task RecoverLease_rejects_duplicate_active_adoption_before_waiting_for_capacity()
    {
        var original = CreatePool(1);
        var assigned = await original.LeaseAsync(default);
        var restarted = CreatePool(1);
        await restarted.RecoverLeaseAsync(assigned, default);
        await restarted
            .Invoking(value => value.RecoverLeaseAsync(assigned, default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancelled_recovery_returns_its_index_and_unblocks_preferred_waiters()
    {
        var pool = CreatePool(2);
        var first = await pool.LeaseAsync(default);
        var second = await pool.LeaseAsync(default);
        var original = CreatePool(3);
        await original.LeaseAsync(default);
        await original.LeaseAsync(default);
        var target = await original.LeaseAsync(default);
        using var cancellation = new CancellationTokenSource();
        var recovery = pool.RecoverLeaseAsync(target, cancellation.Token);
        var preferred = pool.LeasePreferredAsync(target, default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        await pool.ReturnAsync(first, default);
        await pool.ReturnAsync(second, default);
        var acquired = await preferred.WaitAsync(TimeSpan.FromSeconds(5));
        acquired.Index.Should().Be(target.Index);
        await pool.ReturnAsync(acquired, default);
        var reused = await pool.LeaseAsync(default);
        reused.Index.Should().Be(target.Index);
        await pool.ReturnAsync(reused, default);
    }

    [Fact]
    public async Task RecoverLease_rejects_an_address_outside_configured_pool()
    {
        var pool = CreatePool(1);
        var foreign = new ReviewSlot(
            0,
            _outsideRoot,
            Path.Combine(_outsideRoot, "store"),
            Path.Combine(_outsideRoot, "scratch")
        );
        await pool.Invoking(value => value.RecoverLeaseAsync(foreign, default))
            .Should()
            .ThrowAsync<SlotAddressUnusableException>();
    }

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
        Directory
            .Exists(slot.StorePath)
            .Should()
            .BeFalse("repository ownership starts only after the slot is mounted through SandboxClient");
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
        File.Exists(Path.Combine(second.StorePath, "partial"))
            .Should()
            .BeTrue("the pool does not classify or repair repository state");
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
    public async Task Preferred_lease_waits_for_and_returns_the_exact_persisted_address()
    {
        var pool = CreatePool(maxSlots: 2);
        var preferred = await pool.LeaseAsync(default);
        var other = await pool.LeaseAsync(default);
        await pool.ReturnAsync(other, default);

        var preferredLease = pool.LeasePreferredAsync(preferred, default);
        preferredLease.IsCompleted.Should().BeFalse();
        await pool.ReturnAsync(preferred, default);

        (await preferredLease.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(preferred);
    }

    [Fact]
    public async Task Try_lease_returns_immediately_when_the_only_slot_is_active()
    {
        var pool = CreatePool(maxSlots: 1);
        var active = await pool.LeaseAsync(default);

        (await pool.TryLeaseAsync(default)).Should().BeNull();

        await pool.ReturnAsync(active, default);
        (await pool.TryLeasePreferredAsync(active, default)).Should().Be(active);
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
            slotDirPrefix: "review-slot-"
        );

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
            slotDirPrefix: "review-slot-"
        );

        var name = pool.SlotDirectoryName(index);

        S2SReviewWorkspacePreparer.SanitizeLeaf(name).Should().Be(name);
    }

    [Fact]
    public void Ctor_WithABlankSlotPrefix_ThrowsArgumentException()
    {
        var act = () =>
            new ReviewSlotPool(1, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance, slotDirPrefix: "  ");

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

        await act.Should().ThrowAsync<SlotAddressUnusableException>();
        Directory
            .Exists(Path.Combine(_outsideRoot, "scratch"))
            .Should()
            .BeFalse(
                "CreateDirectory succeeds through a junction, so an unguarded lease builds the slot outside the pool"
            );
    }

    [Fact]
    public async Task LeaseAsync_WhenOnlyTheStoreIsRedirected_StillRefuses()
    {
        Directory.CreateDirectory(Path.Combine(_hostRoot, "slot-0"));
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0", "store"), _outsideRoot);
        var pool = CreatePool(maxSlots: 1);

        var act = async () => await pool.LeaseAsync(default);

        await act.Should()
            .ThrowAsync<SlotAddressUnusableException>()
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

        await act.Should()
            .ThrowAsync<SlotAddressUnusableException>()
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

        var refusal = await act.Should().ThrowAsync<SlotAddressUnusableException>();
        refusal
            .Which.Message.Should()
            .Contain($"'{Path.Combine(_hostRoot, "slot-0")}'")
            .And.NotContain(
                Path.Combine(_hostRoot, "slot-0", "store"),
                "checking a child resolves THROUGH the slot dir, so a child-first order reports an entry the "
                    + "operator will never find at the address the message gives"
            );
    }

    [Fact]
    public async Task LeaseAsync_AfterARefusal_HandsOutAFreshAddressInsteadOfTheRefusedOne()
    {
        Directory.CreateDirectory(_hostRoot);
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0"), _outsideRoot);
        var pool = CreatePool(maxSlots: 1);
        var refused = async () => await pool.LeaseAsync(default);
        await refused.Should().ThrowAsync<SlotAddressUnusableException>();

        var next = await pool.LeaseAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        next.Index.Should().Be(1, "the free list is a stack, so recycling a refused index refuses every later lease");
        next.HostPath.Should().Be(Path.Combine(_hostRoot, "slot-1"));
        Directory.Exists(next.ScratchPath).Should().BeTrue("the pool is still serving leases at full concurrency");
    }

    [Fact]
    public async Task RetireAsync_DoesNotHandTheRetiredAddressOutAgain()
    {
        // The caller's half of the same rule the refused LEASE above already follows. A refusal raised during
        // PREPARATION names an entry beneath the slot — a descendant of the three paths the lease guard checks —
        // so the next lease of that index sees nothing wrong, hands it out, and the preparation refuses again.
        // The free list is a stack, so ReturnAsync would make the poisoned index the VERY NEXT one out: a run
        // per cycle, each burning a full lease and a re-clone attempt, with nothing that ever breaks it.
        var pool = CreatePool(maxSlots: 2);
        var first = await pool.LeaseAsync(default);

        await pool.RetireAsync(first, default);
        var next = await pool.LeaseAsync(default);

        next.Index.Should().NotBe(first.Index, "a retired address is spent until somebody looks at the disk");
        next.Index.Should().Be(1);
    }

    [Fact]
    public async Task RetireAsync_StillReleasesTheLease()
    {
        // Retiring costs an address and must cost nothing else. If it withheld the permit as well as the index,
        // one planted entry would permanently cut the pool's concurrency by one, and N of them would stop the
        // daemon dead — turning a contained refusal into the outage the refusal was supposed to avoid.
        var pool = CreatePool(maxSlots: 1);
        var first = await pool.LeaseAsync(default);

        await pool.RetireAsync(first, default);
        var next = await pool.LeaseAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        next.Index.Should().Be(1);
        Directory.Exists(next.ScratchPath).Should().BeTrue();
    }
}
