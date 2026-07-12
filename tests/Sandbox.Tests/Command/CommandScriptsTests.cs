using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandScriptsTests
{
    private const string Op = "abcdef0123456789abcdef0123456789";
    private const string Gen = "0123456789abcdef0123456789abcdef";
    private const string Digest = "d0d1d2d3d4d5d6d7d8d9dadbdcdddedfd0d1d2d3d4d5d6d7d8d9dadbdcdddedf0";

    private static string[] AllScripts() =>
        [
            CommandScripts.BuildRun(Op, "digesthex", "'ls' '-la'", "sub/dir", 120),
            CommandScripts.BuildProbe(Op),
            CommandScripts.BuildRead(Op, "stdout", 12288, 12288),
            CommandScripts.BuildReclaim(Op, Gen, Digest),
            CommandScripts.BuildGc(256),
            CommandScripts.BuildGcPurge(Op),
        ];

    [Fact]
    public void BuildRun_EmbedsThePosixQuotedArgvVerbatim()
    {
        var quoted = PosixArgv.Join(["git", "commit", "-m", "a'; rm -rf / #"]);

        var script = CommandScripts.BuildRun("op", "digest", quoted, string.Empty, 120);

        script.Should().Contain(quoted);
    }

    [Fact]
    public void BuildRun_QuotesTheWorkingDirectory()
    {
        var script = CommandScripts.BuildRun("op", "digest", "'ls'", "a b/c", 120);

        // The working directory is single-quoted, so a space can never split it into a second token.
        script.Should().Contain("'a b/c'");
    }

    [Fact]
    public void BuildRun_SelfRecoversAnAbandonedClaim_OnlyUnderTheGcLockAfterRevalidation()
    {
        var script = CommandScripts.BuildRun(Op, "digest", "'ls'", string.Empty, 120);

        // The only claim-directory delete is the guarded abandoned-claim self-recovery: it is gated on
        // winning the per-operation GC lock and on re-validating, UNDER the lock, that the lease is still
        // expired — never an eager, unguarded takeover that could double-run a command.
        script.Should().Contain("if gclock_try; then");
        script.Should().Contain("rm -rf \"$OP\"");
        script.Should().Contain("rnow=$(date +%s)");
        var lockIndex = script.IndexOf("if gclock_try; then", StringComparison.Ordinal);
        var deleteIndex = script.IndexOf("rm -rf \"$OP\"", StringComparison.Ordinal);
        deleteIndex.Should().BeGreaterThan(lockIndex, "the abandoned-claim delete must sit inside the GC-lock-won branch");
    }

    [Fact]
    public void BuildRun_DefersToAnyExistingGcLock_BeforeCreatingAClaim()
    {
        var script = CommandScripts.BuildRun(Op, "digest", "'ls'", string.Empty, 120);

        // The GC lock is non-stealable, so its mere presence (an in-flight purge/reclaim/self-recovery or a
        // crash-orphaned lock) makes RUN report PENDING rather than create a claim that races it — never a
        // liveness/TTL check and never a steal.
        script.Should().Contain("GCL=\"$OP.gc\"");
        script.Should().Contain("if [ -d \"$GCL\" ]; then break; fi");
        script.Should().NotContain("gclock_is_live", "the non-stealable lock has no liveness/TTL notion");
    }

    [Fact]
    public void BuildRun_ScopesRestrictiveUmaskAroundTheCommand()
    {
        var script = CommandScripts.BuildRun(Op, "digest", "'ls'", string.Empty, 120);

        // Capture the caller's umask, harden for SDK artifacts, restore for the command, harden again.
        script.Should().Contain("OLDUMASK=$(umask)");
        script.Should().Contain("umask 077");
        script.Should().Contain("umask \"$OLDUMASK\"");
    }

    [Fact]
    public void BuildRun_PublishesManifestAtomicallyViaSiblingTempAndRename()
    {
        var script = CommandScripts.BuildRun(Op, "digest", "'ls'", string.Empty, 120);

        // The manifest is written to a restrictive sibling temp then renamed — never printed directly to
        // manifest.json — so a concurrent PROBE can never observe a torn, partially-written manifest.
        script.Should().Contain(".manifest.$$.tmp");
        script.Should().Contain("mv \"$MTMP\" \"$MAN\"");
    }

    [Fact]
    public void Sha256Function_IsPortable_TriesSha256sumThenShasumFallback_OverStdin()
    {
        CommandScripts.Sha256Function.Should().Contain("sha256sum");
        CommandScripts.Sha256Function.Should().Contain("shasum -a 256");
        // Hashing reads the file on stdin (< "$1"), so no coreutils variant appends a filename.
        CommandScripts.Sha256Function.Should().Contain("< \"$1\"");
    }

    [Fact]
    public void BuildGc_OnlyListsFixedLengthLowercaseHexNames()
    {
        var script = CommandScripts.BuildGc(256);

        // A foreign/hostile directory name is filtered before it can become a cleanup candidate.
        script.Should().Contain("*[!0-9a-f]*");
        script.Should().Contain("${#name} -eq 32");
    }

    [Fact]
    public void BuildGcPurge_ReValidatesLeaseAndAgeAtDeleteTimeBeforeRemoving()
    {
        var script = CommandScripts.BuildGcPurge(Op);

        // The delete decision is re-made from the current state (date +%s + a fresh lease/created read),
        // never trusted from the SDK's earlier listing snapshot.
        script.Should().Contain("now=$(date +%s)");
        script.Should().Contain("rm -rf \"$OP\"");
        // The age test is STRICTLY past the retention window (an operation exactly 24h old is retained).
        script.Should().Contain("$((now - created))\" -gt \"$STALE\"");
    }

    [Fact]
    public void BuildGcPurge_DeletesOnlyUnderTheGcLock_LosersNeverDelete()
    {
        var script = CommandScripts.BuildGcPurge(Op);

        // The whole re-validate-and-delete body is gated on winning the per-op GC lock, so a purger that
        // loses the atomic mkdir election never reaches the rm -rf.
        script.Should().Contain("GCL=\"$OP.gc\"");
        script.Should().Contain("if gclock_try; then");
        script.Should().Contain("gclock_release");
        var lockIndex = script.IndexOf("if gclock_try; then", StringComparison.Ordinal);
        var deleteIndex = script.IndexOf("rm -rf \"$OP\"", StringComparison.Ordinal);
        lockIndex.Should().BeGreaterThan(-1);
        deleteIndex.Should().BeGreaterThan(lockIndex, "the delete must be inside the GC-lock-won branch");
    }

    [Fact]
    public void GcLock_IsNonStealable_MkdirElectedOwnerTokenFenced_AndReleaseIsOwnershipChecked()
    {
        // Every GC-lock-bearing script carries the non-stealable owner-token primitives: a per-acquisition
        // token (lmsbx_uid) written into <op>.gc/owner, a gclock_try that ONLY wins the atomic mkdir (never
        // steals an existing lock), an ownership check, and a release gated on ownership.
        foreach (
            var script in new[]
            {
                CommandScripts.BuildRun(Op, Digest, "'ls'", string.Empty, 120),
                CommandScripts.BuildGcPurge(Op),
                CommandScripts.BuildReclaim(Op, Gen, Digest),
            }
        )
        {
            script.Should().Contain("OWNER=$(lmsbx_uid)");
            script.Should().Contain("> \"$GCL/owner\"", "the owner token is persisted inside the lock directory");
            script.Should().Contain("gclock_owned() { [ -f \"$GCL/owner\" ] && [ \"$(gclock_owner_token)\" = \"$OWNER\" ]; }");
            // Acquisition is a single atomic mkdir with NO fallback steal (no liveness/TTL check, no
            // rm-then-remkdir), so once a lock exists no contender can ever remove or replace it.
            script.Should().Contain("gclock_try() { mkdir \"$GCL\" 2>/dev/null || return 1; gclock_write_owner; gclock_owned; }");
            script.Should().NotContain("gclock_is_live", "a non-stealable lock has no liveness/TTL notion");
            script.Should().NotContain("GCLOCKTTL", "a non-stealable lock has no TTL");
            // Release removes the lock ONLY when still owned — never a lock the caller does not hold.
            script.Should().Contain("gclock_release() { gclock_owned && rm -rf \"$GCL\" 2>/dev/null; return 0; }");
        }
    }

    [Fact]
    public void BuildRun_WritesImmutableExecutionGeneration_IntoTheSidecarAndManifest()
    {
        var script = CommandScripts.BuildRun(Op, Digest, "'ls'", string.Empty, 120);

        // A fresh per-execution generation is minted (lmsbx_uid), persisted beside the other marker files,
        // and embedded in the manifest JSON so any SDK that reads the manifest learns the generation.
        script.Should().Contain("GEN=$(lmsbx_uid)");
        script.Should().Contain("printf '%s' \"$GEN\" > \"$OP/gen\"");
        script.Should().Contain("\"gen\":\"%s\"");
    }

    [Fact]
    public void BuildReclaim_IsGcLockGuarded_AndOnlyDeletesOutputForTheSameGenerationAndDigest()
    {
        var script = CommandScripts.BuildReclaim(Op, Gen, Digest);

        // The reclaim carries the verified generation/digest, acquires the GC lock, re-reads the CURRENT
        // generation/digest under the lock, re-verifies ownership, and only then drops stdout/stderr.
        script.Should().Contain("GEN='" + Gen + "'");
        script.Should().Contain("DIGEST='" + Digest + "'");
        script.Should().Contain("if gclock_try; then");
        script.Should().Contain("curgen=$(cat \"$OP/gen\" 2>/dev/null)");
        script.Should().Contain("[ \"$curgen\" = \"$GEN\" ] && [ \"$curdig\" = \"$DIGEST\" ] && gclock_owned");
        script.Should().Contain("rm -f \"$OP/stdout\" \"$OP/stderr\"");
        var lockIndex = script.IndexOf("if gclock_try; then", StringComparison.Ordinal);
        var deleteIndex = script.IndexOf("rm -f \"$OP/stdout\"", StringComparison.Ordinal);
        deleteIndex.Should().BeGreaterThan(lockIndex, "the output delete must sit inside the GC-lock-won, generation-matched branch");
    }

    [Fact]
    public void NoScript_ReferencesTheGatewaysUnstableOutputTxtPath()
    {
        foreach (var script in AllScripts())
        {
            script
                .Should()
                .NotContain(
                    "output_",
                    "the SDK must never depend on the gateway's unstable output_*.txt truncation file"
                );
        }
    }

    [Fact]
    public void EveryScript_ReferencesTheReservedArtifactRoot()
    {
        foreach (var script in AllScripts())
        {
            script.Should().Contain(CommandArtifactLayout.ArtifactRootRelative);
        }
    }

    [Fact]
    public void ParseRequest_RoundTripsRun()
    {
        var request = CommandScripts.ParseRequest(CommandScripts.BuildRun("op-dir", "the-digest", "'ls'", "sub", 120));

        request.Kind.Should().Be(CommandScriptKind.Run);
        request.OperationDirectory.Should().Be("op-dir");
        request.Digest.Should().Be("the-digest");
    }

    [Fact]
    public void ParseRequest_RoundTripsProbeReclaimGcAndGcPurge()
    {
        CommandScripts.ParseRequest(CommandScripts.BuildProbe("op-dir")).Kind.Should().Be(CommandScriptKind.Probe);

        var reclaim = CommandScripts.ParseRequest(CommandScripts.BuildReclaim("op-dir", Gen, Digest));
        reclaim.Kind.Should().Be(CommandScriptKind.Reclaim);
        reclaim.Generation.Should().Be(Gen, "the reclaim carries the verified execution generation");
        reclaim.Digest.Should().Be(Digest, "the reclaim carries the verified command digest");

        CommandScripts.ParseRequest(CommandScripts.BuildGcPurge("op-dir")).Kind.Should().Be(CommandScriptKind.GcPurge);

        var gc = CommandScripts.ParseRequest(CommandScripts.BuildGc(256));
        gc.Kind.Should().Be(CommandScriptKind.Gc);
        gc.Max.Should().Be(256);
    }

    [Fact]
    public void ParseRequest_RoundTripsReadWithOffsetAndLength()
    {
        var request = CommandScripts.ParseRequest(CommandScripts.BuildRead("op-dir", "stderr", 4096, 12288));

        request.Kind.Should().Be(CommandScriptKind.Read);
        request.OperationDirectory.Should().Be("op-dir");
        request.Stream.Should().Be("stderr");
        request.Offset.Should().Be(4096);
        request.Length.Should().Be(12288);
    }

    [Fact]
    public void ParseRequest_MissingMarker_Throws()
    {
        var act = () => CommandScripts.ParseRequest("echo not a wrapper\n");

        act.Should().Throw<FormatException>();
    }
}
