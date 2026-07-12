using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Tests.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// Behavioral transport tests for the exact, verified file/listing transfers
/// (<see cref="SandboxClient.ReadTextFileAsync"/>, <see cref="SandboxClient.WriteTextFileAsync"/>,
/// <see cref="SandboxClient.ListDirectoryAsync"/>) driven through the in-memory
/// <see cref="TransferFakeGateway"/>. The tests assert genuine outcomes — exact reassembled bytes,
/// mutation-between-chunks detection that never yields mixed content, atomic replace that preserves the
/// original on failure, and NUL-safe listings — rather than how often a collaborator was called.
/// </summary>
public class SandboxClientFileTransferTests
{
    private const string Session = "session-1";

    [Fact]
    public async Task ReadTextFile_AsciiRoundTrip_ReturnsExactText()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFileUtf8("dir/file.txt", "hello world");
        using var client = fake.CreateClient();

        var text = await client.ReadTextFileAsync(Session, "dir/file.txt");

        text.Should().Be("hello world");
    }

    [Fact]
    public async Task ReadTextFile_CrlfAndNoFinalNewline_PreservedExactly()
    {
        var fake = new TransferFakeGateway();
        const string content = "line-1\r\nline-2\r\nno-final-newline";
        fake.SeedFileUtf8("notes.txt", content);
        using var client = fake.CreateClient();

        var text = await client.ReadTextFileAsync(Session, "notes.txt");

        text.Should().Be(content);
    }

    [Fact]
    public async Task ReadTextFile_MultibyteUtf8_RoundTripsExactly()
    {
        var fake = new TransferFakeGateway();
        const string content = "café — naïve — 日本語 — 😀";
        fake.SeedFileUtf8("unicode.txt", content);
        using var client = fake.CreateClient();

        var text = await client.ReadTextFileAsync(Session, "unicode.txt");

        text.Should().Be(content);
    }

    [Fact]
    public async Task ReadTextFile_EmptyFile_ReturnsEmptyString()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFile("empty.txt", []);
        using var client = fake.CreateClient();

        var text = await client.ReadTextFileAsync(Session, "empty.txt");

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadTextFile_LargeMultiChunk_ReassemblesByteForByte()
    {
        var fake = new TransferFakeGateway();
        var bytes = CommandTestSupport.PrintablePattern(40_000, seed: 3);
        fake.SeedFile("big.txt", bytes);
        using var client = fake.CreateClient();

        var text = await client.ReadTextFileAsync(Session, "big.txt");

        Encoding.UTF8.GetBytes(text).Should().Equal(bytes);
    }

    [Fact]
    public async Task ReadTextFile_MissingFile_ThrowsNotFound()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "missing.txt");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [Fact]
    public async Task ReadTextFile_MalformedUtf8_ThrowsIntegrity()
    {
        var fake = new TransferFakeGateway();
        // A lone continuation byte (0x80) and a lone 0xFF are never valid UTF-8.
        fake.SeedFile("bad.bin", [0x41, 0x80, 0xFF, 0x42]);
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "bad.bin");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
    }

    [Fact]
    public async Task ReadTextFile_MtimeChangesBetweenChunks_ThrowsIntegrity_NeverMixedContent()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFile("big.txt", CommandTestSupport.PrintablePattern(40_000, seed: 4), mtime: 100);
        // After the first chunk, bump the mtime — a metadata change mid-transfer.
        fake.MutateAfterRead("big.txt", afterReadCount: 1, () => fake.SetMtime("big.txt", 200));
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "big.txt");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
    }

    [Fact]
    public async Task ReadTextFile_SizeChangesBetweenChunks_ThrowsIntegrity()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFile("big.txt", CommandTestSupport.PrintablePattern(40_000, seed: 5));
        // After the first chunk, replace with a shorter body — the size no longer matches the probe.
        fake.MutateAfterRead(
            "big.txt",
            afterReadCount: 1,
            () => fake.ReplaceBytes("big.txt", CommandTestSupport.PrintablePattern(20_000, seed: 5), keepMtime: true)
        );
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "big.txt");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
    }

    [Fact]
    public async Task ReadTextFile_SameLengthSameMtimeEdit_CaughtByWholeFileDigest_ThrowsIntegrity()
    {
        var fake = new TransferFakeGateway();
        var original = CommandTestSupport.PrintablePattern(8_000, seed: 6);
        fake.SeedFile("f.txt", original, mtime: 500);
        // An adversarial same-length, same-mtime edit applied right after the probe: only the reassembled
        // whole-file digest can catch it.
        var tampered = CommandTestSupport.PrintablePattern(8_000, seed: 7);
        fake.MutateAfterStat("f.txt", () => fake.ReplaceBytes("f.txt", tampered, keepMtime: true));
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "f.txt");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
    }

    [Fact]
    public async Task ReadTextFile_ShortChunk_OffsetDiscontinuity_ThrowsIntegrity()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFile("f.txt", CommandTestSupport.PrintablePattern(4_000, seed: 8));
        fake.ForceShortChunkNextRead("f.txt");
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "f.txt");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
    }

    [Fact]
    public async Task ReadTextFile_TransientTransportError_IsRetried_ThenSucceeds()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFileUtf8("f.txt", "recovered");
        // The first two reads fail transiently; the idempotent retry re-reads the same offset and succeeds.
        fake.SetTransientReadErrors("f.txt", 2);
        using var client = fake.CreateClient();

        var text = await client.ReadTextFileAsync(Session, "f.txt");

        text.Should().Be("recovered");
    }

    [Fact]
    public async Task WriteTextFile_CreatesFile_WithExactUtf8Bytes()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();
        const string content = "written content\r\nwith CRLF and no trailing newline";

        await client.WriteTextFileAsync(Session, "out/new.txt", content);

        fake.GetFileBytes("out/new.txt").Should().Equal(Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public async Task WriteTextFile_EmptyContent_CreatesEmptyFile()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        await client.WriteTextFileAsync(Session, "empty.txt", string.Empty);

        fake.GetFileBytes("empty.txt").Should().Equal([]);
    }

    [Fact]
    public async Task WriteTextFile_LargeMultiChunk_PersistsExactly()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();
        var content = Encoding.UTF8.GetString(CommandTestSupport.PrintablePattern(50_000, seed: 9));

        await client.WriteTextFileAsync(Session, "big-out.txt", content);

        fake.GetFileBytes("big-out.txt").Should().Equal(Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public async Task WriteTextFile_ReplacesExistingFile_Atomically()
    {
        var fake = new TransferFakeGateway();
        fake.SeedFileUtf8("target.txt", "OLD CONTENT");
        using var client = fake.CreateClient();

        await client.WriteTextFileAsync(Session, "target.txt", "NEW CONTENT");

        Encoding.UTF8.GetString(fake.GetFileBytes("target.txt")!).Should().Be("NEW CONTENT");
    }

    [Fact]
    public async Task WriteTextFile_RoundTripsThroughRead()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();
        const string content = "round trip 日本語 😀";

        await client.WriteTextFileAsync(Session, "rt.txt", content);
        var read = await client.ReadTextFileAsync(Session, "rt.txt");

        read.Should().Be(content);
    }

    [Fact]
    public async Task WriteTextFile_FailedVerification_PreservesOriginal_AndLeavesNoTemp()
    {
        var fake = new TransferFakeGateway();
        var original = Encoding.UTF8.GetBytes("ORIGINAL — must survive");
        fake.SeedFile("target.txt", original);
        // The temp is corrupted right before finalize verifies it: the digest check fails and the rename
        // never happens, so the original target is left untouched.
        fake.CorruptTempOnFinalize("target.txt");
        var tempCountBefore = fake.TempFileCount;
        using var client = fake.CreateClient();

        var act = () => client.WriteTextFileAsync(Session, "target.txt", "NEW CONTENT THAT MUST NOT LAND");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
        fake.GetFileBytes("target.txt").Should().Equal(original);
        // The abandoned temp was cleaned up, so the file set is back to just the original target.
        fake.TempFileCount.Should().Be(tempCountBefore);
    }

    [Fact]
    public async Task ListDirectory_ReturnsEntryNames_IncludingDotfiles_ExcludingDotAndDotDot()
    {
        var fake = new TransferFakeGateway();
        fake.SeedDirectory("proj", "a.txt", "b.txt", ".hidden", "subdir");
        using var client = fake.CreateClient();

        var names = await client.ListDirectoryAsync(Session, "proj");

        names.Should().BeEquivalentTo("a.txt", "b.txt", ".hidden", "subdir");
    }

    [Fact]
    public async Task ListDirectory_NulSafeNames_WithSpacesAndNewlines_SurviveExactly()
    {
        var fake = new TransferFakeGateway();
        fake.SeedDirectory("weird", "name with spaces.txt", "line1\nline2", "tab\tname");
        using var client = fake.CreateClient();

        var names = await client.ListDirectoryAsync(Session, "weird");

        names.Should().BeEquivalentTo("name with spaces.txt", "line1\nline2", "tab\tname");
    }

    [Fact]
    public async Task ListDirectory_EmptyDirectory_ReturnsEmptyList()
    {
        var fake = new TransferFakeGateway();
        fake.SeedDirectory("empty-dir");
        using var client = fake.CreateClient();

        var names = await client.ListDirectoryAsync(Session, "empty-dir");

        names.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDirectory_MissingDirectory_ThrowsNotFound()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        var act = () => client.ListDirectoryAsync(Session, "no-such-dir");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [Fact]
    public async Task ListDirectory_RemovesTheListingArtifact_AfterReadingIt()
    {
        var fake = new TransferFakeGateway();
        fake.SeedDirectory("proj", "a.txt");
        using var client = fake.CreateClient();

        _ = await client.ListDirectoryAsync(Session, "proj");

        // Every keyed entry left behind is the seeded directory listing's underlying store; the transient
        // artifact the SDK produced and read back must have been cleaned up (no leftover file entries).
        fake.TempFileCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    [InlineData("..\\escape")]
    [InlineData("a/../../b")]
    [InlineData("bad\\path")]
    public async Task ReadTextFile_RejectsUnsafePaths(string path)
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, path);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadTextFile_RejectsNulInPath()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, "dir/na\0me.txt");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadTextFile_RejectsWorkspaceRootPath()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        var act = () => client.ReadTextFileAsync(Session, string.Empty);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WriteTextFile_RejectsWorkspaceRootPath()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        var act = () => client.WriteTextFileAsync(Session, string.Empty, "x");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Transfers_RejectEmptySessionId()
    {
        var fake = new TransferFakeGateway();
        using var client = fake.CreateClient();

        await new Func<Task>(() => client.ReadTextFileAsync(" ", "f.txt")).Should().ThrowAsync<ArgumentException>();
        await new Func<Task>(() => client.WriteTextFileAsync(" ", "f.txt", "x")).Should().ThrowAsync<ArgumentException>();
        await new Func<Task>(() => client.ListDirectoryAsync(" ", "d")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Transfers_ThrowObjectDisposed_AfterDispose()
    {
        var fake = new TransferFakeGateway();
        var client = fake.CreateClient();
        client.Dispose();

        await new Func<Task>(() => client.ReadTextFileAsync(Session, "f.txt")).Should().ThrowAsync<ObjectDisposedException>();
        await new Func<Task>(() => client.WriteTextFileAsync(Session, "f.txt", "x")).Should().ThrowAsync<ObjectDisposedException>();
        await new Func<Task>(() => client.ListDirectoryAsync(Session, "d")).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Transfers_AreRemoteOnly_CreateNoLocalHostFiles()
    {
        var fake = new TransferFakeGateway();
        fake.SeedDirectory("proj", "a.txt");
        using var client = fake.CreateClient();
        var before = SnapshotDirectory(AppContext.BaseDirectory);

        await client.WriteTextFileAsync(Session, "remote/only.txt", "content");
        var read = await client.ReadTextFileAsync(Session, "remote/only.txt");
        _ = await client.ListDirectoryAsync(Session, "proj");

        read.Should().Be("content");
        var after = SnapshotDirectory(AppContext.BaseDirectory);
        after.Should().BeEquivalentTo(before, "file transfers are remote-only and must never touch the host filesystem");
        File.Exists(Path.Combine(AppContext.BaseDirectory, "remote", "only.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Transfers_NeverPlaceRawPathOrSecretOnTheWire()
    {
        var fake = new TransferFakeGateway();
        fake.SeedDirectory("secret-name-dir", "entry");
        using var client = fake.CreateClient();

        await client.WriteTextFileAsync(Session, "secret-name-dir/target.txt", "payload");
        _ = await client.ListDirectoryAsync(Session, "secret-name-dir");

        // The marker/classification line must carry only opaque hex keys — the raw workspace path is only
        // ever inside a POSIX single-quoted shell assignment, never on the marker comment.
        foreach (var body in fake.RequestBodies)
        {
            var markerLine = ExtractMarkerLine(body);
            markerLine.Should().NotContain("secret-name-dir");
            markerLine.Should().NotContain("target.txt");
        }
    }

    private static string ExtractMarkerLine(string requestBody)
    {
        using var document = System.Text.Json.JsonDocument.Parse(requestBody);
        var command = document
            .RootElement.GetProperty("params")
            .GetProperty("arguments")
            .GetProperty("command")
            .GetString()!;
        return command.Split('\n', 2)[0];
    }

    private static HashSet<string> SnapshotDirectory(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)];
}
