using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandScriptsTests
{
    private const string Op = "abcdef0123456789abcdef0123456789";

    private static string[] AllScripts() =>
        [
            CommandScripts.BuildRun(Op, "digesthex", "'ls' '-la'", "sub/dir", 120),
            CommandScripts.BuildProbe(Op),
            CommandScripts.BuildRead(Op, "stdout", 12288, 12288),
            CommandScripts.BuildReclaim(Op),
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
    public void BuildRun_RespectsAnActiveGcLock_BeforeCreatingAClaim()
    {
        var script = CommandScripts.BuildRun(Op, "digest", "'ls'", string.Empty, 120);

        // Claim creation checks the sibling GC lock and its liveness, so a claim is never created into an
        // in-progress purge of the same operation.
        script.Should().Contain("GCL=\"$OP.gc\"");
        script.Should().Contain("if [ -d \"$GCL\" ]; then");
        script.Should().Contain("gclock_is_live");
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
        CommandScripts.ParseRequest(CommandScripts.BuildReclaim("op-dir")).Kind.Should().Be(CommandScriptKind.Reclaim);
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
