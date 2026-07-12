using AchieveAi.LmDotnetTools.Sandbox.Command;
using AchieveAi.LmDotnetTools.Sandbox.Transfer;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>Maximum times an idempotent transfer step is retried on a transient transport/protocol failure before surfacing it.</summary>
    private const int TransferRetryLimit = 4;

    /// <summary>
    /// Reads a workspace file as UTF-8 text, returning EXACTLY its bytes decoded — never a mixed, partial,
    /// or replacement-substituted result. The file is probed for its size, mtime, and whole-file SHA-256,
    /// read back in bounded integrity-verified chunks, then re-verified against that digest before a strict
    /// UTF-8 decode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mutation is never stitched.</b> Every chunk is tagged with the file's current size and mtime; if
    /// either changes between chunks — or a chunk comes back short, or the reassembled whole-file digest no
    /// longer matches the probe — the read fails <see cref="SandboxErrorKind.Integrity"/> rather than
    /// returning bytes drawn from two different file states. A same-length, same-mtime adversarial edit is
    /// still caught by the final whole-file digest check.
    /// </para>
    /// <para>
    /// <paramref name="path"/> is a workspace-relative POSIX path, validated exactly like
    /// <see cref="SandboxCommand.WorkingDirectory"/> (rooted, drive/UNC/device-qualified, backslash-bearing,
    /// <c>..</c>-escaping, or NUL-bearing values are rejected). The gateway remains authoritative for symlink
    /// containment.
    /// </para>
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The path does not exist or is not a regular file (<see cref="SandboxErrorKind.NotFound"/>); the bytes
    /// mutated between chunks or are not valid UTF-8 (<see cref="SandboxErrorKind.Integrity"/>); a malformed
    /// gateway reply (<see cref="SandboxErrorKind.Protocol"/>); or the transport deadline elapsed
    /// (<see cref="SandboxErrorKind.TransportTimeout"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<string> ReadTextFileAsync(string sessionId, string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var relativePath = NormalizeFilePath(path);

        var bytes = await ReadVerifiedFileAsync(sessionId, relativePath, ct).ConfigureAwait(false);
        return DecodeTransferUtf8OrThrow(bytes, "file");
    }

    /// <summary>
    /// Writes <paramref name="content"/> as UTF-8 to a workspace file, replacing any existing file
    /// ATOMICALLY: the bytes are streamed in bounded idempotent chunks into an exclusive sibling temp in the
    /// SAME directory, the temp's exact size and SHA-256 are verified, and only then is the temp
    /// <c>mv</c>-renamed over the target (an atomic same-directory rename). The parent directory is created
    /// first (<c>mkdir -p</c>).
    /// </summary>
    /// <remarks>
    /// <b>The original is always preserved on failure.</b> Every chunk writes only the temp; the target is
    /// touched solely by the final rename. If any chunk write or the temp's verification fails, the temp is
    /// discarded and the target is never modified — there is no window in which the target is half-written.
    /// The write is idempotent: re-running a chunk at the same offset, or the finalize, is safe.
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The temp failed its size/digest verification (<see cref="SandboxErrorKind.Integrity"/>); a malformed
    /// gateway reply (<see cref="SandboxErrorKind.Protocol"/>); or the transport deadline elapsed
    /// (<see cref="SandboxErrorKind.TransportTimeout"/>).
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

        var bytes = S_strictUtf8.GetBytes(content);
        var expectedSha = Sha256Hex(bytes);
        var opId = TransferPath.NewOperationId();

        try
        {
            if (bytes.Length == 0)
            {
                await WriteChunkAsync(sessionId, relativePath, opId, 0, 0, string.Empty, ct).ConfigureAwait(false);
            }
            else
            {
                long offset = 0;
                while (offset < bytes.Length)
                {
                    var length = (int)Math.Min(CommandArtifactLayout.ReadChunkBytes, bytes.Length - offset);
                    var chunkBase64 = Convert.ToBase64String(bytes, (int)offset, length);
                    await WriteChunkAsync(sessionId, relativePath, opId, offset, length, chunkBase64, ct)
                      .ConfigureAwait(false);
                    offset += length;
                }
            }

            await FinalizeWriteAsync(sessionId, relativePath, opId, bytes.Length, expectedSha, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: discard the abandoned temp so a failed write leaves no half-written sibling behind.
            // The target itself was never touched (only the final rename does that), so it is already preserved.
            await BestEffortCleanupAsync(sessionId, TransferPath.TempRelative(relativePath, opId), ct)
              .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Lists the NON-RECURSIVE entry names of a workspace directory — names only, not full paths, excluding
    /// <c>.</c> and <c>..</c> but INCLUDING dotfiles. The names are captured into a NUL-delimited listing
    /// artifact in the sandbox and read back through the same integrity-verified bounded reader, so a name
    /// containing spaces or newlines survives exactly (only NUL, which cannot occur in a filename, is the
    /// delimiter).
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> is a workspace-relative POSIX path (the empty/normalized-empty value lists the
    /// workspace root), validated like <see cref="SandboxCommand.WorkingDirectory"/>. The gateway remains
    /// authoritative for symlink containment.
    /// </remarks>
    /// <exception cref="SandboxException">
    /// The directory does not exist or is not a directory (<see cref="SandboxErrorKind.NotFound"/>); the
    /// listing artifact mutated or is not valid UTF-8 (<see cref="SandboxErrorKind.Integrity"/>); a malformed
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
        var directoryRelativePath = WorkspaceRelativePath.Normalize(path, nameof(path));

        var opId = TransferPath.NewOperationId();
        var artifactRelativePath = TransferPath.ListArtifactRelative(opId);

        var (kind, _) = await SubmitTransferAsync(
            sessionId,
            TransferScripts.BuildList(directoryRelativePath, artifactRelativePath),
            "directory listing",
            ct
          )
          .ConfigureAwait(false);

        if (string.Equals(kind, TransferSentinel.KindNotFound, StringComparison.Ordinal))
        {
            throw new SandboxException(
              SandboxErrorKind.NotFound,
              "The sandbox directory does not exist (or is not a directory)."
            );
        }

        if (!string.Equals(kind, TransferSentinel.KindOk, StringComparison.Ordinal))
        {
            throw new SandboxException(SandboxErrorKind.Protocol, "Sandbox directory listing returned an unexpected status.");
        }

        try
        {
            var bytes = await ReadVerifiedFileAsync(sessionId, artifactRelativePath, ct).ConfigureAwait(false);
            var listing = DecodeTransferUtf8OrThrow(bytes, "directory listing");
            return TransferPath.SplitNulListing(listing);
        }
        finally
        {
            await BestEffortCleanupAsync(sessionId, artifactRelativePath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The single internal bounded-transfer reader shared by <see cref="ReadTextFileAsync"/> and
    /// <see cref="ListDirectoryAsync"/>: probe (size/mtime/digest), read integrity-verified chunks resuming
    /// by offset, then verify the reassembled length and whole-file digest. Returns the exact bytes.
    /// </summary>
    private async Task<byte[]> ReadVerifiedFileAsync(string sessionId, string relativePath, CancellationToken ct)
    {
        var meta = await StatAsync(sessionId, relativePath, ct).ConfigureAwait(false);
        if (!meta.Exists)
        {
            throw new SandboxException(
              SandboxErrorKind.NotFound,
              "The sandbox path does not exist (or is not a regular file)."
            );
        }

        using var buffer = new MemoryStream();
        long offset = 0;
        while (offset < meta.Size)
        {
            var length = (int)Math.Min(CommandArtifactLayout.ReadChunkBytes, meta.Size - offset);
            var chunk = await ReadChunkWithRetryAsync(sessionId, relativePath, offset, length, meta, ct)
              .ConfigureAwait(false);
            buffer.Write(chunk, 0, chunk.Length);
            offset += length;
        }

        var bytes = buffer.ToArray();
        if (bytes.Length != meta.Size)
        {
            throw new SandboxException(
              SandboxErrorKind.Integrity,
              $"Sandbox file length changed during transfer (expected {meta.Size}, assembled {bytes.Length})."
            );
        }

        if (!string.Equals(Sha256Hex(bytes), meta.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SandboxException(
              SandboxErrorKind.Integrity,
              "Sandbox file was mutated during transfer (whole-file digest mismatch); no mixed content is returned."
            );
        }

        return bytes;
    }

    /// <summary>Probes a file's existence, exact size, mtime, and whole-file SHA-256.</summary>
    private async Task<TransferFileMeta> StatAsync(string sessionId, string relativePath, CancellationToken ct)
    {
        var (kind, tokens) = await SubmitTransferAsync(
            sessionId,
            TransferScripts.BuildStat(relativePath),
            "file stat",
            ct
          )
          .ConfigureAwait(false);

        if (string.Equals(kind, TransferSentinel.KindNotFound, StringComparison.Ordinal))
        {
            return TransferFileMeta.Missing;
        }

        if (
          !string.Equals(kind, TransferSentinel.KindMeta, StringComparison.Ordinal)
          || tokens.Length < 3
          || !long.TryParse(tokens[0], out var size)
          || !long.TryParse(tokens[1], out var mtime)
          || tokens[2].Length == 0
        )
        {
            throw new SandboxException(SandboxErrorKind.Protocol, "Sandbox file stat returned a malformed reply.");
        }

        return new TransferFileMeta(true, size, mtime, tokens[2]);
    }

    /// <summary>
    /// Reads one verified chunk at <paramref name="offset"/>, retrying an idempotent transient
    /// transport/protocol failure from the same offset within a bound. A mutation signal (the chunk's current
    /// size/mtime no longer matches the probe, or the chunk is short) is NOT retried — it is a deterministic
    /// integrity failure, and re-reading would only ever return more of the mutated content.
    /// </summary>
    private async Task<byte[]> ReadChunkWithRetryAsync(
      string sessionId,
      string relativePath,
      long offset,
      int length,
      TransferFileMeta meta,
      CancellationToken ct
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (kind, tokens) = await SubmitTransferAsync(
                    sessionId,
                    TransferScripts.BuildRead(relativePath, offset, length),
                    "file read",
                    ct
                  )
                  .ConfigureAwait(false);

                if (
                  !string.Equals(kind, TransferSentinel.KindChunk, StringComparison.Ordinal)
                  || tokens.Length < 2
                  || !long.TryParse(tokens[0], out var currentSize)
                  || !long.TryParse(tokens[1], out var currentMtime)
                )
                {
                    throw new SandboxException(SandboxErrorKind.Protocol, "Sandbox file read returned a malformed chunk.");
                }

                if (currentSize != meta.Size || currentMtime != meta.Mtime)
                {
                    throw new SandboxException(
                      SandboxErrorKind.Integrity,
                      "Sandbox file was mutated during transfer (size/mtime changed between chunks); no mixed content is returned."
                    );
                }

                var chunkBase64 = tokens.Length >= 3 ? tokens[2] : string.Empty;
                var chunk = DecodeTransferBase64OrThrow(chunkBase64);
                if (chunk.Length != length)
                {
                    throw new SandboxException(
                      SandboxErrorKind.Integrity,
                      $"Sandbox file chunk at offset {offset} returned {chunk.Length} of {length} expected bytes (the file was truncated or mutated)."
                    );
                }

                return chunk;
            }
            catch (SandboxException ex)
              when (ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Protocol
                && attempt < TransferRetryLimit
              )
            {
                await Task.Delay(S_transferRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Writes one idempotent chunk into the sibling temp, verifying the reported new temp size equals <c>offset + length</c>.</summary>
    private async Task WriteChunkAsync(
      string sessionId,
      string relativePath,
      string opId,
      long offset,
      int length,
      string chunkBase64,
      CancellationToken ct
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (kind, tokens) = await SubmitTransferAsync(
                    sessionId,
                    TransferScripts.BuildWriteChunk(relativePath, opId, offset, length, chunkBase64),
                    "file write",
                    ct
                  )
                  .ConfigureAwait(false);

                if (string.Equals(kind, TransferSentinel.KindWrote, StringComparison.Ordinal))
                {
                    if (tokens.Length < 1 || !long.TryParse(tokens[0], out var newSize) || newSize != offset + length)
                    {
                        throw new SandboxException(
                          SandboxErrorKind.Integrity,
                          "Sandbox file write reported an unexpected temp size; the original target is untouched."
                        );
                    }

                    return;
                }

                if (string.Equals(kind, TransferSentinel.KindMismatch, StringComparison.Ordinal))
                {
                    throw new SandboxException(
                      SandboxErrorKind.Integrity,
                      "Sandbox file write found the temp in an unexpected state; the original target is untouched."
                    );
                }

                throw new SandboxException(SandboxErrorKind.Protocol, "Sandbox file write returned an unexpected status.");
            }
            catch (SandboxException ex)
              when (ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Protocol
                && attempt < TransferRetryLimit
              )
            {
                // The chunk write is idempotent (offset-addressed into a per-call unique temp), so a transient
                // transport/protocol failure is safe to retry from the same offset.
                await Task.Delay(S_transferRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Verifies the temp's size + digest and atomically renames it over the target; an integrity failure preserves the original.</summary>
    private async Task FinalizeWriteAsync(
      string sessionId,
      string relativePath,
      string opId,
      long size,
      string sha256,
      CancellationToken ct
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (kind, _) = await SubmitTransferAsync(
                    sessionId,
                    TransferScripts.BuildFinalize(relativePath, opId, size, sha256),
                    "file finalize",
                    ct
                  )
                  .ConfigureAwait(false);

                if (string.Equals(kind, TransferSentinel.KindFinalized, StringComparison.Ordinal))
                {
                    return;
                }

                if (string.Equals(kind, TransferSentinel.KindIntegrity, StringComparison.Ordinal))
                {
                    throw new SandboxException(
                      SandboxErrorKind.Integrity,
                      "Sandbox file write failed verification; the original target is preserved."
                    );
                }

                throw new SandboxException(SandboxErrorKind.Protocol, "Sandbox file finalize returned an unexpected status.");
            }
            catch (SandboxException ex)
              when (ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Protocol
                && attempt < TransferRetryLimit
              )
            {
                // Finalize is idempotent: a retried rename observes the temp already gone but the target already
                // matching the expected digest, and still reports success.
                await Task.Delay(S_transferRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Best-effort delete of a transient artifact (a listing artifact or an abandoned write temp); never throws.</summary>
    private async Task BestEffortCleanupAsync(string sessionId, string relativePath, CancellationToken ct)
    {
        try
        {
            _ = await SubmitTransferAsync(sessionId, TransferScripts.BuildCleanup(relativePath), "transfer cleanup", ct)
              .ConfigureAwait(false);
        }
        catch (SandboxException)
        {
            // Best-effort maintenance: a stray temp/artifact is harmless.
        }
        catch (OperationCanceledException)
        {
            // Best-effort maintenance: cancellation simply skips cleanup.
        }
    }

    /// <summary>Submits one transfer Bash script and parses its single sentinel status line, mapping a gateway error or missing line to <see cref="SandboxErrorKind.Protocol"/>.</summary>
    private async Task<(string Kind, string[] Tokens)> SubmitTransferAsync(
      string sessionId,
      string script,
      string phase,
      CancellationToken ct
    )
    {
        var arguments = new { command = script, timeout = GatewayExecutionTimeoutSeconds() };
        var result = await SendMcpToolCallAsync(sessionId, BashToolName, arguments, ct).ConfigureAwait(false);
        var (text, isError) = ExtractBashResult(result, phase, operationId: null);
        if (isError)
        {
            throw new SandboxException(SandboxErrorKind.Protocol, $"Sandbox gateway returned an error for {phase}.");
        }

        return TransferSentinel.Parse(text);
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

    private static byte[] DecodeTransferBase64OrThrow(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new SandboxException(SandboxErrorKind.Protocol, "Sandbox transfer chunk was not valid base64.");
        }
    }

    /// <summary>
    /// Decodes verified transfer bytes as STRICT UTF-8. The bytes have already passed their length + digest
    /// check, so they are exactly what the sandbox held; if they are nevertheless not well-formed UTF-8 this
    /// raises <see cref="SandboxErrorKind.Integrity"/> rather than silently substituting U+FFFD replacement
    /// characters.
    /// </summary>
    private static string DecodeTransferUtf8OrThrow(byte[] bytes, string description)
    {
        try
        {
            return S_strictUtf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new SandboxException(
              SandboxErrorKind.Integrity,
              $"Sandbox {description} is not valid UTF-8; transfers expose UTF-8 text only."
            );
        }
    }

    /// <summary>Delay between idempotent transfer-step retries.</summary>
    private static readonly TimeSpan S_transferRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>A probed file's exact identity: existence, size, mtime, and whole-file SHA-256.</summary>
    private readonly record struct TransferFileMeta(bool Exists, long Size, long Mtime, string Sha256)
    {
        public static readonly TransferFileMeta Missing = new(false, 0, 0, string.Empty);
    }
}
