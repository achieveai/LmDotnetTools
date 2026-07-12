using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Capability-guarded real-shell proofs of the per-operation GC lock's NON-STEALABLE ownership and of the
/// lock/generation-safe output reclaim. The lock scenarios source the ACTUAL
/// <see cref="CommandScripts.GcLockFunctions"/>/<see cref="CommandScripts.UidFunction"/> the wrapper embeds
/// and drive them under a POSIX shell; the run/reclaim scenarios run the ACTUAL
/// <see cref="CommandScripts.BuildRun"/>/<see cref="CommandScripts.BuildReclaim"/>/
/// <see cref="CommandScripts.BuildGcPurge"/> scripts. Each skips visibly (or fails when
/// <c>LMSBX_REQUIRE_POSIX_SHELL</c> is set) when no shell is present — never a silently passing contract test.
/// </summary>
public class CommandGcLockRealShellTests
{
    private const string Op = "abcdef0123456789abcdef0123456789";
    private const string GenOld = "00000000000000000000000000000001";
    private const string GenNew = "00000000000000000000000000000002";
    private static readonly string S_digest = new('d', 64);

    // ---- Scenario: the lock is non-stealable — any existing lock makes a contender back off ----

    public static IEnumerable<object[]> NonStealableLockScenarios =>
        [
            ["an owner-less lock (a holder in the mkdir->token gap)", null!],
            ["a freshly-owned lock", "holder"],
            ["a would-be-stale owned lock (a crashed holder — once TTL-reclaimed, now never)", "crashed-holder 1"],
        ];

    [SkippableTheory]
    [MemberData(nameof(NonStealableLockScenarios))]
    public async Task RealShell_AnyExistingLock_IsNeverStolen_AContenderBacksOff(string scenario, string? ownerContents)
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        SeedLock(workspace, ownerContents);

        var result = await RunLockAsync(
            workspace,
            owner: "contender",
            body: "if gclock_try; then printf STOLE; else printf BACKED_OFF; fi"
        );

        result.Stdout.Trim().Should().Be("BACKED_OFF", scenario + " is non-stealable, so a contender backs off");
        LockExists(workspace).Should().BeTrue("the contender must never remove an existing lock");
        LockOwner(workspace)
            .Should()
            .Be(ownerContents, "the contender must never write or overwrite a lock it does not hold");
    }

    // ---- Scenario: a release from a caller that does not hold the lock must never remove it ----

    [SkippableFact]
    public async Task RealShell_ReleaseByANonOwner_NeverRemovesTheLock_ButTheOwnersReleaseDoes()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        SeedLock(workspace, "owner");

        // A caller that does not hold the lock issues its release — it must be a no-op.
        var byNonOwner = await RunLockAsync(
            workspace,
            owner: "not-the-owner",
            body: "gclock_release; if [ -d \"$GCL\" ]; then printf PRESERVED; else printf REMOVED; fi"
        );

        byNonOwner.Stdout.Trim().Should().Be("PRESERVED", "a release from a non-owner must never remove the lock");
        OwnerToken(workspace).Should().Be("owner", "the true owner still owns its lock after a non-owner's release");

        // The true owner's release DOES remove it — the fence only blocks a non-owner.
        var byOwner = await RunLockAsync(
            workspace,
            owner: "owner",
            body: "gclock_release; if [ -d \"$GCL\" ]; then printf PRESERVED; else printf REMOVED; fi"
        );
        byOwner.Stdout.Trim().Should().Be("REMOVED", "the current owner's release removes its own lock");
    }

    // ---- Scenario: a purger that does not own the lock never deletes ----

    [SkippableFact]
    public async Task RealShell_APurgerThatDoesNotOwnTheLock_FailsThePreDeleteOwnershipReCheck()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        SeedLock(workspace, "owner");

        // The old purger re-verifies its own token immediately before the destructive action — and fails.
        var result = await RunLockAsync(
            workspace,
            owner: "old-purger",
            body: "if gclock_owned; then printf WOULD_DELETE; else printf SPARED; fi"
        );

        result.Stdout.Trim().Should().Be("SPARED", "a caller that does not own the lock must decline the destructive action");
    }

    [SkippableTheory]
    [InlineData("an owner-less establishing lock", null)]
    [InlineData("an owned (crash-orphaned) lock", "crashed-holder 1")]
    public async Task RealShell_AnyPreExistingLock_BlocksAStaleSweepPurge_EndToEnd(string scenario, string? ownerContents)
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A claim that would otherwise be purged (expired + strictly past the retention window)...
        SeedClaimDirectory(workspace, Op, lease: 1, created: 1);
        // ...but a pre-existing GC lock beside it (in-flight or crash-orphaned).
        Directory.CreateDirectory(workspace.HostFile($".lmsbx-sdk/ops/{Op}.gc"));
        if (ownerContents is not null)
        {
            File.WriteAllText(workspace.HostFile($".lmsbx-sdk/ops/{Op}.gc/owner"), ownerContents);
        }

        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(Op), workspace);

        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{Op}"))
            .Should()
            .BeTrue(scenario + " is non-stealable, so the purger never wins the lock and never deletes under it");
    }

    // ---- Scenario: a crash-orphaned cleanup lock freezes same-id recovery, but the manifest fast path survives ----

    [SkippableFact]
    public async Task RealShell_OrphanLock_FreezesSameIdRunRecovery_ButAManifestIsStillFastPathed()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var argv = PosixArgv.Join(["sh", "-c", "printf out"]);

        // An abandoned, expired claim (established-but-expired lease, no manifest) beside a crash-orphaned GC
        // lock. Normally a same-id RUN would self-recover the abandoned claim; the orphan lock freezes it.
        SeedClaimDirectory(workspace, Op, lease: 1, created: 1);
        Directory.CreateDirectory(workspace.HostFile($".lmsbx-sdk/ops/{Op}.gc"));

        var frozen = await PosixShellHarness.RunAsync(
            CommandScripts.BuildRun(Op, S_digest, argv, string.Empty, 120),
            workspace
        );

        CommandSentinel
            .Parse(frozen.Stdout)
            .Kind.Should()
            .Be(
                CommandSentinel.KindPending,
                "a crash-orphaned cleanup lock is retained interrupted state — same-id recovery is frozen, never re-run"
            );
        workspace
            .HostFileExists($".lmsbx-sdk/ops/{Op}/manifest.json")
            .Should()
            .BeFalse("the frozen RUN must not have run the command (no duplicate side effect)");
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{Op}.gc"))
            .Should()
            .BeTrue("the orphan lock is left intact — never stolen");

        // A committed manifest is ALWAYS recovered from the fast path, which never consults the lock — so
        // 24h retention / same-id idempotency survives even while the orphan lock is present.
        File.WriteAllText(workspace.HostFile($".lmsbx-sdk/ops/{Op}/manifest.json"), "{}");

        var recovered = await PosixShellHarness.RunAsync(
            CommandScripts.BuildRun(Op, S_digest, argv, string.Empty, 120),
            workspace
        );

        CommandSentinel
            .Parse(recovered.Stdout)
            .Kind.Should()
            .Be(CommandSentinel.KindManifest, "a committed manifest is fast-pathed even under an orphan lock");
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

    private static async Task<ShellResult> RunLockAsync(ShellWorkspace workspace, string owner, string body)
    {
        var script = string.Join(
            '\n',
            "GCL=\"$SANDBOX_WORKSPACE/op.gc\"",
            $"OWNER='{owner}'",
            CommandScripts.UidFunction,
            CommandScripts.GcLockFunctions,
            body
        );
        return await PosixShellHarness.RunAsync(script, workspace);
    }

    /// <summary>Seeds a per-operation GC lock: the <c>op.gc</c> directory, optionally with an exact <c>owner</c> file (a holder mid-establishment has none).</summary>
    private static void SeedLock(ShellWorkspace workspace, string? ownerContents)
    {
        var directory = workspace.HostFile("op.gc");
        Directory.CreateDirectory(directory);
        if (ownerContents is not null)
        {
            File.WriteAllText(Path.Combine(directory, "owner"), ownerContents);
        }
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
