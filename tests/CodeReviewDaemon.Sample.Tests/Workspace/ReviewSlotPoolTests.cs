using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging;
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
    public async Task RetireAsync_DoesNotReportAContainmentFailureForOrdinarySafetyRetirement()
    {
        var logger = new CapturingLogger<ReviewSlotPool>();
        var pool = new ReviewSlotPool(1, _hostRoot, "scratch", logger);
        var slot = await pool.LeaseAsync(default);

        await pool.RetireAsync(slot, default);

        logger.CountAtLevel(LogLevel.Error, "could not be established as contained").Should().Be(0);
    }

    [Fact]
    public async Task LeaseAsync_WhenContainmentFails_StillReportsTheContainmentCause()
    {
        Directory.CreateDirectory(_hostRoot);
        Directory.CreateDirectory(_outsideRoot);
        DirectoryLink.Create(Path.Combine(_hostRoot, "slot-0"), _outsideRoot);
        var logger = new CapturingLogger<ReviewSlotPool>();
        var pool = new ReviewSlotPool(1, _hostRoot, "scratch", logger);

        var act = () => pool.LeaseAsync(default);

        await act.Should().ThrowAsync<SlotAddressUnusableException>();
        logger.CountAtLevel(LogLevel.Error, "could not be established as contained").Should().Be(1);
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

    [Fact]
    public async Task A_descendant_of_a_current_slot_quarantines_that_slot()
    {
        var pool = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            quarantinedHostPaths: [Path.Combine(_hostRoot, "slot-0", "child")]
        );

        var slot = await pool.LeaseAsync(default);

        slot.Index.Should().Be(1, "the persisted descendant may identify a mounted path within slot 0");
    }

    [Fact]
    public async Task A_claim_from_a_previous_pool_root_does_not_block_the_current_namespace()
    {
        var logger = new CapturingLogger<ReviewSlotPool>();
        var oldRoot = Path.Combine(Path.GetTempPath(), "crd-old-pool-" + Guid.NewGuid().ToString("N"));
        var pool = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            logger,
            quarantinedHostPaths: [Path.Combine(oldRoot, "slot-0")]
        );

        var slot = await pool.LeaseAsync(default);

        slot.Index.Should().Be(0);
        logger.CountAtLevel(LogLevel.Error, oldRoot).Should().Be(1);
    }

    [Fact]
    public async Task A_claim_from_a_previous_slot_prefix_does_not_block_the_current_namespace()
    {
        var logger = new CapturingLogger<ReviewSlotPool>();
        var historicalPath = Path.Combine(_hostRoot, "review-slot-0");
        var pool = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            logger,
            slotDirPrefix: "slot-",
            quarantinedHostPaths: [historicalPath]
        );

        var slot = await pool.LeaseAsync(default);

        slot.Index.Should().Be(0);
        logger.CountAtLevel(LogLevel.Error, historicalPath).Should().Be(1);
    }

    [Theory]
    [InlineData("slot-01")]
    [InlineData("slot--1")]
    [InlineData("other-0")]
    public async Task An_unallocatable_historical_name_does_not_block_startup(string relativePath)
    {
        var logger = new CapturingLogger<ReviewSlotPool>();
        var historicalPath = Path.Combine(_hostRoot, relativePath);
        var pool = new ReviewSlotPool(1, _hostRoot, "scratch", logger, quarantinedHostPaths: [historicalPath]);

        var slot = await pool.LeaseAsync(default);

        slot.Index.Should().Be(0);
        logger.CountAtLevel(LogLevel.Error, historicalPath).Should().Be(1);
    }

    [Fact]
    public async Task A_new_process_skips_an_unresolved_address_before_its_first_lease()
    {
        var processA = CreatePool(maxSlots: 1);
        var mounted = await processA.LeaseAsync(default);

        var processB = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            quarantinedHostPaths: [mounted.HostPath]
        );
        var unrelatedRun = await processB.LeaseAsync(default);

        unrelatedRun.Index.Should().Be(1, "process A may still have slot 0 mounted after the daemon dies");
    }

    [Fact]
    public async Task A_new_process_skips_every_unresolved_address_not_just_the_first_one()
    {
        var processA = CreatePool(maxSlots: 2);
        var firstMounted = await processA.LeaseAsync(default);
        var secondMounted = await processA.LeaseAsync(default);

        var processB = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            quarantinedHostPaths: [firstMounted.HostPath, secondMounted.HostPath]
        );
        var unrelatedRun = await processB.LeaseAsync(default);

        unrelatedRun.Index.Should().Be(2, "every unresolved process-A mount must be quarantined globally");
    }

    [Fact]
    public async Task A_quarantined_address_is_skipped_when_it_reaches_the_free_stack()
    {
        var pool = new ReviewSlotPool(
            2,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            quarantinedHostPaths: [Path.Combine(_hostRoot, "slot-0")]
        );
        var first = await pool.LeaseAsync(default);
        await pool.ReturnAsync(new ReviewSlot(0, Path.Combine(_hostRoot, "slot-0"), "unused", "unused"), default);

        var next = await pool.LeaseAsync(default);

        first.Index.Should().Be(1, "fresh allocation must skip quarantine");
        next.Index.Should().Be(2, "free-stack reuse must independently skip quarantine");
    }

    [Fact]
    public async Task Quarantine_above_the_concurrency_limit_remains_effective_when_allocation_reaches_it()
    {
        var pool = new ReviewSlotPool(
            1,
            _hostRoot,
            "scratch",
            NullLogger<ReviewSlotPool>.Instance,
            quarantinedHostPaths: [Path.Combine(_hostRoot, "slot-3")]
        );

        var seen = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var slot = await pool.LeaseAsync(default);
            seen.Add(slot.Index);
            await pool.RetireAsync(slot, default);
        }

        seen.Should().Equal(0, 1, 2, 4);
    }
}
