namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>How a failed git command should be treated by the ContextReady recovery ladder.</summary>
internal enum GitFailureKind
{
    /// <summary>Network/auth/rate-limit — retry (with backoff); do NOT re-clone the slot.</summary>
    Transient,

    /// <summary>Local repo corruption/contention (stale lock, dirty tree, broken object) — re-clone the slot.</summary>
    Corrupt,

    /// <summary>Unrecognized — treated as transient, but logged for pattern-gap review.</summary>
    Unknown,
}

/// <summary>
/// Classifies a failed git command from its stderr so the ContextReady stage can tell a transient network
/// fault (retry, keep the warm store) from local slot corruption (re-clone the slot). A pure function of
/// its input — like <see cref="ReviewBot.CloneFailureClassifier"/> — so the recovery ladder is unit-testable
/// without a real git. Corrupt markers are checked first because they are more specific than the generic
/// "unable to access" a transient failure emits.
/// </summary>
internal static class GitFailureClassifier
{
    // Stale lock / dirty tree / broken object — the store is contended or damaged and must be re-cloned.
    // Markers are SPECIFIC on purpose: bare fragments like "unable to create" (also a permission/disk error)
    // or "is empty" (also a normal empty remote — "repository is empty") would misclassify unrelated or
    // benign failures as corruption and trigger a destructive reclone. The real corruption cases those were
    // meant to catch are already covered by the anchored markers below (".lock': file exists"/"index.lock" for
    // the stuck-lock incident; "object file" for an empty/broken object). Anything unmatched falls to Unknown
    // (treated as transient + logged), which is the safe default.
    private static readonly string[] CorruptMarkers =
    [
        "index.lock",
        "shallow.lock",
        ".lock': file exists",
        NotARepositoryMarker,
        "object file",
        "loose object",
        "corrupt",
        "would be overwritten",
        "cannot lock ref",
        "bad object",
    ];

    private const string NotARepositoryMarker = "not a git repository";

    // Network / DNS / TLS / rate-limit — likely transient; retry without discarding the warm store.
    private static readonly string[] TransientMarkers =
    [
        "could not resolve host",
        "failed to connect",
        "connection timed out",
        "connection reset",
        "connection refused",
        "operation timed out",
        "temporary failure",
        "returned error: 5",
        "returned error: 429",
        "early eof",
        "rpc failed",
        "ssl",
    ];

    /// <summary>
    /// Classifies a failed git command from its <paramref name="stderr"/>.
    /// <para>
    /// <paramref name="nestedGitDirExists"/> is the deinit carve-out's filesystem oracle (issue #582): when the
    /// message matches <see cref="IsMissingNestedGitDir"/>'s shape, the SAME stderr shape is emitted whether the
    /// registered submodule's <c>.git/modules/&lt;name&gt;</c> is genuinely gone (deinit'd — benign, see
    /// <see cref="IsMissingNestedGitDir"/>) or present but internally corrupt (a NUL-filled <c>HEAD</c> — real
    /// corruption). The three states this can be in are all handled explicitly, not just the two the carve-out
    /// used to know about: <c>true</c> (a caller probed and the gitdir IS there) keeps the "not a git
    /// repository" marker active so the failure still condemns the slot; <c>false</c> (probed, confirmed
    /// absent) and <c>null</c> (not probed — e.g. a caller with no filesystem to check, or the message did not
    /// match the shape at all) both take the pre-#582 benign path. <c>null</c> defaulting to benign rather than
    /// to corrupt preserves every existing caller that does not pass the oracle.
    /// </para>
    /// </summary>
    public static GitFailureKind Classify(string? stderr, bool? nestedGitDirExists = null)
    {
        var text = (stderr ?? string.Empty).ToLowerInvariant();

        // The one "not a git repository" that is NOT corruption is subtracted here rather than by weakening
        // the marker, so every other shape of it still condemns the slot. The subtraction itself is now gated
        // on the oracle: a confirmed-present gitdir (nestedGitDirExists == true) must NOT take the carve-out,
        // because that is exactly the present-but-corrupt shape #582 exists to fix.
        var takeBenignCarveOut = IsMissingNestedGitDir(text) && nestedGitDirExists != true;
        var corruptMarkers = takeBenignCarveOut
            ? CorruptMarkers.Where(static marker => marker != NotARepositoryMarker)
            : CorruptMarkers;
        if (corruptMarkers.Any(text.Contains))
        {
            return GitFailureKind.Corrupt;
        }

        if (TransientMarkers.Any(text.Contains))
        {
            return GitFailureKind.Transient;
        }

        return GitFailureKind.Unknown;
    }

    /// <summary>
    /// Whether the failure SHAPE is a registered submodule whose gitdir might be gone, rather than a damaged
    /// repository. A prior lease that deinit'd a submodule (worktree + <c>.git/modules/&lt;name&gt;</c> removed,
    /// URL retained) makes git report <c>fatal: not a git repository: sub/../.git/modules/sub</c> — byte-identical
    /// to what a PRESENT-but-corrupt gitdir also reports (issue #582: a NUL-filled <c>HEAD</c> inside an otherwise
    /// intact <c>.git/modules/&lt;name&gt;</c> produces this exact text). This predicate cannot tell the two
    /// apart by itself — that is <see cref="Classify"/>'s <c>nestedGitDirExists</c> oracle's job, checked at the
    /// call site against the real filesystem. What this predicate narrows is everything ELSE: a superproject that
    /// really lost its own git dir does not carry <c>.git/modules/</c> in the message at all, so it is excluded
    /// here regardless of what the oracle later says. Both path separators are matched because git reports the
    /// path it was handed, native-separator or not.
    /// </summary>
    private static bool IsMissingNestedGitDir(string text) =>
        text.Contains(NotARepositoryMarker + ": ", StringComparison.Ordinal)
        && (
            text.Contains(".git/modules/", StringComparison.Ordinal)
            || text.Contains(@".git\modules\", StringComparison.Ordinal)
        );

    /// <summary>
    /// Resolves the nested gitdir <see cref="IsMissingNestedGitDir"/> names in <paramref name="stderr"/> into a
    /// single candidate filesystem path a caller can test for existence, so <see cref="Classify"/>'s
    /// <c>nestedGitDirExists</c> oracle can be answered without the classifier itself touching a disk (it stays
    /// a pure function of its input, per the type's own docs — the existence check belongs at the call site).
    /// <para>
    /// Git does NOT collapse the <c>..</c> segments in this message (observed both relative — <c>sub/../.git/
    /// modules/sub</c> — and, when the failing command's own <c>-C</c> target was itself absolute, with that
    /// same dotted suffix appended to the full path), so this collapses them by hand rather than via
    /// <see cref="System.IO.Path"/>, which treats <c>\</c> as a separator on Windows and would corrupt a sandbox
    /// path that only ever uses <c>/</c>. When the extracted text is already anchored (a drive letter or a
    /// leading <c>/</c>) it is resolved on its own; <paramref name="workingDirectory"/> is prepended only when
    /// the extracted text is relative, since a "-C &lt;absolute target&gt;" failure's message already IS the
    /// full path — combining it with the working directory again would double the submodule segment.
    /// </para>
    /// Returns null when <paramref name="stderr"/> does not match <see cref="IsMissingNestedGitDir"/>'s shape.
    /// </summary>
    public static string? ResolveNestedGitDirPath(string workingDirectory, string? stderr)
    {
        var raw = stderr ?? string.Empty;
        var lower = raw.ToLowerInvariant();
        if (!IsMissingNestedGitDir(lower))
        {
            return null;
        }

        var markerIndex = lower.IndexOf(NotARepositoryMarker + ": ", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + NotARepositoryMarker.Length + 2;
        var end = raw.IndexOfAny(['\r', '\n'], start);
        var extracted = (end >= 0 ? raw[start..end] : raw[start..]).Trim();
        if (extracted.Length == 0)
        {
            return null;
        }

        return CollapseDotSegments(workingDirectory, extracted);
    }

    /// <summary>
    /// Joins <paramref name="basePath"/> and <paramref name="relativeOrAbsolute"/> and collapses <c>.</c>/<c>..</c>
    /// segments by hand, ignoring <paramref name="basePath"/> entirely when the second argument is itself
    /// anchored (a drive letter or a leading <c>/</c>) — see <see cref="ResolveNestedGitDirPath"/> for why.
    /// </summary>
    private static string CollapseDotSegments(string basePath, string relativeOrAbsolute)
    {
        var normalizedTail = relativeOrAbsolute.Replace('\\', '/');
        var tailIsAnchored = normalizedTail.StartsWith('/') || (normalizedTail.Length >= 2 && normalizedTail[1] == ':');

        string anchor;
        List<string> segments;
        if (tailIsAnchored)
        {
            // Only the ANCHOR is wanted here — the raw segments SplitAnchor would also return are the tail's
            // own path taken literally, "." / ".." included, and the loop below re-adds every one of them
            // (correctly collapsed this time) right after. Keeping SplitAnchor's segments as the starting point
            // duplicated the tail: once verbatim, once collapsed.
            (anchor, _) = SplitAnchor(normalizedTail);
            segments = [];
        }
        else
        {
            (anchor, segments) = SplitAnchor(basePath.Replace('\\', '/'));
        }

        foreach (var segment in normalizedTail.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        var joined = string.Join('/', segments);
        return anchor.Length == 2 ? $"{anchor}/{joined}" : $"{anchor}{joined}";
    }

    /// <summary>Splits a leading drive letter (<c>C:</c>) or POSIX root (<c>/</c>) from the rest of the path, so
    /// the caller can rebuild it after collapsing segments without re-deriving the anchor.</summary>
    private static (string Anchor, List<string> Segments) SplitAnchor(string normalized)
    {
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            return (normalized[..2], [.. normalized[2..].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        }

        if (normalized.StartsWith('/'))
        {
            return ("/", [.. normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        }

        return (string.Empty, [.. normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
    }
}
