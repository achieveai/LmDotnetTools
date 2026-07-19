using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// REST surface for the workspace file browser (WI #195): list/navigate/preview/download/upload/delete of
/// files in the ACTIVE conversation's sandbox workspace. Session resolution is server-authoritative and
/// non-creating — it never provisions a first-time sandbox — and every path is resolved one component at a
/// time against the gateway's authoritative listing (exactly one non-lossy ordinal match per level, using
/// the server-returned type). Client-supplied workspace/session/app ids and entry types are never trusted.
/// </summary>
[ApiController]
[Route("api/conversations/{threadId}/files")]
[InboundS2SAuth]
public sealed class FileBrowserController(
    IConversationStore store,
    IWorkspaceFileBrowser fileBrowser,
    ILogger<FileBrowserController> logger
) : ControllerBase
{
    private enum ResolveFailure
    {
        None,
        NotFound,
        Ambiguous,
        NotADirectory,
        InvalidPath,
    }

    private readonly record struct ResolvedTarget(bool Success, string ServerPath, SandboxEntryType Type, ResolveFailure Failure)
    {
        public static ResolvedTarget Ok(string serverPath, SandboxEntryType type) => new(true, serverPath, type, ResolveFailure.None);

        public static ResolvedTarget Fail(ResolveFailure failure) => new(false, string.Empty, default, failure);
    }

    private readonly record struct SessionContext(bool Ok, SandboxSession? Session, string? WorkspaceId, IActionResult? Error, bool NoSession);

    // -------- Endpoints --------

    /// <summary>Lists a workspace directory. Returns 200 with a no-session state when the conversation has no established sandbox.</summary>
    [HttpGet]
    public async Task<IActionResult> List(string threadId, [FromQuery] string? path, CancellationToken ct)
    {
        var context = await ResolveSessionAsync(threadId, isListing: true, ct);
        if (!context.Ok)
        {
            return context.Error!;
        }

        if (context.NoSession)
        {
            return Ok(NoSessionStateDto.For(context.WorkspaceId));
        }

        var session = context.Session!;
        try
        {
            var target = await ResolveTargetAsync(session.SessionId, path ?? string.Empty, ct);
            if (!target.Success)
            {
                return ResolveFailureResult(target.Failure, threadId);
            }

            if (target.Type != SandboxEntryType.Directory)
            {
                return BadRequest(new { error = "not_a_directory", code = "not_a_directory", threadId });
            }

            var entries = await fileBrowser.ListWorkspaceDirectoryAsync(session.SessionId, target.ServerPath, ct);
            var shown = entries.Take(FileBrowserLimits.MaxListingRows).Select(FileEntryDto.From).ToList();
            var moreCount = Math.Max(0, entries.Count - FileBrowserLimits.MaxListingRows);
            return Ok(new DirectoryListingDto(context.WorkspaceId ?? string.Empty, target.ServerPath, shown, moreCount));
        }
        catch (SandboxException ex)
        {
            return MapSandbox(ex, threadId);
        }
    }

    /// <summary>Downloads a workspace file as an attachment (nosniff), bounded-buffered at 64 MiB.</summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download(string threadId, [FromQuery] string? path, CancellationToken ct)
    {
        var context = await ResolveSessionAsync(threadId, isListing: false, ct);
        if (!context.Ok)
        {
            return context.Error!;
        }

        var session = context.Session!;
        try
        {
            var target = await ResolveTargetAsync(session.SessionId, path ?? string.Empty, ct);
            if (!target.Success)
            {
                return ResolveFailureResult(target.Failure, threadId);
            }

            if (target.Type != SandboxEntryType.File)
            {
                return BadRequest(new { error = "not_a_file", code = "not_a_file", threadId });
            }

            // Deterministic 413 from the authoritative listed size — refuse an over-cap file without
            // reading a byte, so an oversize download is rejected without truncation.
            var size = await ResolveFileSizeAsync(session.SessionId, target.ServerPath, ct);
            if (size is > FileBrowserLimits.MaxDownloadBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "file_too_large", code = "file_too_large", threadId });
            }

            var bytes = await fileBrowser.ReadWorkspaceFileBytesAsync(session.SessionId, target.ServerPath, FileBrowserLimits.MaxDownloadBytes, ct);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            var fileName = LastComponent(target.ServerPath);
            return File(bytes, "application/octet-stream", fileDownloadName: fileName);
        }
        catch (SandboxException ex) when (IsOverCap(ex))
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "file_too_large", code = "file_too_large", threadId });
        }
        catch (SandboxException ex)
        {
            return MapSandbox(ex, threadId);
        }
    }

    /// <summary>Returns an inline text preview when the file is on the server-side allowlist and within the caps; otherwise a non-previewable reason.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(string threadId, [FromQuery] string? path, CancellationToken ct)
    {
        var context = await ResolveSessionAsync(threadId, isListing: false, ct);
        if (!context.Ok)
        {
            return context.Error!;
        }

        var session = context.Session!;
        try
        {
            var target = await ResolveTargetAsync(session.SessionId, path ?? string.Empty, ct);
            if (!target.Success)
            {
                return ResolveFailureResult(target.Failure, threadId);
            }

            if (target.Type != SandboxEntryType.File)
            {
                return Ok(new PreviewResultDto(false, "not_a_file", null, null));
            }

            var name = LastComponent(target.ServerPath);
            // Server-authoritative allowlist decides eligibility BEFORE any read.
            if (!FilePreviewPolicy.IsPreviewable(name))
            {
                return Ok(new PreviewResultDto(false, "binary", null, null));
            }

            // Refuse an over-large file by its listed size WITHOUT reading a byte.
            var size = await ResolveFileSizeAsync(session.SessionId, target.ServerPath, ct);
            if (size is > FileBrowserLimits.PreviewByteCap)
            {
                return Ok(new PreviewResultDto(false, "too_large", null, null));
            }

            var bytes = await fileBrowser.ReadWorkspaceFileBytesAsync(session.SessionId, target.ServerPath, FileBrowserLimits.PreviewByteCap + 1, ct);
            if (bytes.LongLength > FileBrowserLimits.PreviewByteCap)
            {
                return Ok(new PreviewResultDto(false, "too_large", null, null));
            }

            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Ok(new PreviewResultDto(false, "not_utf8", null, null));
            }

            var lineCount = CountLines(text);
            if (lineCount > FileBrowserLimits.PreviewLineCap)
            {
                return Ok(new PreviewResultDto(false, "too_large", null, null));
            }

            return Ok(new PreviewResultDto(true, null, text, lineCount));
        }
        catch (SandboxException ex)
        {
            return MapSandbox(ex, threadId);
        }
    }

    /// <summary>Uploads ONE file into a workspace directory. Inclusive 64 MiB per file (declared and observed); advisory overwrite is last-writer-wins.</summary>
    [HttpPost]
    [RequestSizeLimit(FileBrowserLimits.MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = FileBrowserLimits.MaxUploadRequestBytes)]
    public async Task<IActionResult> Upload(string threadId, [FromQuery] string? path, [FromForm(Name = "file")] IFormFile? file, CancellationToken ct)
    {
        var context = await ResolveSessionAsync(threadId, isListing: false, ct);
        if (!context.Ok)
        {
            return context.Error!;
        }

        if (file is null)
        {
            return BadRequest(new { error = "no_file", code = "no_file", threadId });
        }

        if (!IsValidBaseName(file.FileName))
        {
            return BadRequest(new { error = "invalid_file_name", code = "invalid_file_name", threadId });
        }

        // Declared-length check first, then re-check the OBSERVED bytes while reading — a lying declared
        // length can never smuggle more than the cap into the workspace.
        if (file.Length > FileBrowserLimits.MaxFileBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "file_too_large", code = "file_too_large", threadId });
        }

        var session = context.Session!;
        try
        {
            var target = await ResolveTargetAsync(session.SessionId, path ?? string.Empty, ct);
            if (!target.Success)
            {
                return ResolveFailureResult(target.Failure, threadId);
            }

            if (target.Type != SandboxEntryType.Directory)
            {
                return BadRequest(new { error = "not_a_directory", code = "not_a_directory", threadId });
            }

            var bytes = await ReadUploadWithinCapAsync(file, ct);
            if (bytes is null)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "file_too_large", code = "file_too_large", threadId });
            }

            var serverPath = target.ServerPath.Length == 0 ? file.FileName : $"{target.ServerPath}/{file.FileName}";
            await fileBrowser.WriteWorkspaceFileBytesAsync(session.SessionId, serverPath, bytes, ct);
            return Ok(new UploadResultDto(file.FileName, bytes.LongLength));
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.Conflict)
        {
            return Conflict(new { error = "target_busy", code = "target_busy", threadId });
        }
        catch (SandboxException ex)
        {
            return MapSandbox(ex, threadId);
        }
    }

    /// <summary>Deletes a workspace file/directory via an <c>rm</c> assembled from the SERVER-verified entry type (no client-supplied type/recursion).</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete(string threadId, [FromQuery] string? path, CancellationToken ct)
    {
        var context = await ResolveSessionAsync(threadId, isListing: false, ct);
        if (!context.Ok)
        {
            return context.Error!;
        }

        var session = context.Session!;
        try
        {
            var target = await ResolveTargetAsync(session.SessionId, path ?? string.Empty, ct);
            if (!target.Success)
            {
                return ResolveFailureResult(target.Failure, threadId);
            }

            if (target.ServerPath.Length == 0)
            {
                return BadRequest(new { error = "cannot_delete_root", code = "cannot_delete_root", threadId });
            }

            // Build the argv SOLELY from the server-returned final type. `--` terminates option parsing so a
            // leading-dash name is an operand, never a flag.
            string[] argv = target.Type == SandboxEntryType.Directory
                ? ["rm", "-r", "--", target.ServerPath]
                : ["rm", "--", target.ServerPath];

            var result = await fileBrowser.ExecuteWorkspaceCommandAsync(session.SessionId, new SandboxCommand(argv), ct);
            if (result.ExitCode != 0)
            {
                logger.LogWarning(
                    "File browser delete failed for thread {ThreadId} session {SessionId}: rm exited {ExitCode} for {Path}",
                    threadId,
                    session.SessionId,
                    result.ExitCode,
                    target.ServerPath
                );
                return UnprocessableEntity(new { error = "delete_failed", code = "delete_failed", exitCode = result.ExitCode, threadId });
            }

            logger.LogInformation(
                "File browser deleted {Path} ({Type}) for thread {ThreadId} session {SessionId}",
                target.ServerPath,
                target.Type,
                threadId,
                session.SessionId
            );
            return NoContent();
        }
        catch (SandboxException ex)
        {
            return MapSandbox(ex, threadId);
        }
    }

    // -------- Prologue / resolution --------

    private async Task<SessionContext> ResolveSessionAsync(string threadId, bool isListing, CancellationToken ct)
    {
        var metadata = await store.LoadMetadataAsync(threadId, ct);
        if (metadata is null)
        {
            return new SessionContext(false, null, null, NotFound(new { error = "unknown_thread", code = "unknown_thread", threadId }), false);
        }

        var workspaceId = ReadWorkspaceId(metadata);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return NoSessionContext(isListing, workspaceId);
        }

        var credential = TryBuildCallerCredential();
        var resolution = await fileBrowser.ResolveThreadWorkspaceSessionAsync(threadId, workspaceId, credential, ct);
        switch (resolution.Outcome)
        {
            case SandboxSessionResolutionOutcome.CredentialConflict:
                logger.LogWarning(
                    "File browser request for thread {ThreadId} rejected: caller credential conflict (existing {ExistingAppId}, requested {RequestedAppId})",
                    threadId,
                    resolution.ExistingAppId ?? "(none)",
                    resolution.RequestedAppId ?? "(none)"
                );
                return new SessionContext(
                    false,
                    null,
                    workspaceId,
                    Conflict(new { error = "caller_credential_conflict", code = "caller_credential_conflict", threadId }),
                    false
                );
            case SandboxSessionResolutionOutcome.NoSession:
                return NoSessionContext(isListing, workspaceId);
            case SandboxSessionResolutionOutcome.Resolved:
            default:
                return new SessionContext(true, resolution.Session, workspaceId, null, false);
        }
    }

    private SessionContext NoSessionContext(bool isListing, string? workspaceId)
    {
        // Listing surfaces a structured 200 state so the browser can render "no sandbox yet"; a mutating or
        // content action returns 409 with a stable code instead.
        if (isListing)
        {
            return new SessionContext(true, null, workspaceId, null, true);
        }

        return new SessionContext(
            false,
            null,
            workspaceId,
            Conflict(new { code = NoSessionStateDto.StateValue, error = NoSessionStateDto.StateValue }),
            false
        );
    }

    /// <summary>
    /// Resolves <paramref name="requestedPath"/> one component at a time against the authoritative gateway
    /// listing. At each level exactly one ordinal, non-lossy match is required; every non-final match must
    /// be a directory. The returned path is assembled from the SERVER names (inherently normalized), and the
    /// returned type is the server's — client-supplied types/flags are never trusted. The empty path is the
    /// workspace root.
    /// </summary>
    private async Task<ResolvedTarget> ResolveTargetAsync(string sessionId, string requestedPath, CancellationToken ct)
    {
        var normalized = requestedPath.Replace('\\', '/');
        var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var serverParts = new List<string>(components.Length);
        var currentDir = string.Empty;
        var currentType = SandboxEntryType.Directory;

        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (component is "." or ".." || component.Contains('\0'))
            {
                return ResolvedTarget.Fail(ResolveFailure.InvalidPath);
            }

            var entries = await fileBrowser.ListWorkspaceDirectoryAsync(sessionId, currentDir, ct);
            var matches = entries
                .Where(e => !e.NameLossy && string.Equals(e.Name, component, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                return ResolvedTarget.Fail(ResolveFailure.NotFound);
            }

            if (matches.Count > 1)
            {
                return ResolvedTarget.Fail(ResolveFailure.Ambiguous);
            }

            var matched = matches[0];
            var isLast = i == components.Length - 1;
            if (!isLast && matched.Type != SandboxEntryType.Directory)
            {
                return ResolvedTarget.Fail(ResolveFailure.NotADirectory);
            }

            serverParts.Add(matched.Name);
            currentDir = string.Join('/', serverParts);
            currentType = matched.Type;
        }

        return ResolvedTarget.Ok(currentDir, currentType);
    }

    private async Task<long?> ResolveFileSizeAsync(string sessionId, string serverPath, CancellationToken ct)
    {
        var parent = ParentPath(serverPath);
        var name = LastComponent(serverPath);
        var entries = await fileBrowser.ListWorkspaceDirectoryAsync(sessionId, parent, ct);
        var entry = entries.FirstOrDefault(e => !e.NameLossy && string.Equals(e.Name, name, StringComparison.Ordinal));
        return entry?.Size;
    }

    // -------- Helpers --------

    private IActionResult ResolveFailureResult(ResolveFailure failure, string threadId) =>
        failure switch
        {
            ResolveFailure.NotFound => NotFound(new { error = "not_found", code = "not_found", threadId }),
            ResolveFailure.Ambiguous => BadRequest(new { error = "ambiguous_path", code = "ambiguous_path", threadId }),
            ResolveFailure.NotADirectory => BadRequest(new { error = "not_a_directory", code = "not_a_directory", threadId }),
            _ => BadRequest(new { error = "invalid_path", code = "invalid_path", threadId }),
        };

    private IActionResult MapSandbox(SandboxException ex, string threadId)
    {
        if (string.Equals(ex.ErrorCode, "session_not_found", StringComparison.Ordinal))
        {
            return Conflict(new { error = "session_expired", code = "session_expired", threadId });
        }

        return ex.Kind switch
        {
            SandboxErrorKind.NotFound => NotFound(new { error = "not_found", code = "not_found", threadId }),
            SandboxErrorKind.Conflict => Conflict(new { error = "target_busy", code = "target_busy", threadId }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = "gateway_error", code = "gateway_error", threadId }),
        };
    }

    private static bool IsOverCap(SandboxException ex) =>
        ex.Kind == SandboxErrorKind.Protocol && ex.Message.Contains("direct-read cap", StringComparison.Ordinal);

    private SandboxCredential? TryBuildCallerCredential()
    {
        var appId = Request.Headers[SandboxCredential.AppIdHeader].ToString();
        if (string.IsNullOrEmpty(appId))
        {
            return null;
        }

        var appKey = Request.Headers[SandboxCredential.AppKeyHeader].ToString();
        return new SandboxCredential(appId, appKey);
    }

    private static string? ReadWorkspaceId(ThreadMetadata metadata) =>
        metadata.Properties is not null
        && metadata.Properties.TryGetValue(MultiTurnAgentPool.WorkspacePropertyKey, out var value)
            ? value as string
            : null;

    private static async Task<byte[]?> ReadUploadWithinCapAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > FileBrowserLimits.MaxFileBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Rejects anything that is not a bare file name: empty, <c>.</c>/<c>..</c>, path separators, NUL, rooted/drive/UNC.</summary>
    private static bool IsValidBaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        if (name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
        {
            return false;
        }

        // A drive-qualified name (C:foo) has a colon in the second position; reject any colon to be safe on
        // a Windows host (a POSIX file name may legitimately contain ':' but the workspace is addressed
        // POSIX-relative, so a basename never needs one).
        return !name.Contains(':');
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        // A non-empty final line with no trailing newline still counts (CRLF and LF are each one boundary;
        // the '\r' of a CRLF is not counted separately).
        if (text[^1] != '\n')
        {
            lines++;
        }

        return lines;
    }

    private static string LastComponent(string serverPath)
    {
        var slash = serverPath.LastIndexOf('/');
        return slash < 0 ? serverPath : serverPath[(slash + 1)..];
    }

    private static string ParentPath(string serverPath)
    {
        var slash = serverPath.LastIndexOf('/');
        return slash < 0 ? string.Empty : serverPath[..slash];
    }
}
