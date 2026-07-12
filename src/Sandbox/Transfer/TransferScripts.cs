using System.Globalization;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Transfer;

/// <summary>The role a transfer Bash submission plays; all are stateless request/response steps.</summary>
internal enum TransferScriptKind
{
    /// <summary>Probe a file's existence, exact size, mtime, and whole-file SHA-256.</summary>
    Stat,

    /// <summary>Read one bounded base64 chunk of a file, tagged with the file's current size/mtime for mutation detection.</summary>
    Read,

    /// <summary>Idempotently write one chunk at a byte offset into an exclusive sibling temp file.</summary>
    Write,

    /// <summary>Verify the temp file's size + digest and atomically rename it over the target.</summary>
    Finalize,

    /// <summary>Produce a NUL-delimited listing artifact for a directory (non-recursive, dotfiles included).</summary>
    List,

    /// <summary>Best-effort delete of a transient artifact (a listing artifact or an abandoned write temp).</summary>
    Cleanup,
}

/// <summary>A parsed transfer marker line (plus, for a write, the decoded chunk bytes) — the classification handle for a submission.</summary>
internal readonly record struct TransferScriptRequest(
  TransferScriptKind Kind,
  string? PathKey,
  string? TmpKey,
  string? DstKey,
  string? DirKey,
  string? ArtKey,
  long Offset,
  long Length,
  long Size,
  string? Sha,
  byte[]? ChunkBytes
);

/// <summary>
/// Builds the POSIX <c>sh</c> scripts the SDK submits to the gateway Bash tool for exact, verified file
/// and directory-listing transfers, and parses the marker line that identifies a submission's role.
/// </summary>
/// <remarks>
/// <para>
/// Every script begins with a <c>#LMSBX 1 XFER …</c> marker comment. It is inert to a real shell, but
/// deterministically classifies the submission for a test double. <b>The marker carries only opaque hex
/// keys and numbers — never a raw path.</b> A target path enters a script solely through a POSIX
/// single-quoted assignment (<c>F="$WS/"'&lt;path&gt;'</c>, via <see cref="PosixArgv.Quote(string)"/>),
/// so nothing a caller puts in a path — spaces, <c>$</c>, quotes, globs, newlines — is ever interpreted
/// by the shell or leaks onto the marker/emitted lines.
/// </para>
/// <para>
/// Portable, self-contained primitives only: file hashing reuses <see cref="CommandScripts.Sha256Function"/>
/// (content on stdin, <c>sha256sum</c> then <c>shasum -a 256</c>), mtime tries GNU <c>stat -c %Y</c> then
/// BSD <c>stat -f %m</c>, base64 decode tries <c>base64 -d</c> then <c>base64 -D</c>, and each read chunk's
/// base64 is joined with <c>tr -d '\n'</c> so a single chunk is always one line under the gateway's
/// 500-line / 20&#160;KB <c>exec</c> caps.
/// </para>
/// </remarks>
internal static class TransferScripts
{
    private const string MarkerPrefix = "#LMSBX 1 XFER ";
    private const string WorkspaceAssignment = "WS=\"${SANDBOX_WORKSPACE:-/workspace}\"";
    private const string SentinelAssignment = "SENT='" + TransferSentinel.Marker + "'";

    /// <summary>Portable current-mtime (seconds): GNU <c>stat -c %Y</c>, then BSD <c>stat -f %m</c>, else 0.</summary>
    private const string MtimeExpression = "stat -c %Y \"$F\" 2>/dev/null || stat -f %m \"$F\" 2>/dev/null || echo 0";

    /// <summary>Builds a probe of a file's existence, exact byte size, mtime, and whole-file SHA-256.</summary>
    public static string BuildStat(string relativePath) =>
      string.Join(
        '\n',
        MarkerPrefix + "STAT path=" + TransferPath.Key(relativePath),
        "set -u",
        WorkspaceAssignment,
        "F=" + FileAssignment(relativePath),
        SentinelAssignment,
        CommandScripts.Sha256Function,
        "if [ -f \"$F\" ]; then",
        "  sz=$(wc -c < \"$F\" 2>/dev/null || echo 0)",
        "  mt=$(" + MtimeExpression + ")",
        "  sh=$(lmsbx_sha256 \"$F\")",
        "  printf '%s %s %s %s %s\\n' \"$SENT\" '" + TransferSentinel.KindMeta + "' \"$sz\" \"$mt\" \"$sh\"",
        "else",
        "  printf '%s %s\\n' \"$SENT\" '" + TransferSentinel.KindNotFound + "'",
        "fi"
      );

    /// <summary>
    /// Builds an idempotent base64 read of the byte range <c>[offset, offset+length)</c>, tagged with the
    /// file's CURRENT size and mtime so the SDK can detect a mutation between chunks and never stitch bytes
    /// from two different file states.
    /// </summary>
    public static string BuildRead(string relativePath, long offset, long length) =>
      string.Join(
        '\n',
        MarkerPrefix
          + "READ path="
          + TransferPath.Key(relativePath)
          + " off="
          + offset.ToString(CultureInfo.InvariantCulture)
          + " len="
          + length.ToString(CultureInfo.InvariantCulture),
        "set -u",
        WorkspaceAssignment,
        "F=" + FileAssignment(relativePath),
        SentinelAssignment,
        "sz=$(wc -c < \"$F\" 2>/dev/null || echo 0)",
        "mt=$(" + MtimeExpression + ")",
        "printf '%s %s %s %s ' \"$SENT\" '" + TransferSentinel.KindChunk + "' \"$sz\" \"$mt\"",
        "tail -c +"
          + (offset + 1).ToString(CultureInfo.InvariantCulture)
          + " \"$F\" 2>/dev/null | head -c "
          + length.ToString(CultureInfo.InvariantCulture)
          + " | base64 | tr -d '\\n'",
        "printf '\\n'"
      );

    /// <summary>
    /// Builds an idempotent write of one chunk (<paramref name="chunkBase64"/>) at
    /// <paramref name="offset"/> into the sibling temp <c>&lt;target&gt;.&lt;opId&gt;.tmp</c>. Re-running the
    /// same chunk is safe: <c>offset==0</c> truncates the temp (a clean restart), the chunk is appended only
    /// when the temp is exactly <paramref name="offset"/> bytes, and a temp already at
    /// <c>offset+length</c> bytes (a retried chunk whose prior write landed) is a no-op. The original target
    /// is NEVER touched here — only the temp is written.
    /// </summary>
    public static string BuildWriteChunk(
      string targetRelativePath,
      string opId,
      long offset,
      long length,
      string chunkBase64
    ) =>
      string.Join(
        '\n',
        MarkerPrefix
          + "WRITE tmp="
          + TransferPath.Key(TransferPath.TempRelative(targetRelativePath, opId))
          + " off="
          + offset.ToString(CultureInfo.InvariantCulture)
          + " len="
          + length.ToString(CultureInfo.InvariantCulture),
        "set -u",
        WorkspaceAssignment,
        "F=" + FileAssignment(targetRelativePath),
        "TMP=\"$F\"" + PosixArgv.Quote("." + opId + ".tmp"),
        "DIR=$(dirname \"$F\")",
        SentinelAssignment,
        "B64='" + chunkBase64 + "'",
        "OFF=" + offset.ToString(CultureInfo.InvariantCulture),
        "mkdir -p \"$DIR\" 2>/dev/null",
        "cur=$(wc -c < \"$TMP\" 2>/dev/null || echo 0)",
        "if [ \"$OFF\" -eq 0 ]; then : > \"$TMP\"; cur=0; fi",
        "if [ \"$cur\" -eq \"$OFF\" ]; then",
        "  if printf '%s' \"$B64\" | base64 -d > \"$TMP.d\" 2>/dev/null || printf '%s' \"$B64\" | base64 -D > \"$TMP.d\" 2>/dev/null; then cat \"$TMP.d\" >> \"$TMP\"; fi",
        "  rm -f \"$TMP.d\" 2>/dev/null",
        "  new=$(wc -c < \"$TMP\" 2>/dev/null || echo 0)",
        "  printf '%s %s %s\\n' \"$SENT\" '" + TransferSentinel.KindWrote + "' \"$new\"",
        "elif [ \"$cur\" -eq "
          + (offset + length).ToString(CultureInfo.InvariantCulture)
          + " ]; then",
        "  printf '%s %s %s\\n' \"$SENT\" '" + TransferSentinel.KindWrote + "' \"$cur\"",
        "else",
        "  printf '%s %s %s\\n' \"$SENT\" '" + TransferSentinel.KindMismatch + "' \"$cur\"",
        "fi"
      );

    /// <summary>
    /// Builds the atomic finalize: verify the temp's exact size and SHA-256, then <c>mv</c> it over the
    /// target (an atomic same-directory rename). On any verification failure the temp is discarded and the
    /// target is left untouched. Idempotent: if the temp is already gone but the target already matches the
    /// expected digest (a retried finalize), it reports success.
    /// </summary>
    public static string BuildFinalize(string targetRelativePath, string opId, long size, string sha256) =>
      string.Join(
        '\n',
        MarkerPrefix
          + "FINALIZE tmp="
          + TransferPath.Key(TransferPath.TempRelative(targetRelativePath, opId))
          + " dst="
          + TransferPath.Key(targetRelativePath)
          + " size="
          + size.ToString(CultureInfo.InvariantCulture)
          + " sha="
          + sha256,
        "set -u",
        WorkspaceAssignment,
        "F=" + FileAssignment(targetRelativePath),
        "TMP=\"$F\"" + PosixArgv.Quote("." + opId + ".tmp"),
        SentinelAssignment,
        "EXPSHA='" + sha256 + "'",
        "EXPSIZE=" + size.ToString(CultureInfo.InvariantCulture),
        CommandScripts.Sha256Function,
        "if [ -f \"$TMP\" ]; then",
        "  sz=$(wc -c < \"$TMP\" 2>/dev/null || echo 0)",
        "  sh=$(lmsbx_sha256 \"$TMP\")",
        "  if [ \"$sz\" -eq \"$EXPSIZE\" ] && [ \"$sh\" = \"$EXPSHA\" ]; then",
        "    if mv \"$TMP\" \"$F\" 2>/dev/null; then printf '%s %s\\n' \"$SENT\" '"
          + TransferSentinel.KindFinalized
          + "'; else printf '%s %s\\n' \"$SENT\" '"
          + TransferSentinel.KindIntegrity
          + "'; fi",
        "  else",
        "    rm -f \"$TMP\" 2>/dev/null",
        "    printf '%s %s\\n' \"$SENT\" '" + TransferSentinel.KindIntegrity + "'",
        "  fi",
        "elif [ -f \"$F\" ]; then",
        "  sh=$(lmsbx_sha256 \"$F\")",
        "  if [ \"$sh\" = \"$EXPSHA\" ]; then printf '%s %s\\n' \"$SENT\" '"
          + TransferSentinel.KindFinalized
          + "'; else printf '%s %s\\n' \"$SENT\" '"
          + TransferSentinel.KindIntegrity
          + "'; fi",
        "else",
        "  printf '%s %s\\n' \"$SENT\" '" + TransferSentinel.KindIntegrity + "'",
        "fi"
      );

    /// <summary>
    /// Builds a listing artifact for <paramref name="directoryRelativePath"/>: a NUL-delimited stream of the
    /// directory's entry NAMES (non-recursive, dotfiles included, <c>.</c>/<c>..</c> excluded) written to
    /// <paramref name="artifactRelativePath"/>, which the SDK then reads back through the same verified
    /// bounded reader. A missing (or non-directory) target reports NOTFOUND.
    /// </summary>
    public static string BuildList(string directoryRelativePath, string artifactRelativePath) =>
      string.Join(
        '\n',
        MarkerPrefix
          + "LIST dir="
          + TransferPath.Key(directoryRelativePath)
          + " art="
          + TransferPath.Key(artifactRelativePath),
        "set -u",
        WorkspaceAssignment,
        "D=" + FileAssignment(directoryRelativePath),
        "A=" + FileAssignment(artifactRelativePath),
        SentinelAssignment,
        "if [ -d \"$D\" ]; then",
        "  mkdir -p \"$WS/" + TransferPath.ArtifactRootRelative + "\" 2>/dev/null",
        "  : > \"$A\"",
        "  for e in \"$D\"/* \"$D\"/.*; do",
        "    [ -e \"$e\" ] || [ -L \"$e\" ] || continue",
        "    name=${e##*/}",
        "    case \"$name\" in .|..) continue ;; esac",
        "    printf '%s\\0' \"$name\" >> \"$A\"",
        "  done",
        "  printf '%s %s\\n' \"$SENT\" '" + TransferSentinel.KindOk + "'",
        "else",
        "  printf '%s %s\\n' \"$SENT\" '" + TransferSentinel.KindNotFound + "'",
        "fi"
      );

    /// <summary>Builds a best-effort delete of a transient artifact (a listing artifact or an abandoned write temp).</summary>
    public static string BuildCleanup(string relativePath) =>
      string.Join(
        '\n',
        MarkerPrefix + "CLEANUP path=" + TransferPath.Key(relativePath),
        "set -u",
        WorkspaceAssignment,
        "P=" + FileAssignment(relativePath),
        SentinelAssignment,
        "rm -f \"$P\" 2>/dev/null",
        "printf '%s %s\\n' \"$SENT\" '" + TransferSentinel.KindOk + "'"
      );

    /// <summary>
    /// The workspace-rooted file assignment <c>"$WS/"'&lt;path&gt;'</c>: the workspace prefix stays in double
    /// quotes so <c>$WS</c> expands, while the caller path is POSIX single-quoted so no byte in it is ever
    /// interpreted by the shell.
    /// </summary>
    private static string FileAssignment(string relativePath) => "\"$WS/\"" + PosixArgv.Quote(relativePath);

    /// <summary>
    /// Parses the leading marker line of a submitted transfer script into its role and parameters (and, for
    /// a WRITE, the decoded chunk bytes read from the embedded <c>B64='…'</c> assignment). Throws
    /// <see cref="FormatException"/> for a body with no recognizable marker.
    /// </summary>
    public static TransferScriptRequest ParseRequest(string command)
    {
        var firstLine = command.Split('\n', 2)[0];
        if (!firstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Command body is missing the LMSBX XFER marker line.");
        }

        var tokens = firstLine[MarkerPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = tokens[0] switch
        {
            "STAT" => TransferScriptKind.Stat,
            "READ" => TransferScriptKind.Read,
            "WRITE" => TransferScriptKind.Write,
            "FINALIZE" => TransferScriptKind.Finalize,
            "LIST" => TransferScriptKind.List,
            "CLEANUP" => TransferScriptKind.Cleanup,
            _ => throw new FormatException($"Unknown LMSBX XFER kind '{tokens[0]}'."),
        };

        string? pathKey = null,
          tmpKey = null,
          dstKey = null,
          dirKey = null,
          artKey = null,
          sha = null;
        long offset = 0,
          length = 0,
          size = 0;
        foreach (var token in tokens.Skip(1))
        {
            var separator = token.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = token[..separator];
            var value = token[(separator + 1)..];
            switch (key)
            {
                case "path":
                    pathKey = value;
                    break;
                case "tmp":
                    tmpKey = value;
                    break;
                case "dst":
                    dstKey = value;
                    break;
                case "dir":
                    dirKey = value;
                    break;
                case "art":
                    artKey = value;
                    break;
                case "sha":
                    sha = value;
                    break;
                case "off":
                    offset = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "len":
                    length = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "size":
                    size = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    break;
            }
        }

        var chunkBytes = kind == TransferScriptKind.Write ? ExtractChunkBytes(command) : null;
        return new TransferScriptRequest(kind, pathKey, tmpKey, dstKey, dirKey, artKey, offset, length, size, sha, chunkBytes);
    }

    /// <summary>Reads the decoded chunk bytes from a WRITE script's embedded <c>B64='…'</c> assignment (base64 has no single quote, so the closing quote is unambiguous).</summary>
    private static byte[] ExtractChunkBytes(string command)
    {
        const string prefix = "\nB64='";
        var start = command.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        start += prefix.Length;
        var end = command.IndexOf('\'', start);
        var base64 = end < 0 ? command[start..] : command[start..end];
        return base64.Length == 0 ? [] : Convert.FromBase64String(base64);
    }
}
