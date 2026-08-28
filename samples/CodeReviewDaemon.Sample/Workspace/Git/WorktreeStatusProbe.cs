namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>
/// Reads <c>git status --porcelain -b -z</c> and says what a dirty listing actually MEANS: whether the probe
/// answered at all, what entries it reported, and which of those entries are leftover content as opposed to
/// paths the repository's own filters merely report as modified.
/// <para>
/// Split out of the caller so all three questions are answerable without a git process: <see cref="Reported"/>
/// and <see cref="Parse"/> are pure, and <see cref="ClassifyAsync"/> is the only member that runs anything.
/// </para>
/// </summary>
internal static class WorktreeStatusProbe
{
    /// <summary>
    /// Above this many dirty paths the tree is wrong in bulk, not normalized oddly, so it is reported as
    /// leftover without spending a pair of git invocations per path to say so.
    /// </summary>
    internal const int MaxClassifiedLeftovers = 25;

    /// <summary>
    /// What <c>-b</c> prepends. Two '#' is not a status code — porcelain codes are drawn from
    /// <c> MADRCU?!</c> — so this prefix cannot collide with a real entry, and a path that merely CONTAINS
    /// "## " still arrives behind its own two-character code and is unaffected.
    /// </summary>
    internal const string BranchHeaderPrefix = "## ";

    /// <summary>
    /// One status listing sorted into what the caller must condemn and what it may accept.
    /// <paramref name="Leftovers"/> are entries that are not the checked-out commit's content — untracked,
    /// deleted, staged, renamed, unmerged, genuinely edited, or of an identity no probe could establish.
    /// <paramref name="Normalized"/> are paths git reports as modified whose bytes on disk ARE the blob the
    /// index records, which is a filter disagreeing with what was committed rather than contamination.
    /// </summary>
    internal sealed record StatusClassification(
        IReadOnlyList<string> Leftovers,
        IReadOnlyList<string> Normalized);

    /// <summary>
    /// Whether the probe's output is the answer of a git that actually ran. <c>status --porcelain -b</c>
    /// always emits a branch header, so its ABSENCE means the output was lost rather than that the tree was
    /// clean — the two are otherwise the same empty string, which is the whole reason <c>-b</c> is passed.
    /// This daemon has already measured a git command exiting 0 with its output silently lost (run 200,
    /// <c>git rev-parse HEAD</c> returning nothing, which no real git invocation does), so the empty-string
    /// case is not hypothetical: without the header, that failure is read as a clean tree and reported as a
    /// verified one — a positive claim about a probe whose answer never arrived.
    /// </summary>
    internal static bool Reported(string? stdout) =>
        (stdout ?? string.Empty).StartsWith(BranchHeaderPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Parses <c>status --porcelain -b -z</c> into (two-letter code, path) pairs.
    /// </summary>
    /// <remarks>
    /// The NUL-delimited form is used rather than the newline one because porcelain v1 QUOTES any path with a
    /// space, quote or non-ASCII byte in it, and a path that arrives quoted would never match the index entry
    /// <see cref="ClassifyAsync"/> looks up. Rename and copy records carry a second path field, which is
    /// consumed with the record it belongs to so it is not mistaken for an entry of its own.
    /// <para>
    /// The <c>-b</c> branch header is skipped, and skipping it is load-bearing rather than tidy. It arrives as
    /// its own NUL-terminated field and is long enough to clear the short-field guard, so without this it
    /// would be read as code <c>##</c> at path <c>HEAD (no branch)</c> — a leftover that is not a file, on
    /// every single run, in a checkout the daemon deliberately keeps DETACHED. That turns a probe meant to
    /// confirm cleanliness into one that condemns every slot. Verified against git 2.53.0: a clean detached
    /// checkout answers exactly <c>"## HEAD (no branch)\0"</c>.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(string Code, string Path)> Parse(string? stdout)
    {
        var entries = new List<(string, string)>();
        var fields = (stdout ?? string.Empty).Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            // Only the leading field can be the branch header; git emits it first and once. Bounding the skip
            // to index 0 keeps a real path that happens to start "## " reachable.
            if (i == 0 && fields[i].StartsWith(BranchHeaderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // "XY path" — anything shorter is the empty tail after the final delimiter.
            if (fields[i].Length < 4)
            {
                continue;
            }

            var code = fields[i][..2];
            entries.Add((code, fields[i][3..]));
            if (code[0] is 'R' or 'C')
            {
                i++;
            }
        }

        return entries;
    }

    /// <summary>
    /// Splits <paramref name="entries"/> into paths that are genuinely not the checked-out commit's content
    /// and paths whose worktree bytes already ARE the recorded blob.
    /// </summary>
    /// <remarks>
    /// A repository can commit a blob whose line endings contradict its own <c>.gitattributes</c> — a
    /// <c>text eol=crlf</c> path whose stored blob already holds CRLF is the common shape. Git then runs the
    /// clean filter over the worktree copy on every comparison, converts CRLF to LF, and finds it unequal to
    /// the CRLF blob, so the path reports modified on a checkout nothing has touched. Measured on WeveNova:
    /// one <c>ServiceConfig.ini</c>, all 91 of its 91 lines "changed", surviving <c>checkout --force</c>,
    /// <c>reset --hard</c> and <c>clean -ffdx</c> alike, because no operation that writes the worktree can
    /// produce bytes the clean filter maps back onto that blob. Condemning the slot for it re-clones the store
    /// into exactly the same condition, on every lease, forever.
    /// <para>
    /// The discriminator is the blob identity of the RAW bytes: <c>hash-object --no-filters</c> bypasses the
    /// clean filter, so it answers "what does this file actually contain" rather than "what would git store
    /// for it". Equal to the index blob means the file on disk is byte-for-byte the recorded content, whatever
    /// <c>status</c> says about it. A real edit changes those bytes and so changes that hash — the check
    /// cannot be talked into passing content the commit does not have, which is the whole point of the guard.
    /// Everything else (untracked, deleted, staged, renamed, unmerged) stays a leftover, and a probe that
    /// could not run leaves the path a leftover too: this half fails CLOSED, which is what
    /// <see cref="GitAnswer.Unknown"/> is here to keep visible at the one place that decides it.
    /// </para>
    /// </remarks>
    internal static async Task<StatusClassification> ClassifyAsync(
        GitRunner git,
        string repoRoot,
        IReadOnlyList<(string Code, string Path)> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(entries);

        var leftovers = new List<string>();
        var normalized = new List<string>();
        var worthClassifying = entries.Count <= MaxClassifiedLeftovers;

        foreach (var (code, path) in entries)
        {
            if (worthClassifying
                && string.Equals(code, " M", StringComparison.Ordinal)
                && await HoldsRecordedBytesAsync(git, repoRoot, path, cancellationToken).ConfigureAwait(false)
                    == GitAnswer.Yes)
            {
                normalized.Add(path);
                continue;
            }

            leftovers.Add($"{code} {path}");
        }

        return new StatusClassification(leftovers, normalized);
    }

    /// <summary>
    /// Whether <paramref name="path"/>'s bytes on disk are exactly the blob the index records for it, so the
    /// only thing making it report modified is a filter git applies during comparison —
    /// <see cref="GitAnswer.Unknown"/> when either probe did not answer, because "I could not look" is not a
    /// finding that the bytes differ.
    /// </summary>
    private static async Task<GitAnswer> HoldsRecordedBytesAsync(
        GitRunner git, string repoRoot, string path, CancellationToken cancellationToken)
    {
        var recorded = await git
            .RunAsync(["-C", repoRoot, "rev-parse", $":{path}"], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!recorded.Succeeded)
        {
            return GitAnswer.Unknown;
        }

        var onDisk = await git
            .RunAsync(["-C", repoRoot, "hash-object", "--no-filters", "--", path], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!onDisk.Succeeded)
        {
            return GitAnswer.Unknown;
        }

        var recordedBlob = recorded.Stdout?.Trim() ?? string.Empty;
        if (recordedBlob.Length == 0)
        {
            // An exit-0 lookup that printed nothing is the same lost-output failure `-b` exists to catch one
            // level up; an empty string compares equal to nothing useful, so it is not an answer either.
            return GitAnswer.Unknown;
        }

        return string.Equals(recordedBlob, onDisk.Stdout?.Trim() ?? string.Empty, StringComparison.Ordinal)
            ? GitAnswer.Yes
            : GitAnswer.No;
    }
}
