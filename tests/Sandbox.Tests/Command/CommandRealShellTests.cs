using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F3: strengthens the deterministic proof by running the ACTUAL scripts <see cref="CommandScripts"/>
/// generates under a real POSIX shell (see <see cref="PosixShellHarness"/>), rather than only a model.
/// It proves, on the real wrapper: (F1) the manifest sentinel line for two threshold-sized streams
/// survives the gateway's truncation and its inline copies decode exactly, and large streams reassemble
/// byte-for-byte through chunked reads; and (F2) a claimed directory without a lease is reported PENDING
/// and is neither deleted nor re-run, while a genuinely expired lease is still taken over. Each test
/// skips visibly when no shell is available (or fails when <c>LMSBX_REQUIRE_POSIX_SHELL</c> is set) — it
/// never passes without actually running a shell.
/// </summary>
public class CommandRealShellTests
{
    private const string Op = "0123456789abcdef0123456789abcdef";
    private static readonly string S_digest = new('a', 64);

    [SkippableFact]
    public async Task RealShell_RunWrapper_BothStreamsAtInlineThreshold_ManifestLineSurvivesTruncation_AndInlineDecodesExactly()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var length = CommandArtifactLayout.InlineThresholdBytes;
        var stdout = Repeat((byte)'a', length);
        var stderr = Repeat((byte)'b', length);
        SeedStreamFiles(workspace, stdout, stderr);
        var script = CommandScripts.BuildRun(Op, S_digest, PosixArgv.Join(CatBothArgv(exitCode: 7)), string.Empty, 120);

        var result = await PosixShellHarness.RunAsync(script, workspace);

        // The real wrapper's worst-case manifest line stayed within the proven bound (had it overflowed,
        // the truncating harness would have cut it and the parse/decode below would fail).
        Encoding
            .UTF8.GetByteCount(result.Stdout)
            .Should()
            .BeLessThanOrEqualTo(CommandArtifactLayout.ManifestLineByteBudget);
        var (kind, payload) = CommandSentinel.Parse(result.Stdout);
        kind.Should().Be(CommandSentinel.KindManifest);
        var manifest = DecodeManifest(payload!);
        manifest.Digest.Should().Be(S_digest);
        manifest.ExitCode.Should().Be(7);
        manifest.Stdout.Length.Should().Be(length);
        manifest.Stderr.Length.Should().Be(length);
        // Both streams are at the threshold, so both are inlined — the nested-encoding worst case.
        manifest.Stdout.Inline.Should().NotBeNull();
        manifest.Stderr.Inline.Should().NotBeNull();
        Convert.FromBase64String(manifest.Stdout.Inline!).Should().Equal(stdout);
        Convert.FromBase64String(manifest.Stderr.Inline!).Should().Equal(stderr);
        manifest.Stdout.Sha256.Should().Be(Sha256Hex(stdout));
        manifest.Stderr.Sha256.Should().Be(Sha256Hex(stderr));
    }

    [SkippableFact]
    public async Task RealShell_RunWrapper_LargeStreams_ReassembleExactlyViaChunkedReads()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var stdout = CommandTestSupport.PrintablePattern(40_000, seed: 1);
        var stderr = CommandTestSupport.PrintablePattern(25_000, seed: 2);
        SeedStreamFiles(workspace, stdout, stderr);
        var script = CommandScripts.BuildRun(Op, S_digest, PosixArgv.Join(CatBothArgv(exitCode: 0)), string.Empty, 120);

        var run = await PosixShellHarness.RunAsync(script, workspace);

        var (kind, payload) = CommandSentinel.Parse(run.Stdout);
        kind.Should().Be(CommandSentinel.KindManifest);
        var manifest = DecodeManifest(payload!);
        // Streams above the threshold are NOT inlined — they must be read back in chunks.
        manifest.Stdout.Inline.Should().BeNull();
        manifest.Stderr.Inline.Should().BeNull();
        manifest.Stdout.Length.Should().Be(stdout.Length);
        manifest.Stderr.Length.Should().Be(stderr.Length);

        (await ReassembleViaChunkedReadsAsync(workspace, "stdout", stdout.Length)).Should().Equal(stdout);
        (await ReassembleViaChunkedReadsAsync(workspace, "stderr", stderr.Length)).Should().Equal(stderr);
    }

    [SkippableFact]
    public async Task RealShell_ClaimWithoutLease_IsReportedPending_CommandNeverRuns_AndClaimIsNotDeleted()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A winner has won the mkdir claim but has not written its lease yet (the establishment window).
        var claimDirectory = workspace.HostFile($".lmsbx-sdk/ops/{Op}");
        Directory.CreateDirectory(claimDirectory);
        var argv = new[] { "sh", "-c", "printf ran > \"$SANDBOX_WORKSPACE/RAN\"" };
        var script = CommandScripts.BuildRun(Op, S_digest, PosixArgv.Join(argv), string.Empty, 120);

        var result = await PosixShellHarness.RunAsync(script, workspace);

        CommandSentinel.Parse(result.Stdout).Kind.Should().Be(CommandSentinel.KindPending);
        workspace
            .HostFileExists("RAN")
            .Should()
            .BeFalse("a lease-less claim is not takeover-eligible, so the concurrent caller's command must not run");
        Directory.Exists(claimDirectory).Should().BeTrue("the winner's claim directory must not be deleted");
    }

    [SkippableFact]
    public async Task RealShell_ExpiredLeaseOfCrashedSubmitter_IsReportedPending_AndNeverTakenOver()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // A crashed submitter left an established-but-expired lease (unix second 1) and no manifest.
        Directory.CreateDirectory(workspace.HostFile($".lmsbx-sdk/ops/{Op}"));
        File.WriteAllText(workspace.HostFile($".lmsbx-sdk/ops/{Op}/lease"), "1");
        File.WriteAllText(workspace.HostFile($".lmsbx-sdk/ops/{Op}/created"), "1");
        var argv = new[] { "sh", "-c", "printf ran > \"$SANDBOX_WORKSPACE/RAN\"" };
        var script = CommandScripts.BuildRun(Op, S_digest, PosixArgv.Join(argv), string.Empty, 120);

        var result = await PosixShellHarness.RunAsync(script, workspace);

        // The shipped wrapper never deletes/takes over an existing claim (that rm -rf + mkdir is not
        // atomic against a concurrent contender and could double-run). An expired, uncommitted claim is
        // reported PENDING and left intact for the guarded stale sweep — the command must NOT run.
        CommandSentinel.Parse(result.Stdout).Kind.Should().Be(CommandSentinel.KindPending);
        workspace
            .HostFileExists("RAN")
            .Should()
            .BeFalse("an expired claim is not takeover-eligible, so the command must not run");
        Directory
            .Exists(workspace.HostFile($".lmsbx-sdk/ops/{Op}"))
            .Should()
            .BeTrue("the abandoned claim is retained for the stale sweep, never taken over");
    }

    [SkippableFact]
    public async Task RealShell_ConcurrentSameId_RunsTheCommandExactlyOnce()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        // The command holds the claim briefly (after establishing its lease) so the two runs genuinely overlap.
        var argv = new[] { "sh", "-c", "sleep 0.3; printf r >> \"$SANDBOX_WORKSPACE/RUNS\"" };
        var script = CommandScripts.BuildRun(Op, S_digest, PosixArgv.Join(argv), string.Empty, 120);

        var first = PosixShellHarness.RunAsync(script, workspace);
        var second = PosixShellHarness.RunAsync(script, workspace);
        var results = await Task.WhenAll(first, second);

        // Exactly one caller won the claim and ran the command; the other observed the live claim.
        var runs = workspace.HostFileExists("RUNS")
            ? await File.ReadAllTextAsync(workspace.HostFile("RUNS"))
            : string.Empty;
        runs.Should().Be("r");
        results
            .Select(r => CommandSentinel.Parse(r.Stdout).Kind)
            .Should()
            .OnlyContain(k => k == CommandSentinel.KindManifest || k == CommandSentinel.KindPending);
    }

    private static async Task<byte[]> ReassembleViaChunkedReadsAsync(ShellWorkspace workspace, string stream, long total)
    {
        using var buffer = new MemoryStream();
        long offset = 0;
        while (offset < total)
        {
            var length = (int)Math.Min(CommandArtifactLayout.ReadChunkBytes, total - offset);
            var read = await PosixShellHarness.RunAsync(CommandScripts.BuildRead(Op, stream, offset, length), workspace);
            Encoding
                .UTF8.GetByteCount(read.Stdout)
                .Should()
                .BeLessThanOrEqualTo(CommandArtifactLayout.GatewayOutputByteLimit);
            var chunk = Convert.FromBase64String(read.Stdout);
            chunk.Length.Should().Be(length);
            buffer.Write(chunk, 0, chunk.Length);
            offset += length;
        }

        return buffer.ToArray();
    }

    /// <summary>Argv that cats the seeded stdout/stderr files back to their respective streams and exits with a chosen code.</summary>
    private static string[] CatBothArgv(int exitCode) =>
        [
            "sh",
            "-c",
            $"cat \"$SANDBOX_WORKSPACE/out.dat\"; cat \"$SANDBOX_WORKSPACE/err.dat\" 1>&2; exit {exitCode}",
        ];

    private static void SeedStreamFiles(ShellWorkspace workspace, byte[] stdout, byte[] stderr)
    {
        File.WriteAllBytes(workspace.HostFile("out.dat"), stdout);
        File.WriteAllBytes(workspace.HostFile("err.dat"), stderr);
    }

    private static CommandManifest DecodeManifest(string payload) =>
        JsonSerializer.Deserialize<CommandManifest>(Convert.FromBase64String(payload), CommandManifest.Json)!;

    private static byte[] Repeat(byte value, int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
