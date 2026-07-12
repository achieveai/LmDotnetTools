using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Deterministic, script-level state-machine proofs of the per-operation GC lock's REAL exclusive
/// ownership (owner-token fencing) and of the lock/generation-safe output reclaim, driven through the
/// exact interleavings that must never yield double ownership or delete a live claim / a newer execution's
/// output. Each test mirrors the primitives
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts"/> emits (<c>gclock_try</c>,
/// <c>gclock_owned</c>, <c>gclock_is_live</c>, <c>gclock_release</c>, and the generation-gated reclaim); the
/// real generated scripts are exercised under a POSIX shell by <c>CommandGcLockRealShellTests</c>.
/// </summary>
public class CommandGcLockInterleavingTests
{
    private const long Now = 1_000_000;

    private static ClaimModel NewModel()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        model.Fs.Now = Now;
        return model;
    }

    // ---- Scenario: a contender arriving during the mkdir -> token establishment gap ----

    [Fact]
    public void OwnerlessLock_DuringMkdirToTokenGap_IsTreatedLive_AndAContenderBacksOff()
    {
        var model = NewModel();
        // A holder has won the atomic mkdir of the lock but has NOT yet written its owner token.
        model.Fs.Mkdir(ClaimModel.GcLock).Should().BeTrue();

        model.GcLockIsLive().Should().BeTrue("a lock with no owner token yet is mid-establishment — treated LIVE, never stale");
        var acquired = model.GcLockTry("contender");

        acquired.Should().BeFalse("a contender in the mkdir->token gap must back off, never reclaim into double ownership");
        model.Fs.Read(ClaimModel.GcLockOwner).Should().BeNull("the contender must not have reclaimed or written an owner token");
    }

    // ---- Scenario: stale owner replacement ----

    [Fact]
    public void StaleOwnerLock_IsReclaimedAndReElectedToASingleNewOwner()
    {
        var model = NewModel();
        // A crashed holder's leftover: an owner token stamped far in the past (older than the TTL).
        model.Fs.Mkdir(ClaimModel.GcLock);
        model.Fs.Write(ClaimModel.GcLockOwner, "crashed-owner 1");

        model.GcLockIsLive().Should().BeFalse("an owner token stamped older than the TTL is a crashed holder's leftover");
        var acquired = model.GcLockTry("replacement");

        acquired.Should().BeTrue("the stale lock is reclaimed and re-elected to a single new owner");
        model.GcLockOwned("replacement").Should().BeTrue();
        model.GcLockOwned("crashed-owner").Should().BeFalse("the crashed holder no longer owns the reclaimed lock");
    }

    // ---- Scenario: an old owner's DELAYED release must not remove a successor's lock ----

    [Fact]
    public void DelayedReleaseByAnOldOwner_NeverRemovesASuccessorsLock()
    {
        var model = NewModel();
        // A successor currently holds the lock (freshly stamped).
        model.Fs.Mkdir(ClaimModel.GcLock);
        model.Fs.Write(ClaimModel.GcLockOwner, $"successor {Now}");

        // The old owner (whose lock was already stale-reclaimed by the successor) issues its delayed release.
        model.GcLockRelease("old-owner");

        model.Fs.DirExists(ClaimModel.GcLock).Should().BeTrue("a delayed release from an old owner must not remove the successor's lock");
        model.GcLockOwned("successor").Should().BeTrue("the successor still owns its lock after the old owner's release");

        // The true owner's release DOES remove it — the fence only blocks a non-owner.
        model.GcLockRelease("successor");
        model.Fs.DirExists(ClaimModel.GcLock).Should().BeFalse("the current owner's release removes its own lock");
    }

    // ---- Scenario: a replacement active claim is never deleted by a purger that lost ownership ----

    [Fact]
    public void PurgerWhoseLockWasReplaced_FailsTheOwnershipReCheck_AndNeverDeletesTheReplacementActiveClaim()
    {
        var model = NewModel();
        // A replacement ACTIVE claim (a far-future lease) now occupies the operation directory.
        model.Fs.Mkdir(ClaimModel.Op);
        model.Fs.Write(ClaimModel.Lease, (Now + 100_000).ToString());
        model.Fs.Write(ClaimModel.Created, Now.ToString());
        // The lock is now owned by the successor that installed the replacement.
        model.Fs.Mkdir(ClaimModel.GcLock);
        model.Fs.Write(ClaimModel.GcLockOwner, $"successor {Now}");

        // The old purger re-verifies ownership immediately before its destructive delete — and fails it.
        var oldPurgerStillOwns = model.GcLockOwned("old-purger");

        oldPurgerStillOwns.Should().BeFalse("the old purger's token was replaced, so its pre-delete ownership re-check fails");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("the replacement active claim is never deleted by a purger that lost ownership");
    }

    [Fact]
    public void LockReclaimRace_OnlyTheFinalOwnerPassesThePreDeleteOwnershipReCheck()
    {
        var model = NewModel();
        // Model the residual gclock_try reclaim race: two purgers both believe they hold a reclaimed lock,
        // but the owner file's LAST writer wins, so only that token passes the mandatory pre-delete re-check.
        model.Fs.Mkdir(ClaimModel.GcLock);
        model.Fs.Write(ClaimModel.GcLockOwner, $"A {Now}");
        model.Fs.Write(ClaimModel.GcLockOwner, $"B {Now}"); // B's token is the last write.

        model.GcLockOwned("A").Should().BeFalse("A lost the race, so its pre-delete ownership re-check declines the delete");
        model.GcLockOwned("B").Should().BeTrue("only the final owner may perform the destructive action");
    }

    // ---- Scenario: a delayed reclaim from an expired old execution must not touch a newer generation ----

    [Fact]
    public void DelayedReclaimFromAnExpiredOldExecution_NeverDeletesANewerReExecutionsOutput()
    {
        var model = NewModel();

        // Execution 1 runs and commits (generation gen-1) with its captured output.
        RunToCompletion(model.Run("first"));
        var oldGeneration = model.Fs.Read(ClaimModel.GenerationFile);
        oldGeneration.Should().Be("gen-1");
        model.Fs.FileExists(ClaimModel.StdoutFile).Should().BeTrue();

        // The retention window elapses: the directory is swept and the SAME id is re-executed (generation
        // gen-2) with fresh output, before the OLD execution's reclaim finally lands.
        model.Fs.RmRf(ClaimModel.Op);
        RunToCompletion(model.Run("second"));
        var newGeneration = model.Fs.Read(ClaimModel.GenerationFile);
        newGeneration.Should().Be("gen-2").And.NotBe(oldGeneration);

        // The delayed reclaim carries the OLD generation: under the lock it re-reads the current generation,
        // sees the mismatch, and leaves the newer execution's output completely intact.
        model.Reclaim("first-delayed", oldGeneration!, ClaimModel.Digest);

        model.Fs.FileExists(ClaimModel.StdoutFile).Should().BeTrue("a stale-generation reclaim must not delete a newer re-execution's stdout");
        model.Fs.FileExists(ClaimModel.StderrFile).Should().BeTrue("a stale-generation reclaim must not delete a newer re-execution's stderr");
        model.Fs.DirExists(ClaimModel.GcLock).Should().BeFalse("the reclaim releases the GC lock it took");

        // The matching-generation reclaim DOES drop the output — proving the negative case above is not vacuous.
        model.Reclaim("second", newGeneration!, ClaimModel.Digest);
        model.Fs.FileExists(ClaimModel.StdoutFile).Should().BeFalse("a reclaim for the current generation drops the large output");
        model.Fs.FileExists(ClaimModel.StderrFile).Should().BeFalse();
    }

    [Fact]
    public void Reclaim_WithADifferentDigest_LeavesTheOutputIntact()
    {
        var model = NewModel();
        RunToCompletion(model.Run("first"));
        var generation = model.Fs.Read(ClaimModel.GenerationFile)!;

        // A reclaim whose digest does not match the persisted one (a different command reused the id) is a
        // no-op, even when the generation happens to match.
        model.Reclaim("mismatch", generation, "a-different-digest");

        model.Fs.FileExists(ClaimModel.StdoutFile).Should().BeTrue("a digest mismatch must leave the output intact");
    }

    private static void RunToCompletion(IEnumerable<string> steps)
    {
        foreach (var _ in steps)
        {
        }
    }
}
