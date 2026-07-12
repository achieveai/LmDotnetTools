using System.Globalization;

namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>The five idempotency-aware roles a command Bash submission can play.</summary>
internal enum CommandScriptKind
{
    /// <summary>The one side-effecting submission: atomically claim, run the command, persist the manifest.</summary>
    Run,

    /// <summary>Idempotent read of an operation's current claim/manifest state.</summary>
    Probe,

    /// <summary>Idempotent read of a byte range of a captured output stream.</summary>
    Read,

    /// <summary>Delete a verified operation's artifacts.</summary>
    Clean,

    /// <summary>Bounded listing of artifact directories for stale cleanup.</summary>
    Gc,
}

/// <summary>A parsed command-script marker line — the deterministic classification handle for a Bash submission.</summary>
internal readonly record struct CommandScriptRequest(
    CommandScriptKind Kind,
    string OperationDirectory,
    string? Digest,
    string? Stream,
    long Offset,
    long Length,
    int Max
);

/// <summary>
/// Builds the POSIX <c>sh</c> scripts the SDK submits to the gateway Bash tool for each phase of a
/// recoverable command execution, and parses the marker line that identifies a script's role.
/// </summary>
/// <remarks>
/// <para>
/// Every script begins with a <c>#LMSBX 1 …</c> marker comment. It is inert to a real shell (a
/// comment), but it deterministically classifies the submission — which is what lets a scripted test
/// double (and a human reader) tell the single side-effecting RUN submission apart from the
/// idempotent PROBE/READ/CLEAN/GC calls without parsing shell semantics.
/// </para>
/// <para>
/// Only RUN mutates state (claim + run + manifest). PROBE/READ/CLEAN/GC are idempotent reads or
/// best-effort maintenance, so re-issuing them during recovery never re-runs the command. Injected
/// values are safe by construction: the operation directory and digest are hex, the stream name and
/// integers are SDK-controlled, and the argv and working directory are POSIX-quoted
/// (<see cref="PosixArgv"/>) before embedding.
/// </para>
/// </remarks>
internal static class CommandScripts
{
    private const string MarkerPrefix = "#LMSBX 1 ";
    private const string RootExpression =
        "\"${SANDBOX_WORKSPACE:-/workspace}/" + CommandArtifactLayout.ArtifactRootRelative + "\"";

    /// <summary>
    /// Builds the single side-effecting wrapper. In order: fast-path an already-persisted manifest;
    /// atomically <c>mkdir</c>-claim and run (electing exactly one submitter); best-effort take over a
    /// claim whose lease has expired (a crashed submitter, so the operation is not blocked until stale
    /// cleanup); otherwise report PENDING so a caller behind a live peer claim polls instead of
    /// re-running. The command's exact stdout/stderr are captured to files and the metadata manifest is
    /// persisted, so nothing depends on the gateway's truncating <c>exec</c> output.
    /// </summary>
    public static string BuildRun(
        string operationDirectory,
        string digest,
        string quotedArgv,
        string normalizedWorkingDirectory,
        long executionTimeoutSeconds
    )
    {
        var runCommandLine =
            (
                normalizedWorkingDirectory.Length == 0
                    ? string.Empty
                    : "  WD=\"$WD/\"" + PosixArgv.Quote(normalizedWorkingDirectory) + "\n"
            )
            + "  if cd \"$WD\" 2>/dev/null; then ( "
            + quotedArgv
            + " ) > \"$OUT\" 2> \"$ERR\"; code=$?; else : > \"$OUT\"; printf 'sandbox: working directory not found\\n' > \"$ERR\"; code=125; fi";

        return string.Join(
            '\n',
            MarkerPrefix + "RUN op=" + operationDirectory + " digest=" + digest,
            "set -u",
            "umask 077",
            "ROOT=" + RootExpression,
            "OP=\"$ROOT/" + operationDirectory + "\"",
            "MAN=\"$OP/manifest.json\"",
            "OUT=\"$OP/stdout\"",
            "ERR=\"$OP/stderr\"",
            "DIGEST='" + digest + "'",
            "THRESH=" + CommandArtifactLayout.InlineThresholdBytes.ToString(CultureInfo.InvariantCulture),
            "EXEC=" + executionTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "GRACE=" + CommandArtifactLayout.LeaseGraceSeconds.ToString(CultureInfo.InvariantCulture),
            "SENT='" + CommandSentinel.Marker + "'",
            "emit_manifest() { b64=$(base64 \"$MAN\" 2>/dev/null | tr -d '\\n'); printf '%s %s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindManifest
                + "' \"$b64\"; }",
            "stream_json() { f=\"$1\"; l=$(wc -c < \"$f\" 2>/dev/null || echo 0); s=$(sha256sum \"$f\" 2>/dev/null | cut -d' ' -f1); if [ \"${l:-0}\" -le \"$THRESH\" ]; then i=\"\\\"$(base64 \"$f\" 2>/dev/null | tr -d '\\n')\\\"\"; else i=null; fi; printf '%s|%s|%s' \"$l\" \"$s\" \"$i\"; }",
            "claim_run() {",
            "  created=$(date +%s)",
            "  lease=$((created + EXEC + GRACE))",
            "  printf '%s' \"$created\" > \"$OP/created\"",
            "  printf '%s' \"$lease\" > \"$OP/lease\"",
            "  printf '%s' \"$DIGEST\" > \"$OP/digest\"",
            "  WD=\"${SANDBOX_WORKSPACE:-/workspace}\"",
            runCommandLine,
            "  so=$(stream_json \"$OUT\"); se=$(stream_json \"$ERR\")",
            "  ol=${so%%|*}; sor=${so#*|}; os=${sor%%|*}; oi=${sor#*|}",
            "  el=${se%%|*}; ser=${se#*|}; es=${ser%%|*}; ei=${ser#*|}",
            "  printf '{\"v\":1,\"digest\":\"%s\",\"exit\":%d,\"stdout\":{\"len\":%d,\"sha256\":\"%s\",\"inline\":%s},\"stderr\":{\"len\":%d,\"sha256\":\"%s\",\"inline\":%s},\"lease\":%d,\"created\":%d}' \"$DIGEST\" \"$code\" \"$ol\" \"$os\" \"$oi\" \"$el\" \"$es\" \"$ei\" \"$lease\" \"$created\" > \"$MAN\"",
            "  emit_manifest",
            "}",
            "mkdir -p \"$ROOT\" 2>/dev/null",
            "if [ -f \"$MAN\" ]; then emit_manifest; exit 0; fi",
            "if mkdir \"$OP\" 2>/dev/null; then claim_run; exit 0; fi",
            "if [ -f \"$MAN\" ]; then emit_manifest; exit 0; fi",
            "STALE=$(cat \"$OP/lease\" 2>/dev/null || echo 0)",
            "if [ \"$(date +%s)\" -gt \"$STALE\" ] && rm -rf \"$OP\" 2>/dev/null && mkdir \"$OP\" 2>/dev/null; then claim_run; exit 0; fi",
            "if [ -f \"$MAN\" ]; then emit_manifest; else printf '%s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindPending
                + "'; fi"
        );
    }

    /// <summary>Builds an idempotent probe of an operation's claim/manifest state.</summary>
    public static string BuildProbe(string operationDirectory) =>
        string.Join(
            '\n',
            MarkerPrefix + "PROBE op=" + operationDirectory,
            "ROOT=" + RootExpression,
            "OP=\"$ROOT/" + operationDirectory + "\"",
            "MAN=\"$OP/manifest.json\"",
            "SENT='" + CommandSentinel.Marker + "'",
            "if [ -f \"$MAN\" ]; then b64=$(base64 \"$MAN\" 2>/dev/null | tr -d '\\n'); printf '%s %s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindManifest
                + "' \"$b64\"; elif [ -d \"$OP\" ]; then printf '%s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindPending
                + "'; else printf '%s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindNone
                + "'; fi"
        );

    /// <summary>Builds an idempotent base64 read of the byte range <c>[offset, offset+length)</c> of a captured stream.</summary>
    public static string BuildRead(string operationDirectory, string stream, long offset, long length) =>
        string.Join(
            '\n',
            MarkerPrefix
                + "READ op="
                + operationDirectory
                + " stream="
                + stream
                + " off="
                + offset.ToString(CultureInfo.InvariantCulture)
                + " len="
                + length.ToString(CultureInfo.InvariantCulture),
            "ROOT=" + RootExpression,
            "F=\"$ROOT/" + operationDirectory + "/" + stream + "\"",
            "tail -c +"
                + (offset + 1).ToString(CultureInfo.InvariantCulture)
                + " \"$F\" 2>/dev/null | head -c "
                + length.ToString(CultureInfo.InvariantCulture)
                + " | base64 | tr -d '\\n'"
        );

    /// <summary>Builds a best-effort delete of a verified operation's artifacts.</summary>
    public static string BuildClean(string operationDirectory) =>
        string.Join(
            '\n',
            MarkerPrefix + "CLEAN op=" + operationDirectory,
            "ROOT=" + RootExpression,
            "rm -rf \"$ROOT/" + operationDirectory + "\" 2>/dev/null",
            "printf '%s %s\\n' '" + CommandSentinel.Marker + "' '" + CommandSentinel.KindNone + "'"
        );

    /// <summary>Builds a bounded listing (at most <paramref name="max"/> entries) of artifact directories for stale cleanup.</summary>
    public static string BuildGc(int max) =>
        string.Join(
            '\n',
            MarkerPrefix + "GC max=" + max.ToString(CultureInfo.InvariantCulture),
            "ROOT=" + RootExpression,
            "GC='" + CommandSentinel.GcMarker + "'",
            "n=0",
            "for d in \"$ROOT\"/*/; do",
            "  [ -d \"$d\" ] || continue",
            "  name=$(basename \"$d\")",
            "  lease=$(cat \"$d/lease\" 2>/dev/null || echo 0)",
            "  created=$(cat \"$d/created\" 2>/dev/null || echo 0)",
            "  printf '%s %s %s %s\\n' \"$GC\" \"$name\" \"$lease\" \"$created\"",
            "  n=$((n+1))",
            "  [ \"$n\" -ge " + max.ToString(CultureInfo.InvariantCulture) + " ] && break",
            "done"
        );

    /// <summary>
    /// Parses the leading marker line of a submitted command into its role and parameters. Throws
    /// <see cref="FormatException"/> for a body with no recognizable marker (a programming error at
    /// call sites that only ever pass scripts this class built, and a clear failure for a test double).
    /// </summary>
    public static CommandScriptRequest ParseRequest(string command)
    {
        var firstLine = command.Split('\n', 2)[0];
        if (!firstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Command body is missing the LMSBX marker line.");
        }

        var tokens = firstLine[MarkerPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = tokens[0] switch
        {
            "RUN" => CommandScriptKind.Run,
            "PROBE" => CommandScriptKind.Probe,
            "READ" => CommandScriptKind.Read,
            "CLEAN" => CommandScriptKind.Clean,
            "GC" => CommandScriptKind.Gc,
            _ => throw new FormatException($"Unknown LMSBX command kind '{tokens[0]}'."),
        };

        string op = string.Empty,
            stream = string.Empty;
        string? digest = null;
        long offset = 0,
            length = 0;
        var max = 0;
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
                case "op":
                    op = value;
                    break;
                case "digest":
                    digest = value;
                    break;
                case "stream":
                    stream = value;
                    break;
                case "off":
                    offset = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "len":
                    length = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "max":
                    max = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    break;
            }
        }

        return new CommandScriptRequest(kind, op, digest, stream.Length == 0 ? null : stream, offset, length, max);
    }
}
