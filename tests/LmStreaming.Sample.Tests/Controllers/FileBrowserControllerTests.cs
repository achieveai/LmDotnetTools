using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Covers the WI #195 <see cref="FileBrowserController"/> HTTP contract against a fake
/// <see cref="IWorkspaceFileBrowser"/>: session-resolution prologue (unknown thread, no session, credential
/// conflict), component-wise authoritative path resolution (lossy/ambiguous rejection), and each endpoint's
/// caps, headers, validation, and error mapping. Client-supplied workspace/session/type inputs are never
/// trusted — the persisted conversation workspace and server-returned entry type win.
/// </summary>
public class FileBrowserControllerTests
{
    private const string ThreadId = "t1";

    private static SandboxSession LiveSession => new("default", "sess-1", "/workspace", "/host/ws");

    private sealed class FakeFileBrowser : IWorkspaceFileBrowser
    {
        public SandboxSessionResolution Resolution { get; set; } =
            new(SandboxSessionResolutionOutcome.Resolved, LiveSession, "app", null);

        public Exception? ResolveThrows { get; set; }

        public Dictionary<string, IReadOnlyList<SandboxDirectoryEntry>> Listings { get; } = new(StringComparer.Ordinal);
        public byte[] FileBytes { get; set; } = [];
        public Exception? ReadThrows { get; set; }
        public Exception? WriteThrows { get; set; }
        public SandboxCommandResult ExecResult { get; set; } = new() { ExitCode = 0, StandardOutput = "", StandardError = "", OperationId = "op" };
        public List<(string Path, byte[] Bytes)> Writes { get; } = [];
        public List<SandboxCommand> Commands { get; } = [];
        public int ReadCalls { get; private set; }

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(string threadId, string persistedWorkspaceId, SandboxCredential? requestCredential, CancellationToken ct = default) =>
            ResolveThrows is not null ? Task.FromException<SandboxSessionResolution>(ResolveThrows) : Task.FromResult(Resolution);

        public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(string sessionId, string relativePath, CancellationToken ct = default) =>
            Listings.TryGetValue(relativePath, out var entries)
                ? Task.FromResult(entries)
                : Task.FromResult<IReadOnlyList<SandboxDirectoryEntry>>([]);

        public Task<byte[]> ReadWorkspaceFileBytesAsync(string sessionId, string relativePath, long? maxBytes, CancellationToken ct = default)
        {
            ReadCalls++;
            return ReadThrows is not null ? Task.FromException<byte[]>(ReadThrows) : Task.FromResult(FileBytes);
        }

        public Task WriteWorkspaceFileBytesAsync(string sessionId, string relativePath, byte[] bytes, CancellationToken ct = default)
        {
            if (WriteThrows is not null)
            {
                return Task.FromException(WriteThrows);
            }

            Writes.Add((relativePath, bytes));
            return Task.CompletedTask;
        }

        public Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(string sessionId, SandboxCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(ExecResult);
        }
    }

    private sealed class FakeFormFile(string fileName, byte[] content, long? declaredLength = null) : IFormFile
    {
        private readonly byte[] _content = content;

        public string ContentType { get; set; } = "application/octet-stream";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; } = declaredLength ?? content.Length;
        public string Name { get; set; } = "file";
        public string FileName { get; } = fileName;

        public void CopyTo(Stream target) => target.Write(_content, 0, _content.Length);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            target.WriteAsync(_content, cancellationToken).AsTask();

        public Stream OpenReadStream() => new MemoryStream(_content);
    }

    private static (FileBrowserController Controller, FakeFileBrowser Browser) Build(string? workspaceId = "default")
    {
        var store = new Mock<IConversationStore>();
        var metadata = workspaceId is null
            ? null
            : new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = ImmutableDictionary<string, object>.Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, workspaceId),
            };
        store.Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);

        var browser = new FakeFileBrowser();
        var controller = new FileBrowserController(store.Object, browser, NullLogger<FileBrowserController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, browser);
    }

    private static (FileBrowserController Controller, FakeFileBrowser Browser) BuildUnknownThread()
    {
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>())).ReturnsAsync((ThreadMetadata?)null);
        var browser = new FakeFileBrowser();
        var controller = new FileBrowserController(store.Object, browser, NullLogger<FileBrowserController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, browser);
    }

    private static SandboxDirectoryEntry File(string name, long? size = 10, bool lossy = false) => new(name, SandboxEntryType.File, size, lossy);

    private static SandboxDirectoryEntry Dir(string name, bool lossy = false) => new(name, SandboxEntryType.Directory, null, lossy);

    // -------- Prologue --------

    [Fact]
    public async Task List_UnknownThread_Returns404()
    {
        var (controller, _) = BuildUnknownThread();
        var result = await controller.List(ThreadId, path: null, CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task List_LegacyMetadataWithoutWorkspace_Returns200NoSession()
    {
        var (controller, _) = Build(workspaceId: "   ");
        var result = await controller.List(ThreadId, path: null, CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeOfType<NoSessionStateDto>().Which.State.Should().Be("no_session_yet");
    }

    [Fact]
    public async Task List_NoSessionResolution_Returns200NoSession()
    {
        var (controller, browser) = Build();
        browser.Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.NoSession, null, null, null);
        var result = await controller.List(ThreadId, path: null, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<NoSessionStateDto>();
    }

    [Fact]
    public async Task Download_NoSession_Returns409NoSessionYet()
    {
        var (controller, browser) = Build();
        browser.Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.NoSession, null, null, null);
        var result = await controller.Download(ThreadId, "a.txt", CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task List_CredentialConflict_Returns409()
    {
        var (controller, browser) = Build();
        browser.Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.CredentialConflict, null, "owner", "intruder");
        var result = await controller.List(ThreadId, path: null, CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task List_GatewayUnavailableDuringResolve_Returns503()
    {
        var (controller, browser) = Build();
        // Resolution can recreate an idle-evicted session; a gateway blip throws SandboxSessionUnavailableException
        // (NOT a SandboxException). It must map to 503, not escape as an unmapped 500.
        browser.ResolveThrows = new SandboxSessionUnavailableException("default", null, "gateway down");
        var result = await controller.List(ThreadId, path: null, CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task List_ResolvesWorkspace_WhenPersistedPropertyIsAJsonElement()
    {
        // The persistent FileConversationStore round-trips ThreadMetadata.Properties through System.Text.Json,
        // so the "workspace" value comes back boxed as a JsonElement (ValueKind.String), NOT a System.String.
        // Regression guard for the live-E2E bug: a plain `value as string` returned null → every operation
        // resolved to no_session_yet even for a correctly-bound conversation.
        var stringMeta = new ThreadMetadata
        {
            ThreadId = ThreadId,
            LastUpdated = 0,
            Properties = ImmutableDictionary<string, object>.Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, "default"),
        };
        // Serialize + deserialize to reproduce the on-disk store's JsonElement-valued properties exactly.
        var roundTripped = JsonSerializer.Deserialize<ThreadMetadata>(JsonSerializer.Serialize(stringMeta))!;
        roundTripped.Properties![MultiTurnAgentPool.WorkspacePropertyKey].Should().BeOfType<JsonElement>();

        var store = new Mock<IConversationStore>();
        store.Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>())).ReturnsAsync(roundTripped);
        var browser = new FakeFileBrowser();
        browser.Listings[""] = [File("a.txt")];
        var controller = new FileBrowserController(store.Object, browser, NullLogger<FileBrowserController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.List(ThreadId, path: null, CancellationToken.None);

        // With the fix the workspace resolves and the real listing is returned — NOT a no_session_yet state.
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DirectoryListingDto>();
    }

    [Theory]
    [InlineData(42)]
    [InlineData(true)]
    public async Task List_ReturnsNoSession_WhenWorkspacePropertyIsNonStringJsonElement(object rawValue)
    {
        // A malformed persisted workspace value (a JSON number/bool/object) must NOT be reinterpreted as a
        // workspace id via a blanket ToString() — it is ignored, yielding the no-session state (mirrors
        // MultiTurnAgentPool's strict string normalization).
        var element = JsonSerializer.SerializeToElement(rawValue);
        var metadata = new ThreadMetadata
        {
            ThreadId = ThreadId,
            LastUpdated = 0,
            Properties = ImmutableDictionary<string, object>.Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, element),
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        var browser = new FakeFileBrowser();
        var controller = new FileBrowserController(store.Object, browser, NullLogger<FileBrowserController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.List(ThreadId, path: null, CancellationToken.None);

        // The non-string value is ignored → no_session_yet, never a bogus "42"/"True" workspace identity.
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<NoSessionStateDto>();
    }

    // -------- Listing --------

    [Fact]
    public async Task List_Root_ReturnsEntries()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("a.txt"), Dir("sub"), new SandboxDirectoryEntry("link", SandboxEntryType.Symlink, null, false)];
        var result = await controller.List(ThreadId, path: "", CancellationToken.None);
        var listing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DirectoryListingDto>().Which;
        listing.Entries.Should().HaveCount(3);
        listing.MoreCount.Should().Be(0);
        listing.Entries.Select(e => e.Type).Should().Contain("symlink");
    }

    [Fact]
    public async Task List_CapsAt500Rows_ReportsMoreCount()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [.. Enumerable.Range(0, 501).Select(i => File($"f{i}.txt"))];
        var result = await controller.List(ThreadId, path: "", CancellationToken.None);
        var listing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DirectoryListingDto>().Which;
        listing.Entries.Should().HaveCount(500);
        listing.MoreCount.Should().Be(1);
    }

    [Fact]
    public async Task List_LossyComponent_IsNotAddressable_Returns404()
    {
        var (controller, browser) = Build();
        // The only entry rendering as "weird" is lossy → no non-lossy ordinal match → not addressable.
        browser.Listings[""] = [File("weird", size: 1, lossy: true)];
        var result = await controller.List(ThreadId, path: "weird", CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task List_AmbiguousComponent_Returns400()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("dup"), Dir("dup")];
        var result = await controller.List(ThreadId, path: "dup", CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_DescendsThroughDirectories_UsingServerNames()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("sub")];
        browser.Listings["sub"] = [File("b.txt")];
        var result = await controller.List(ThreadId, path: "sub", CancellationToken.None);
        var listing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DirectoryListingDto>().Which;
        listing.Path.Should().Be("sub");
        listing.Entries.Should().ContainSingle().Which.Name.Should().Be("b.txt");
    }

    // -------- Download --------

    [Fact]
    public async Task Download_File_SetsAttachmentAndNosniff()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("a.txt", size: 5)];
        browser.FileBytes = [1, 2, 3, 4, 5];
        var result = await controller.Download(ThreadId, "a.txt", CancellationToken.None);
        var file = result.Should().BeOfType<FileContentResult>().Which;
        file.FileDownloadName.Should().Be("a.txt");
        file.ContentType.Should().Be("application/octet-stream");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task Download_ExactlyAtCap_Succeeds()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("big.bin", size: FileBrowserLimits.MaxDownloadBytes)];
        browser.FileBytes = [0];
        var result = await controller.Download(ThreadId, "big.bin", CancellationToken.None);
        result.Should().BeOfType<FileContentResult>();
    }

    [Fact]
    public async Task Download_OverCapByListedSize_Returns413_WithoutReading()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("huge.bin", size: FileBrowserLimits.MaxDownloadBytes + 1)];
        var result = await controller.Download(ThreadId, "huge.bin", CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Download_Directory_Returns400()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("sub")];
        var result = await controller.Download(ThreadId, "sub", CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // -------- Preview --------

    [Fact]
    public async Task Preview_NotAllowlisted_ReturnsBinary_WithoutReading()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("image.png", size: 10)];
        var result = await controller.Preview(ThreadId, "image.png", CancellationToken.None);
        var preview = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PreviewResultDto>().Which;
        preview.Previewable.Should().BeFalse();
        preview.Reason.Should().Be("binary");
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Preview_TooLargeByListedSize_ReturnsTooLarge_WithoutReading()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("big.txt", size: FileBrowserLimits.PreviewByteCap + 1)];
        var result = await controller.Preview(ThreadId, "big.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PreviewResultDto>().Which.Reason.Should().Be("too_large");
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Preview_InvalidUtf8_ReturnsNotUtf8()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("a.txt", size: 4)];
        browser.FileBytes = [0xFF, 0xFE, 0x00, 0x80];
        var result = await controller.Preview(ThreadId, "a.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PreviewResultDto>().Which.Reason.Should().Be("not_utf8");
    }

    [Theory]
    [InlineData("a\nb\nc", 3)]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\r\nb", 2)]
    [InlineData("solo", 1)]
    [InlineData("", 0)]
    public async Task Preview_LineCount_IsDeterministic(string text, int expectedLines)
    {
        var (controller, browser) = Build();
        var bytes = Encoding.UTF8.GetBytes(text);
        browser.Listings[""] = [File("a.txt", size: bytes.Length)];
        browser.FileBytes = bytes;
        var result = await controller.Preview(ThreadId, "a.txt", CancellationToken.None);
        var preview = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PreviewResultDto>().Which;
        preview.Previewable.Should().BeTrue();
        preview.Text.Should().Be(text);
        preview.LineCount.Should().Be(expectedLines);
    }

    // -------- Upload --------

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("C:evil.txt")]
    public async Task Upload_InvalidBaseName_Returns400(string fileName)
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile(fileName, [1, 2, 3]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Upload_DeclaredLengthOverCap_Returns413()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        // A declared over-cap length is rejected before any read (no bytes needed).
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("big.bin", [], declaredLength: FileBrowserLimits.MaxFileBytes + 1), CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Upload_Success_WritesBaseNameOntoTargetDirectory()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("sub")];
        browser.Listings["sub"] = [];
        var result = await controller.Upload(ThreadId, "sub", new FakeFormFile("note.txt", [9, 8, 7]), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<UploadResultDto>().Which.Name.Should().Be("note.txt");
        browser.Writes.Should().ContainSingle();
        browser.Writes[0].Path.Should().Be("sub/note.txt");
        browser.Writes[0].Bytes.Should().Equal(9, 8, 7);
    }

    [Fact]
    public async Task Upload_TargetBusy_Returns409()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        browser.WriteThrows = new SandboxException(SandboxErrorKind.Conflict, "target locked");
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("note.txt", [1]), CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
    }

    // -------- Delete --------

    [Fact]
    public async Task Delete_File_RunsRmDashDash_AndReturns204()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("a.txt")];
        var result = await controller.Delete(ThreadId, "a.txt", CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
        browser.Commands.Should().ContainSingle();
        browser.Commands[0].Arguments.Should().Equal("rm", "--", "a.txt");
    }

    [Fact]
    public async Task Delete_Directory_RunsRmRecursive_FromServerType()
    {
        var (controller, browser) = Build();
        // The client cannot force -r: the recursion is derived from the SERVER-returned directory type.
        browser.Listings[""] = [Dir("sub")];
        var result = await controller.Delete(ThreadId, "sub", CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
        browser.Commands[0].Arguments.Should().Equal("rm", "-r", "--", "sub");
    }

    [Fact]
    public async Task Delete_NonZeroExit_Returns422()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("a.txt")];
        browser.ExecResult = new SandboxCommandResult { ExitCode = 1, StandardOutput = "", StandardError = "denied", OperationId = "op" };
        var result = await controller.Delete(ThreadId, "a.txt", CancellationToken.None);
        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task Delete_Root_Returns400()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.Delete(ThreadId, "", CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        browser.Commands.Should().BeEmpty();
    }

    // -------- Review-driven boundary tests --------

    [Fact]
    public async Task Upload_ObservedBytesExceedCap_Returns413_EvenWhenDeclaredLengthIsSmall()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        // A LYING declared length (small) must not smuggle an over-cap body: the observed streaming count
        // trips independently. The stream yields MaxFileBytes+1 bytes lazily (no source allocation).
        var file = new LazyLargeFormFile("sneaky.bin", streamLength: FileBrowserLimits.MaxFileBytes + 1, declaredLength: 10);
        var result = await controller.Upload(ThreadId, "", file, CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        browser.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_BytesExceedCapAfterRead_ReturnsTooLarge_WhenListedSizeAbsent()
    {
        var (controller, browser) = Build();
        // Listed size is null (under-reported), so the pre-read gate passes and the read happens once; the
        // post-read length guard then rejects the cap+1 payload.
        browser.Listings[""] = [File("a.txt", size: null)];
        browser.FileBytes = new byte[FileBrowserLimits.PreviewByteCap + 1];
        var result = await controller.Preview(ThreadId, "a.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PreviewResultDto>().Which.Reason.Should().Be("too_large");
        browser.ReadCalls.Should().Be(1);
    }

    [Fact]
    public async Task Download_StreamedOverCap_MapsTypedSignalTo413()
    {
        var (controller, browser) = Build();
        // Listed size null so the deterministic pre-check passes; the SDK then refuses the over-cap body and
        // the typed IsDirectReadCapExceeded signal maps to 413 (not an opaque 502).
        browser.Listings[""] = [File("a.bin", size: null)];
        browser.ReadThrows = new SandboxException(SandboxErrorKind.Protocol, "exceeded the direct-read cap") { IsDirectReadCapExceeded = true };
        var result = await controller.Download(ThreadId, "a.bin", CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Preview_ExceedsLineCap_ReturnsTooLarge()
    {
        var (controller, browser) = Build();
        var text = string.Concat(Enumerable.Repeat("x\n", FileBrowserLimits.PreviewLineCap + 1));
        var bytes = Encoding.UTF8.GetBytes(text);
        browser.Listings[""] = [File("many.txt", size: bytes.Length)];
        browser.FileBytes = bytes;
        var result = await controller.Preview(ThreadId, "many.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PreviewResultDto>().Which.Reason.Should().Be("too_large");
    }

    /// <summary>An IFormFile whose stream lazily yields <c>streamLength</c> zero bytes (no source allocation), with a caller-set declared <c>Length</c> — exercises the observed-vs-declared upload cap.</summary>
    private sealed class LazyLargeFormFile(string fileName, long streamLength, long declaredLength) : IFormFile
    {
        public string ContentType { get; set; } = "application/octet-stream";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; } = declaredLength;
        public string Name { get; set; } = "file";
        public string FileName { get; } = fileName;

        public void CopyTo(Stream target) => throw new NotSupportedException();

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Stream OpenReadStream() => new ZeroStream(streamLength);

        private sealed class ZeroStream(long length) : Stream
        {
            private long _remaining = length;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0)
                {
                    return 0;
                }

                var produced = (int)Math.Min(count, _remaining);
                Array.Clear(buffer, offset, produced);
                _remaining -= produced;
                return produced;
            }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
