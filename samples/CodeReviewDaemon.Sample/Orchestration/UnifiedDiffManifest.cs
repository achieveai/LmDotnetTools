using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>One line inside a hunk body, tagged with the line number(s) it holds on each side.</summary>
/// <param name="Kind">Context (unchanged), Added (right side only) or Deleted (left side only).</param>
/// <param name="OldLineNumber">The line's position in the pre-image, or null for an <see cref="DiffLineKind.Added"/> line.</param>
/// <param name="NewLineNumber">The line's position in the post-image, or null for a <see cref="DiffLineKind.Deleted"/> line.</param>
/// <param name="Text">The line's content, marker character stripped. Never trimmed — leading whitespace in the
/// reviewed code is part of what a citation is verifying.</param>
internal sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);

/// <summary>What one <see cref="DiffLine"/> is, in unified-diff terms.</summary>
internal enum DiffLineKind
{
    /// <summary>Present, unchanged, on both sides. Carries both line numbers.</summary>
    Context,

    /// <summary>Present only on the post-image (right) side.</summary>
    Added,

    /// <summary>Present only on the pre-image (left) side; removed by this diff.</summary>
    Deleted,
}

/// <summary>A contiguous run of 1-based line numbers on one side of a diff.</summary>
/// <param name="Start">First line number in the run.</param>
/// <param name="Count">How many lines the run covers. Zero for an empty anchor — a pure-addition hunk's old
/// side (nothing to point at pre-image) or a pure-deletion hunk's new side.</param>
internal sealed record LineRange(int Start, int Count)
{
    /// <summary>Last line number the range covers. Meaningless (and never read) when <see cref="Count"/> is 0.
    /// Computed in <c>long</c> and clamped rather than left to wrap: <see cref="Start"/>/<see cref="Count"/>
    /// are validated at every construction site this parser uses, but a defensive endpoint keeps this type
    /// from silently reporting a wrapped (negative) end if that ever stops being true (F-001).</summary>
    public int End => Count > 0 ? (int)Math.Min((long)Start + Count - 1, int.MaxValue) : Start - 1;

    /// <summary>Whether <paramref name="line"/> falls inside this range. Always false when <see cref="Count"/> is 0.</summary>
    public bool Contains(int line) => Count > 0 && line >= Start && line <= End;
}

/// <summary>One deleted line, carried apart from <see cref="DiffHunk.Lines"/> for the callers whose only
/// question is "what did this hunk remove" — the freshness evidence a citation-verification caller reads
/// when a finding cites removed code (see <see cref="DiffCitationVerifier"/>).</summary>
/// <param name="OldLineNumber">The line's position in the pre-image.</param>
/// <param name="Text">The removed line's content.</param>
internal sealed record DiffDeletedLine(int OldLineNumber, string Text);

/// <summary>
/// A conservative, heuristic signal that a hunk touches security- or invariant-sensitive territory. This is
/// NOT a verdict — see <see cref="DiffRiskClassifier"/> — it is a keyword match over the changed lines and
/// the file path, meant to prioritise review attention, never to gate it. A hunk with <see cref="None"/> can
/// still be security- or invariant-relevant; the classifier only ever asserts a positive it can point at.
/// </summary>
[Flags]
internal enum DiffRiskClassification
{
    None = 0,

    /// <summary>The path or the changed lines match security-adjacent vocabulary (auth, crypto, secrets,
    /// permissions, injection classes). A match here is a prompt to look closer, not a finding.</summary>
    Security = 1 << 0,

    /// <summary>The path or the changed lines match invariant/contract vocabulary (guards, assertions, null
    /// checks, "must not/always" language). Flags both a new invariant AND a removed one — deleting a guard is
    /// exactly as relevant as adding one, and the classifier does not try to tell which happened.</summary>
    Invariant = 1 << 1,
}

/// <summary>How one file's identity changed across the diff, independent of whether its content also did.</summary>
internal enum DiffChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
}

/// <summary>
/// One hunk of one file's diff: a stable identity, its span on each side, and the derived views a citation
/// check or a triage pass actually wants instead of re-deriving them from <see cref="Lines"/> every time.
/// </summary>
/// <param name="HunkId">
/// Stable across re-parses of the same diff text, and across two diffs that happen to touch this file at
/// this exact position — see <see cref="UnifiedDiffParser.BuildHunkId"/>. It is a readable, versioned string
/// (not a hash) so a log line or a test failure shows what it means without a lookup table.
/// </param>
/// <param name="OldRange">The hunk's span in the pre-image. <see cref="LineRange.Count"/> is 0 for a hunk that
/// only adds lines (a brand-new file, or an insertion with no surrounding pre-image span reported).</param>
/// <param name="NewRange">The hunk's span in the post-image. <see cref="LineRange.Count"/> is 0 for a hunk that
/// only deletes lines (a deleted file, or a pure removal).</param>
/// <param name="Lines">Every line the hunk carries, in file order — the source of truth. <see cref="ChangedRightRanges"/>
/// and <see cref="DeletedLines"/> are both derived from this list and kept only as a convenience.</param>
/// <param name="ChangedRightRanges">
/// The post-image (right-side) line numbers a reviewer may actually anchor a comment to — GitHub's REST API
/// rejects an inline comment whose line is not part of the diff's added/changed side (see
/// <c>daemon-prompts.yaml</c>'s "line must be a line that appears in the PR diff on the RIGHT side"). Computed
/// from the added lines' own new-line numbers, so an interleaved deletion between two additions does not
/// split the run (a deletion consumes no new-side number), while an intervening CONTEXT line correctly does
/// (context consumes one, breaking contiguity).
/// </param>
/// <param name="DeletedLines">Every line this hunk removed, pre-image numbered. Not coalesced into ranges —
/// unlike an addition, a deletion is not something a reviewer can anchor a NEW comment to, so what a caller
/// wants here is the line's content (was this a guard clause? a TODO? a whole method?), not a span.</param>
/// <param name="Classification">See <see cref="DiffRiskClassification"/>.</param>
/// <param name="SectionHeading">The hunk header's trailing context (<c>@@ ... @@ void Foo()</c>), when git's
/// heuristic found an enclosing function/class name. Empty when it did not.</param>
internal sealed record DiffHunk(
    string HunkId,
    LineRange OldRange,
    LineRange NewRange,
    IReadOnlyList<DiffLine> Lines,
    IReadOnlyList<LineRange> ChangedRightRanges,
    IReadOnlyList<DiffDeletedLine> DeletedLines,
    DiffRiskClassification Classification,
    string SectionHeading
);

/// <summary>One file's worth of the diff: identity plus every hunk touching it.</summary>
/// <param name="Path">The post-image path (repo-relative, forward slashes). For a deleted file this is the
/// last path it had (the pre-image path, since there is no post-image).</param>
/// <param name="OldPath">The pre-image path, set only when it differs from <see cref="Path"/> (a rename or
/// copy). Null otherwise — including for an ordinary modify, where carrying a same-valued OldPath would
/// invite a caller to branch on "is this a rename" by string comparison instead of <see cref="ChangeKind"/>.</param>
/// <param name="ChangeKind">See <see cref="DiffChangeKind"/>.</param>
/// <param name="IsBinary">True for a <c>Binary files ... differ</c> entry. <see cref="Hunks"/> is always empty
/// in that case — there is no line-level content to parse, and a citation against a binary file's path is
/// always out of scope (see <see cref="DiffCitationVerifier"/>).</param>
/// <param name="Hunks">Empty for a binary file, and for a pure rename/copy with no content change (git omits
/// hunks entirely when the similarity index is 100%).</param>
internal sealed record DiffFileEntry(
    string Path,
    string? OldPath,
    DiffChangeKind ChangeKind,
    bool IsBinary,
    IReadOnlyList<DiffHunk> Hunks
);

/// <summary>
/// The canonical, parsed form of one <c>git diff</c> (or <c>git diff --no-color</c>) unified-diff text: every
/// file it touches, every hunk within each file, and the per-side line accounting a citation check needs.
/// </summary>
/// <param name="BaseSha">The diff's pre-image commit, when the caller has one to attach. Not read by the
/// parser — carried only so <see cref="DiffCitationVerifier"/> can tell a manifest built for one base...head
/// pair from one built for another.</param>
/// <param name="HeadSha">The diff's post-image commit. See <see cref="BaseSha"/>; this is the SHA a citation's
/// freshness is checked against — see <c>DiffCitationVerifier.Verify</c>'s <c>expectedHeadSha</c> parameter.</param>
/// <param name="Files">Every file entry the diff carries, in the order the diff text presented them.</param>
internal sealed record DiffManifest(string? BaseSha, string? HeadSha, IReadOnlyList<DiffFileEntry> Files)
{
    /// <summary>
    /// Finds the file entry <paramref name="path"/> refers to, or null when this diff never touched it, or
    /// when more than one file entry could plausibly be the one <paramref name="path"/> means.
    /// <para>
    /// Three tiers, each tried only if the previous one found nothing: an exact, case-sensitive path match
    /// (post-image path, then pre-image path); failing that, a case-insensitive exact match, but only when
    /// exactly one file qualifies — a case-sensitive repository can legitimately hold both <c>Foo.cs</c> and
    /// <c>foo.cs</c>, and picking whichever one iteration happens to reach first would silently pull evidence
    /// from the wrong file; failing that, the same path-suffix-at-a-segment-boundary fallback
    /// <see cref="ReviewFindingReconciler"/> uses — a finding and this diff can each be written relative to a
    /// different root — again only when exactly one file qualifies.
    /// </para>
    /// <para>
    /// A match is authoritative only when it is unique. Two files that could each plausibly be the citation's
    /// target is not a coin a citation check gets to flip — it is treated the same as no match at all.
    /// </para>
    /// </summary>
    public DiffFileEntry? FindFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizePath(path);

        var exactMatches = Files.Where(f => IsExactPathMatch(f, normalized, StringComparison.Ordinal)).ToList();
        if (exactMatches.Count > 0)
        {
            return exactMatches.Count == 1 ? exactMatches[0] : null;
        }

        var caseInsensitiveMatches = Files
            .Where(f => IsExactPathMatch(f, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (caseInsensitiveMatches.Count > 0)
        {
            return caseInsensitiveMatches.Count == 1 ? caseInsensitiveMatches[0] : null;
        }

        var suffixMatches = Files.Where(f => IsSuffixPathMatch(f, normalized)).ToList();
        return suffixMatches.Count == 1 ? suffixMatches[0] : null;
    }

    private static bool IsExactPathMatch(DiffFileEntry file, string normalized, StringComparison comparison) =>
        string.Equals(file.Path, normalized, comparison)
        || (file.OldPath is not null && string.Equals(file.OldPath, normalized, comparison));

    private static bool IsSuffixPathMatch(DiffFileEntry file, string normalized) =>
        IsPathSuffix(file.Path, normalized)
        || IsPathSuffix(normalized, file.Path)
        || (
            file.OldPath is not null
            && (IsPathSuffix(file.OldPath, normalized) || IsPathSuffix(normalized, file.OldPath))
        );

    private static string NormalizePath(string raw)
    {
        var path = raw.Replace('\\', '/').Trim();
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.TrimStart('/');
    }

    private static bool IsPathSuffix(string longer, string shorter) =>
        longer.Length > shorter.Length
        && longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase)
        && longer[longer.Length - shorter.Length - 1] == '/';
}

/// <summary>
/// Parses a raw unified-diff text (as <c>git diff</c> emits it) into a <see cref="DiffManifest"/>. Never
/// throws: a line this parser does not recognise is skipped rather than failing the whole diff, on the same
/// reasoning <see cref="ReviewFindingReconciler.ParseFindings"/> uses — a partially-parsed manifest that is
/// missing one hunk is still useful; an exception is not.
/// <para>
/// <b>What it does not attempt.</b> Word-level (character) diffs, context-line count other than what the
/// hunk header states, and combined (merge) diff format (<c>diff --cc</c>) are all out of scope — none of
/// them appear in a GitHub/ADO pull-request diff, which is the only input this parser is built to read.
/// </para>
/// </summary>
internal static partial class UnifiedDiffParser
{
    /// <summary>
    /// Parses <paramref name="diffText"/> into a manifest. <paramref name="baseSha"/>/<paramref name="headSha"/>
    /// are carried through unread — see <see cref="DiffManifest.BaseSha"/> — so a caller that already knows
    /// them (the run's persisted <c>ContextArtifactPayload</c>) does not have to re-derive them from the text.
    /// </summary>
    public static DiffManifest Parse(string? diffText, string? baseSha = null, string? headSha = null)
    {
        if (string.IsNullOrEmpty(diffText))
        {
            return new DiffManifest(baseSha, headSha, []);
        }

        var files = new List<DiffFileEntry>();
        var lines = diffText.Split('\n');

        FileBuilder? file = null;
        HunkBuilder? hunk = null;

        // Set when a hunk header line was recognised (syntactically shaped, `HunkHeader()` matched) but its
        // coordinates were numerically invalid and the header itself was rejected — see the header-match
        // block below (F-004, widened by F-010). While this is true, `hunk` is null for a different reason
        // than "no hunk has started yet": the rejected header's own body is still coming, and NONE of the
        // file-metadata handlers below (the `--- `/`+++ ` pair, `Binary files `, `rename from `/`rename to `,
        // `copy from `/`copy to `, `new file mode`/`deleted file mode`) may act on it — every one of them is
        // a plausible shape for quarantined body content to coincidentally match, and any of them mutating
        // `file` would forge identity or metadata for whatever this manifest attributes next (F-010). The one
        // deliberate exception is `diff --git `, checked unconditionally at the very top of the loop: that is
        // the genuine file boundary, the only line this format gives the parser to end quarantine and start a
        // new file on, so it must always be honoured even mid-quarantine — a real PR diff routinely follows a
        // malformed trailing hunk with the very next file's ordinary header. Cleared on that file boundary or
        // the next hunk header line, valid or not — at that point either a new hunk has legitimately started,
        // or a new rejected header has already replaced whatever this one flagged.
        var hunkHeaderRejected = false;

        void FlushHunk()
        {
            if (file is null || hunk is null)
            {
                return;
            }

            var built = hunk.Build(file.Path);
            if (built is not null)
            {
                file.Hunks.Add(built);
            }

            hunk = null;
        }

        void FlushFile()
        {
            FlushHunk();
            if (file is not null)
            {
                files.Add(file.Build());
            }

            file = null;
        }

        foreach (var rawLine in lines)
        {
            // A trailing '\r' is a CRLF-checked-out diff, not part of the line's meaning to this parser: every
            // marker this loop matches is pure ASCII at the start of the line, and stripping it from a content
            // line changes nothing a citation check reads (the line's TEXT for citation purposes is compared
            // by line number, not rendered back to the author).
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                file = new FileBuilder();
                hunkHeaderRejected = false;

                // Fallback path source for the one case nothing else provides one: a binary file, or a pure
                // 100%-similarity rename/copy, both of which git renders with no "--- "/"+++ " pair at all.
                // Whenever a "--- "/"+++ "/"rename to"/"copy to" line DOES follow, it overwrites this — those
                // are unambiguous about which side changed, while this line's "a/X b/Y" split is only a
                // best-effort guess when a path itself could contain " b/" (real repo paths essentially never
                // do). Both sides start equal here on purpose: absent any override, that reads as "not a
                // rename", which is the correct default for an ordinary modified/binary file.
                var diffGitMatch = DiffGitHeader().Match(line);
                if (diffGitMatch.Success)
                {
                    file.Path = diffGitMatch.Groups["new"].Value;
                    file.OldPath = diffGitMatch.Groups["old"].Value;
                }

                continue;
            }

            if (file is null)
            {
                // Preamble before the first "diff --git" (e.g. a covering message some tools prepend). Not a
                // parse failure — there is simply nothing to attach this line to yet.
                continue;
            }

            // Every metadata handler from here down to the "--- "/"+++ " pair is gated on
            // `!hunkHeaderRejected` for the same reason (F-010): none of these prefixes ("Binary files ",
            // "rename from "/"rename to ", "copy from "/"copy to ", "new file mode"/"deleted file mode")
            // start with a character a genuine hunk-body marker (' ', '+', '-', '\\') can produce, so they
            // cannot arise from the marker-prepend collision the "--- "/"+++ " comment above describes — but
            // a rejected header's quarantined body is raw, unvalidated text, and nothing stops it from
            // containing one of these lines verbatim. Left ungated, any of them would still mutate `file`
            // (identity, change kind, or binary flag) from inside what is supposed to be inert, discarded
            // content, forging provenance for a citation check to trust. `diff --git ` is deliberately the
            // one exception (see its own comment above): it is the genuine file boundary this quarantine
            // must still end at, not one more thing to quarantine against.
            if (!hunkHeaderRejected && line.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                FlushHunk();
                file.IsBinary = true;
                continue;
            }

            if (!hunkHeaderRejected && line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                file.OldPath = line["rename from ".Length..].Trim();
                file.ChangeKind = DiffChangeKind.Renamed;
                continue;
            }

            if (!hunkHeaderRejected && line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                file.Path = line["rename to ".Length..].Trim();
                continue;
            }

            if (!hunkHeaderRejected && line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                file.OldPath = line["copy from ".Length..].Trim();
                file.ChangeKind = DiffChangeKind.Copied;
                continue;
            }

            if (!hunkHeaderRejected && line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                file.Path = line["copy to ".Length..].Trim();
                continue;
            }

            if (!hunkHeaderRejected && line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                file.ChangeKind = DiffChangeKind.Added;
                continue;
            }

            if (!hunkHeaderRejected && line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                file.ChangeKind = DiffChangeKind.Deleted;
                continue;
            }

            // Gated on `hunk is null`: once a hunk is active, a body line whose CONTENT happens to start
            // with "-- " or "++ " renders as "--- "/"+++ " once its own diff marker is prepended (e.g. a
            // deleted line reading "-- config" becomes "--- config"). Recognising these as file headers
            // unconditionally would drop that line from the hunk and could stomp the file's path (F-002) —
            // so file-header recognition is restricted to the pre-hunk state, and once a hunk has started
            // these markers fall through to the hunk-body switch below like any other line.
            //
            // ALSO gated on `!hunkHeaderRejected`: a rejected hunk header's body is exactly as capable of
            // carrying marker-like content, but there is no active `HunkBuilder` for it to fall through into
            // — `hunk` is null for that quarantined body too. Without this second gate, a body line here
            // would still read as a genuine "--- "/"+++ " file-header pair and mutate `file.Path`/`OldPath`,
            // letting a syntactically-shaped-but-numerically-rejected header forge citation attribution
            // for whatever file the parser reads next (F-004). Once flagged, this quarantined body is
            // skipped like any other unrecognised pre-hunk line, all the way to the next file boundary or
            // the next hunk header (which clears the flag either way).
            if (hunk is null && !hunkHeaderRejected && line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = ParseDiffPath(line, "--- ");
                if (path is null)
                {
                    file.OldIsDevNull = true;
                }
                else
                {
                    file.OldPath = path;
                }

                continue;
            }

            if (hunk is null && !hunkHeaderRejected && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = ParseDiffPath(line, "+++ ");
                if (path is null)
                {
                    file.NewIsDevNull = true;
                }
                else
                {
                    file.Path = path;
                }

                continue;
            }

            var headerMatch = HunkHeader().Match(line);
            if (headerMatch.Success)
            {
                FlushHunk();

                // A hunk header's coordinates are matched by \d+ — unbounded digit runs, not bounded ints —
                // so a malformed or adversarial diff can present a number `int.Parse` throws on (overflow),
                // a non-1-based start, or a declared/consumed mismatch long before it could ever describe a
                // real file. Reject such a header the same way any other line this parser does not
                // recognise is rejected: skip it, do not throw (F-001). `hunk` stays null, and
                // `hunkHeaderRejected` is set so this header's own body — which can itself contain
                // marker-like content — is quarantined rather than being read as a new file header or
                // attributed to whatever hunk preceded it (F-004).
                if (
                    TryParseHunkRange(
                        headerMatch.Groups["oldStart"].Value,
                        headerMatch.Groups["oldCount"],
                        out var oldRange
                    )
                    && TryParseHunkRange(
                        headerMatch.Groups["newStart"].Value,
                        headerMatch.Groups["newCount"],
                        out var newRange
                    )
                )
                {
                    hunk = new HunkBuilder(oldRange, newRange, headerMatch.Groups["heading"].Value.Trim());
                    hunkHeaderRejected = false;
                }
                else
                {
                    hunkHeaderRejected = true;
                }

                continue;
            }

            if (hunk is null)
            {
                // Something between the file header and the first hunk that this parser does not recognise
                // (e.g. "index abcd..ef01 100644", "similarity index NN%", "old mode"/"new mode", or a mode
                // line for a mode-only change with no content diff at all). Nothing is lost: none of these
                // carry line-level information a citation check would need.
                continue;
            }

            if (line.Length == 0)
            {
                // A line with no marker at all. Real diff content lines always start with ' ', '+', '-' or
                // '\\' — this is a defensive no-op rather than a guess at which side an unmarked blank belongs
                // to, on the same "never invent" principle as ReviewFindingReconciler's disposition quoting.
                continue;
            }

            var marker = line[0];
            var text = line.Length > 1 ? line[1..] : string.Empty;
            switch (marker)
            {
                case ' ':
                    hunk.Body.Add(new DiffLine(DiffLineKind.Context, hunk.OldCursor, hunk.NewCursor, text));
                    hunk.OldCursor++;
                    hunk.NewCursor++;
                    break;
                case '-':
                    hunk.Body.Add(new DiffLine(DiffLineKind.Deleted, hunk.OldCursor, null, text));
                    hunk.OldCursor++;
                    break;
                case '+':
                    hunk.Body.Add(new DiffLine(DiffLineKind.Added, null, hunk.NewCursor, text));
                    hunk.NewCursor++;
                    break;
                case '\\':
                    // "\ No newline at end of file" — a note about the line before it, not a line of its own.
                    break;
                default:
                    // Unrecognised marker (e.g. a diff produced by an unusual tool). Skipped, not thrown —
                    // see the type's own doc comment.
                    break;
            }
        }

        FlushFile();
        return new DiffManifest(baseSha, headSha, files);
    }

    /// <summary>
    /// The hunk's stable identity: a readable, versioned string rather than a hash, so it prints usefully in a
    /// log or a failed assertion. It is stable under two things that matter — re-parsing the same diff text
    /// twice, and two diffs that place a hunk at the same position in the same file — and unstable under a
    /// third that is expected to change it: any edit that shifts the hunk's own start or size gets a new id,
    /// because that IS a different hunk as far as a citation is concerned.
    /// </summary>
    internal static string BuildHunkId(string path, LineRange oldRange, LineRange newRange) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"v1:{path}:{oldRange.Start},{oldRange.Count}->{newRange.Start},{newRange.Count}"
        );

    /// <summary>
    /// Parses one side's start/count pair off a hunk header into a <see cref="LineRange"/>, bounded and
    /// never-throwing (F-001). <paramref name="startText"/> and <paramref name="countGroup"/> both come off
    /// <c>\d+</c> regex captures — an unbounded digit run, not a bounded integer — so both the parse itself
    /// and the range it would describe are validated before a <see cref="LineRange"/> is built: a digit run
    /// too long for <see cref="int"/> fails <see cref="int.TryParse(string,NumberStyles,IFormatProvider,out int)"/>
    /// rather than throwing, and a start/count pair whose <see cref="LineRange.End"/> would overflow
    /// <see cref="int"/> is rejected outright rather than silently wrapping. Unified-diff line numbers are
    /// 1-based, so a non-empty range (<c>count &gt; 0</c>) that starts at line 0 is also rejected here — that
    /// combination cannot describe a real pre-/post-image span and would otherwise resolve as line-zero
    /// citation evidence. The one legitimate zero-start case, a count-0 anchor (e.g. <c>-0,0</c> on a brand
    /// new file's old side), is preserved: the check below only fires when <c>count &gt; 0</c> (F-001 residual).
    /// </summary>
    private static bool TryParseHunkRange(string startText, Group countGroup, out LineRange range)
    {
        range = new LineRange(0, 0);

        if (!TryParseBoundedInt(startText, out var start) || start < 0)
        {
            return false;
        }

        var count = 1;
        if (countGroup.Success && (!TryParseBoundedInt(countGroup.Value, out count) || count < 0))
        {
            return false;
        }

        if (count > 0 && start == 0)
        {
            return false;
        }

        // Bounded-endpoint check, computed in `long` so the check itself cannot be the thing that overflows.
        if (count > 0 && (long)start + count - 1 > int.MaxValue)
        {
            return false;
        }

        range = new LineRange(start, count);
        return true;
    }

    private static bool TryParseBoundedInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Reads the path off a <c>--- </c>/<c>+++ </c> line, stripping the <c>a/</c>/<c>b/</c> prefix git adds by
    /// default. Returns null for <c>/dev/null</c> (the "this side does not exist" marker for an added or
    /// deleted file) — the one case a caller must not read as a literal path.
    /// </summary>
    private static string? ParseDiffPath(string line, string marker)
    {
        var value = line[marker.Length..];
        var tab = value.IndexOf('\t');
        if (tab >= 0)
        {
            value = value[..tab];
        }

        value = value.TrimEnd();
        if (value == "/dev/null")
        {
            return null;
        }

        if (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }

    [GeneratedRegex(
        @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@[ \t]?(?<heading>.*)$"
    )]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"^diff --git a/(?<old>.+) b/(?<new>.+)$")]
    private static partial Regex DiffGitHeader();

    /// <summary>Mutable accumulator for one file entry while the parse loop is inside it.</summary>
    private sealed class FileBuilder
    {
        public string Path { get; set; } = string.Empty;
        public string? OldPath { get; set; }
        public DiffChangeKind ChangeKind { get; set; } = DiffChangeKind.Modified;
        public bool IsBinary { get; set; }
        public bool OldIsDevNull { get; set; }
        public bool NewIsDevNull { get; set; }
        public List<DiffHunk> Hunks { get; } = [];

        public DiffFileEntry Build()
        {
            // /dev/null on either side is definitive and overrides whatever "new file mode"/"deleted file
            // mode" already set — those header lines are always present too, but the marker on --- / +++ is
            // the one that is unambiguous about WHICH side vanished, which matters for Path below.
            var changeKind = ChangeKind;
            string? oldPath = OldIsDevNull ? null : OldPath;
            var path = Path;

            if (NewIsDevNull)
            {
                changeKind = DiffChangeKind.Deleted;
                path = oldPath ?? path;
                oldPath = null;
            }
            else if (OldIsDevNull)
            {
                changeKind = DiffChangeKind.Added;
                oldPath = null;
            }
            else if (oldPath is not null && !string.Equals(oldPath, path, StringComparison.Ordinal))
            {
                if (changeKind is not (DiffChangeKind.Renamed or DiffChangeKind.Copied))
                {
                    changeKind = DiffChangeKind.Renamed;
                }
            }
            else
            {
                // Same path on both sides: never a rename/copy regardless of what a "rename from"/"rename
                // to" pair (rare, but git can emit an identity rename after -M with a low similarity floor)
                // claimed — the paths agreeing is the ground truth here.
                oldPath = null;
                if (changeKind is DiffChangeKind.Renamed or DiffChangeKind.Copied)
                {
                    changeKind = DiffChangeKind.Modified;
                }
            }

            return new DiffFileEntry(path, oldPath, changeKind, IsBinary, Hunks);
        }
    }

    /// <summary>Mutable accumulator for one hunk while the parse loop is inside it.</summary>
    private sealed class HunkBuilder(LineRange oldRange, LineRange newRange, string heading)
    {
        public int OldCursor { get; set; } = oldRange.Start;
        public int NewCursor { get; set; } = newRange.Start;
        public List<DiffLine> Body { get; } = [];

        /// <summary>
        /// Builds the hunk, or returns <c>null</c> if the header's declared old/new counts do not reconcile
        /// with what the body actually consumed. F-005: a hunk header lies about how many old-side
        /// (Context+Deleted) and new-side (Context+Added) rows follow it; the parse loop has no lookahead and
        /// simply keeps consuming body lines until the next boundary, so an under-counted, over-counted, or
        /// one-sided ("side-mismatch") header would otherwise still produce a hunk whose <see cref="DiffHunk.Lines"/>
        /// carry line numbers and content the header never actually promised — exactly the kind of fabricated
        /// evidence <see cref="DiffCitationVerifier"/> must not be able to verify a citation against. Dropping
        /// the whole hunk (never adding it to <c>file.Hunks</c>) keeps this consistent with the parser's
        /// existing "never fabricate, skip what's malformed" philosophy. A well-formed hunk — including a
        /// legitimate zero-count side for a pure add or pure delete — always reconciles exactly, so this never
        /// rejects valid input.
        /// </summary>
        public DiffHunk? Build(string path)
        {
            var oldConsumed = 0;
            var newConsumed = 0;
            foreach (var bodyLine in Body)
            {
                if (bodyLine.Kind == DiffLineKind.Context)
                {
                    oldConsumed++;
                    newConsumed++;
                }
                else if (bodyLine.Kind == DiffLineKind.Deleted)
                {
                    oldConsumed++;
                }
                else if (bodyLine.Kind == DiffLineKind.Added)
                {
                    newConsumed++;
                }
            }

            if (oldConsumed != oldRange.Count || newConsumed != newRange.Count)
            {
                return null;
            }

            var addedRuns = new List<LineRange>();
            var deleted = new List<DiffDeletedLine>();
            var runStart = -1;
            var runLength = 0;

            void CloseRun()
            {
                if (runLength > 0)
                {
                    addedRuns.Add(new LineRange(runStart, runLength));
                }

                runLength = 0;
            }

            foreach (var line in Body)
            {
                if (line.Kind == DiffLineKind.Added)
                {
                    var num = line.NewLineNumber!.Value;
                    if (runLength > 0 && num == runStart + runLength)
                    {
                        runLength++;
                    }
                    else
                    {
                        CloseRun();
                        runStart = num;
                        runLength = 1;
                    }
                }
                else if (line.Kind == DiffLineKind.Deleted)
                {
                    // A deleted line consumes no new-side line number, so it cannot break contiguity of an
                    // in-progress added run — the run stays open across it. Only a Context line (which DOES
                    // consume a new-side number, see the class doc comment) or the end of the hunk closes it.
                    deleted.Add(new DiffDeletedLine(line.OldLineNumber!.Value, line.Text));
                }
                else
                {
                    CloseRun();
                }
            }

            CloseRun();

            var changedTexts = Body.Where(l => l.Kind != DiffLineKind.Context).Select(l => l.Text);
            var classification = DiffRiskClassifier.Classify(path, changedTexts);

            return new DiffHunk(
                BuildHunkId(path, oldRange, newRange),
                oldRange,
                newRange,
                Body,
                addedRuns,
                deleted,
                classification,
                heading
            );
        }
    }
}

/// <summary>
/// A conservative keyword classifier over a hunk's changed lines and file path. Not a security scanner and
/// not a static analyser — a signal cheap enough to compute for every hunk, meant to help a reviewer (or a
/// downstream triage pass) notice where to look harder, in the same spirit as <c>ReviewFindingReconciler</c>'s
/// severity-word matching: heuristic, stated as such, and never silently upgraded to a verdict.
/// </summary>
internal static partial class DiffRiskClassifier
{
    /// <summary>
    /// Classifies one hunk from its file path and its changed (non-context) lines. Deletions are scanned on
    /// equal footing with additions — removing a permission check or an assertion is exactly as relevant as
    /// adding one, and this classifier does not attempt to tell which happened, only that the vocabulary is
    /// present.
    /// <para>
    /// The path and the line text are matched by two different regexes on purpose. A path segment (e.g.
    /// <c>Security</c>, <c>Auth</c>, <c>Crypto</c>) is already delimited by <c>/</c> — it does not need a
    /// dictionary word's precision to be a meaningful signal, and a strict word-boundary match would miss most
    /// real path segments (a path saying "Security" contains no whole English word from the code-line list
    /// below). A changed LINE, in contrast, is prose or code where a bare substring match (e.g. "auth" inside
    /// "author") would false-positive constantly, so that side stays a strict, wordish match.
    /// </para>
    /// </summary>
    internal static DiffRiskClassification Classify(string path, IEnumerable<string> changedLineTexts)
    {
        ArgumentNullException.ThrowIfNull(changedLineTexts);

        var flags = DiffRiskClassification.None;
        if (!string.IsNullOrEmpty(path))
        {
            if (SecurityPathHint().IsMatch(path))
            {
                flags |= DiffRiskClassification.Security;
            }

            if (InvariantPathHint().IsMatch(path))
            {
                flags |= DiffRiskClassification.Invariant;
            }
        }

        foreach (var text in changedLineTexts)
        {
            if (flags.HasFlag(DiffRiskClassification.Security) && flags.HasFlag(DiffRiskClassification.Invariant))
            {
                break;
            }

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (!flags.HasFlag(DiffRiskClassification.Security) && SecurityKeyword().IsMatch(text))
            {
                flags |= DiffRiskClassification.Security;
            }

            if (!flags.HasFlag(DiffRiskClassification.Invariant) && InvariantKeyword().IsMatch(text))
            {
                flags |= DiffRiskClassification.Invariant;
            }
        }

        return flags;
    }

    /// <summary>Security-adjacent path segments. Substring matching (no word boundary) — see the remarks on
    /// <see cref="Classify"/> for why a path is checked differently from a line.</summary>
    [GeneratedRegex(@"auth|security|crypto|secret|oauth|webhook|sandbox|policy", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityPathHint();

    /// <summary>Invariant/contract-adjacent path segments. See <see cref="SecurityPathHint"/>.</summary>
    [GeneratedRegex(@"invariant|contract|guard", RegexOptions.IgnoreCase)]
    private static partial Regex InvariantPathHint();

    /// <summary>Security-adjacent vocabulary: secrets/credentials, crypto primitives, auth/permission checks,
    /// and the injection-class names a reviewer would recognise (sql injection, xss, csrf).</summary>
    [GeneratedRegex(
        @"\b(?:password|secret|credential|api[_-]?key|token|encrypt|decrypt|cipher|hmac|jwt|oauth|"
            + @"authoriz\w*|authentic\w*|permission\w*|certificate|private[_\s]?key|signature|sanitiz\w*|"
            + @"sql\s*inject\w*|xss|csrf|escape\w*)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SecurityKeyword();

    /// <summary>Invariant/contract vocabulary: guard clauses, assertions, the framework's own null/argument
    /// checks, and "must (not/always)" language.</summary>
    [GeneratedRegex(
        @"\b(?:invariant\w*|precondition\w*|postcondition\w*|guard\w*|assert\w*|contract\w*|throwifnull\w*|"
            + @"argumentnullexception|argumentexception|invalidoperationexception|must\s+not|must\s+always|"
            + @"never\s+null)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex InvariantKeyword();
}
