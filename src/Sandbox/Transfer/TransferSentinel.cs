namespace AchieveAi.LmDotnetTools.Sandbox.Transfer;

/// <summary>
/// The tiny, single-line protocol a transfer script prints on its standard output and the SDK parses
/// out of the gateway Bash result. Like the command sentinel it keeps the signal on one
/// marker-prefixed line so it survives the gateway's <c>exec</c> truncation (20&#160;KB / 500 lines):
/// the actual file bytes are never printed raw, only a bounded base64 chunk whose newlines are stripped
/// (<c>tr -d '\n'</c>) so a single chunk is always one line under both caps.
/// </summary>
internal static class TransferSentinel
{
    /// <summary>Prefix of every status line a transfer script emits (distinct from the command sentinel).</summary>
    public const string Marker = "@@LMSBX-XFER@@";

    /// <summary>A STAT probe found a regular file: payload is <c>&lt;size&gt; &lt;mtime&gt; &lt;sha256&gt;</c>.</summary>
    public const string KindMeta = "META";

    /// <summary>A STAT/LIST target does not exist (or is not a regular file / directory) — the recoverable "missing" signal.</summary>
    public const string KindNotFound = "NOTFOUND";

    /// <summary>A READ chunk: payload is <c>&lt;size&gt; &lt;mtime&gt; &lt;base64&gt;</c> (current size/mtime for mutation detection, then the chunk).</summary>
    public const string KindChunk = "CHUNK";

    /// <summary>A WRITE chunk was applied: payload is the temp file's new byte length.</summary>
    public const string KindWrote = "WROTE";

    /// <summary>A WRITE chunk could not be applied because the temp file was in an unexpected state: payload is its current length.</summary>
    public const string KindMismatch = "MISMATCH";

    /// <summary>The temp file was verified and atomically renamed over the target (or the target already matched).</summary>
    public const string KindFinalized = "FINALIZED";

    /// <summary>The temp file failed its size/digest verification (or the finalize was ambiguous); the target was left untouched.</summary>
    public const string KindIntegrity = "INTEGRITY";

    /// <summary>A side-effecting maintenance step (list-artifact produced, cleanup) completed.</summary>
    public const string KindOk = "OK";

    /// <summary>
    /// Extracts the single status line from a Bash result body, returning its kind and the remaining
    /// space-separated tokens (empty when the line carries only a kind). Throws
    /// <see cref="SandboxException"/> (<see cref="SandboxErrorKind.Protocol"/>) when no marker line is
    /// present — a transfer script that ran always emits exactly one, so its absence is a protocol
    /// violation. At most <paramref name="maxTokens"/> tokens are split so a base64 payload (which never
    /// contains a space) is returned whole as the last token.
    /// </summary>
    public static (string Kind, string[] Tokens) Parse(string text, int maxTokens = 4)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', maxTokens + 1, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return (string.Empty, []);
            }

            return (parts[1], parts[2..]);
        }

        throw new SandboxException(
          SandboxErrorKind.Protocol,
          "Sandbox transfer script returned no recognizable status line."
        );
    }
}
