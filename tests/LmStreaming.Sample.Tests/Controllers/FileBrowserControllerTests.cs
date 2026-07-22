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

        /// <summary>The <c>persistedWorkspaceId</c> the controller passed to the last resolve call (the value
        /// <c>ReadWorkspaceId</c> extracted from metadata) — asserted by the JsonElement regression tests.</summary>
        public string? LastPersistedWorkspaceId { get; private set; }

        public Dictionary<string, IReadOnlyList<SandboxDirectoryEntry>> Listings { get; } = new(StringComparer.Ordinal);
        public byte[] FileBytes { get; set; } = [];
        public Exception? ReadThrows { get; set; }
        public Exception? WriteThrows { get; set; }
        public SandboxCommandResult ExecResult { get; set; } = new() { ExitCode = 0, StandardOutput = "", StandardError = "", OperationId = "op" };
        public List<(string Path, byte[] Bytes)> Writes { get; } = [];
        public List<SandboxCommand> Commands { get; } = [];
        public int ReadCalls { get; private set; }

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(string threadId, string persistedWorkspaceId, SandboxCredential? requestCredential, CancellationToken ct = default)
        {
            LastPersistedWorkspaceId = persistedWorkspaceId;
            return ResolveThrows is not null ? Task.FromException<SandboxSessionResolution>(ResolveThrows) : Task.FromResult(Resolution);
        }

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

    private static SandboxDirectoryEntry Symlink(string name, bool lossy = false) => new(name, SandboxEntryType.Symlink, null, lossy);

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

        // The workspace resolves and the real listing is returned — NOT a no_session_yet state.
        var listing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DirectoryListingDto>().Subject;
        // Pin the EXTRACTED value, not merely "not no-session": the id handed to the resolver and echoed on
        // the DTO must both be exactly "default" (a loose ToString() of a non-string JsonElement, or a wrong
        // non-empty value, would still yield a listing but a different id here).
        browser.LastPersistedWorkspaceId.Should().Be("default");
        listing.WorkspaceId.Should().Be("default");
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
        // And the malformed value never even reached the resolver (short-circuited before session resolution).
        browser.LastPersistedWorkspaceId.Should().BeNull();
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

    [Fact]
    public async Task Resolve_RejectsBackslashPath_AsInvalidPath_NotAsSeparator()
    {
        var (controller, browser) = Build();
        // A literal POSIX file named "a\b" at root, alongside a real "a" directory with a "b" child. The
        // backslash must be rejected (invalid_path), NOT rewritten to '/', which would misresolve the
        // literal "a\b" onto the a/b directory entry (wrong-object identity).
        browser.Listings[""] = [File("a\\b"), Dir("a")];
        browser.Listings["a"] = [File("b")];

        var result = await controller.List(ThreadId, path: "a\\b", CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeEquivalentTo(new { error = "invalid_path", code = "invalid_path", threadId = ThreadId });
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
        var result = await controller.Upload(ThreadId, "", new FakeFormFile(fileName, [1, 2, 3]), relativePath: null, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Upload_DeclaredLengthOverCap_Returns413()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        // A declared over-cap length is rejected before any read (no bytes needed).
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("big.bin", [], declaredLength: FileBrowserLimits.MaxFileBytes + 1), relativePath: null, CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Upload_Success_WritesBaseNameOntoTargetDirectory()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("sub")];
        browser.Listings["sub"] = [];
        var result = await controller.Upload(ThreadId, "sub", new FakeFormFile("note.txt", [9, 8, 7]), relativePath: null, CancellationToken.None);
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
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("note.txt", [1]), relativePath: null, CancellationToken.None);
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

    // -------- Create directory (WI #214) --------

    [Fact]
    public async Task CreateDirectory_ValidName_CreatesWithMkdir_AtRoot_AndReturns200()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest("newdir"), CancellationToken.None);
        var dto = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CreateDirectoryResultDto>().Which;
        dto.Path.Should().Be("newdir");
        // A single `mkdir --` under the resolved (verified real) directory — NOT `mkdir -p`, which would follow a
        // symlink in the chain. `--` keeps a leading-dash name an operand.
        browser.Commands.Should().ContainSingle();
        browser.Commands[0].Arguments.Should().Equal("mkdir", "--", "newdir");
    }

    [Fact]
    public async Task CreateDirectory_UnderResolvedSubdirectory_UsesServerPath()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("sub")];
        browser.Listings["sub"] = [];
        var result = await controller.CreateDirectory(ThreadId, "sub", new CreateDirectoryRequest("child"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CreateDirectoryResultDto>().Which.Path.Should().Be("sub/child");
        browser.Commands[0].Arguments.Should().Equal("mkdir", "--", "sub/child");
    }

    [Fact]
    public async Task CreateDirectory_ExistingDirectory_IsIdempotentSuccess_WithoutMkdir()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("existing")];
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest("existing"), CancellationToken.None);
        // An existing REAL directory is idempotent success — no mkdir is run (server verifies the type first).
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CreateDirectoryResultDto>().Which.Path.Should().Be("existing");
        browser.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDirectory_ExistingSymlink_Returns409Conflict_WithoutMkdir()
    {
        var (controller, browser) = Build();
        // #214: an existing symlink at the path must FAIL — never silently 200 by letting `mkdir -p` follow it.
        browser.Listings[""] = [Symlink("linked")];
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest("linked"), CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        browser.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDirectory_ExistingFile_Returns409Conflict_WithoutMkdir()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("readme.md")];
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest("readme.md"), CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        browser.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:evil")]
    [InlineData("a\0b")]
    public async Task CreateDirectory_InvalidName_Returns400_WithoutRunningCommand(string name)
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest(name), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        browser.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDirectory_MissingBody_Returns400_WithoutRunningCommand()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.CreateDirectory(ThreadId, "", request: null, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        browser.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDirectory_NonZeroExit_Returns422()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        // A non-zero `mkdir -p` exit (e.g. a file/symlink already occupies the path) is a structured failure.
        browser.ExecResult = new SandboxCommandResult { ExitCode = 1, StandardOutput = "", StandardError = "File exists", OperationId = "op" };
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest("clash"), CancellationToken.None);
        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task CreateDirectory_NoSession_Returns409()
    {
        var (controller, browser) = Build();
        browser.Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.NoSession, null, null, null);
        var result = await controller.CreateDirectory(ThreadId, "", new CreateDirectoryRequest("newdir"), CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        browser.Commands.Should().BeEmpty();
    }

    // -------- Folder upload: relative-path destination (WI #214) --------

    [Fact]
    public async Task Upload_WithRelativePath_CreatesParentsOneComponentAtATime_AndWritesNestedDestination()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("readme.md", [1, 2, 3]), relativePath: "proj/docs/readme.md", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        // Parents are created ONE component at a time with `mkdir --` (never `mkdir -p`), so no symlink in the
        // chain can be followed; the write lands at the full relative destination.
        browser.Commands.Select(c => c.Arguments).Should().SatisfyRespectively(
            first => first.Should().Equal("mkdir", "--", "proj"),
            second => second.Should().Equal("mkdir", "--", "proj/docs")
        );
        browser.Writes.Should().ContainSingle();
        browser.Writes[0].Path.Should().Be("proj/docs/readme.md");
        browser.Writes[0].Bytes.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Upload_WithRelativePath_UnderResolvedTargetDirectory()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("up")];
        browser.Listings["up"] = [];
        var result = await controller.Upload(ThreadId, "up", new FakeFormFile("a.txt", [7]), relativePath: "x/a.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        browser.Commands.Should().ContainSingle();
        browser.Commands[0].Arguments.Should().Equal("mkdir", "--", "up/x");
        browser.Writes[0].Path.Should().Be("up/x/a.txt");
    }

    [Fact]
    public async Task Upload_WithRelativePath_ReusesExistingRealDirectoryParent_WithoutMkdir()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [Dir("existing")];
        browser.Listings["existing"] = [];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("n.txt", [1]), relativePath: "existing/n.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        // An existing REAL directory parent is reused — no mkdir.
        browser.Commands.Should().BeEmpty();
        browser.Writes[0].Path.Should().Be("existing/n.txt");
    }

    [Fact]
    public async Task Upload_WithRelativePath_SymlinkParent_Returns409Conflict_WithoutMkdirOrWrite()
    {
        var (controller, browser) = Build();
        // THE security fix: a symlink in the parent chain (`linked/`) is rejected, not traversed — a relative
        // path can no longer escape the resolved directory THROUGH a symlink.
        browser.Listings[""] = [Symlink("linked")];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("f.txt", [1]), relativePath: "linked/f.txt", CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        browser.Commands.Should().BeEmpty();
        browser.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_WithRelativePath_FileParent_Returns409Conflict_WithoutMkdirOrWrite()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [File("data")];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("f.txt", [1]), relativePath: "data/f.txt", CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        browser.Commands.Should().BeEmpty();
        browser.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_WithRelativePath_SymlinkAtLeaf_Returns409Conflict_WithoutWrite()
    {
        var (controller, browser) = Build();
        // A symlink at the write target itself must not be written THROUGH either.
        browser.Listings[""] = [Symlink("link.txt")];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("link.txt", [1]), relativePath: "link.txt", CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        browser.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_WithSingleSegmentRelativePath_SkipsMkdir_AndWritesAtRoot()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("a.txt", [1]), relativePath: "a.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        browser.Commands.Should().BeEmpty();
        browser.Writes[0].Path.Should().Be("a.txt");
    }

    [Fact]
    public async Task Upload_WithRelativePath_EchoesRelativePathAsName()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("a.txt", [1]), relativePath: "d/a.txt", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<UploadResultDto>().Which.Name.Should().Be("d/a.txt");
    }

    [Theory]
    [InlineData("../a.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("/abs.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("C:/x.txt")]
    [InlineData("a/b\0c.txt")]
    [InlineData("..")]
    [InlineData("a/")]
    public async Task Upload_InvalidRelativePath_Returns400_WithoutCommandOrWrite(string relativePath)
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("leaf.txt", [1, 2]), relativePath, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        browser.Commands.Should().BeEmpty();
        browser.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_RelativePath_MkdirNonZeroExit_Returns422_WithoutWriting()
    {
        var (controller, browser) = Build();
        browser.Listings[""] = [];
        // A failed `mkdir --` (non-zero exit) creating a missing parent surfaces as a structured failure, no write.
        browser.ExecResult = new SandboxCommandResult { ExitCode = 1, StandardOutput = "", StandardError = "denied", OperationId = "op" };
        var result = await controller.Upload(ThreadId, "", new FakeFormFile("a.txt", [1]), relativePath: "newdir/a.txt", CancellationToken.None);
        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        browser.Writes.Should().BeEmpty();
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
        var result = await controller.Upload(ThreadId, "", file, relativePath: null, CancellationToken.None);
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
