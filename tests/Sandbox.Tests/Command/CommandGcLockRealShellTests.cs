using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Capability-guarded real-shell proofs of the per-operation GC lock's owner-token fencing and of the
/// lock/generation-safe output reclaim. The lock scenarios source the ACTUAL
/// <see cref="CommandScripts.GcLockFunctions"/>/<see cref="CommandScripts.UidFunction"/> the wrapper embeds
/// and drive them under a POSIX shell; the reclaim scenario runs the ACTUAL
/// <see cref="CommandScripts.BuildReclaim"/> script. Each skips visibly (or fails when
/// <c>LMSBX_REQUIRE_POSIX_SHELL</c> is set) when no shell is present — never a silently passing contract test.
/// </summary>
public class CommandGcLockRealShellTests
{
    private const string Op = "abcdef0123456789abcdef0123456789";
    private const string GenOld = "00000000000000000000000000000001";
    private const string GenNew = "00000000000000000000000000000002";
    private static readonly string S_digest = new('d', 64);

    // ---- Scenario: a contender during the mkdir -> owner-token establishment gap ----

    [SkippableFact]
    public async Task RealShell_OwnerlessLock_IsTreatedLive_AndAContenderBacksOff()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A holder won the atomic mkdir of the lock but has not yet written its owner token.
        SeedOwnerlessLock(workspace);

        var result = await RunLockAsync(
            workspace,
            owner: "contender",
            body: "if gclock_try; then printf ACQUIRED; else printf BACKED_OFF; fi"
        );

        result.Stdout.Trim().Should().Be("BACKED_OFF", "an owner-less lock is treated LIVE, so a contender in the gap backs off");
        LockOwner(workspace).Should().BeNull("the contender must not reclaim or write an owner token into the establishing lock");
        LockExists(workspace).Should().BeTrue();
    }

    // ---- Scenario: stale owner replacement ----

    [SkippableFact]
    public async Task RealShell_StaleOwnerLock_IsReclaimed_AndReElectedToASingleNewOwner()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A crashed holder's leftover: an owner token stamped in the distant past (older than the TTL).
        SeedLockOwner(workspace, token: "crashed-owner", timestamp: 1);

        var result = await RunLockAsync(
            workspace,
            owner: "replacement",
            body: "if gclock_try; then printf 'OWNED '; cut -d' ' -f1 \"$GCL/owner\"; else printf FAILED; fi"
        );

        result.Stdout.Should().StartWith("OWNED ", "the stale lock is reclaimed");
        result.Stdout.Should().Contain("replacement", "and re-elected to the single new owner");
        OwnerToken(workspace).Should().Be("replacement");
    }

    // ---- Scenario: an old owner's DELAYED release must not remove a successor's lock ----

    [SkippableFact]
    public async Task RealShell_DelayedReleaseByAnOldOwner_NeverRemovesASuccessorsLock()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // A successor currently holds a freshly stamped lock.
        SeedLockOwner(workspace, token: "successor", timestamp: now);

        // The old owner (whose lock the successor already stale-reclaimed) issues its delayed release.
        var delayed = await RunLockAsync(
            workspace,
            owner: "old-owner",
            body: "gclock_release; if [ -d \"$GCL\" ]; then printf PRESERVED; else printf REMOVED; fi"
        );

        delayed.Stdout.Trim().Should().Be("PRESERVED", "a delayed release from an old owner must not remove the successor's lock");
        OwnerToken(workspace).Should().Be("successor", "the successor still owns its lock after the old owner's release");

        // The true owner's release DOES remove it — the fence only blocks a non-owner.
        var owned = await RunLockAsync(
            workspace,
            owner: "successor",
            body: "gclock_release; if [ -d \"$GCL\" ]; then printf PRESERVED; else printf REMOVED; fi"
        );
        owned.Stdout.Trim().Should().Be("REMOVED", "the current owner's release removes its own lock");
    }

    // ---- Scenario: a purger that lost ownership never deletes a replacement active claim ----

    [SkippableFact]
    public async Task RealShell_PurgerThatLostOwnership_FailsThePreDeleteOwnershipReCheck()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The lock is now owned by the successor that installed a replacement active claim.
        SeedLockOwner(workspace, token: "successor", timestamp: now);

        // The old purger re-verifies its own token immediately before the destructive action — and fails.
        var result = await RunLockAsync(
            workspace,
            owner: "old-purger",
            body: "if gclock_owned; then printf WOULD_DELETE; else printf SPARED; fi"
        );

        result.Stdout.Trim().Should().Be("SPARED", "a purger whose token was replaced must decline the delete, sparing the replacement claim");
    }

    [SkippableFact]
    public async Task RealShell_OwnerlessLock_BlocksAStaleSweepPurge_EndToEnd()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A claim that would otherwise be purged (expired + strictly past the retention window)...
        SeedClaimDirectory(workspace, Op, lease: 1, created: 1);
        // ...but an owner-less GC lock (a holder mid-establishment) is present beside it.
        Directory.CreateDirectory(workspace.HostFile($".lmsbx-sdk/ops/{Op}.gc"));

        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(Op), workspace);

        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{Op}"))
            .Should()
            .BeTrue("a purger must treat the owner-less (establishing) lock as live and never delete under it");
    }

    // ---- Scenario: a delayed reclaim from an expired old execution must not touch a newer generation ----

    [SkippableFact]
    public async Task RealShell_DelayedReclaim_FromAnOldGeneration_SparesANewerReExecutionsOutput()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // The directory currently holds a NEWER re-execution (generation GenNew) with fresh output.
        SeedCompletedExecution(workspace, Op, generation: GenNew, digest: S_digest);

        // A delayed reclaim from the expired OLD execution (generation GenOld) lands.
        await PosixShellHarness.RunAsync(CommandScripts.BuildReclaim(Op, GenOld, S_digest), workspace);

        workspace
            .HostFileExists($".lmsbx-sdk/ops/{Op}/stdout")
            .Should()
            .BeTrue("a stale-generation reclaim must not delete a newer re-execution's stdout");
        workspace.HostFileExists($".lmsbx-sdk/ops/{Op}/stderr").Should().BeTrue();
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{Op}.gc"))
            .Should()
            .BeFalse("the reclaim releases the GC lock it took");

        // The reclaim for the CURRENT generation DOES drop the output — proving the negative case is real.
        await PosixShellHarness.RunAsync(CommandScripts.BuildReclaim(Op, GenNew, S_digest), workspace);

        workspace
            .HostFileExists($".lmsbx-sdk/ops/{Op}/stdout")
            .Should()
            .BeFalse("a reclaim for the current generation drops the large output");
        workspace.HostFileExists($".lmsbx-sdk/ops/{Op}/stderr").Should().BeFalse();
        workspace
            .HostFileExists($".lmsbx-sdk/ops/{Op}/manifest.json")
            .Should()
            .BeTrue("the bounded completion marker (manifest) is always retained");
    }

    [SkippableFact]
    public async Task RealShell_Run_MintsAValidatorPassing32HexGeneration_MatchingItsSidecar()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var argv = new[] { "sh", "-c", "printf out; printf err 1>&2" };
        var script = CommandScripts.BuildRun(Op, S_digest, PosixArgv.Join(argv), string.Empty, 120);

        var result = await PosixShellHarness.RunAsync(script, workspace);

        var (kind, payload) = CommandSentinel.Parse(result.Stdout);
        kind.Should().Be(CommandSentinel.KindManifest);
        var manifest = JsonSerializer.Deserialize<CommandManifest>(
            Convert.FromBase64String(payload!),
            CommandManifest.Json
        )!;
        manifest
            .Generation.Should()
            .MatchRegex("^[0-9a-f]{32}$", "the wrapper's lmsbx_uid mints a 32-hex per-execution generation");
        var validate = () => CommandManifestValidator.Validate(manifest, "op-1");
        validate.Should().NotThrow("the wrapper's generation must pass the manifest validator end-to-end");
        // The persisted gen sidecar (which the reclaim re-reads under the lock) matches the manifest exactly.
        var sidecar = await File.ReadAllTextAsync(workspace.HostFile($".lmsbx-sdk/ops/{Op}/gen"));
        sidecar.Should().Be(manifest.Generation);
    }

    private static async Task<ShellResult> RunLockAsync(ShellWorkspace workspace, string owner, string body, long ttl = 300)
    {
        var script = string.Join(
            '\n',
            "GCL=\"$SANDBOX_WORKSPACE/op.gc\"",
            $"OWNER='{owner}'",
            $"GCLOCKTTL={ttl}",
            CommandScripts.UidFunction,
            CommandScripts.GcLockFunctions,
            body
        );
        return await PosixShellHarness.RunAsync(script, workspace);
    }

    private static void SeedOwnerlessLock(ShellWorkspace workspace) =>
        Directory.CreateDirectory(workspace.HostFile("op.gc"));

    private static void SeedLockOwner(ShellWorkspace workspace, string token, long timestamp)
    {
        var directory = workspace.HostFile("op.gc");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner"), $"{token} {timestamp}");
    }

    private static bool LockExists(ShellWorkspace workspace) => Directory.Exists(workspace.HostFile("op.gc"));

    private static string? LockOwner(ShellWorkspace workspace)
    {
        var path = workspace.HostFile("op.gc/owner");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? OwnerToken(ShellWorkspace workspace) => LockOwner(workspace)?.Split(' ')[0];

    private static void SeedClaimDirectory(ShellWorkspace workspace, string name, long lease, long created)
    {
        var directory = workspace.HostFile($".lmsbx-sdk/ops/{name}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "lease"), lease.ToString());
        File.WriteAllText(Path.Combine(directory, "created"), created.ToString());
    }

    /// <summary>Seeds a fully committed execution: the marker files (manifest/gen/digest/lease/created) plus the large stdout/stderr a reclaim targets.</summary>
    private static void SeedCompletedExecution(ShellWorkspace workspace, string name, string generation, string digest)
    {
        var directory = workspace.HostFile($".lmsbx-sdk/ops/{name}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "gen"), generation);
        File.WriteAllText(Path.Combine(directory, "digest"), digest);
        File.WriteAllText(Path.Combine(directory, "lease"), "1");
        File.WriteAllText(Path.Combine(directory, "created"), "1");
        File.WriteAllText(Path.Combine(directory, "stdout"), "captured-stdout-bytes");
        File.WriteAllText(Path.Combine(directory, "stderr"), "captured-stderr-bytes");
    }
}
