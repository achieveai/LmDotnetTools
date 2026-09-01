namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Which side of the diff a citation's line number is claimed to be on.</summary>
internal enum DiffSide
{
    /// <summary>The post-image (right/added) side — where a GitHub inline comment must anchor.</summary>
    New,

    /// <summary>The pre-image (left/removed) side — the only side a citation against deleted code can use,
    /// since a deleted line has no post-image line number to cite.</summary>
    Old,
}

/// <summary>One citation to verify: a file, a line number, and which side that number is measured on.</summary>
/// <param name="Path">The path the citation names. Matched against the manifest the same way
/// <see cref="DiffManifest.FindFile"/> matches any path — exact first, then suffix-at-a-boundary.</param>
/// <param name="Line">The 1-based line number, on <paramref name="Side"/>.</param>
/// <param name="Side">Defaults to <see cref="DiffSide.New"/> because that is what almost every citation means:
/// a finding written against the code as it now reads. A citation about a line that was removed must set
/// <see cref="DiffSide.Old"/> explicitly — there is no post-image number for a deleted line to default to.</param>
internal sealed record DiffCitation(string Path, int Line, DiffSide Side = DiffSide.New);

/// <summary>What a citation resolved to against a <see cref="DiffManifest"/>.</summary>
internal enum CitationOutcome
{
    /// <summary>The line is an added (right-side) line in some hunk — exactly what GitHub requires for an
    /// inline comment. The strongest possible verification for a citation about new/changed code.</summary>
    VerifiedChanged,

    /// <summary>The line is a deleted (left-side) line in some hunk. Cannot anchor a GitHub inline comment
    /// (there is no post-image line), but confirms the citation's premise — the code it describes really was
    /// removed by this diff — which is what "deleted-line evidence" means throughout this type.</summary>
    VerifiedDeleted,

    /// <summary>The line exists in a hunk but as unchanged context, not as part of what the diff changed. The
    /// citation points at real code the diff happens to carry, but not at anything this diff did.</summary>
    ContextOnly,

    /// <summary>The manifest never covers this path/line at all — not in any hunk, on either side. This is the
    /// only outcome a citation about a file outside the diff (or a line the diff never touched — e.g. a line
    /// from a sibling/ancestor branch not part of THIS pull request's diff) can produce, since resolution is a
    /// pure coverage check against this manifest and never consults git topology.</summary>
    OutOfScope,

    /// <summary>The caller supplied an <c>expectedHeadSha</c> that does not match the manifest's own
    /// <see cref="DiffManifest.HeadSha"/>, so the manifest itself predates whatever the citation is being
    /// checked against now. Returned before any line lookup — a manifest built for a since-superseded head is
    /// not authoritative evidence for anything, changed or deleted, verified against the current head.</summary>
    Expired,
}

/// <summary>
/// The result of resolving one <see cref="DiffCitation"/> against a <see cref="DiffManifest"/>.
/// </summary>
/// <param name="Outcome">See <see cref="CitationOutcome"/>.</param>
/// <param name="HunkId">The hunk the citation resolved inside, when it resolved inside one. Null for
/// <see cref="CitationOutcome.OutOfScope"/> and <see cref="CitationOutcome.Expired"/>.</param>
/// <param name="Evidence">
/// The cited line's own text, when the citation resolved to a line. This is the freshness evidence a caller
/// checking a "deleted code" claim actually wants to read — not just that SOME line at that number was
/// removed, but what it said. Null for <see cref="CitationOutcome.OutOfScope"/> and
/// <see cref="CitationOutcome.Expired"/>, where there is no line to quote.
/// </param>
internal sealed record CitationVerification(CitationOutcome Outcome, string? HunkId, string? Evidence)
{
    /// <summary>True for either verified outcome — the citation's premise (this code changed or this code was
    /// removed) is confirmed by the diff, regardless of whether it can anchor a GitHub inline comment.</summary>
    public bool IsVerified => Outcome is CitationOutcome.VerifiedChanged or CitationOutcome.VerifiedDeleted;

    /// <summary>True only when the citation can anchor a GitHub inline comment as-is — the RIGHT-side rule
    /// <c>daemon-prompts.yaml</c> states. <see cref="CitationOutcome.VerifiedDeleted"/> is real evidence but
    /// is NOT commentable; a caller that needs to post a comment about deleted code must re-anchor it to the
    /// nearest changed line itself, the same fallback the prompt documents for a rejected line.</summary>
    public bool IsCommentable => Outcome == CitationOutcome.VerifiedChanged;
}

/// <summary>
/// Resolves a citation (a finding's <c>path:line</c>) against a <see cref="DiffManifest"/>: does the diff
/// actually carry this line, on the side the citation claims, and is the manifest itself still current for
/// whatever head the citation is being checked against.
/// <para>
/// <b>Deliberately narrow.</b> This is a pure lookup over one manifest's own hunks — it never reads git
/// history, never walks merge-bases, and never asks whether a line exists on some OTHER branch. A citation
/// about a line from a stacked/sibling branch that this PR's diff never touches resolves to
/// <see cref="CitationOutcome.OutOfScope"/> for the same reason a citation about a typo'd path does: the
/// manifest does not cover it. That is the entire "stacked-branch" story this type tells — it does not
/// attempt topology, because <see cref="Workspace.Git.MergeBaseResolver"/> already owns that, and duplicating
/// its reasoning here would give the daemon two answers to "is this branch stacked" that could disagree.
/// </para>
/// </summary>
internal static class DiffCitationVerifier
{
    /// <summary>
    /// Verifies <paramref name="citation"/> against <paramref name="manifest"/>.
    /// </summary>
    /// <param name="manifest">The diff to check the citation against.</param>
    /// <param name="citation">The citation to resolve.</param>
    /// <param name="expectedHeadSha">
    /// When supplied, and it does not match <paramref name="manifest"/>'s <see cref="DiffManifest.HeadSha"/>,
    /// verification short-circuits to <see cref="CitationOutcome.Expired"/> before any line lookup runs. This
    /// is the freshness check for deleted-line evidence: a manifest parsed for round N's diff is not
    /// authoritative for a claim being checked at round N+1's head, even if a line at the same number happens
    /// to still parse as deleted in the stale manifest — the code at that path may have changed again since.
    /// Omit (leave null) to skip the check entirely, e.g. when a caller already knows the manifest is current.
    /// </param>
    public static CitationVerification Verify(
        DiffManifest manifest,
        DiffCitation citation,
        string? expectedHeadSha = null
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(citation);

        if (expectedHeadSha is not null && !string.Equals(manifest.HeadSha, expectedHeadSha, StringComparison.Ordinal))
        {
            return new CitationVerification(CitationOutcome.Expired, null, null);
        }

        var file = manifest.FindFile(citation.Path);
        if (file is null)
        {
            return new CitationVerification(CitationOutcome.OutOfScope, null, null);
        }

        foreach (var hunk in file.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                var candidate = citation.Side == DiffSide.New ? line.NewLineNumber : line.OldLineNumber;
                if (candidate != citation.Line)
                {
                    continue;
                }

                // A line only carries a New-side number when it is Context or Added, and only carries an
                // Old-side number when it is Context or Deleted — so having matched on the requested side
                // already rules out the "wrong kind" case (an Added line can never match on the Old side, a
                // Deleted line never on the New side). The switch below is exhaustive over what remains.
                var outcome = line.Kind switch
                {
                    DiffLineKind.Added => CitationOutcome.VerifiedChanged,
                    DiffLineKind.Deleted => CitationOutcome.VerifiedDeleted,
                    _ => CitationOutcome.ContextOnly,
                };
                return new CitationVerification(outcome, hunk.HunkId, line.Text);
            }
        }

        return new CitationVerification(CitationOutcome.OutOfScope, null, null);
    }
}
