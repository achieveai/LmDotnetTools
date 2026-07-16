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
        using var response = await SendDirectAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/files/{mountId}?path={Uri.EscapeDataString(relativePath)}",
                content: null,
                sessionId,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(response, $"reading file '{path}'", sessionId, ct).ConfigureAwait(false);
        }

        var bytes = await ReadCappedBytesAsync(response, $"reading file '{path}'", operationId: null, ct).ConfigureAwait(false);
        return DecodeFileUtf8OrThrow(bytes, "file");
    }

    /// <summary>
    /// Writes <paramref name="content"/> as UTF-8 to a workspace file via the gateway's direct files API
    /// (ADR 0031 / issue #119): a single <c>PUT api/v1/sandboxes/{session_id}/files/{mount_id}?path=...</c>
    /// carrying the exact new bytes as the request body. The gateway performs the atomic replace itself
    /// (a temp write plus same-directory rename) and creates any missing parent directories, so the SDK
    /// no longer streams chunks, verifies a temp's digest, or issues a separate finalize/rename step.
    /// </summary>
    /// <remarks>
    /// The write is a single request: either the gateway reports the new byte count and the target is
    /// atomically replaced, or the request fails and the target is left untouched (the gateway never
    /// exposes a partially-written file).
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The gateway reported writing a different number of bytes than were sent
    /// (<see cref="SandboxErrorKind.Protocol"/>); the target is held by a concurrent writer
    /// (<see cref="SandboxErrorKind.Conflict"/>); the transport deadline elapsed
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
            throw await MapDirectErrorAsync(response, $"writing file '{path}'", sessionId, ct).ConfigureAwait(false);
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
              $"Sandbox gateway returned a malformed write response for '{path}'.",
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
              $"Sandbox gateway reported an unexpected byte count writing '{path}'."
            );
        }
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
                throw await MapDirectErrorAsync(response, operation, sessionId, ct).ConfigureAwait(false);
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
