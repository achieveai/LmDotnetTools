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

        slot.HostPath.Should().Be(Path.Combine(_hostRoot, "review-slot-0"));
        slot.StorePath.Should().Be(Path.Combine(slot.HostPath, "store"));
    }

    [Fact]
    public async Task LeaseAsync_WithARepoKey_MountsThePerRepoWorktreeLayout()
    {
        var pool = new ReviewSlotPool(
            2, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance, slotDirPrefix: "review-");
        const string RepoKey = "dev.azure.com/o365exchange/Weve_DA/_git/Nova";

        var slot = await pool.LeaseAsync(RepoKey, default);

        var mount = Path.Combine(_hostRoot, pool.MountDirectoryName(RepoKey));
        slot.HostPath.Should().Be(mount, "the mount is per REPO — every slot of it is mounted at /workspace");
        slot.SharedStorePath.Should().Be(Path.Combine(mount, "store"));
        slot.UsesSharedStore.Should().BeTrue();
        slot.SlotDirName.Should().Be("slot-0");
        slot.StorePath.Should().Be(Path.Combine(mount, "slot-0", "notes"));
        slot.TargetPath.Should().Be(Path.Combine(mount, "slot-0", "repo"));
        slot.ScratchPath.Should().Be(Path.Combine(mount, "slot-0", "scratch"));
    }

    [Fact]
    public async Task LeaseAsync_ForTwoReposConcurrently_GivesEachItsOwnMountAndItsOwnIndexSpace()
    {
        var pool = CreatePool(maxSlots: 2);

        var nova = await pool.LeaseAsync("dev.azure.com/o365exchange/Weve_DA/_git/Nova", default);
        var astra = await pool.LeaseAsync("dev.azure.com/o365exchange/Weve_DA/_git/Astra", default);

        nova.HostPath.Should().NotBe(astra.HostPath, "one shared object store per repo, not per daemon");
        astra.Index.Should().Be(
            0, "indexes only have to be unique within the mount whose directory they name");
        astra.SlotDirName.Should().Be(nova.SlotDirName);
        astra.StorePath.Should().NotBe(
            nova.StorePath, "each slot commits its own per-PR notes branch in its own worktree");
    }

    [Fact]
    public async Task LeaseAsync_TwiceForOneRepo_SharesTheStoreButNotTheWorktrees()
    {
        var pool = CreatePool(maxSlots: 2);
        const string RepoKey = "github.com/gautamb_microsoft/NOVA_reviews";

        var first = await pool.LeaseAsync(RepoKey, default);
        var second = await pool.LeaseAsync(RepoKey, default);

        second.HostPath.Should().Be(first.HostPath);
        second.SharedStorePath.Should().Be(
            first.SharedStorePath, "concurrent reviews of one repo share its objects — that is the point");
        second.Index.Should().Be(1);
        second.StorePath.Should().NotBe(first.StorePath);
        second.TargetPath.Should().NotBe(
            first.TargetPath, "two worktrees of one submodule may sit at different commits");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public void SlotDirectoryName_OnTheS2SPath_SurvivesLmStreamingsSanitizerUnchanged(int index)
    {
        var name = ReviewSlotPool.SlotDirectoryName(index);

        S2SReviewWorkspacePreparer.SanitizeLeaf(name).Should().Be(name);
    }

    [Theory]
    [InlineData("dev.azure.com/o365exchange/Weve_DA/_git/Nova")]
    [InlineData("dev.azure.com/o365exchange/O365 Core/_git/MODISService")]
    [InlineData("github.com/gautamb_microsoft/NOVA_reviews")]
    [InlineData("a")]
    public void MountDirectoryName_ForAnyRepoKey_SurvivesLmStreamingsSanitizerUnchanged(string repoKey)
    {
        var pool = new ReviewSlotPool(
            1, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance, slotDirPrefix: "review-");

        var name = pool.MountDirectoryName(repoKey);

        // The mount is created on the host under this exact name and then addressed through the workspace
        // API, which sanitizes its leaf. If the two disagree the workspace points at a directory that does
        // not exist, so the name must be a FIXED POINT of the sanitizer, not merely close to one.
        S2SReviewWorkspacePreparer.SanitizeLeaf(name).Should().Be(name);
    }

    [Fact]
    public void RepoSlug_ForTwoReposThatNormalizeToTheSameLabel_StaysDistinct()
    {
        // Two projects in one org can each hold a 'Nova'. The readable label is identical for both; only the
        // hash of the full key separates them — and if it did not, the two would share one mount, one object
        // store and one set of worktrees while being entirely different repositories.
        var weve = ReviewSlotPool.RepoSlug("dev.azure.com/o365exchange/Weve_DA/_git/Nova");
        var core = ReviewSlotPool.RepoSlug("dev.azure.com/o365exchange/O365 Core/_git/Nova");

        weve.Should().NotBe(core);
        weve.Should().EndWith("nova-" + weve.Split('-')[^1]);
    }

    [Fact]
    public void RepoSlug_ForALongKey_KeepsTheDistinguishingTailWithinTheLengthCap()
    {
        var slug = ReviewSlotPool.RepoSlug(
            "dev.azure.com/o365exchange/An Extremely Long Project Name That Runs On/_git/MODISService");

        slug.Should().Contain("modisservice", "identities read host-first, so the NAME is the tail");
        slug.Length.Should().BeLessThanOrEqualTo(49, "40-char label + '-' + 8 hex chars");
        slug.Should().MatchRegex("^[a-z0-9-]+$").And.NotStartWith("-");
    }

    [Fact]
    public void RepoSlug_ForTheSameKey_IsStableAcrossCalls()
    {
        // Stability is what makes the mount REUSABLE: a slug that varied per process would re-clone the store
        // on every daemon restart instead of finding the one already on disk.
        ReviewSlotPool.RepoSlug("github.com/gautamb_microsoft/NOVA_reviews")
            .Should()
            .Be(ReviewSlotPool.RepoSlug("github.com/gautamb_microsoft/NOVA_reviews"));
    }

    [Fact]
    public void Ctor_WithABlankSlotPrefix_ThrowsArgumentException()
    {
        var act = () => new ReviewSlotPool(
            1, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance, slotDirPrefix: "  ");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// This pool gives one repository one mount, and the confidentiality gate depends on it: an untrusted PR
    /// is kept away from sibling repositories by not initializing them, which withholds nothing once a single
    /// depot serves every repo and the siblings are already on disk from someone else's review.
    /// <para>
    /// The answer is DERIVED from the mount name rather than declared, so it cannot go stale. The second
    /// assertion is the derivation itself: two repo keys get two mounts. A layout that keys the mount on the
    /// store makes that false, <see cref="ReviewSlotPool.MountIsDedicatedTo"/> follows it to false without
    /// anyone remembering to update a flag, and untrusted runs are refused the mount at the point of use.
    /// </para>
    /// </summary>
    /// <summary>
    ///     A repository whose key MIMICS the dedication probe still gets its own mount.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ReviewSlotPool.MountIsDedicatedTo"/> asks its question by appending a probe suffix
    ///         and checking the leaf changed. The obvious worry is a repo key that already ends in that
    ///         suffix: it would compare equal to its own probe, the pool would report the mount is NOT
    ///         dedicated, and an untrusted PR for that repo would be refused a mount it was entitled to —
    ///         or, if the layout were inverted, granted one it was not.
    ///     </para>
    ///     <para>
    ///         It cannot happen, and the reason is worth pinning because it is not the reason the code looks
    ///         like it is: the NUL in the probe key is defence in depth, not the mechanism. A key carrying
    ///         the probe suffix is simply a DIFFERENT key, so it differs both in the readable tail
    ///         <see cref="ReviewSlotPool.RepoSlug"/> keeps and in the SHA-256 digest it appends — either one
    ///         alone separates them here.
    ///     </para>
    ///     <para>
    ///         So this test does NOT pin the digest, and an earlier version of this comment claimed it did.
    ///         Mutation settled it: deleting the digest from <c>RepoSlug</c> leaves this test GREEN and reddens
    ///         <see cref="RepoSlug_ForTwoReposThatNormalizeToTheSameLabel_StaysDistinct"/> instead — which is
    ///         the test that actually holds the digest in place, because only there do two keys share a
    ///         readable label. The two are complementary and neither covers the other.
    ///     </para>
    ///     <para>
    ///         Both spellings are covered: the literal probe suffix including its NUL, and the suffix as it
    ///         survives slug sanitization (which drops the NUL and the space). The second is the one a real
    ///         key could plausibly carry.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(" \0dedication-probe")]
    [InlineData(" dedication-probe")]
    [InlineData("-dedication-probe")]
    public void MountIsDedicatedTo_isNotFooledByARepoKeyThatMimicsItsOwnProbe(string mimickedSuffix)
    {
        var pool = CreatePool(maxSlots: 1);
        var key = "dev.azure.com/o365exchange/weve_da/nova" + mimickedSuffix;

        pool.MountIsDedicatedTo(key).Should().BeTrue(
            "the probe distinguishes keys by a digest of the whole key, so a key that spells the probe "
                + "suffix is still a different key and still gets a different mount");
        pool.MountDirectoryName(key).Should().NotBe(
            pool.MountDirectoryName("dev.azure.com/o365exchange/weve_da/nova"),
            "otherwise two distinct repositories would share one mount, which is the exact condition the "
                + "untrusted-PR gate exists to detect");
    }

    // ---------------------------------------------------------------------------------------------------
    // The depot layout (#39): one mount, keyed on the STORE, serving every reviewed repository.
    // ---------------------------------------------------------------------------------------------------

    private const string DepotKey = "https://github.com/achieveai/AchieveAiReviews.git";

    private ReviewSlotPool CreateDepotPool(int maxSlots) =>
        new(maxSlots, _hostRoot, "scratch", NullLogger<ReviewSlotPool>.Instance, sharedDepotKey: DepotKey);

    /// <summary>
    /// The producer-side half of the confidentiality gate, and the reason
    /// <see cref="IReviewSlotPool.MountIsDedicatedTo"/> was given no default implementation: a depot pool must
    /// answer <c>false</c> about its OWN layout.
    /// <para>
    /// Nothing here declares that answer. <see cref="ReviewSlotPool.MountIsDedicatedTo"/> is untouched by the
    /// depot change — it still probes whether the mount leaf varies with the repo key, and under a store-keyed
    /// mount the two probes simply collide. That is the whole design: the layout change alone moves the
    /// answer, with no flag for a later reader to forget. Reverting the keying turns this red.
    /// </para>
    /// <para>
    /// Asserted for three unrelated repositories rather than one, because a single key cannot distinguish
    /// "the mount ignores its argument" from "this one key happens to collide with its probe".
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("dev.azure.com/o365exchange/Weve_DA/_git/Nova")]
    [InlineData("dev.azure.com/o365exchange/O365 Core/_git/MODISService")]
    [InlineData("github.com/achieveai/LmDotnetTools")]
    public void MountIsDedicatedTo_OnADepotKeyedPool_IsFalseForEveryRepository(string repoKey)
    {
        var pool = CreateDepotPool(maxSlots: 2);

        pool.MountIsDedicatedTo(repoKey).Should().BeFalse(
            "a depot mount holds every reviewed repository, so nothing checked out under it belongs to this "
                + "repo alone — which is precisely what the untrusted-PR gate has to be told");
    }

    [Fact]
    public void MountDirectoryName_OnADepotKeyedPool_IsTheSameLeafForEveryRepository()
    {
        var pool = CreateDepotPool(maxSlots: 2);

        var nova = pool.MountDirectoryName("dev.azure.com/o365exchange/Weve_DA/_git/Nova");
        var astra = pool.MountDirectoryName("dev.azure.com/o365exchange/Weve_DA/_git/Astra");

        nova.Should().Be(astra, "one depot serves every repo — that is the point of the layout");
        nova.Should().Be(
            $"slot-{ReviewSlotPool.RepoSlug(DepotKey)}",
            "the leaf is keyed on the STORE, so every daemon pointed at one store finds the same directory "
                + "on disk instead of re-cloning per repo");
        S2SReviewWorkspacePreparer.SanitizeLeaf(nova).Should().Be(
            nova, "the depot leaf is mounted through the workspace API like any other");
    }

    /// <summary>
    /// Slot indexes are allocated per MOUNT, not per repo. On the per-repo layout the two are the same thing,
    /// which is why the distinction was invisible until now: under a depot, two repositories each taking
    /// "slot-0" would be handed the same directory inside the same mount, and two concurrent reviews would
    /// prepare their worktrees on top of one another.
    /// </summary>
    [Fact]
    public async Task LeaseAsync_OnADepotKeyedPool_GivesTwoReposOneMountButNeverOneSlotDirectory()
    {
        var pool = CreateDepotPool(maxSlots: 2);

        var nova = await pool.LeaseAsync("dev.azure.com/o365exchange/Weve_DA/_git/Nova", default);
        var astra = await pool.LeaseAsync("dev.azure.com/o365exchange/Weve_DA/_git/Astra", default);

        nova.HostPath.Should().Be(astra.HostPath, "one depot, one mount");
        nova.SharedStorePath.Should().Be(astra.SharedStorePath, "and one object store, which is the saving");
        astra.SlotDirName.Should().NotBe(
            nova.SlotDirName,
            "two live reviews must never share a slot directory — under the per-repo layout the repo key kept "
                + "them apart, and a depot removes exactly that separation");
        astra.TargetPath.Should().NotBe(nova.TargetPath);
        astra.StorePath.Should().NotBe(nova.StorePath);
    }

    [Fact]
    public async Task LeaseAsync_OnAPoolWithNoDepotKey_KeepsThePerRepoMount()
    {
        // The depot is opt-in construction state. Absent it, nothing about today's layout moves — including
        // the answer the gate reads, which stays true.
        var pool = CreatePool(maxSlots: 2);
        const string RepoKey = "dev.azure.com/o365exchange/Weve_DA/_git/Nova";

        var slot = await pool.LeaseAsync(RepoKey, default);

        slot.HostPath.Should().Be(Path.Combine(_hostRoot, pool.MountDirectoryName(RepoKey)));
        pool.MountIsDedicatedTo(RepoKey).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------------
    // The dedicated lease: what an untrusted run gets on a depot-shaped pool.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The producer half of ruling (2). An untrusted run still gets reviewed — it is simply not given the
    /// depot, because a diff written outside the trust domain does not get to buy the depot's saving at the
    /// cost of sitting beside every other repository.
    /// </summary>
    [Fact]
    public async Task LeaseDedicatedAsync_OnADepotKeyedPool_GivesTheRepoAMountOfItsOwn()
    {
        var pool = CreateDepotPool(maxSlots: 2);
        const string RepoKey = "dev.azure.com/o365exchange/Weve_DA/_git/Nova";

        var shared = await pool.LeaseAsync(RepoKey, default);
        var dedicated = await pool.LeaseDedicatedAsync(RepoKey, default);

        dedicated.HostPath.Should().NotBe(shared.HostPath, "the whole point is that it is not the depot");
        dedicated.HostPath.Should().Be(
            Path.Combine(_hostRoot, pool.DedicatedMountDirectoryName(RepoKey)),
            "and the mount it does get is keyed on the repo, exactly as the per-repo layout would name it");
        dedicated.SharedStorePath.Should().NotBe(
            shared.SharedStorePath,
            "a dedicated mount that shared the depot's object store would still hold every other repo's "
                + "objects, which is the co-location being avoided");
    }

    /// <summary>
    /// The predicate the untrusted-PR gate reads is DERIVED from the mount leaf the slot actually carries,
    /// not from a flag recorded at lease time. A flag can be set wrongly; the directory name is what the
    /// agent is really mounted on, so it cannot disagree with reality.
    /// </summary>
    [Fact]
    public async Task WasLeasedDedicated_TellsTheTwoLeasesApartOnADepotPoolAndSaysYesOnAPerRepoPool()
    {
        const string RepoKey = "dev.azure.com/o365exchange/Weve_DA/_git/Nova";
        var depot = CreateDepotPool(maxSlots: 2);

        depot.WasLeasedDedicated(await depot.LeaseAsync(RepoKey, default)).Should().BeFalse(
            "a depot lease is co-located with every other repository by construction");
        depot.WasLeasedDedicated(await depot.LeaseDedicatedAsync(RepoKey, default)).Should().BeTrue();

        // On the per-repo layout both entry points name the same mount, so the honest answer is yes for both
        // — which is why that layout needs no branch at the lease site to stay safe.
        var perRepo = CreatePool(maxSlots: 2);
        perRepo.WasLeasedDedicated(await perRepo.LeaseAsync(RepoKey, default)).Should().BeTrue();
        perRepo.WasLeasedDedicated(await perRepo.LeaseDedicatedAsync(RepoKey, default)).Should().BeTrue();
    }

    /// <summary>
    /// The dedicated mount is a different mount, so it needs its own index space — and returning a slot has
    /// to put the index back into the space it came from. <c>ReturnAsync</c> is given only the slot, so it
    /// re-derives which space that was; getting it wrong would leak an index out of one mount and hand a
    /// duplicate out of the other.
    /// </summary>
    [Fact]
    public async Task ReturnAsync_PutsADedicatedSlotsIndexBackInTheDedicatedSpaceNotTheDepots()
    {
        var pool = CreateDepotPool(maxSlots: 3);
        const string RepoKey = "dev.azure.com/o365exchange/Weve_DA/_git/Nova";

        var depotSlot = await pool.LeaseAsync(RepoKey, default);
        var dedicated = await pool.LeaseDedicatedAsync(RepoKey, default);
        depotSlot.Index.Should().Be(0);
        dedicated.Index.Should().Be(0, "separate mounts have separate index spaces");
        dedicated.HostPath.Should().NotBe(depotSlot.HostPath, "so index 0 twice is not a collision");

        await pool.ReturnAsync(dedicated, default);
        var reLeased = await pool.LeaseDedicatedAsync(RepoKey, default);

        reLeased.Index.Should().Be(0, "the freed index came back to the dedicated space");
        reLeased.HostPath.Should().Be(dedicated.HostPath);
        var stillHeld = await pool.LeaseAsync(RepoKey, default);
        stillHeld.Index.Should().Be(
            1, "the depot's slot 0 is still leased, so returning the dedicated slot must not have freed it");
    }

    /// <summary>
    /// The other direction, and it exists because a mutation found it missing. Hand-writing
    /// <see cref="ReviewSlotPool.WasLeasedDedicated"/> to <c>true</c> — the exact defeat this seam is
    /// supposed to forbid — left the test above GREEN, because a dedicated slot returned into the dedicated
    /// space is right for the wrong reason. Only returning a DEPOT slot distinguishes the two, and nothing
    /// asserted it.
    /// <para>
    /// The consequence of getting it wrong is not a failure: the depot's index would leak into the
    /// repository's space, the depot's <c>Next</c> would climb forever, and reviews would silently start
    /// preparing worktrees in directories nobody reclaimed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReturnAsync_PutsADepotSlotsIndexBackInTheDepotSpaceNotTheDedicatedOne()
    {
        var pool = CreateDepotPool(maxSlots: 3);
        const string RepoKey = "dev.azure.com/o365exchange/Weve_DA/_git/Nova";

        var first = await pool.LeaseAsync(RepoKey, default);
        var second = await pool.LeaseAsync(RepoKey, default);
        first.Index.Should().Be(0);
        second.Index.Should().Be(1);

        await pool.ReturnAsync(first, default);
        var reLeased = await pool.LeaseAsync(RepoKey, default);

        reLeased.Index.Should().Be(0, "the freed depot index has to come back to the DEPOT space");
        reLeased.HostPath.Should().Be(first.HostPath);
    }
}
