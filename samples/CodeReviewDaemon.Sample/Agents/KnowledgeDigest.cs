using System.Text;
using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Pure, deterministic construction of the "prior knowledge" block that rides on the review input.
/// <para>
/// The Knowledge Base grows unbounded while the prompt budget does not, and the reviewer cannot
/// discover entries on its own — a root-level Grep in the tool-assisted checkout can come back empty
/// even when the file exists, so anything the agent is not *handed* is effectively invisible. This
/// helper therefore ranks the <c>_index.jsonl</c> metadata against the files the PR actually touches
/// and renders the survivors as title + tags + scope + <b>exact absolute path</b>, which the agent (and
/// any sub-agent it dispatches) can open with a plain Read.
/// </para>
/// Kept free of IO and of the clock so the ranking is unit-testable and byte-stable: the same index and
/// the same diff always produce the same digest.
/// </summary>
internal static class KnowledgeDigest
{
    /// <summary>Weight of a tag hit. Tags are curated, so they are stronger evidence than title prose.</summary>
    private const int TagWeight = 2;

    /// <summary>
    /// Weight of an entry whose <see cref="KnowledgeEntryMeta.Scope"/> is the repository under review.
    /// Set above <see cref="TagWeight"/> so a repo-specific lesson outranks a generic one that matched on
    /// a single tag — locality beats vocabulary overlap.
    /// </summary>
    private const int ScopeBonus = 3;

    /// <summary>Shortest token worth matching; below this, path noise ("cs", "lm") dominates.</summary>
    private const int MinTokenLength = 3;

    /// <summary>
    /// Tokens that appear in nearly every path or title and would match everything equally, adding cost
    /// without adding ranking signal.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "src", "test", "tests", "sample", "samples", "docs", "bin", "obj", "lib", "csproj", "sln",
        "json", "jsonl", "yaml", "yml", "md", "txt", "cshtml", "razor",
        "the", "and", "for", "with", "from", "into", "not", "this", "that", "are", "its", "use",
        "using", "when", "before", "after", "all", "any", "one", "two", "per", "has", "have", "was",
        "were", "will", "can", "get", "set", "must", "new", "old", "main",
    };

    /// <summary>
    /// Reads the paths a unified diff touches from its <c>diff --git a/… b/…</c> headers. Both sides are
    /// reported (deduplicated, in first-seen order) so a rename contributes its old and new path — a
    /// lesson may be filed against either. Returns empty for null, blank, or header-less input.
    /// </summary>
    public static IReadOnlyList<string> ExtractChangedPaths(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return [];
        }

        const string Header = "diff --git ";
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in SplitCappedLines(diff).Lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith(Header, StringComparison.Ordinal))
            {
                continue;
            }

            var (left, right) = SplitHeaderPaths(line[Header.Length..]);
            foreach (var path in new[] { left, right })
            {
                if (path.Length > 0 && path != "/dev/null" && seen.Add(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// Reads the paths of a <c>git diff --name-only</c> listing (one path per line, git's quoted form
    /// allowed) into a deduplicated, first-seen-order list. This is the PREFERRED ranking input: the diff
    /// text the review persists is capped, so on a large PR its later <c>diff --git</c> headers are simply
    /// gone and <see cref="ExtractChangedPaths"/> cannot see the files changed last — the name-only listing
    /// stays orders of magnitude smaller and survives the same cap intact. A trailing truncation marker (if
    /// even this listing was capped) is dropped rather than ranked against. Returns empty for absent or
    /// empty input, which lets the caller fall back to the diff headers for artifacts written before this
    /// existed — but NOT for whitespace, which names a real file (see the per-line rule below).
    /// </summary>
    public static IReadOnlyList<string> ParseChangedPaths(string? nameOnlyListing) =>
        ParseChangedPaths(nameOnlyListing, out _);

    /// <summary>
    /// As <see cref="ParseChangedPaths(string?)"/>, additionally reporting whether the listing carried the
    /// truncation marker.
    /// <para>
    /// A truncated listing is a PARTIAL answer that looks like a complete one. It is non-empty, so the
    /// caller's "fell back to the diff headers when empty" route never fires, and every file past the cut is
    /// ranked against nothing while the log reports a healthy path count. The caller cannot recover from
    /// what it is never told about, so the fact travels with the paths rather than being re-derived from the
    /// marker somewhere else — one rule, in one place.
    /// </para>
    /// Reports that the listing was cut AND drops the record the cut landed inside, so what survives is
    /// whole either way — see <see cref="SplitCappedLines"/> for how the two cases are told apart.
    /// </summary>
    public static IReadOnlyList<string> ParseChangedPaths(string? nameOnlyListing, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(nameOnlyListing))
        {
            return [];
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var (lines, wasCut) = SplitCappedLines(nameOnlyListing);
        truncated = wasCut;

        foreach (var raw in lines)
        {
            // Only the line terminator is stripped, and the split already did that. git permits a filename
            // to begin or end with a space and does NOT quote for one - quoting triggers on non-ASCII,
            // control, quote and backslash bytes only - so trimming here would rename the file into a path
            // git never reported, and Unquote could not repair it because there was never a quoted form.
            // Emptiness is therefore length, not whitespace: "  " is a legal (absurd) filename and survives
            // as one.
            if (raw.Length == 0)
            {
                continue;
            }

            var path = Unquote(raw);
            if (path.Length > 0 && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Splits capped command output into lines, reporting whether it carried the truncation marker and
    /// discarding both the marker and any record the cut landed inside.
    /// <para>
    /// Shared by both changed-path parsers because it is one rule, and because the rule is about the
    /// PRODUCER's cut rather than about either format. Two caps reach this code:
    /// <see cref="SandboxLimits.CapRecordListing"/> cuts between records and keeps the last one's newline,
    /// while <see cref="SandboxLimits.CapOutput"/> is character-exact and can halve a record. The marker
    /// opens with <c>\n</c>, so the first case leaves an EMPTY element in front of the marker and the second
    /// leaves the stump — which is the whole of the evidence, and it is enough. A stump is dropped because it
    /// reads exactly like a real path or a real <c>diff --git</c> header and would be ranked against as one,
    /// silently, since the result is still non-empty.
    /// </para>
    /// The marker is matched against the trimmed form so that detecting it does not depend on how the
    /// producer happened to space it.
    /// </summary>
    private static (IReadOnlyList<string> Lines, bool Truncated) SplitCappedLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var markerHead = SandboxLimits.TruncationMarker.TrimStart('\n');
        var marker = Array.FindIndex(
            lines, line => line.Trim().StartsWith(markerHead, StringComparison.Ordinal));

        if (marker < 0)
        {
            return (lines, false);
        }

        var kept = marker;
        if (kept > 0 && lines[kept - 1].Length > 0)
        {
            kept--;
        }

        return (lines[..kept], true);
    }

    /// <summary>
    /// Ranks <paramref name="entries"/> against <paramref name="changedPaths"/> and returns at most
    /// <paramref name="maxEntries"/> of them, best first. An entry scores on tag and title tokens shared
    /// with the changed paths, plus a bonus when its scope is <paramref name="repoScope"/>.
    /// <para>
    /// Entries that match nothing are ranked last but are <b>not</b> dropped: the tag vocabulary is coarse
    /// and the Knowledge Base is small, so returning nothing on a miss would reproduce exactly the
    /// knowledge-blindness this digest exists to remove. The cap, not the score, is what bounds the cost.
    /// </para>
    /// Ties break by <see cref="KnowledgeEntryMeta.Updated"/> descending then
    /// <see cref="KnowledgeEntryMeta.File"/> ordinal, so the result does not depend on input order.
    /// </summary>
    public static IReadOnlyList<KnowledgeEntryMeta> SelectRelevant(
        IReadOnlyList<KnowledgeEntryMeta> entries,
        IReadOnlyList<string> changedPaths,
        string? repoScope,
        int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(changedPaths);

        if (entries.Count == 0 || maxEntries <= 0)
        {
            return [];
        }

        var pathTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changedPaths)
        {
            AddTokens(path, pathTokens);
        }

        return
        [
            .. entries
                .OrderByDescending(entry => Score(entry, pathTokens, repoScope))
                .ThenByDescending(entry => entry.Updated, StringComparer.Ordinal)
                .ThenBy(entry => entry.File, StringComparer.Ordinal)
                .Take(maxEntries),
        ];
    }

    /// <summary>
    /// Renders <paramref name="entries"/> as the prior-knowledge block, resolving each entry's KB-relative
    /// <see cref="KnowledgeEntryMeta.File"/> against <paramref name="knowledgeBaseRoot"/> into an absolute
    /// path the agent can Read directly. Every entry is resolved BEFORE the budget is applied, so an entry
    /// that escapes the Knowledge Base is refused whether or not it would have fitted. Entries are then
    /// appended while the block fits in
    /// <paramref name="charBudget"/>; whatever does not fit, plus the <paramref name="omitted"/> entries
    /// the ranking already dropped, is reported in a footer that points at <c>_toc.md</c> so nothing
    /// disappears silently. Returns an empty <see cref="KnowledgeDigestBlock.Text"/> when there is nothing
    /// to say, letting the caller leave the review input untouched.
    /// <para>
    /// <see cref="KnowledgeDigestBlock.Rendered"/> lists the entries that SURVIVED the budget, which is
    /// deliberately not the same as the entries passed in: the caller logs that list as its proof that
    /// retrieval reached the reviewer, and a proof that names entries the reviewer never received is worse
    /// than no proof at all — it is the silent failure this logging exists to expose, wearing a green badge.
    /// </para>
    /// </summary>
    public static KnowledgeDigestBlock Render(
        IReadOnlyList<KnowledgeEntryMeta> entries,
        string knowledgeBaseRoot,
        int charBudget,
        int omitted)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        // EVERY entry is resolved before ANY of them is rendered. Folding the check into the render loop
        // would leave the entries past the budget cut unexamined, and budget pressure is the normal case,
        // not a corner - so an escaping entry sitting beyond the cut would never reach Rejected, would
        // never be warned about, and would still be counted below as an entry the agent can go and fetch
        // from _toc.md. That is precisely the silent disappearance the rejection reporting exists to stop.
        var resolved = new List<(KnowledgeEntryMeta Entry, string Path)>(entries.Count);
        var rejected = new List<KnowledgeEntryMeta>();
        foreach (var entry in entries)
        {
            if (TryResolveEntryPath(knowledgeBaseRoot, entry.File, out var absolute))
            {
                resolved.Add((entry, absolute));
            }
            else
            {
                rejected.Add(entry);
            }
        }

        // Space for the footer is reserved before anything is written, against the largest count it could
        // ever report, so the promise of a route to what did not fit can never be the thing that overruns
        // the budget. Appending it afterwards would make it the one unchecked write in the method.
        var header = Header();
        var reserve = Footer(omitted + resolved.Count, knowledgeBaseRoot).Length;
        var builder = new StringBuilder();
        var rendered = new List<KnowledgeEntryMeta>(resolved.Count);

        // The header is an append like any other and is checked like one. There is no "at least one entry"
        // exemption anywhere in this loop: an entry allowed to skip the check is an entry that can carry an
        // unbounded model-authored title straight past the budget, which is how a nominal 8 KiB block ends
        // up crowding the PR itself out of the reviewer's context window.
        if (header.Length + reserve <= charBudget)
        {
            _ = builder.Append(header);
            foreach (var (entry, absolute) in resolved)
            {
                var line = RenderEntry(entry, absolute, charBudget - builder.Length - reserve);
                if (line.Length == 0)
                {
                    // Skip this entry, do NOT stop. An empty render is a fact about THIS entry - its path
                    // alone could not fit - and not a signal that the budget is spent, because the room an
                    // entry needs is dominated by its own model-authored path length. One oversized path
                    // ranked above the rest therefore used to discard every entry behind it, leaving the
                    // agent a header and a _toc.md pointer: knowledge-blind, which is the outcome this
                    // whole feature exists to prevent, reachable through a single "file" value.
                    continue;
                }

                _ = builder.Append(line);
                rendered.Add(entry);
            }
        }

        // Counted off the RESOLVED pool, so a rejected entry is neither rendered nor missing. The footer's
        // promise is that whatever did not fit is reachable through _toc.md, and pointing the agent at an
        // entry we just refused to resolve would be an invitation to go and find the very thing refused.
        var missing = omitted + (resolved.Count - rendered.Count);
        if (rendered.Count == 0)
        {
            // Nothing survived - every entry was refused, or the budget could not hold even one. A header
            // with no paths beneath it reads exactly like a Knowledge Base that happens to be empty, so say
            // nothing at all unless there is a count worth reporting, and let the caller leave the review
            // input untouched; refusals travel back through Rejected, which is where they get logged.
            return new KnowledgeDigestBlock(
                builder.Length > 0 && missing > 0 ? header + Footer(missing, knowledgeBaseRoot) : string.Empty,
                [],
                rejected);
        }

        return new KnowledgeDigestBlock(
            builder.Append(missing > 0 ? Footer(missing, knowledgeBaseRoot) : string.Empty).ToString(),
            rendered,
            rejected);
    }

    /// <summary>
    /// Splits <paramref name="entries"/> into those whose <see cref="KnowledgeEntryMeta.File"/> resolves
    /// inside <paramref name="knowledgeBaseRoot"/> and those that escape it.
    /// <para>
    /// Exists so containment can be decided BEFORE the entry cap is applied. The index is written by the
    /// knowledge-extraction agent, so escaping entries are entries an LLM produced — and if the cap is taken
    /// off the raw list, enough of them ranked highly enough will consume every retrieval slot and push
    /// sound knowledge out entirely. The review then proceeds knowledge-blind, which is the exact outcome
    /// this feature exists to prevent, reached through the containment check added to make it safer. The cap
    /// has to count entries the agent can actually use.
    /// </para>
    /// </summary>
    public static KnowledgeContainmentPartition PartitionByContainment(
        IReadOnlyList<KnowledgeEntryMeta> entries, string knowledgeBaseRoot)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        var usable = new List<KnowledgeEntryMeta>(entries.Count);
        var refused = new List<KnowledgeEntryMeta>();
        foreach (var entry in entries)
        {
            (TryResolveEntryPath(knowledgeBaseRoot, entry.File, out _) ? usable : refused).Add(entry);
        }

        return new KnowledgeContainmentPartition(usable, refused);
    }

    /// <summary>
    /// Renders a raw <c>_toc.md</c> as the prior-knowledge block, for when <c>_index.jsonl</c> is missing or
    /// torn (a Knowledge Base written before the index existed, or a crash mid-write). Strictly weaker than
    /// <see cref="Render"/> — the ToC carries titles and KB-relative links, so there are no tags, no scope
    /// and no ranking — but it arrives under the same <see cref="Heading"/> and states the absolute root the
    /// links hang off, so the agent can still open an entry and still hand paths to its sub-agents. Returns
    /// an empty <see cref="KnowledgeTocBlock.Text"/> for a blank table of contents, letting the caller leave
    /// the review input untouched.
    /// <para>
    /// Bounded by the SAME <paramref name="charBudget"/> as the ranked path. This is the degraded route, so
    /// an unbounded one here would mean the only uncapped prior-knowledge block is the one rendered after
    /// something has already gone wrong — and the Knowledge Base only grows, so it crosses the budget on its
    /// own eventually. The cut lands between lines, never inside one: a half-written link is worse than an
    /// absent one, because the agent will try to open the truncated path.
    /// </para>
    /// </summary>
    public static KnowledgeTocBlock RenderTableOfContents(
        string? tableOfContents, string knowledgeBaseRoot, int charBudget)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        if (string.IsNullOrWhiteSpace(tableOfContents))
        {
            return new KnowledgeTocBlock(string.Empty, 0, 0, false, []);
        }

        var root = knowledgeBaseRoot.Replace('\\', '/').TrimEnd('/');
        var header = $"""
            {Heading}

            Durable lessons from earlier reviews. The ranked index was unavailable, so this is the Knowledge
            Base table of contents verbatim, read from {Join(knowledgeBaseRoot, "_toc.md")}. Its links are
            RELATIVE to {root}/ — join a link onto that directory to get the entry's exact absolute path and
            open it with the Read tool; do NOT Grep or Glob for it, because a root-level Grep can come back
            empty even when the file exists. When you dispatch a sub-agent for a dimension, copy the paths
            that match that dimension into its brief; it has no other way to see them and will otherwise
            review with no prior knowledge at all.


            """;

        var lines = tableOfContents
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n').Split('\n');
        var total = lines.Count(IsTocEntry);

        // Room for the closing note is reserved up front, against whichever of the two forms is longer and
        // against the largest count the footer could report, so the promise of a route to what was cut can
        // never itself be what overruns the budget.
        var reserve = Math.Max(
            total > 0 ? Footer(total, knowledgeBaseRoot).Length : 0, TruncatedNotice(knowledgeBaseRoot).Length);

        var builder = new StringBuilder();
        var listed = 0;
        var truncated = false;
        var refused = new List<string>();

        // Refused LINES, not refused links: one line can carry more than one escaping destination, and the
        // total below counts entry lines. Reporting every bad link is what the operator needs; subtracting
        // every bad link is what would drive the dropped count negative.
        var refusedLines = 0;

        // Truncation is tracked on its OWN flag, not inferred from the entry count. A torn or hand-edited
        // _toc.md - which is precisely the state that sends us down this fallback - can contain no
        // "- [Title](path)" lines at all, and a gate that only fires once an entry has been listed would
        // then never fire, appending the whole file unbounded while reporting nothing was dropped.
        if (header.Length + reserve <= charBudget)
        {
            _ = builder.Append(header);
            foreach (var line in lines)
            {
                // Containment applies here for the same reason it applies to the ranked path, and with the
                // SAME rule rather than a second one written for this side: the _toc.md is written by the
                // knowledge agent, and this fallback runs exactly when that file is torn or hand-edited. A
                // link that resolves outside the Knowledge Base presents something that is not knowledge as
                // though it were, with nothing in the block for the reviewer to tell the difference by - and
                // the degraded route was the one still doing it.
                var links = TocLinks(line);
                var escapes = links
                    .Where(link => !IsLinkTheAgentCanSafelyJoin(link.Destination, root))
                    .Select(link => link.Destination)
                    .ToList();
                if (escapes.Count > 0)
                {
                    refused.AddRange(escapes);
                    refusedLines++;
                    continue;
                }

                var text = FitTocLine(line, charBudget - builder.Length - reserve, links);
                if (text is null)
                {
                    // Skipped rather than stopped at, for the same reason as the ranked path above: a null
                    // here is a fact about THIS line, not about the remaining room. A non-entry line has no
                    // link to shorten so it fails outright, and an entry fails once its "](link)" suffix
                    // alone exceeds the room - both while a short entry two lines later would fit easily.
                    // The cut is still admitted; it is reported by the flag and by the dropped count, which
                    // is what keeps a block with holes in it honest about having them.
                    truncated = true;
                    continue;
                }

                _ = builder.Append(text);
                if (IsTocEntry(line))
                {
                    listed++;
                }
            }
        }
        else
        {
            truncated = true;
        }

        // A refused entry is a different fact from one that did not fit, so it is subtracted from the total
        // rather than counted as dropped: a footer promising "1 more entry" in _toc.md would route the agent
        // straight back to the link just refused.
        var dropped = total - listed - refusedLines;
        if (builder.Length == 0)
        {
            return new KnowledgeTocBlock(string.Empty, 0, dropped, true, refused);
        }

        var closing = dropped > 0 ? Footer(dropped, knowledgeBaseRoot)
            : truncated ? TruncatedNotice(knowledgeBaseRoot)
            : string.Empty;
        return new KnowledgeTocBlock(
            builder.Append(closing).ToString(), listed, dropped, truncated, refused);
    }

    /// <summary>
    /// Whether a <c>_toc.md</c> link lands inside the Knowledge Base once the agent resolves it the way the
    /// block's header tells it to.
    /// <para>
    /// Uses <see cref="TryResolveEntryPath"/> so traversal is judged by ONE rule shared with the ranked path,
    /// plus two conditions the ranked path does not need. <see cref="Render"/> hands the agent a path it has
    /// already joined onto the root, which makes a leading slash harmless — it lands under the root either
    /// way. This fallback prints the link VERBATIM and asks the agent to do the join itself, and an agent
    /// handed <c>/etc/passwd</c> will read it as already absolute and open it. The link is rejected here not
    /// because the rule differs but because on this path nothing performs the join that made it safe.
    /// </para>
    /// A URI is rejected for the same reason and is invisible to <see cref="TryResolveEntryPath"/>, which
    /// splits on <c>/</c> and finds <c>https:</c> and <c>evil.example</c> to be perfectly ordinary segments
    /// that resolve inside the root. It is not a path at all, and an agent handed one follows it as written.
    /// The scheme test also catches a Windows drive letter, which is absolute by another spelling.
    /// </summary>
    private static bool IsLinkTheAgentCanSafelyJoin(string link, string knowledgeBaseRoot) =>
        link.Length > 0
        && !link.StartsWith('/')
        && !link.StartsWith('\\')
        && !HasUriScheme(link)
        && TryResolveEntryPath(knowledgeBaseRoot, link, out _);

    /// <summary>
    /// Whether the link opens with an RFC 3986 scheme (<c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</c>).
    /// A single-letter scheme is a Windows drive prefix as often as it is a real one; both are refused, and
    /// no Knowledge Base entry filename can contain a colon anyway.
    /// </summary>
    private static bool HasUriScheme(string link)
    {
        var colon = link.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            var allowed = i == 0
                ? char.IsAsciiLetter(link[i])
                : char.IsAsciiLetterOrDigit(link[i]) || link[i] is '+' or '-' or '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One Markdown link found on a <c>_toc.md</c> line: the destination an agent would resolve, and the
    /// offset of the <c>](</c> that opened it, so no later caller has to go looking for it a second time.
    /// </summary>
    private readonly record struct TocLink(string Destination, int Marker);

    /// <summary>
    /// Every link destination on a <c>_toc.md</c> entry line, normalized, in source order. Empty when the
    /// line is not an entry (a heading, blank or prose line carries no path and is not containment-checked).
    /// <para>
    /// Every one, not the last one. The previous reading took <c>LastIndexOf("](")</c>, so a line carrying
    /// two links had one of them checked and both of them rendered — the escape only had to not be written
    /// last. The check was right both times it was defeated; what reached it was not.
    /// </para>
    /// <para>
    /// This is the only place the syntax is read. Callers get the offsets back rather than re-deriving them,
    /// because a second parser over the same syntax is a second set of assumptions to be wrong about, and
    /// this one has already been wrong twice.
    /// </para>
    /// </summary>
    private static IReadOnlyList<TocLink> TocLinks(string line)
    {
        if (!IsTocEntry(line))
        {
            return [];
        }

        var links = new List<TocLink>();
        var at = 0;
        while (true)
        {
            var marker = line.IndexOf("](", at, StringComparison.Ordinal);
            if (marker < 0)
            {
                break;
            }

            var open = marker + "](".Length;

            // Which form the destination is in has to be settled BEFORE deciding where it ends, because the
            // two forms end on different characters. Cutting at the first ")" and unwrapping whatever that
            // produced is the same defect one layer down: "<a)/../../../../etc/passwd>" cuts to "<a", which
            // unwraps to the contained name "a" and is ACCEPTED, while an agent resolving CommonMark reads
            // the whole angle-bracketed path and walks out of the store. Inside <...> a ")" is an ordinary
            // character; bare, it terminates — which is exactly where CommonMark's own parser stops.
            var angle = open < line.Length && line[open] == '<' ? line.IndexOf('>', open + 1) : -1;
            var close = line.IndexOf(')', angle < 0 ? open : angle + 1);
            if (close < 0 && angle < 0)
            {
                break;
            }

            links.Add(
                new TocLink(
                    NormalizeLinkDestination(angle < 0 ? line[open..close] : line[open..(angle + 1)]), marker));
            at = (close < 0 ? angle : close) + 1;
        }

        return links;
    }

    /// <summary>
    /// The destination an agent resolving Markdown normally would act on, from the raw destination text the
    /// caller delimited — angle brackets included, when it is in that form.
    /// <para>
    /// CommonMark permits the destination to be wrapped in angle brackets, and a bare destination to be
    /// followed by a title. Neither form was being unwrapped, so <c>&lt;/etc/passwd&gt;</c> reached the
    /// containment rule beginning with <c>&lt;</c> rather than <c>/</c> and the leading-slash rejection —
    /// added the round before for exactly this target — never fired. Validating the text as written rather
    /// than as resolved is the defect; this is where it is repaired, once, for every caller.
    /// </para>
    /// <para>
    /// The two forms are mutually exclusive here, deliberately: only a BARE destination is split at
    /// whitespace, because inside angle brackets a space belongs to the path. Splitting there would validate
    /// a short prefix of a path the agent follows whole.
    /// </para>
    /// </summary>
    private static string NormalizeLinkDestination(string destination)
    {
        var text = destination.Trim();
        if (text.StartsWith('<'))
        {
            var end = text.IndexOf('>');
            return (end < 0 ? text[1..] : text[1..end]).Trim();
        }

        var space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// A <c>_toc.md</c> line rendered to fit <paramref name="room"/>, or <c>null</c> when it cannot fit at
    /// all. An entry line whose title is too long is rewritten with the title cut and the
    /// <c>](path)</c> link kept whole — the title is model-authored and cosmetic, the link is the only
    /// reason the line is worth carrying, and a half-written path is worse than an absent one because the
    /// agent will try to open it.
    /// </summary>
    private static string? FitTocLine(string line, int room, IReadOnlyList<TocLink> links)
    {
        if (line.Length + 1 <= room)
        {
            return line + "\n";
        }

        // Only a single-link line can be shortened safely. The title cut is anchored on the link's opening
        // "](", so on a two-link line everything between the first link and the last is swallowed as title
        // text and the render comes back as "- [First entry's title (truncated)](second entry's link)": a
        // label naming one entry over a link pointing at another. Misattributed knowledge is worse than
        // absent knowledge, and the entry is counted as dropped with a route to the full _toc.md either way,
        // so such a line fits whole or not at all.
        if (links.Count != 1)
        {
            return null;
        }

        // The anchor comes from the parser that read the line, not from a fresh LastIndexOf("](") here. That
        // second reading agreed with the first only while no destination contained "](" itself: given
        // "- [t](<system/xxx](b.md>)" - one link, contained, cleared - it landed INSIDE the destination and
        // cut the line down over the fragment "b.md>", a path belonging to no entry at all.
        var link = links[0].Marker;
        var suffix = line[link..];
        var titleRoom = room - "- [".Length - suffix.Length - TruncationMarker.Length - 1;
        if (titleRoom < 0)
        {
            return null;
        }

        var title = line["- [".Length..link];
        return $"- [{title[..Math.Min(titleRoom, title.Length)]}{TruncationMarker}{suffix}\n";
    }

    /// <summary>
    /// Says the block was cut when entries alone cannot say it — a table of contents with no recognisable
    /// entry lines still has to admit that the reviewer did not receive all of it.
    /// </summary>
    private static string TruncatedNotice(string knowledgeBaseRoot) =>
        $"\nThis table of contents was truncated to fit; the full list is in "
        + $"{Join(knowledgeBaseRoot, "_toc.md")}.\n";

    /// <summary>
    /// Joins Knowledge Base entry paths for a log line, bounded by <paramref name="charBudget"/> and
    /// reporting how many it left out.
    /// <para>
    /// The digest itself is budgeted, but the "surfaced"/"refused" log lines quote the SAME model-authored
    /// <see cref="KnowledgeEntryMeta.File"/> values, and a joined list has no bound of its own — one absurd
    /// entry writes its whole length into the daemon's JSONL on every review that ranks it, and the refusal
    /// line is reached by exactly the malformed entries most likely to carry one. Paths are dropped whole
    /// rather than cut, for the same reason they are in the digest: half a path names nothing, and an
    /// operator reading the log would take it for a real one.
    /// </para>
    /// </summary>
    public static string DescribePaths(IEnumerable<string> paths, int charBudget)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var all = paths.ToList();
        var builder = new StringBuilder();
        var listed = 0;
        foreach (var path in all)
        {
            // The suffix is reserved against the largest count it could report, so admitting the elision can
            // never be the thing that overruns the budget.
            //
            // A path that will not fit is SKIPPED, not stopped at, for the same reason as both render loops:
            // whether it fits is a fact about that path's own length, not about the room left. It matters
            // most on this line of the three - this joiner exists precisely because "file" is model-authored
            // and can be absurd, and the refusal line is reached by the malformed entries likeliest to carry
            // an absurd one. Stopping there would let a single 5k path reduce the whole line to "(+N more)",
            // costing the operator the names of every other refused entry: the ones they can act on.
            var separator = listed == 0 ? string.Empty : ", ";
            if (builder.Length + separator.Length + path.Length + MoreSuffix(all.Count).Length > charBudget)
            {
                continue;
            }

            _ = builder.Append(separator).Append(path);
            listed++;
        }

        return listed == all.Count ? builder.ToString() : builder.Append(MoreSuffix(all.Count - listed)).ToString();
    }

    private static string MoreSuffix(int remaining) => $" … (+{remaining} more)";

    /// <summary>
    /// Whether a <c>_toc.md</c> line is an entry rather than structure. The ToC is generated by
    /// <see cref="KnowledgeTableOfContents"/>, which writes entries as <c>- [Title](rel/path.md)</c> under a
    /// document header and per-scope headings; only the entries are worth counting, because only they are
    /// what the footer promises a route to.
    /// </summary>
    private static bool IsTocEntry(string line) =>
        line.StartsWith("- [", StringComparison.Ordinal) && line.Contains("](", StringComparison.Ordinal);

    private static string Header() =>
        $"""
        {Heading}

        Durable lessons from earlier reviews, ranked by relevance to the files this PR changes. Open one
        with the Read tool using the EXACT ABSOLUTE PATH shown below — do NOT Grep or Glob for it, because
        a root-level Grep can come back empty even when the file exists. When you dispatch a sub-agent for
        a dimension, copy the paths that match that dimension into its brief; it has no other way to see
        them and will otherwise review with no prior knowledge at all.


        """;

    /// <summary>
    /// The one heading the prior-knowledge block ever carries. The review prompt teaches this exact string
    /// AND teaches that its absence means no Knowledge Base exists, so a block rendered under any other
    /// heading is one the agent has been told not to go looking for — it would read as knowledge-blind even
    /// though the knowledge was right there in its input. Both the ranked digest and the
    /// <see cref="RenderTableOfContents"/> fallback therefore share it.
    /// </summary>
    private const string Heading = "## Prior knowledge (Knowledge Base)";

    private static string Footer(int missing, string knowledgeBaseRoot) =>
        $"\n{missing} more entr{(missing == 1 ? "y is" : "ies are")} not listed here; the full list is in "
        + $"{Join(knowledgeBaseRoot, "_toc.md")}.\n";

    /// <summary>
    /// Renders one entry within <paramref name="maxLength"/>, or an empty string when not even a truncated
    /// form fits.
    /// <para>
    /// Title, tags and scope all come from the knowledge-extraction agent and are unbounded; the absolute
    /// path is computed here and is the one part of the line the reviewer acts on. So when the line does not
    /// fit, the METADATA gives way and the path is kept whole: a cut title costs the agent a hint about
    /// what the entry is, while a cut path costs it the entry itself and hands it something that looks
    /// openable and is not.
    /// </para>
    /// </summary>
    private static string RenderEntry(KnowledgeEntryMeta entry, string absolutePath, int maxLength)
    {
        var title = string.IsNullOrWhiteSpace(entry.Title) ? entry.File : entry.Title;
        var tags = entry.Tags.Count == 0 ? "(none)" : string.Join(", ", entry.Tags);
        var scope = string.IsNullOrWhiteSpace(entry.Scope) ? "(unscoped)" : entry.Scope;

        var pathLine = $"  {absolutePath}\n";
        var metadata = $"- {title}\n  tags: {tags} | scope: {scope}\n";
        if (metadata.Length + pathLine.Length <= maxLength)
        {
            return metadata + pathLine;
        }

        // The marker and the newline that closes the cut line are part of what has to fit, so they are
        // subtracted before the metadata is measured rather than appended on top of a full block.
        var room = maxLength - pathLine.Length - TruncationMarker.Length - 1;
        return room < MinimumMetadataChars
            ? string.Empty
            : $"{metadata[..room]}{TruncationMarker}\n{pathLine}";
    }

    /// <summary>
    /// Marks metadata this renderer cut. Deliberately parenthesised rather than bracketed so it stays
    /// harmless inside a Markdown link's title text in <see cref="FitTocLine"/>.
    /// </summary>
    private const string TruncationMarker = " (truncated)";

    /// <summary>
    /// Below this there is no point rendering an entry at all: the line would be a bullet, a fragment of a
    /// title and a path, which tells the agent less than leaving the entry out and counting it in the footer.
    /// </summary>
    private const int MinimumMetadataChars = 8;

    /// <summary>
    /// Resolves an entry's KB-relative <see cref="KnowledgeEntryMeta.File"/> against the Knowledge Base
    /// root, refusing anything whose canonical form lands outside it.
    /// <para>
    /// Worth the trouble because this value is NOT trusted input. During regeneration the index is built
    /// from a directory listing, but the digest reads it back from <c>_index.jsonl</c> on disk in the
    /// store, and the store's <c>KnowledgeBase/</c> is written by the knowledge agent - an LLM with
    /// file-write tools. A hand-edited, torn or model-authored <c>"file"</c> value therefore reaches this
    /// method, and an absolute path outside the Knowledge Base would present something that is not
    /// knowledge as though it were, with nothing in the block for the reviewer to tell the difference by.
    /// </para>
    /// Containment, not a ban on <c>..</c>: segments are resolved, so a path that steps out and back in
    /// is fine while one that pops past the root is refused outright rather than quietly rewritten into
    /// something safe - a rewritten path would still be offered to the agent as knowledge. A backslash
    /// separates here too, since <see cref="Join"/> normalizes it and an escape spelled with backslashes
    /// escapes just as one spelled with slashes does. A LEADING slash is not an escape: <see cref="Join"/>
    /// already contains it, so it lands harmlessly under the root.
    /// </summary>
    private static bool TryResolveEntryPath(string knowledgeBaseRoot, string? file, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in file.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment != "..")
            {
                segments.Add(segment);
                continue;
            }

            if (segments.Count == 0)
            {
                return false; // Popped past the Knowledge Base root.
            }

            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count == 0)
        {
            return false; // Names no file at all; the root itself is not an entry.
        }

        absolutePath = Join(knowledgeBaseRoot, string.Join('/', segments));
        return true;
    }

    /// <summary>Joins a KB-relative entry path onto the root with forward slashes (the checkout the agent
    /// sees is POSIX, whatever the daemon host is).</summary>
    private static string Join(string root, string relative) =>
        $"{root.Replace('\\', '/').TrimEnd('/')}/{relative.Replace('\\', '/').TrimStart('/')}";

    /// <summary>
    /// Splits the <c>a/… b/…</c> remainder of a <c>diff --git</c> header. Either side may be given in git's
    /// quoted form (<c>"a/caf\303\251.cs"</c>) when the path carries non-ASCII or special bytes, so both the
    /// bare <c>" b/"</c> and the quoted <c>" \"b/"</c> separators are considered. Paths may themselves
    /// contain a separator, so the candidate whose two sides are equal wins (the overwhelmingly common
    /// unchanged-name case); failing that, the first candidate is taken, which is correct for a rename.
    /// </summary>
    private static (string Left, string Right) SplitHeaderPaths(string remainder)
    {
        var fallback = (Left: string.Empty, Right: string.Empty);
        for (var i = 0; i < remainder.Length - 2; i++)
        {
            if (remainder[i] != ' ')
            {
                continue;
            }

            var rest = remainder[(i + 1)..];
            if (!rest.StartsWith("b/", StringComparison.Ordinal)
                && !rest.StartsWith("\"b/", StringComparison.Ordinal))
            {
                continue;
            }

            var left = Unprefix(remainder[..i], "a/");
            var right = Unprefix(rest, "b/");
            if (left == right)
            {
                return (left, right);
            }

            if (fallback.Left.Length == 0)
            {
                fallback = (left, right);
            }
        }

        return fallback;
    }

    private static string Unprefix(string value, string prefix)
    {
        var trimmed = Unquote(value.Trim());
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) ? trimmed[prefix.Length..] : trimmed;
    }

    /// <summary>
    /// Decodes git's quoted path form — a double-quoted C string whose non-ASCII bytes appear as three-digit
    /// octal escapes, one per UTF-8 byte. Escapes are collected as BYTES and decoded once at the end, so a
    /// multi-byte character split across several escapes round-trips. A value that is not quoted is returned
    /// unchanged, so callers can pass either form.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        var inner = value[1..^1];
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(inner[i].ToString()));
                continue;
            }

            var next = inner[++i];
            if (next is >= '0' and <= '7')
            {
                var octal = 0;
                var digits = 0;
                while (digits < 3 && i < inner.Length && inner[i] is >= '0' and <= '7')
                {
                    octal = (octal * 8) + (inner[i] - '0');
                    i++;
                    digits++;
                }

                i--;
                bytes.Add((byte)octal);
                continue;
            }

            bytes.AddRange(
                Encoding.UTF8.GetBytes(
                    (next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        'a' => '\a',
                        'b' => '\b',
                        'f' => '\f',
                        'v' => '\v',
                        _ => next, // \\ and \" decode to themselves.
                    }).ToString()));
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static int Score(KnowledgeEntryMeta entry, HashSet<string> pathTokens, string? repoScope)
    {
        var score = !string.IsNullOrWhiteSpace(repoScope)
            && string.Equals(entry.Scope, repoScope, StringComparison.OrdinalIgnoreCase)
                ? ScopeBonus
                : 0;

        if (pathTokens.Count == 0)
        {
            return score;
        }

        var tagTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in entry.Tags)
        {
            AddTokens(tag, tagTokens);
        }

        score += TagWeight * tagTokens.Count(pathTokens.Contains);

        var titleTokens = new HashSet<string>(StringComparer.Ordinal);
        AddTokens(entry.Title, titleTokens);
        titleTokens.ExceptWith(tagTokens); // A word already counted as a tag must not be paid for twice.

        return score + titleTokens.Count(pathTokens.Contains);
    }

    /// <summary>
    /// Adds the lowercased, de-noised tokens of <paramref name="text"/> to <paramref name="sink"/>: split
    /// on every non-alphanumeric character, then split each run again on camel-case boundaries and keep
    /// both forms, so <c>CallbackPump.cs</c> contributes "callbackpump", "callback" and "pump".
    /// </summary>
    private static void AddTokens(string? text, HashSet<string> sink)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var run = new StringBuilder();
        foreach (var c in text + "\0")
        {
            if (char.IsLetterOrDigit(c))
            {
                _ = run.Append(c);
                continue;
            }

            if (run.Length > 0)
            {
                var word = run.ToString();
                Keep(word, sink);
                foreach (var part in SplitCamelCase(word))
                {
                    Keep(part, sink);
                }

                _ = run.Clear();
            }
        }
    }

    private static void Keep(string word, HashSet<string> sink)
    {
        var token = word.ToLowerInvariant();
        if (token.Length >= MinTokenLength && !Stopwords.Contains(token))
        {
            _ = sink.Add(token);
        }
    }

    private static IEnumerable<string> SplitCamelCase(string word)
    {
        var start = 0;
        for (var i = 1; i < word.Length; i++)
        {
            // A boundary is an upper-case letter that either follows a non-upper character, or ends an
            // upper-case run that is starting a new word ("HTTPServer" -> "HTTP", "Server").
            var boundary = char.IsUpper(word[i])
                && (!char.IsUpper(word[i - 1]) || (i + 1 < word.Length && char.IsLower(word[i + 1])));
            if (boundary)
            {
                yield return word[start..i];
                start = i;
            }
        }

        if (start > 0)
        {
            yield return word[start..];
        }
    }
}

/// <summary>
/// The rendered prior-knowledge block: its <paramref name="Text"/>, the entries that actually made it into
/// that text after the character budget was applied, and the entries refused because their path did not
/// resolve inside the Knowledge Base. All three are reported together so the caller can log what the
/// reviewer genuinely received AND what was withheld from it - a refusal nobody logs is indistinguishable
/// from a Knowledge Base that never held the entry.
/// </summary>
internal sealed record KnowledgeDigestBlock(
    string Text,
    IReadOnlyList<KnowledgeEntryMeta> Rendered,
    IReadOnlyList<KnowledgeEntryMeta> Rejected);

/// <summary>
/// The rendered <c>_toc.md</c> fallback block plus what it actually carried. <see cref="Listed"/> and
/// <see cref="Dropped"/> exist so the caller can log what the reviewer RECEIVED rather than the size of the
/// file that was read - once the block is budgeted, those two numbers stop being the same, and a log that
/// reports the read is the same silent-failure shape the ranked digest's proof-of-use line was added to fix.
/// <see cref="Truncated"/> is tracked separately because a table of contents with no recognisable entry
/// lines can be cut without <see cref="Dropped"/> ever moving off zero.
/// </summary>
internal sealed record KnowledgeTocBlock(
    string Text, int Listed, int Dropped, bool Truncated, IReadOnlyList<string> Refused);

/// <summary>
/// Knowledge Base entries split by whether their path resolves inside the Knowledge Base root.
/// <see cref="Refused"/> is carried rather than discarded because an entry that simply vanishes is
/// indistinguishable from one the Knowledge Base never held, and these were written by an LLM with file
/// tools - the refusal is the interesting signal, not the omission.
/// </summary>
internal sealed record KnowledgeContainmentPartition(
    IReadOnlyList<KnowledgeEntryMeta> Usable,
    IReadOnlyList<KnowledgeEntryMeta> Refused);
