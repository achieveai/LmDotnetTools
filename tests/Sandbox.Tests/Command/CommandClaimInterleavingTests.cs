using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F2: a deterministic, script-level state-machine model of the RUN claim in
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts.BuildRun"/>, driven through the
/// exact concurrent interleaving that used to double-run a command. Two callers using the same
/// operation id are stepped explicitly — caller A wins the <c>mkdir</c> claim and is paused BEFORE it
/// establishes its lease, then caller B runs to completion, then A resumes — and the number of
/// side-effecting command runs is asserted.
/// </summary>
/// <remarks>
/// The model mirrors the shell wrapper step for step (see the mapped comments). The pre-fix variant is
/// retained ONLY to prove this test genuinely reproduces the old hole: with the old lease-defaults-to-0
/// takeover and created-before-lease ordering the interleaving yields TWO side effects, whereas the
/// shipped logic (lease established first, takeover gated on an existing lease) yields exactly ONE.
/// The real generated script is separately exercised by the capability-guarded <c>CommandRealShellTests</c>.
/// </remarks>
public class CommandClaimInterleavingTests
{
    [Fact]
    public void PreFixLogic_ConcurrentEstablishmentWindow_RunsTheCommandTwice_ReproducingTheHole()
    {
        var model = new ClaimModel(leaseFirst: false, leaseGuardedTakeover: false);

        var sideEffects = RunEstablishmentWindowInterleaving(model);

        // The old hole: B reads a missing lease as 0, deletes A's just-won claim, and re-runs — so the
        // one operation's command executes for BOTH callers.
        sideEffects.Should().HaveCount(2).And.Contain("A").And.Contain("B");
    }

    [Fact]
    public void ShippedLogic_ConcurrentEstablishmentWindow_RunsTheCommandExactlyOnce()
    {
        var model = new ClaimModel(leaseFirst: true, leaseGuardedTakeover: true);

        var sideEffects = RunEstablishmentWindowInterleaving(model);

        // The fix: B sees a claim with no lease yet, refuses to take it over, and reports PENDING; only
        // the winner A runs the command.
        sideEffects.Should().Equal("A");
        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.FileExists(ClaimModel.Manifest).Should().BeTrue();
    }

    [Fact]
    public void ShippedLogic_LeaselessClaim_IsReportedPending_AndNeverDeleted()
    {
        var model = new ClaimModel(leaseFirst: true, leaseGuardedTakeover: true);
        model.Fs.Now = 1_000_000;
        // A winner has won the mkdir claim but has not written its lease yet (establishment window).
        model.Fs.Mkdir(ClaimModel.Op).Should().BeTrue();

        RunToCompletion(model.Run("B"));

        model.Fs.EmittedBy("B").Should().Be("PENDING");
        model.Fs.SideEffects.Should().BeEmpty();
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue("a lease-less claim must never be taken over/deleted");
    }

    [Fact]
    public void ShippedLogic_ExpiredLeaseOfCrashedSubmitter_IsStillTakenOver()
    {
        var model = new ClaimModel(leaseFirst: true, leaseGuardedTakeover: true);
        model.Fs.Now = 1_000_000;
        // A crashed submitter established its lease (now expired) but never committed a manifest.
        model.Fs.Mkdir(ClaimModel.Op);
        model.Fs.Write(ClaimModel.Lease, (model.Fs.Now - 100).ToString());
        model.Fs.Write(ClaimModel.Created, (model.Fs.Now - 10_000).ToString());

        RunToCompletion(model.Run("C"));

        // The lease exists and is expired, so takeover proceeds and the operation is not blocked.
        model.Fs.SideEffects.Should().Equal("C");
        model.Fs.EmittedBy("C").Should().Be("MANIFEST");
    }

    [Fact]
    public void ShippedLogic_LiveLeaseOfPeerSubmitter_IsNotTakenOver()
    {
        var model = new ClaimModel(leaseFirst: true, leaseGuardedTakeover: true);
        model.Fs.Now = 1_000_000;
        // A live peer holds the claim with an unexpired lease and has not committed yet.
        model.Fs.Mkdir(ClaimModel.Op);
        model.Fs.Write(ClaimModel.Lease, (model.Fs.Now + 10_000).ToString());
        model.Fs.Write(ClaimModel.Created, model.Fs.Now.ToString());

        RunToCompletion(model.Run("D"));

        model.Fs.SideEffects.Should().BeEmpty();
        model.Fs.EmittedBy("D").Should().Be("PENDING");
        model.Fs.DirExists(ClaimModel.Op).Should().BeTrue();
    }

    /// <summary>Drives the "A wins the claim, pauses before its lease, B runs fully, A resumes" interleaving and returns the ordered side effects.</summary>
    private static IReadOnlyList<string> RunEstablishmentWindowInterleaving(ClaimModel model)
    {
        model.Fs.Now = 1_000_000;
        var a = model.Run("A").GetEnumerator();
        var b = model.Run("B").GetEnumerator();

        AdvanceUntil(a, ClaimModel.WonClaimBeforeLease);
        RunToCompletion(b);
        RunToCompletion(a);

        return model.Fs.SideEffects;
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
