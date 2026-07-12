using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using AchieveAi.LmDotnetTools.Sandbox.Tests.Command;
using AchieveAi.LmDotnetTools.Sandbox.Transfer;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// Runs the ACTUAL transfer scripts <see cref="TransferScripts"/> generates under a real POSIX shell
/// (native <c>/bin/sh</c> on Linux/macOS, or WSL <c>bash</c> driving <c>/bin/sh</c> on Windows), so the
/// real wrapper — not only the in-memory model — is proven: a large file reassembles byte-for-byte via
/// chunked verified reads, a chunked write atomically replaces the target and leaves no temp, a finalize
/// whose digest does not match preserves the original, and a directory with NUL-hostile names lists
/// exactly. Each test skips visibly when no shell is available (or FAILS when
/// <c>LMSBX_REQUIRE_POSIX_SHELL</c> is set), so a "must have a shell" environment can never pass by
/// silently skipping.
/// </summary>
public class TransferRealShellTests
{
    private const string OpId = "0123456789abcdef0123456789abcdef";

    [SkippableFact]
    public async Task RealShell_StatThenChunkedRead_ReassemblesLargeFileExactly()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var bytes = CommandTestSupport.PrintablePattern(40_000, seed: 11);
        SeedHostFile(workspace, "data/big.bin", bytes);

        var stat = await PosixShellHarness.RunAsync(TransferScripts.BuildStat("data/big.bin"), workspace);
        var (statKind, statTokens) = TransferSentinel.Parse(stat.Stdout);

        statKind.Should().Be(TransferSentinel.KindMeta);
        long.Parse(statTokens[0]).Should().Be(bytes.Length);
        statTokens[2].Should().Be(Sha256Hex(bytes));

        var assembled = await ReadBackAsync(workspace, "data/big.bin", bytes.Length, statTokens[0], statTokens[1]);
        assembled.Should().Equal(bytes);
    }

    [SkippableFact]
    public async Task RealShell_Stat_MissingFile_ReportsNotFound()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();

        var stat = await PosixShellHarness.RunAsync(TransferScripts.BuildStat("nope.txt"), workspace);

        TransferSentinel.Parse(stat.Stdout).Kind.Should().Be(TransferSentinel.KindNotFound);
    }

    [SkippableFact]
    public async Task RealShell_ChunkedWriteThenFinalize_AtomicallyCreatesExactFile_AndLeavesNoTemp()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var bytes = CommandTestSupport.PrintablePattern(30_000, seed: 12);

        await WriteAllAsync(workspace, "out/created.bin", bytes);
        await FinalizeAsync(workspace, "out/created.bin", bytes, TransferSentinel.KindFinalized);

        (await File.ReadAllBytesAsync(workspace.HostFile("out/created.bin"))).Should().Equal(bytes);
        File.Exists(workspace.HostFile(TransferPath.TempRelative("out/created.bin", OpId)))
            .Should()
            .BeFalse("the atomic finalize renames the temp away");
    }

    [SkippableFact]
    public async Task RealShell_Write_ReplacesExistingFileAtomically()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        SeedHostFile(workspace, "target.txt", Encoding.UTF8.GetBytes("OLD"));
        var newBytes = Encoding.UTF8.GetBytes("BRAND NEW CONTENT");

        await WriteAllAsync(workspace, "target.txt", newBytes);
        await FinalizeAsync(workspace, "target.txt", newBytes, TransferSentinel.KindFinalized);

        (await File.ReadAllBytesAsync(workspace.HostFile("target.txt"))).Should().Equal(newBytes);
    }

    [SkippableFact]
    public async Task RealShell_Finalize_DigestMismatch_PreservesOriginal()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        var original = Encoding.UTF8.GetBytes("ORIGINAL — must survive a failed write");
        SeedHostFile(workspace, "target.txt", original);
        var attempted = Encoding.UTF8.GetBytes("attempted replacement bytes");

        await WriteAllAsync(workspace, "target.txt", attempted);
        // Finalize with a deliberately wrong expected digest: the temp fails verification and the mv never
        // happens, so the original target is preserved untouched.
        var wrongSha = new string('0', 64);
        var finalize = await PosixShellHarness.RunAsync(
            TransferScripts.BuildFinalize("target.txt", OpId, attempted.Length, wrongSha),
            workspace
        );

        TransferSentinel.Parse(finalize.Stdout).Kind.Should().Be(TransferSentinel.KindIntegrity);
        (await File.ReadAllBytesAsync(workspace.HostFile("target.txt"))).Should().Equal(original);
    }

    [SkippableFact]
    public async Task RealShell_List_ReturnsNulSafeNames_IncludingDotfiles_ExcludingDotAndDotDot()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();
        Directory.CreateDirectory(workspace.HostFile("proj"));
        SeedHostFile(workspace, "proj/a.txt", Encoding.UTF8.GetBytes("a"));
        SeedHostFile(workspace, "proj/.hidden", Encoding.UTF8.GetBytes("h"));
        SeedHostFile(workspace, "proj/name with spaces.txt", Encoding.UTF8.GetBytes("s"));
        Directory.CreateDirectory(workspace.HostFile("proj/subdir"));

        var artifact = ".lmsbx-sdk/xfer/list." + OpId;
        var list = await PosixShellHarness.RunAsync(TransferScripts.BuildList("proj", artifact), workspace);
        TransferSentinel.Parse(list.Stdout).Kind.Should().Be(TransferSentinel.KindOk);

        var artifactBytes = await ReadArtifactAsync(workspace, artifact);
        var names = TransferPath.SplitNulListing(Encoding.UTF8.GetString(artifactBytes));
        names.Should().BeEquivalentTo("a.txt", ".hidden", "name with spaces.txt", "subdir");
    }

    [SkippableFact]
    public async Task RealShell_List_MissingDirectory_ReportsNotFound()
    {
        await PosixShellHarness.RequireCapabilityAsync();
        using var workspace = PosixShellHarness.NewWorkspace();

        var list = await PosixShellHarness.RunAsync(
            TransferScripts.BuildList("nope", ".lmsbx-sdk/xfer/list." + OpId),
            workspace
        );

        TransferSentinel.Parse(list.Stdout).Kind.Should().Be(TransferSentinel.KindNotFound);
    }

    private static async Task<byte[]> ReadBackAsync(
        ShellWorkspace workspace,
        string relativePath,
        long total,
        string expectedSize,
        string expectedMtime
    )
    {
        using var buffer = new MemoryStream();
        long offset = 0;
        while (offset < total)
        {
            var length = (int)Math.Min(CommandArtifactLayout.ReadChunkBytes, total - offset);
            var read = await PosixShellHarness.RunAsync(
                TransferScripts.BuildRead(relativePath, offset, length),
                workspace
            );
            Encoding
                .UTF8.GetByteCount(read.Stdout)
                .Should()
                .BeLessThanOrEqualTo(CommandArtifactLayout.GatewayOutputByteLimit);
            var (kind, tokens) = TransferSentinel.Parse(read.Stdout);
            kind.Should().Be(TransferSentinel.KindChunk);
            tokens[0].Should().Be(expectedSize, "the file size must stay stable across chunks");
            tokens[1].Should().Be(expectedMtime, "the file mtime must stay stable across chunks");
            var chunk = Convert.FromBase64String(tokens[2]);
            chunk.Length.Should().Be(length);
            buffer.Write(chunk, 0, chunk.Length);
            offset += length;
        }

        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadArtifactAsync(ShellWorkspace workspace, string relativePath)
    {
        var stat = await PosixShellHarness.RunAsync(TransferScripts.BuildStat(relativePath), workspace);
        var (kind, tokens) = TransferSentinel.Parse(stat.Stdout);
        kind.Should().Be(TransferSentinel.KindMeta);
        return await ReadBackAsync(workspace, relativePath, long.Parse(tokens[0]), tokens[0], tokens[1]);
    }

    private static async Task WriteAllAsync(ShellWorkspace workspace, string relativePath, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            var empty = await PosixShellHarness.RunAsync(
                TransferScripts.BuildWriteChunk(relativePath, OpId, 0, 0, string.Empty),
                workspace
            );
            TransferSentinel.Parse(empty.Stdout).Kind.Should().Be(TransferSentinel.KindWrote);
            return;
        }

        long offset = 0;
        while (offset < bytes.Length)
        {
            var length = (int)Math.Min(CommandArtifactLayout.ReadChunkBytes, bytes.Length - offset);
            var chunkBase64 = Convert.ToBase64String(bytes, (int)offset, length);
            var write = await PosixShellHarness.RunAsync(
                TransferScripts.BuildWriteChunk(relativePath, OpId, offset, length, chunkBase64),
                workspace
            );
            var (kind, tokens) = TransferSentinel.Parse(write.Stdout);
            kind.Should().Be(TransferSentinel.KindWrote);
            long.Parse(tokens[0]).Should().Be(offset + length);
            offset += length;
        }
    }

    private static async Task FinalizeAsync(ShellWorkspace workspace, string relativePath, byte[] bytes, string expectedKind)
    {
        var finalize = await PosixShellHarness.RunAsync(
            TransferScripts.BuildFinalize(relativePath, OpId, bytes.Length, Sha256Hex(bytes)),
            workspace
        );
        TransferSentinel.Parse(finalize.Stdout).Kind.Should().Be(expectedKind);
    }

    private static void SeedHostFile(ShellWorkspace workspace, string relativePath, byte[] bytes)
    {
        var hostPath = workspace.HostFile(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
        File.WriteAllBytes(hostPath, bytes);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
