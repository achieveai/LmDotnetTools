using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Deterministic, script-level state-machine proofs of the per-operation GC lock's REAL exclusive,
/// NON-STEALABLE ownership and of the lock/generation-safe output reclaim, driven through the exact
/// interleavings that must never yield double ownership, remove a lock the caller does not hold, or delete
/// a newer execution's output. Each test mirrors the primitives
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts"/> emits (<c>gclock_try</c>,
/// <c>gclock_owned</c>, <c>gclock_release</c>, and the generation-gated reclaim); the real generated
/// scripts are exercised under a POSIX shell by <c>CommandGcLockRealShellTests</c>.
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

    // ---- Scenario: the lock is non-stealable — any existing lock makes a contender back off ----

    [Theory]
    [InlineData("an owner-less lock (a holder in the mkdir->token gap)", false, null)]
    [InlineData("a freshly-owned lock", true, "holder")]
    [InlineData("a would-be-stale owned lock (a crashed holder — once TTL-reclaimed, now never)", true, "crashed-holder")]
    public void AnyExistingLock_IsNeverStolen_AContenderBacksOff_AndTheLockAndOwnerAreUntouched(
        string scenario,
        bool hasOwner,
        string? ownerToken
    )
    {
        var model = NewModel();
        // A holder has won the atomic mkdir of the lock (optionally having written its owner token).
        model.Fs.Mkdir(ClaimModel.GcLock).Should().BeTrue(scenario);
        if (hasOwner)
        {
            model.Fs.Write(ClaimModel.GcLockOwner, ownerToken!);
        }

        var acquired = model.GcLockTry("contender");

        acquired.Should().BeFalse("a non-stealable lock can never be acquired by a contender while it exists");
        model.Fs.DirExists(ClaimModel.GcLock).Should().BeTrue("the contender must never remove an existing lock");
        model
            .Fs.Read(ClaimModel.GcLockOwner)
            .Should()
            .Be(hasOwner ? ownerToken : null, "the contender must never write or overwrite a lock it does not hold");
        model.GcLockOwned("contender").Should().BeFalse();
    }

    // ---- Scenario: a release from a caller that does not hold the lock must never remove it ----

    [Fact]
    public void ReleaseByANonOwner_NeverRemovesTheLock_ButTheOwnersReleaseDoes()
    {
        var model = NewModel();
        model.Fs.Mkdir(ClaimModel.GcLock);
        model.Fs.Write(ClaimModel.GcLockOwner, "owner");

        // A caller that does not hold the lock issues a release — it must be a no-op.
        model.GcLockRelease("not-the-owner");

        model.Fs.DirExists(ClaimModel.GcLock).Should().BeTrue("a release from a non-owner must never remove the lock");
        model.GcLockOwned("owner").Should().BeTrue("the true owner still owns its lock after a non-owner's release");

        // The true owner's release DOES remove it — the fence only blocks a non-owner.
        model.GcLockRelease("owner");
        model.Fs.DirExists(ClaimModel.GcLock).Should().BeFalse("the current owner's release removes its own lock");
    }

    // ---- Scenario: a contender replacing the lock AFTER the owner's ownership check is impossible ----

    [Fact]
    public void AContenderReplacingTheLockAfterTheOwnersOwnershipCheck_Fails_SoTheOwnersDeleteIsSafe()
    {
        var model = NewModel();
        // An abandoned, expired claim the owner is about to self-recover (delete) under the lock.
        SeedExpiredClaim(model);

        // The owner wins the non-stealable lock and passes its pre-delete ownership re-check.
        model.GcLockTry("owner").Should().BeTrue();
        model.GcLockOwned("owner").Should().BeTrue("the owner holds the lock right up to its destructive action");

        // At the ownership-check -> delete seam a contender tries to steal/replace the lock — and fails,
        // because the lock is held and non-stealable, so it cannot be removed or its token overwritten.
        model.GcLockTry("contender").Should().BeFalse("the held lock is non-stealable");
        model.Fs.Read(ClaimModel.GcLockOwner).Should().Be("owner", "the contender could not replace the owner token");

        // The owner — still the sole owner — proceeds with its destructive delete exactly once.
        model.GcLockOwned("owner").Should().BeTrue();
        model.Fs.DeleteOp("owner", ClaimModel.Op);
        model.Fs.Deletions.Should().Equal("owner");
        model.GcLockRelease("owner");
        model.Fs.DirExists(ClaimModel.GcLock).Should().BeFalse("the owner releases the lock after its critical section");
    }

    // ---- Scenario: the generation cannot be replaced under a reclaim's held lock ----

    [Fact]
    public void AContenderCannotReplaceTheGenerationOrLockAfterAReclaimsCheck_SoTheDeleteStaysGenerationSafe()
    {
        var model = NewModel();
        RunToCompletion(model.Run("first")); // Execution 1 commits generation gen-1 with its captured output.
        var generation = model.Fs.Read(ClaimModel.GenerationFile)!;

        // The reclaim for gen-1 acquires the non-stealable lock and passes its ownership+generation check,
        // pausing just before the delete.
        var reclaim = model.ReclaimSteps("reclaimer", generation, ClaimModel.Digest).GetEnumerator();
        AdvanceUntil(reclaim, ClaimModel.CheckedReclaimEligibilityBeforeDelete);

        // While the reclaim holds the lock a contender tries to (a) steal the lock and (b) replace the
        // generation by re-claiming the directory. Replacing the generation requires an rm+re-claim that
        // needs this very lock, so both attempts fail and the current generation cannot change under us.
        model.GcLockTry("contender").Should().BeFalse("the reclaim's lock is non-stealable");
        model
            .Fs.Read(ClaimModel.GenerationFile)
            .Should()
            .Be(generation, "the generation cannot be replaced while the reclaim holds the lock");

        // The reclaim resumes and drops exactly the generation it verified, then releases.
        RunToCompletion(reclaim);
        model.Fs.FileExists(ClaimModel.StdoutFile).Should().BeFalse("the reclaim drops the verified generation's stdout");
        model.Fs.FileExists(ClaimModel.StderrFile).Should().BeFalse();
        model.Fs.DirExists(ClaimModel.GcLock).Should().BeFalse("the reclaim releases the lock it held");
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

    /// <summary>Seeds a crashed submitter's leftovers: the claim exists with an established-but-expired lease and no manifest.</summary>
    private static void SeedExpiredClaim(ClaimModel model)
    {
        model.Fs.Mkdir(ClaimModel.Op);
        model.Fs.Write(ClaimModel.Lease, (Now - 100).ToString());
        model.Fs.Write(ClaimModel.Created, (Now - 10_000).ToString());
    }

    private static void AdvanceUntil(IEnumerator<string> steps, string label)
    {
        while (steps.MoveNext())
        {
            if (steps.Current == label)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Model never reached the '{label}' step.");
    }

    private static void RunToCompletion(IEnumerable<string> steps)
    {
        foreach (var _ in steps)
        {
        }
    }

    private static void RunToCompletion(IEnumerator<string> steps)
    {
        while (steps.MoveNext())
        {
        }
    }
}
