using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// A deterministic, script-level state-machine model of the RUN claim and the stale-sweep purge in
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts"/>, driven through the exact
/// concurrent interleavings that must NOT double-run a command or delete a live claim. The shipped
/// wrapper elects a submitter with a single atomic <c>mkdir</c>; an abandoned (expired-but-uncommitted)
/// claim is self-recovered — but only under the per-operation GC lock, after re-validating under that
/// lock, and re-electing exactly one new claimant — so at most one caller ever runs.
/// </summary>
/// <remarks>
/// The pre-fix variants are retained ONLY to prove these tests genuinely reproduce the holes the fixes
/// close: an unlocked expired-lease takeover double-runs the command, and two unlocked purgers delete a
/// replacement active claim. With the shipped GC-locked logic the same interleavings run the command at
/// most once and never delete a live claim. The real generated scripts are separately exercised by the
/// capability-guarded <c>CommandRealShellTests</c>/<c>CommandRealShellHardeningTests</c>.
/// </remarks>
public class CommandClaimInterleavingTests
{
    [Fact]
    public void PreFixExpiredLeaseTakeover_TwoContenders_DoubleRunsTheCommand_ReproducingTheHole()
    {
        var model = new ClaimModel(takeoverEnabled: true);
        SeedExpiredCrashedClaim(model);

        var a = model.Run("A").GetEnumerator();
        var b = model.Run("B").GetEnumerator();
        // Both contenders see the same expired lease and decide to take it over before either removes it.
        AdvanceUntil(a, ClaimModel.DecidedTakeoverBeforeRemove);
        AdvanceUntil(b, ClaimModel.DecidedTakeoverBeforeRemove);
        RunToCompletion(a); // A rm -rf's, re-mkdirs, runs, commits.
        RunToCompletion(b); // B rm -rf's A's fresh claim+manifest, re-mkdirs, and runs AGAIN.

        // The old hole: the one operation's command executes for BOTH callers.
        model.Fs.SideEffects.Should().HaveCount(2).And.Contain("A").And.Contain("B");
    }

    [Fact]
    public void ShippedSelfRecovery_TwoContendersOnExpiredLease_ExactlyOneRuns()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredCrashedClaim(model);

        RunToCompletion(model.Run("A"));
        RunToCompletion(model.Run("B"));

        // Exactly one contender self-recovers the abandoned claim and runs; the other observes the fresh
        // committed manifest and recovers it — never a double-run.
        model.Fs.SideEffects.Should().Equal("A");
        model.Fs.EmittedBy("A").Should().Be("MANIFEST");
        model.Fs.EmittedBy("B").Should().Be("MANIFEST", "the second caller recovers the first's committed result");
    }

    [Fact]
    public void ShippedSelfRecovery_SameIdExpiredRecovery_InterleavedContenders_YieldAtMostOneSideEffect()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredCrashedClaim(model);

        var a = model.Run("A").GetEnumerator();
        // A wins the GC lock, deletes the abandoned claim, releases the lock — but pauses before it
        // re-elects the new claimant.
        AdvanceUntil(a, ClaimModel.RecoveredBeforeReElect);
        RunToCompletion(model.Run("B")); // B wins the now-free atomic mkdir and runs.
        RunToCompletion(a); // A's re-election mkdir fails; A recovers B's committed manifest instead.

        // Even interleaved at the recovery seam, the command's side effect happens at most once.
        model.Fs.SideEffects.Should().Equal("B");
        model.Fs.EmittedBy("B").Should().Be("MANIFEST");
        model.Fs.EmittedBy("A").Should().Be("MANIFEST");
    }

    [Fact]
    public void ShippedNoTakeover_SimultaneousContendersOnFreshOp_RunTheCommandExactlyOnce()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        model.Fs.Now = 1_000_000;

        var a = model.Run("A").GetEnumerator();
        var b = model.Run("B").GetEnumerator();
        AdvanceUntil(a, ClaimModel.WonClaimBeforeLease); // A wins the atomic mkdir, pauses before its lease.
        RunToCompletion(b); // B loses the mkdir; the claim is fresh (not expired), so nothing to recover — PENDING.
        RunToCompletion(a); // A runs and commits.

        model.Fs.SideEffects.Should().Equal("A");
        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.FileExists(ClaimModel.Manifest).Should().BeTrue();
    }

    [Fact]
    public void ShippedSelfRecovery_ExpiredLeaseSingleCaller_SelfRecoversAndRunsExactlyOnce()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredCrashedClaim(model);

        RunToCompletion(model.Run("C"));

        // A lone same-id retry no longer waits for the 24h sweep: it self-recovers the abandoned claim
        // and runs exactly once, without depending on any unrelated command.
        model.Fs.SideEffects.Should().Equal("C");
        model.Fs.EmittedBy("C").Should().Be("MANIFEST");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("the re-elected claim is committed");
    }

    [Fact]
    public void ShippedSelfRecovery_LeaselessClaim_IsReportedPending_AndNeverRecovered()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        model.Fs.Now = 1_000_000;
        // A winner has won the mkdir claim but has not written its lease yet (establishment window).
        model.Fs.Mkdir(ClaimModel.Op).Should().BeTrue();

        RunToCompletion(model.Run("B"));

        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.SideEffects.Should().BeEmpty();
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("a lease-less (mid-establishment) claim is never recovered or deleted");
    }

    /// <summary>Seeds a crashed submitter's leftovers: the claim exists with an established-but-expired lease and no manifest.</summary>
    private static void SeedExpiredCrashedClaim(ClaimModel model)
    {
        model.Fs.Now = 1_000_000;
        model.Fs.Mkdir(ClaimModel.Op);
        model.Fs.Write(ClaimModel.Lease, (model.Fs.Now - 100).ToString());
        model.Fs.Write(ClaimModel.Created, (model.Fs.Now - 10_000).ToString());
    }

    // ---- F3: atomic purge election via the per-operation GC lock ----

    [Fact]
    public void PreFixUnlockedPurgers_SecondPurgerDeletesAReplacementActiveClaim_ReproducingTheHole()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredOldCrashedClaim(model);

        var a = model.Purge("A", lockGuarded: false).GetEnumerator();
        var b = model.Purge("B", lockGuarded: false).GetEnumerator();
        // Both unlocked purgers re-validate the SAME expired+old claim as stale before either deletes.
        AdvanceUntil(a, ClaimModel.DecidedPurgeWithoutLock);
        AdvanceUntil(b, ClaimModel.DecidedPurgeWithoutLock);
        RunToCompletion(a); // A deletes the abandoned claim.
        RunToCompletion(model.Run("N")); // A fresh, ACTIVE claim replaces it (no lock blocks it).
        RunToCompletion(b); // B — already past re-validation — deletes the REPLACEMENT active claim.

        // The hole: the replacement active claim (whose command already ran) is destroyed by a second
        // purger that decided to delete from a stale view.
        model.Fs.Deletions.Should().Equal("A", "B");
        model.Fs.DirExists(ClaimModel.Op).Should().BeFalse("the second unlocked purger deleted the live replacement");
    }

    [Fact]
    public void LockedPurge_SecondPurgerCannotDeleteAReplacementActiveClaim()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredOldCrashedClaim(model);

        var a = model.Purge("A", lockGuarded: true).GetEnumerator();
        AdvanceUntil(a, ClaimModel.WonGcLock); // A wins the GC lock, before revalidating/deleting.
        RunToCompletion(model.Purge("B", lockGuarded: true)); // B loses the live lock: never deletes.
        RunToCompletion(a); // A revalidates (still stale), deletes the abandoned claim, releases.

        RunToCompletion(model.Run("N")); // A fresh, ACTIVE claim replaces it once the lock is released.
        RunToCompletion(model.Purge("C", lockGuarded: true)); // A second purger of the now-active op.

        // Only the abandoned claim was ever deleted; the losing purger and the second purger both
        // declined to delete, so the replacement active claim (which ran exactly once) survives.
        model.Fs.Deletions.Should().Equal("A");
        model.Fs.SideEffects.Should().Equal("N");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("the replacement active claim is re-validated and spared");
    }

    [Fact]
    public void LockedPurge_LoserOfTheLockElection_NeverDeletes()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredOldCrashedClaim(model);

        var a = model.Purge("A", lockGuarded: true).GetEnumerator();
        AdvanceUntil(a, ClaimModel.WonGcLock); // A holds the lock.
        var b = model.Purge("B", lockGuarded: true).GetEnumerator();
        AdvanceUntil(b, ClaimModel.LostGcLock); // B could not acquire the live lock.

        RunToCompletion(b);
        model.Fs.Deletions.Should().BeEmpty("a purger that lost the GC-lock election must never delete");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue();
    }

    [Fact]
    public void LockedPurge_StaleAbandonedClaim_IsDeletedByTheSoleLockWinner()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredOldCrashedClaim(model);

        RunToCompletion(model.Purge("A", lockGuarded: true));

        // The sole purger wins the lock, re-validates the claim as stale, and deletes it.
        model.Fs.Deletions.Should().Equal("A");
        model.Fs.DirExists(ClaimModel.Op).Should().BeFalse();
    }

    /// <summary>Seeds a crashed submitter's leftovers that are ALSO strictly past the 24h retention window (eligible for the stale sweep).</summary>
    private static void SeedExpiredOldCrashedClaim(ClaimModel model)
    {
        model.Fs.Now = 10_000_000;
        model.Fs.Mkdir(ClaimModel.Op);
        model.Fs.Write(ClaimModel.Lease, (model.Fs.Now - CommandArtifactLayout.StaleAgeSeconds).ToString());
        model.Fs.Write(ClaimModel.Created, (model.Fs.Now - (CommandArtifactLayout.StaleAgeSeconds * 2)).ToString());
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
