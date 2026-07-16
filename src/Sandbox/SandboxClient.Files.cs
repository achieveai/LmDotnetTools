using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Reads a workspace file as UTF-8 text via the gateway's direct files API (ADR 0031 / issue #119):
    /// a single <c>GET api/v1/sandboxes/{session_id}/files/{mount_id}?path=...</c> that returns the
    /// file's exact current bytes as <c>application/octet-stream</c>. The gateway is authoritative for
    /// the file's identity and byte content — there is nothing left for the SDK to reassemble or
    /// verify, only a strict UTF-8 decode of what comes back.
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> is a workspace-relative POSIX path, validated exactly like
    /// <see cref="SandboxCommand.WorkingDirectory"/> (rooted, drive/UNC/device-qualified, backslash-bearing,
    /// <c>..</c>-escaping, or NUL-bearing values are rejected). The gateway remains authoritative for
    /// symlink containment and for what "a file" means (a directory or the mount root is rejected
    /// server-side as <c>not_a_file</c>).
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The path does not exist or does not name a regular file (<see cref="SandboxErrorKind.NotFound"/>);
    /// the returned bytes are not valid UTF-8 (<see cref="SandboxErrorKind.Integrity"/>); the transport
    /// deadline elapsed (<see cref="SandboxErrorKind.TransportTimeout"/>); or the gateway otherwise
    /// rejected or malformed the request (<see cref="SandboxErrorKind.Protocol"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<string> ReadTextFileAsync(string sessionId, string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var relativePath = NormalizeFilePath(path);

        var mountId = await ResolveWorkspaceMountIdAsync(sessionId, ct).ConfigureAwait(false);
        var bytes = await DownloadCappedBytesAsync(
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/files/{mountId}?path={Uri.EscapeDataString(relativePath)}",
                sessionId,
                $"reading file '{path}'",
                operationId: null,
                ct
            )
            .ConfigureAwait(false);
        return DecodeFileUtf8OrThrow(bytes, "file");
    }

    /// <summary>
    /// Writes <paramref name="content"/> as UTF-8 to a workspace file via the gateway's direct files API
    /// (ADR 0031 / issue #119): a single <c>PUT api/v1/sandboxes/{session_id}/files/{mount_id}?path=...</c>
    /// carrying the exact new bytes as the request body. The gateway performs the atomic replace itself
    /// (a temp write plus same-directory rename), so the SDK no longer streams chunks, verifies a temp's
    /// digest, or issues a separate finalize/rename step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direct files API does NOT create the target's parent directory (and the direct API exposes no
    /// directory-create endpoint), so a nested write whose parent does not yet exist is answered
    /// <c>404 path_not_found</c>. To preserve the surface-compatible "a write creates its parents"
    /// contract the removed MCP shell path provided (it ran <c>mkdir -p</c> before the write), this method
    /// self-heals that one case: on a <c>path_not_found</c> for a path WITH a parent component it runs a
    /// single <c>mkdir -p</c> operation for the parent, then retries the PUT exactly once. A top-level
    /// (parentless) write is always a single PUT and never issues an operation.
    /// </para>
    /// <para>
    /// Each PUT is a single request: either the gateway reports the new byte count and the target is
    /// atomically replaced, or the request fails and the target is left untouched (the gateway never
    /// exposes a partially-written file).
    /// </para>
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The gateway reported writing a different number of bytes than were sent
    /// (<see cref="SandboxErrorKind.Protocol"/>); the target is held by a concurrent writer
    /// (<see cref="SandboxErrorKind.Conflict"/>); the parent-directory self-heal (<c>mkdir -p</c>) did not
    /// succeed (<see cref="SandboxErrorKind.Protocol"/>); the transport deadline elapsed
    /// (<see cref="SandboxErrorKind.TransportTimeout"/>); or the gateway otherwise rejected or malformed
    /// the request (<see cref="SandboxErrorKind.Protocol"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task WriteTextFileAsync(
      string sessionId,
      string path,
      string content,
      CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(content);
        var relativePath = NormalizeFilePath(path);

        var mountId = await ResolveWorkspaceMountIdAsync(sessionId, ct).ConfigureAwait(false);
        var bytes = S_strictUtf8.GetBytes(content);

        try
        {
            await PutFileBytesAsync(sessionId, mountId, relativePath, path, bytes, ct).ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
        {
            // The direct files PUT does not create the target's parent (there is no directory-create
            // endpoint), so a nested write whose parent is missing is answered with an explicit
            // path_not_found — the shared SandboxException.IsDefiniteMissingPath signal the adapter degrade
            // paths also use. A code-less or eviction 404 is deliberately NOT self-healed (it is ambiguous
            // with a dead session and would fire an unwanted mkdir + retry). A top-level file has no parent
            // to create — surface the original failure unchanged. Otherwise self-heal the parent with a
            // single `mkdir -p` operation and retry the PUT exactly once.
            var parentDirectory = GetWorkspaceParentDirectory(relativePath);
            if (parentDirectory is null)
            {
                throw;
            }

            await CreateDirectoryAsync(sessionId, mountId, parentDirectory, ct).ConfigureAwait(false);
            await PutFileBytesAsync(sessionId, mountId, relativePath, path, bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues one <c>PUT .../files/{mount_id}?path=...</c> with the exact <paramref name="bytes"/> and
    /// validates the gateway's reported byte count. Extracted so <see cref="WriteTextFileAsync"/> can send
    /// it twice (the retry after a parent-directory self-heal needs a fresh <see cref="HttpContent"/>, since
    /// a sent one cannot be re-sent). <paramref name="displayPath"/> is the caller's original path, used only
    /// for messages.
    /// </summary>
    private async Task PutFileBytesAsync(
        string sessionId,
        long mountId,
        string relativePath,
        string displayPath,
        byte[] bytes,
        CancellationToken ct
    )
    {
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendDirectAsync(
                HttpMethod.Put,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/files/{mountId}?path={Uri.EscapeDataString(relativePath)}",
                body,
                sessionId,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(response, $"writing file '{displayPath}'", sessionId).ConfigureAwait(false);
        }

        WriteFileResponseDto? written;
        try
        {
            written = await response.Content
                .ReadFromJsonAsync<WriteFileResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new SandboxException(
              SandboxErrorKind.Protocol,
              $"Sandbox gateway returned a malformed write response for '{displayPath}'.",
              (int)response.StatusCode,
              ex
            );
        }

        // A cheap end-to-end integrity check: the gateway's own reported byte count should always match
        // what was actually sent (a mismatch means the request or response was corrupted in transit).
        if (written is null || written.BytesWritten != bytes.Length)
        {
            throw new SandboxException(
              SandboxErrorKind.Protocol,
              $"Sandbox gateway reported an unexpected byte count writing '{displayPath}'."
            );
        }
    }

    /// <summary>
    /// Creates <paramref name="relativeDirectory"/> (and any missing ancestors) under the workspace via a
    /// single <c>mkdir -p</c> operation, reusing the operations submit/poll path. Used only to self-heal a
    /// nested <see cref="WriteTextFileAsync"/> whose parent does not exist. <c>mkdir -p</c> is idempotent,
    /// so a directory another writer created concurrently still exits 0. A terminal non-zero exit (e.g. a
    /// read-only or non-directory parent) surfaces as <see cref="SandboxErrorKind.OperationFailed"/>
    /// carrying the exit code + a bounded stderr snippet + the operation id; only a malformed/unrecognized
    /// operation status stays <see cref="SandboxErrorKind.Protocol"/>.
    /// </summary>
    private async Task CreateDirectoryAsync(string sessionId, long mountId, string relativeDirectory, CancellationToken ct)
    {
        var operationId = CommandOperation.ResolveOperationId(null);
        // `--` terminates option parsing so a parent whose first component begins with `-` (e.g. "-m",
        // which WorkspaceRelativePath.Normalize accepts) is treated as an operand, never a mkdir option.
        var requestDto = new CreateOperationRequestDto(
            operationId,
            "mkdir",
            ["-p", "--", relativeDirectory],
            null,
            new OperationCwdDto(mountId, string.Empty),
            GatewayExecutionTimeoutSeconds(),
            null
        );

        var status = await SubmitOperationAsync(sessionId, operationId, requestDto, ct).ConfigureAwait(false);
        if (IsRunning(status.Status))
        {
            status = await PollOperationAsync(sessionId, operationId, ct).ConfigureAwait(false);
        }

        // Happy path: an exit-0 mkdir -p needs no artifact download — proceed straight to the retry PUT.
        if (string.Equals(status.Status, "succeeded", StringComparison.Ordinal) && (status.ExitCode ?? 0) == 0)
        {
            return;
        }

        // Resolve through the shared result path: it classifies a malformed/unknown status as Protocol and
        // a timed_out/output_limit/internal_failure terminal precisely, and otherwise returns the real exit
        // code + downloaded stderr for a genuine non-zero mkdir. Reaching past it means an operational
        // failure (a ran-fine-but-failed mkdir), NOT a malformed response — so it is OperationFailed, not
        // Protocol.
        var result = await ResolveResultAsync(sessionId, operationId, status, ct).ConfigureAwait(false);
        var stderr = result.StandardError.Trim();
        var stderrSuffix = stderr.Length == 0
            ? string.Empty
            : $" stderr: {(stderr.Length > 500 ? stderr[..500] : stderr)}";
        throw new SandboxException(
            SandboxErrorKind.OperationFailed,
            $"Sandbox gateway could not create the parent directory '{relativeDirectory}' before a nested "
                + $"write: mkdir -p exited {result.ExitCode}.{stderrSuffix}",
            operationId: operationId
        );
    }

    /// <summary>
    /// The workspace-relative parent directory of a normalized workspace-relative FILE path, or
    /// <c>null</c> when the file is top-level (has no directory component). The input is already clean
    /// (forward slashes, no leading slash, non-empty), so the parent is simply everything before the last
    /// <c>/</c>.
    /// </summary>
    private static string? GetWorkspaceParentDirectory(string relativeFilePath)
    {
        var lastSlash = relativeFilePath.LastIndexOf('/');
        return lastSlash < 0 ? null : relativeFilePath[..lastSlash];
    }

    /// <summary>
    /// Lists the NON-RECURSIVE entry names of a workspace directory — names only, not full paths,
    /// excluding <c>.</c> and <c>..</c> but INCLUDING dotfiles — via the gateway's direct directories
    /// API (ADR 0031 / issue #119): one or more <c>GET api/v1/sandboxes/{session_id}/directories/{mount_id}?path=...</c>
    /// pages, threading the gateway's opaque <c>next_cursor</c> token verbatim into each subsequent
    /// request until no cursor remains.
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> is a workspace-relative POSIX path (the empty/normalized-empty value lists
    /// the workspace root), validated like <see cref="SandboxCommand.WorkingDirectory"/>. The gateway
    /// remains authoritative for symlink containment.
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The directory does not exist or is not a directory (<see cref="SandboxErrorKind.NotFound"/>); the
    /// directory exceeds the gateway's scan cap (<see cref="SandboxErrorKind.Protocol"/>); a malformed
    /// gateway reply (<see cref="SandboxErrorKind.Protocol"/>); or the transport deadline elapsed
    /// (<see cref="SandboxErrorKind.TransportTimeout"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<IReadOnlyList<string>> ListDirectoryAsync(
      string sessionId,
      string path,
      CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var relativePath = WorkspaceRelativePath.Normalize(path, nameof(path));

        var mountId = await ResolveWorkspaceMountIdAsync(sessionId, ct).ConfigureAwait(false);
        var operation = $"listing directory '{path}'";
        var names = new List<string>();
        string? cursor = null;
        do
        {
            var relativeUri =
              $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/directories/{mountId}?path={Uri.EscapeDataString(relativePath)}";
            if (cursor is not null)
            {
                relativeUri += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var response = await SendDirectAsync(HttpMethod.Get, relativeUri, content: null, sessionId, ct)
              .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await MapDirectErrorAsync(response, operation, sessionId).ConfigureAwait(false);
            }

            ListDirectoryResponseDto page;
            try
            {
                page =
                  await response.Content
                    .ReadFromJsonAsync<ListDirectoryResponseDto>(SandboxJson.RestOptions, ct)
                    .ConfigureAwait(false)
                  ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned an empty response for {operation}."
                  );
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new SandboxException(
                  SandboxErrorKind.Protocol,
                  $"Sandbox gateway returned a malformed directory listing for {operation}.",
                  (int)response.StatusCode,
                  ex
                );
            }

            names.AddRange(
              SelectNonNullOrThrow(
                page.Entries,
                entry =>
                  entry.Name
                  ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a directory entry with no name for {operation}."
                  ),
                operation,
                (int)response.StatusCode
              )
            );

            cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
        } while (cursor is not null);

        return names;
    }

    /// <summary>Normalizes a workspace-relative FILE path, additionally rejecting the empty (workspace-root) form that cannot name a file.</summary>
    private static string NormalizeFilePath(string path)
    {
        var relativePath = WorkspaceRelativePath.Normalize(path, nameof(path));
        if (relativePath.Length == 0)
        {
            throw new ArgumentException("Path must reference a file, not the workspace root.", nameof(path));
        }

        return relativePath;
    }

    /// <summary>
    /// Decodes a file's exact bytes (as returned by the gateway's direct files API) as STRICT UTF-8. If
    /// they are not well-formed UTF-8 this raises <see cref="SandboxErrorKind.Integrity"/> rather than
    /// silently substituting U+FFFD replacement characters.
    /// </summary>
    private static string DecodeFileUtf8OrThrow(byte[] bytes, string description)
    {
        try
        {
            return S_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new SandboxException(
              SandboxErrorKind.Integrity,
              $"Sandbox {description} is not valid UTF-8; file reads expose UTF-8 text only."
            );
        }
    }
}
