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

    /// <summary>Classifies a failed git command from its <paramref name="stderr"/>.</summary>
    public static GitFailureKind Classify(string? stderr)
    {
        var text = (stderr ?? string.Empty).ToLowerInvariant();

        // The one "not a git repository" that is NOT corruption is subtracted here rather than by weakening
        // the marker, so every other shape of it still condemns the slot.
        var corruptMarkers = IsMissingNestedGitDir(text)
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
    /// Whether the failure is a registered submodule whose gitdir is gone, rather than a damaged repository.
    /// A prior lease that deinit'd a submodule (worktree + <c>.git/modules/&lt;name&gt;</c> removed, URL retained)
    /// makes git report <c>fatal: not a git repository: sub/../.git/modules/sub</c> — which the broad marker
    /// read as corruption, so <see cref="SlotHygiene"/> burned a second force-reset pass and then re-cloned a
    /// store that was never broken. Re-cloning cannot help either: hygiene deliberately leaves a deinit'd
    /// submodule alone for the review's own policy-enforced initializer to re-establish with a permitted fetch.
    /// The nested <c>.git/modules/</c> path in the message is what separates it from a superproject that really
    /// has lost its git dir; both path separators are matched because git reports the path it was handed.
    /// </summary>
    private static bool IsMissingNestedGitDir(string text) =>
        text.Contains(NotARepositoryMarker + ": ", StringComparison.Ordinal)
        && (
            text.Contains(".git/modules/", StringComparison.Ordinal)
            || text.Contains(@".git\modules\", StringComparison.Ordinal)
        );
}
