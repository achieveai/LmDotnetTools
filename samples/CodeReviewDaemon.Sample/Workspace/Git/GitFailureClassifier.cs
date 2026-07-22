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
        "not a git repository",
        "object file",
        "loose object",
        "corrupt",
        "would be overwritten",
        "cannot lock ref",
        "bad object",
    ];

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

    /// <summary>Classifies a failed git command from its <paramref name="stderr"/>.</summary>
    public static GitFailureKind Classify(string? stderr)
    {
        var text = (stderr ?? string.Empty).ToLowerInvariant();

        if (CorruptMarkers.Any(text.Contains))
        {
            return GitFailureKind.Corrupt;
        }

        if (TransientMarkers.Any(text.Contains))
        {
            return GitFailureKind.Transient;
        }

        return GitFailureKind.Unknown;
    }
}
