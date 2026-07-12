using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// A deterministic, script-level state-machine model of the RUN claim in
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts.BuildRun"/>, driven through the
/// exact concurrent interleavings that must NOT double-run a command. The shipped wrapper elects a
/// submitter with a single atomic <c>mkdir</c> and never deletes or takes over an existing claim, so no
/// two callers can both run: a loser reports PENDING, and an abandoned (expired-but-uncommitted) claim
/// is left for the guarded stale sweep rather than reclaimed by a racy takeover.
/// </summary>
/// <remarks>
/// The pre-fix takeover variant is retained ONLY to prove these tests genuinely reproduce the hole the
/// removal closes: with an expired-lease takeover, two contenders both delete-and-recreate the claim and
/// the command runs TWICE; with the shipped no-takeover logic the same interleaving runs it ZERO times
/// (both PENDING) and a fresh-op race runs it exactly ONCE. The real generated script is separately
/// exercised by the capability-guarded <c>CommandRealShellTests</c>.
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
    public void ShippedNoTakeover_TwoContendersOnExpiredLease_NeitherRuns_BothReportPending()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredCrashedClaim(model);

        RunToCompletion(model.Run("A"));
        RunToCompletion(model.Run("B"));

        // No Execute path deletes/takes over the claim, so neither contender can run — the double-run is
        // impossible by construction. The abandoned claim is left for the guarded stale sweep.
        model.Fs.SideEffects.Should().BeEmpty();
        model.Fs.EmittedBy("A").Should().Be("PENDING");
        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("an abandoned claim is never taken over, only GC'd after inactivity");
    }

    [Fact]
    public void ShippedNoTakeover_SimultaneousContendersOnFreshOp_RunTheCommandExactlyOnce()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        model.Fs.Now = 1_000_000;

        var a = model.Run("A").GetEnumerator();
        var b = model.Run("B").GetEnumerator();
        AdvanceUntil(a, ClaimModel.WonClaimBeforeLease); // A wins the atomic mkdir, pauses before its lease.
        RunToCompletion(b); // B loses the mkdir, cannot take over, reports PENDING.
        RunToCompletion(a); // A runs and commits.

        model.Fs.SideEffects.Should().Equal("A");
        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.FileExists(ClaimModel.Manifest).Should().BeTrue();
    }

    [Fact]
    public void ShippedNoTakeover_ExpiredLeaseSingleCaller_IsReportedPending_ClaimRetainedForGc()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        SeedExpiredCrashedClaim(model);

        RunToCompletion(model.Run("C"));

        model.Fs.SideEffects.Should().BeEmpty();
        model.Fs.EmittedBy("C").Should().Be("PENDING");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue();
    }

    [Fact]
    public void ShippedNoTakeover_LeaselessClaim_IsReportedPending_AndNeverDeleted()
    {
        var model = new ClaimModel(takeoverEnabled: false);
        model.Fs.Now = 1_000_000;
        // A winner has won the mkdir claim but has not written its lease yet (establishment window).
        model.Fs.Mkdir(ClaimModel.Op).Should().BeTrue();

        RunToCompletion(model.Run("B"));

        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.SideEffects.Should().BeEmpty();
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("a lease-less claim must never be taken over/deleted");
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
