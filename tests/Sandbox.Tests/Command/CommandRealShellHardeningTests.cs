using System.Security.Cryptography;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Capability-guarded real-shell proofs of the reliability/security hardening: portable file hashing
/// (F1 finding #1), re-validated stale purge (finding #4), stale-listing name validation/quoting against
/// injection (finding #5), atomic manifest publish observed by concurrent probes (finding #8), and umask
/// scoping so only SDK artifacts are hardened while the command's own files inherit the caller's umask
/// (finding #12). Each test runs the ACTUAL generated script under an available POSIX shell and skips
/// visibly (or fails when <c>LMSBX_REQUIRE_POSIX_SHELL</c> is set) when no shell is present — never a
/// silently passing contract test.
/// </summary>
public class CommandRealShellHardeningTests
{
    private const string OpA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OpB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OpC = "cccccccccccccccccccccccccccccccc";
    private static readonly string S_digest = new('d', 64);

    private const string PermsFunction =
        "perms() { stat -c '%a' \"$1\" 2>/dev/null || stat -f '%Lp' \"$1\" 2>/dev/null; }";

    [SkippableFact]
    public async Task RealShell_PortableSha256_MatchesDotNet_AcrossSha256sumAndShasumFallback()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // Every byte value, so the hashing is proven binary-safe over stdin (not just printable text).
        var content = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(workspace.HostFile("data.bin"), content);
        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // Primary: whichever tool the shell provides, always exactly 64 lowercase hex.
        (await HashViaHelperAsync(workspace, prefix: string.Empty)).Should().Be(expected);

        // Force the shasum -a 256 fallback by shadowing sha256sum; assert only where shasum exists.
        if (await HasCommandAsync(workspace, "shasum"))
        {
            (await HashViaHelperAsync(workspace, "sha256sum() { return 127; }\n"))
                .Should()
                .Be(expected, "the shasum -a 256 fallback must produce the identical 64-hex digest");
        }

        // Force the sha256sum path by shadowing shasum; assert only where sha256sum exists.
        if (await HasCommandAsync(workspace, "sha256sum"))
        {
            (await HashViaHelperAsync(workspace, "shasum() { return 127; }\n")).Should().Be(expected);
        }
    }

    [SkippableFact]
    public async Task RealShell_GcPurge_ReValidatesAtDeleteTime_PurgesStaleButSparesRefreshed()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // Both looked stale when the SDK listed them (expired lease, ancient created).
        SeedClaimDirectory(workspace, OpA, lease: 1, created: 1);
        SeedClaimDirectory(workspace, OpB, lease: 1, created: 1);
        // ...but OpB is re-activated (a fresh, far-future lease) between the listing and the purge.
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(workspace.HostFile($".lmsbx-sdk/ops/{OpB}/lease"), (nowUnix + 100_000).ToString());
        File.WriteAllText(workspace.HostFile($".lmsbx-sdk/ops/{OpB}/created"), nowUnix.ToString());

        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(OpA), workspace);
        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(OpB), workspace);

        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{OpA}"))
            .Should()
            .BeFalse("a directory still stale at delete time is purged after re-validation");
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{OpB}"))
            .Should()
            .BeTrue("a directory refreshed since the listing must survive the delete-time re-validation");
    }

    [SkippableFact]
    public async Task RealShell_GcPurge_SecondPurgerCannotDeleteAReplacementActiveClaim()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // An abandoned claim that is expired AND strictly past the retention window (ancient lease/created).
        SeedClaimDirectory(workspace, OpA, lease: 1, created: 1);

        // The first purger wins the GC lock, re-validates it as stale, and removes it.
        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(OpA), workspace);
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{OpA}"))
            .Should()
            .BeFalse("the stale abandoned claim is purged under the GC lock");

        // A fresh claimant replaces it with an ACTIVE claim (a far-future lease, no manifest yet).
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SeedClaimDirectory(workspace, OpA, lease: nowUnix + 100_000, created: nowUnix);

        // A second purger re-validates the CURRENT state under the lock and must spare the replacement.
        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(OpA), workspace);

        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{OpA}"))
            .Should()
            .BeTrue("a re-validating, GC-locked purger never deletes a replacement active claim");
    }

    [SkippableFact]
    public async Task RealShell_Run_RespectsALiveGcLock_ReportsPending_WithoutCreatingAClaimOrRunning()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A live GC lock on the op models a purge in progress the claim must not race.
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SeedGcLock(workspace, OpA, timestamp: nowUnix);
        var argv = new[] { "sh", "-c", "printf ran > \"$SANDBOX_WORKSPACE/RAN\"" };
        var script = CommandScripts.BuildRun(OpA, S_digest, PosixArgv.Join(argv), string.Empty, 120);

        var result = await PosixShellHarness.RunAsync(script, workspace);

        CommandSentinel.Parse(result.Stdout).Kind.Should().Be(CommandSentinel.KindPending);
        workspace
            .HostFileExists("RAN")
            .Should()
            .BeFalse("a claim is never created into a live purge, so the command must not run");
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{OpA}"))
            .Should()
            .BeFalse("no claim directory is created while a live GC lock is held");
    }

    [SkippableFact]
    public async Task RealShell_Run_ReclaimsAStaleGcLock_ThenClaimsAndRunsExactlyOnce()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A crashed purger's stale GC lock (ancient timestamp) must not block a fresh run forever.
        SeedGcLock(workspace, OpA, timestamp: 1);
        var argv = new[] { "sh", "-c", "printf ran > \"$SANDBOX_WORKSPACE/RAN\"" };
        var script = CommandScripts.BuildRun(OpA, S_digest, PosixArgv.Join(argv), string.Empty, 120);

        var result = await PosixShellHarness.RunAsync(script, workspace);

        CommandSentinel.Parse(result.Stdout).Kind.Should().Be(CommandSentinel.KindManifest);
        workspace.HostFileExists("RAN").Should().BeTrue("a stale GC lock is reclaimed, so the claim proceeds and runs");
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{OpA}.gc"))
            .Should()
            .BeFalse("the reclaimed stale GC lock is removed, not left behind");
    }

    [SkippableFact]
    public async Task RealShell_StaleListing_IgnoresMaliciousDirectoryNames_AndPurgeNeverExecutesThem()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A directory whose NAME is a command-substitution injection attempt, seeded as a stale claim.
        const string malicious = "$(touch pwned)";
        SeedClaimDirectory(workspace, malicious, lease: 1, created: 1);
        SeedClaimDirectory(workspace, OpC, lease: 1, created: 1);

        var listing = await PosixShellHarness.RunAsync(CommandScripts.BuildGc(256), workspace);
        var names = CommandSentinel.ParseGcListing(listing.Stdout).Select(entry => entry.Name).ToList();

        names.Should().Contain(OpC, "a legitimate 32-hex stale directory is still listed");
        names.Should().NotContain(malicious, "a non-hex name is filtered out of the listing entirely");
        workspace
            .HostFileExists("pwned")
            .Should()
            .BeFalse("the listing never executes the substitution embedded in the directory name");

        // Even if a malicious name were somehow handed to a purge, the PosixArgv single-quoting keeps the
        // substitution inert (the seeded stale directory may be removed, but nothing is executed).
        await PosixShellHarness.RunAsync(CommandScripts.BuildGcPurge(malicious), workspace);
        workspace
            .HostFileExists("pwned")
            .Should()
            .BeFalse("the purge single-quotes the name, so no command substitution ever runs");
    }

    [SkippableFact]
    public async Task RealShell_ConcurrentProbesDuringRun_NeverObserveAPartialManifest()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // The command pauses briefly so probes overlap the claim window and the manifest publish.
        var argv = new[] { "sh", "-c", "sleep 0.4; printf out; printf err 1>&2" };
        var runScript = CommandScripts.BuildRun(OpA, S_digest, PosixArgv.Join(argv), string.Empty, 120);
        var probeScript = CommandScripts.BuildProbe(OpA);

        var runTask = PosixShellHarness.RunAsync(runScript, workspace);
        var probeTexts = new List<string>();
        for (var i = 0; i < 8 && !runTask.IsCompleted; i++)
        {
            probeTexts.Add((await PosixShellHarness.RunAsync(probeScript, workspace)).Stdout);
        }

        var final = await runTask;
        probeTexts.Add((await PosixShellHarness.RunAsync(probeScript, workspace)).Stdout);

        CommandSentinel.Parse(final.Stdout).Kind.Should().Be(CommandSentinel.KindManifest);
        // Every concurrent probe observed a well-formed state — NONE, PENDING, or a fully decodable and
        // valid MANIFEST — never a torn, partially-written manifest.
        foreach (var probeText in probeTexts)
        {
            AssertWellFormedProbeState(probeText);
        }
    }

    [SkippableFact]
    public async Task RealShell_Umask_RestrictiveForSdkArtifacts_ButInheritedForTheCommandsOwnFiles()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        Skip.IfNot(
            await UmaskIsHonoredAsync(workspace),
            "the shell's filesystem does not honor umask (e.g. a Windows drvfs mount); permission scoping is not observable here."
        );

        var argv = new[] { "sh", "-c", "printf hi > \"$SANDBOX_WORKSPACE/cmd_created\"" };
        // Run under a known, non-restrictive caller umask so the command's file is observably NOT owner-only.
        var script = "umask 022\n" + CommandScripts.BuildRun(OpA, S_digest, PosixArgv.Join(argv), string.Empty, 120);
        var run = await PosixShellHarness.RunAsync(script, workspace);
        CommandSentinel.Parse(run.Stdout).Kind.Should().Be(CommandSentinel.KindManifest);

        var manifestPerms = await PermsAsync(workspace, $".lmsbx-sdk/ops/{OpA}/manifest.json");
        var commandFilePerms = await PermsAsync(workspace, "cmd_created");

        manifestPerms.Should().Be("600", "SDK artifacts must be written under the restrictive umask 077");
        commandFilePerms
            .Should()
            .Be("644", "the command runs under the caller's inherited umask (022), not the SDK's 077");
    }

    private static async Task<string> HashViaHelperAsync(ShellWorkspace workspace, string prefix)
    {
        var script = prefix + CommandScripts.Sha256Function + "\nlmsbx_sha256 \"$SANDBOX_WORKSPACE/data.bin\"";
        return (await PosixShellHarness.RunAsync(script, workspace)).Stdout.Trim();
    }

    private static async Task<bool> HasCommandAsync(ShellWorkspace workspace, string commandName)
    {
        var script = $"command -v {commandName} >/dev/null 2>&1 && printf yes || printf no";
        return (await PosixShellHarness.RunAsync(script, workspace)).Stdout.Trim() == "yes";
    }

    private static async Task<bool> UmaskIsHonoredAsync(ShellWorkspace workspace)
    {
        var script =
            "umask 077; : > \"$SANDBOX_WORKSPACE/pa\"; umask 022; : > \"$SANDBOX_WORKSPACE/pb\"; "
            + PermsFunction
            + "\nprintf '%s|%s' \"$(perms \"$SANDBOX_WORKSPACE/pa\")\" \"$(perms \"$SANDBOX_WORKSPACE/pb\")\"";
        var parts = (await PosixShellHarness.RunAsync(script, workspace)).Stdout.Trim().Split('|');
        return parts.Length == 2 && parts[0].Length > 0 && parts[0] != parts[1];
    }

    private static async Task<string> PermsAsync(ShellWorkspace workspace, string relative)
    {
        var script = PermsFunction + $"\nperms \"$SANDBOX_WORKSPACE/{relative}\"";
        return (await PosixShellHarness.RunAsync(script, workspace)).Stdout.Trim();
    }

    private static void SeedClaimDirectory(ShellWorkspace workspace, string name, long lease, long created)
    {
        var directory = workspace.HostFile($".lmsbx-sdk/ops/{name}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "lease"), lease.ToString());
        File.WriteAllText(Path.Combine(directory, "created"), created.ToString());
    }

    /// <summary>Seeds a sibling per-operation GC lock (<c>&lt;op&gt;.gc</c>) owned by a token stamped at the given time, as a holder would leave it (fresh time = live, ancient time = a crashed holder's stale lock).</summary>
    private static void SeedGcLock(ShellWorkspace workspace, string name, long timestamp, string token = "seeded-owner-token")
    {
        var directory = workspace.HostFile($".lmsbx-sdk/ops/{name}.gc");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner"), $"{token} {timestamp}");
    }

    /// <summary>Seeds a GC lock directory with NO owner token yet — the state a holder leaves in the tiny <c>mkdir</c>→token establishment window (which must be treated LIVE, never reclaimed).</summary>
    private static void SeedOwnerlessGcLock(ShellWorkspace workspace, string name) =>
        Directory.CreateDirectory(workspace.HostFile($".lmsbx-sdk/ops/{name}.gc"));

    private static void AssertWellFormedProbeState(string probeText)
    {
        var (kind, payload) = CommandSentinel.Parse(probeText);
        if (kind != CommandSentinel.KindManifest)
        {
            kind.Should().BeOneOf(CommandSentinel.KindPending, CommandSentinel.KindNone);
            return;
        }

        // A MANIFEST must be complete: decodable base64 wrapping deserializable, valid manifest JSON.
        var json = Convert.FromBase64String(payload!);
        var manifest = JsonSerializer.Deserialize<CommandManifest>(json, CommandManifest.Json);
        manifest.Should().NotBeNull();
        manifest!.Digest.Should().Be(S_digest);
    }
}
