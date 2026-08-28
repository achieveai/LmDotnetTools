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
    /// <summary>Weight of a tag hit against a CHANGED PATH. Tags are curated, so they are stronger evidence
    /// than title prose; a path is stronger evidence than the PR's own prose, so this is the heaviest term.
    /// <para>
    /// All four weights below are the historical 2/1 pair scaled by <see cref="ProseScale"/>, so a query with
    /// no prose ranks byte-identically to how it ranked before prose existed. That is deliberate: it makes
    /// the prose feature's effect on an existing corpus provable rather than asserted.
    /// </para></summary>
    private const int PathTagWeight = 4;

    /// <summary>Weight of a title-token hit against a changed path.</summary>
    private const int PathTitleWeight = 2;

    /// <summary>
    /// Weight of a tag hit against the PR's TITLE or DESCRIPTION — half its path-hit weight.
    /// <para>
    /// Half, not equal, because the two are different grades of evidence. A changed path is a fact about what
    /// the PR does; the title and description are the author's account of it, and an account can be generic,
    /// stale, templated, or simply long. Measured on a real run whose title named no pattern, adding prose at
    /// full strength pushed the CORRECT entry from rank 15 to 18: its own score stayed 0 while other entries
    /// absorbed the added generic vocabulary and overtook it. Prose can therefore DEMOTE a right answer, and
    /// this weight is what bounds how far.
    /// </para></summary>
    private const int ProseTagWeight = 2;

    /// <summary>Weight of a title-token hit against the PR's title or description.</summary>
    private const int ProseTitleWeight = 1;

    /// <summary>The factor the historical weights were scaled by to make room for a half-weight prose tier.</summary>
    private const int ProseScale = 2;

    /// <summary>
    /// How much of the PR's title + description is tokenized for ranking.
    /// <para>
    /// A bound on INFLUENCE, not a tuning knob. Description text is unbounded author prose, and every token
    /// it contributes is another chance for an unrelated entry to tie with or overtake a relevant one — the
    /// rank 15 -> 18 demotion above, scaled by length. Past some size prose stops being a signal and becomes
    /// a flood.
    /// </para>
    /// <para>
    /// <b>The threshold itself is NOT measured.</b> I had no distribution of PR description lengths to place
    /// it against, so it sits well above a normal title + summary and should be re-derived from real data
    /// before anyone treats it as tuned.
    /// </para></summary>
    private const int MaxProseCharsScored = 2048;

    /// <summary>
    /// The fraction of the retrieval budget spent on scope breadth: one slot for the best entry of each
    /// distinct scope, until a <c>1/ScopeReserveDivisor</c> share is used up.
    /// <para>
    /// A minority share on purpose. Relevance stays the main signal; this only guarantees that being
    /// second-best in an unpopular topic beats being twentieth-best in a popular one. At the shipped cap of 24
    /// that reserves 8 slots and leaves 16 to the raw ranking.
    /// </para></summary>
    private const int ScopeReserveDivisor = 3;

    /// <summary>Shortest token worth matching; below this, path noise ("cs", "lm") dominates.</summary>
    private const int MinTokenLength = 3;

    /// <summary>
    /// The characters CommonMark lets a backslash escape. Anything else after a backslash is a literal
    /// backslash, which on a link destination is far more likely to be a Windows path separator.
    /// </summary>
    private const string EscapableAsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

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
    /// Ranks <paramref name="entries"/> against what the PR touches and says, and returns at most
    /// <paramref name="maxEntries"/> of them, best first. An entry scores on tag and title tokens shared with
    /// the changed paths, and at half weight on tokens shared with <paramref name="prTitle"/> /
    /// <paramref name="prDescription"/>.
    /// <para>
    /// The prose half exists because sibling PRs on one architectural pattern frequently share NO path tokens
    /// at all — different files, same mistake — while the pattern is named in the title. Keyed on paths alone,
    /// the same defect retrieved different knowledge on each sibling and was blocked on one PR and declined as
    /// out of scope on the other, five times against six inside a single 11.3-hour window.
    /// </para>
    /// <para>
    /// Prose is NOT free. On a run whose title named no pattern, adding it demoted the correct entry from rank
    /// 15 to 18 — the gain depends on incidental vocabulary overlap, and a generic description can lift
    /// unrelated entries past a relevant one that matched nothing. <see cref="ProseTagWeight"/> and
    /// <see cref="MaxProseCharsScored"/> bound that, and the scope reserve below is what stops it from
    /// deciding the whole result.
    /// </para>
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
        string? prTitle,
        string? prDescription,
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

        var proseTokens = new HashSet<string>(StringComparer.Ordinal);
        AddTokens(Clamp(prTitle), proseTokens);
        AddTokens(Clamp(prDescription), proseTokens);

        // A token the PR both TOUCHES and TALKS ABOUT is paid at the path rate, once. Leaving it in both sets
        // would pay 1.5x for the strongest possible evidence and quietly re-tune every existing weight.
        proseTokens.ExceptWith(pathTokens);

        var byScore = entries
            .OrderByDescending(entry => Score(entry, pathTokens, proseTokens))
            .ThenByDescending(entry => entry.Updated, StringComparer.Ordinal)
            .ThenBy(entry => entry.File, StringComparer.Ordinal)
            .ToList();

        return [.. ReserveScopeBreadth(byScore, maxEntries)];
    }

    /// <summary>The prefix of <paramref name="text"/> that is worth tokenizing — see
    /// <see cref="MaxProseCharsScored"/>.</summary>
    private static string? Clamp(string? text) =>
        text is { Length: > MaxProseCharsScored } ? text[..MaxProseCharsScored] : text;

    /// <summary>
    /// Takes <paramref name="maxEntries"/> from <paramref name="byScore"/> (already best-first), but spends
    /// the first <see cref="ScopeReserveDivisor"/>th of the budget on the best entry of each DISTINCT scope
    /// before letting the raw ranking fill the rest.
    /// <para>
    /// Keyed on scope and not on tags, and that is the whole reason it can work. The tag vocabulary is
    /// free-form — 105 distinct tags across 34 entries, 80 of them appearing exactly once — so there is no
    /// stable set of buckets to reserve slots for. <c>scope</c> is a controlled vocabulary of 13 topic values,
    /// which is what makes "one slot each, best first" a statement about coverage rather than about whichever
    /// words the last extraction happened to invent.
    /// </para>
    /// <para>
    /// It matters because retrieval is SATURATED: 35 of 35 measured briefs rendered 23-24 entries against a
    /// 24-entry cap, on all five repositories. At saturation the tail is always being cut, so a single
    /// dominant topic can hold every slot and the reviewer never sees the one lesson from elsewhere that
    /// would have settled the question. Raising the cap does not fix that — it is a keying problem wearing a
    /// capacity symptom, and the index cap it would push against is nowhere near hit.
    /// </para>
    /// <para>
    /// Breadth is bought with a MINORITY of the budget on purpose. Relevance is still the main signal; this
    /// only guarantees that being second-best in an unpopular topic beats being twentieth-best in a popular
    /// one. Entries with a blank scope share the single "unscoped" bucket rather than each counting as their
    /// own topic, which would otherwise turn an unscoped KB into pure round-robin.
    /// </para>
    /// </summary>
    private static List<KnowledgeEntryMeta> ReserveScopeBreadth(
        List<KnowledgeEntryMeta> byScore,
        int maxEntries)
    {
        if (byScore.Count <= maxEntries)
        {
            return byScore;
        }

        var reserve = maxEntries / ScopeReserveDivisor;
        if (reserve == 0)
        {
            return [.. byScore.Take(maxEntries)];
        }

        var seenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in byScore)
        {
            if (reserved.Count == reserve)
            {
                break;
            }

            var scope = string.IsNullOrWhiteSpace(entry.Scope) ? UnscopedBucket : entry.Scope;
            if (seenScopes.Add(scope))
            {
                _ = reserved.Add(entry.File);
            }
        }

        // One pass in RANK order, keeping a slot back for every reserved entry not yet taken. The reserved
        // entries therefore keep their rank position instead of being promoted to the front: this decides
        // membership, not order, and reshuffling the digest would move entries for a reason no reader could
        // see from the rendered block.
        var outstanding = reserved.Count;
        var selected = new List<KnowledgeEntryMeta>(maxEntries);
        foreach (var entry in byScore)
        {
            if (selected.Count == maxEntries)
            {
                break;
            }

            if (reserved.Contains(entry.File))
            {
                selected.Add(entry);
                outstanding--;
            }
            else if (maxEntries - selected.Count > outstanding)
            {
                selected.Add(entry);
            }
        }

        return selected;
    }

    /// <summary>The bucket a blank-scoped entry falls in. Shared by all of them, so an unscoped Knowledge Base
    /// reserves ONE slot rather than turning the whole selection into round-robin.</summary>
    private const string UnscopedBucket = "(unscoped)";

    /// <summary>
    /// Collapses entries that name the SAME Knowledge Base file down to one record each, keyed on the
    /// canonical path <see cref="TryResolveEntryPath"/> produces rather than on the raw
    /// <see cref="KnowledgeEntryMeta.File"/> string — otherwise <c>a/../b.md</c> and <c>b.md</c> stay
    /// distinct and the duplicate survives wearing the one disguise a model-authored path most easily puts
    /// on. Run AFTER containment and sanitization and BEFORE the retrieval cap.
    /// <para>
    /// Not a tidiness pass. <c>_index.jsonl</c> is append-structured and written by an LLM with file tools,
    /// and "the file was concatenated with itself" is a shape the parser already anticipates
    /// (<see cref="KnowledgeIndex.MaxIndexRecords"/>). Identical paths score identically, so the copies sort
    /// adjacent and take consecutive slots: a doubled 20-entry index fills all 24 retrieval slots with 12
    /// files and drops 8 distinct lessons the reviewer needed, while the block reports a full digest. That is
    /// a correctness failure wearing a green badge, not a size problem.
    /// </para>
    /// <para>
    /// The newest record wins, ties going to the first seen, and winners keep their first-appearance order
    /// because the ranking below is stable and a reshuffle here would move entries for no stated reason.
    /// <see cref="KnowledgeDeduplication.Collapsed"/> carries every discarded record, and
    /// <see cref="KnowledgeDeduplication.Conflicting"/> the kept record for each path whose copies
    /// DISAGREED — repetition is merely a doubled index, but disagreement means a torn or half-merged one,
    /// where whichever copy loses is knowledge the reviewer silently will not see.
    /// </para>
    /// An entry whose path does not resolve is keyed on its raw <c>File</c> instead of being dropped:
    /// containment upstream owns that refusal, and inventing a second, quieter one here would delete an
    /// entry with nothing reporting that it went.
    /// </summary>
    public static KnowledgeDeduplication Deduplicate(
        IReadOnlyList<KnowledgeEntryMeta> entries, string knowledgeBaseRoot)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        if (entries.Count == 0)
        {
            return new KnowledgeDeduplication(entries, [], []);
        }

        var winners = new Dictionary<string, KnowledgeEntryMeta>(StringComparer.Ordinal);
        var order = new List<string>();
        var collapsed = new List<KnowledgeEntryMeta>();
        var conflicted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var key = TryResolveEntryPath(knowledgeBaseRoot, entry.File, out var resolved)
                ? resolved
                : entry.File ?? string.Empty;

            if (!winners.TryGetValue(key, out var kept))
            {
                winners.Add(key, entry);
                order.Add(key);
                continue;
            }

            if (!SaysTheSameThing(kept, entry))
            {
                _ = conflicted.Add(key);
            }

            // Newest wins; a tie leaves the incumbent in place, so a doubled index keeps the copy that was
            // read first and the result does not depend on which half of the concatenation it came from.
            if (string.CompareOrdinal(entry.Updated, kept.Updated) > 0)
            {
                winners[key] = entry;
                collapsed.Add(kept);
                continue;
            }

            collapsed.Add(entry);
        }

        if (collapsed.Count == 0)
        {
            return new KnowledgeDeduplication(entries, [], []);
        }

        return new KnowledgeDeduplication(
            [.. order.Select(key => winners[key])],
            collapsed,
            [.. conflicted.Select(key => winners[key])]);
    }

    /// <summary>
    /// Whether two records for one path carry the same metadata. Compared field by field because
    /// <see cref="KnowledgeEntryMeta"/> holds its tags in arrays, and record equality over an array is
    /// reference equality — every record read back from <c>_index.jsonl</c> would then "disagree" with every
    /// other, and a warning that fires on every duplicate says nothing about the one that matters.
    /// </summary>
    private static bool SaysTheSameThing(KnowledgeEntryMeta left, KnowledgeEntryMeta right) =>
        string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.Scope, right.Scope, StringComparison.Ordinal)
        && string.Equals(left.Updated, right.Updated, StringComparison.Ordinal)
        && left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal)
        && left.SourcePrs.SequenceEqual(right.SourcePrs, StringComparer.Ordinal);

    /// <summary>
    /// Renders <paramref name="entries"/> as the prior-knowledge block, resolving each entry's KB-relative
    /// <see cref="KnowledgeEntryMeta.File"/> against <paramref name="knowledgeBaseRoot"/> into an absolute
    /// path the agent can Read directly. Every entry is resolved BEFORE the budget is applied, so an entry
    /// that escapes the Knowledge Base is refused whether or not it would have fitted. An entry whose path
    /// is sound but whose title, tags or scope carry an escaping link is KEPT with that field cleared and
    /// reported through <see cref="KnowledgeDigestBlock.Neutralized"/> — see
    /// <see cref="ClearEscapingMetadata"/> for why the two cases end differently. Entries are then
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
        var neutralized = new List<KnowledgeEntryMeta>();
        foreach (var entry in entries)
        {
            if (!TryResolveEntryPath(knowledgeBaseRoot, entry.File, out var absolute))
            {
                rejected.Add(entry);
                continue;
            }

            // The path cleared, so the entry stays - but its OTHER fields are written by the same agent and
            // are rendered verbatim, so they are cleaned rather than trusted. Reported whether or not this
            // entry later fits the budget: unlike Rendered, this is not a claim about what the reviewer
            // received, it is a defect report about what the extraction agent wrote, and that is true
            // regardless of how much room was left by the time we got here.
            var safe = ClearEscapingMetadata(entry, knowledgeBaseRoot);
            if (!ReferenceEquals(safe, entry))
            {
                neutralized.Add(entry);
            }

            resolved.Add((safe, absolute));
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
                rejected,
                neutralized);
        }

        return new KnowledgeDigestBlock(
            builder.Append(missing > 0 ? Footer(missing, knowledgeBaseRoot) : string.Empty).ToString(),
            rendered,
            rejected,
            neutralized);
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
    /// Clears escaping links out of every entry's title, tags and scope, returning the cleaned entries in
    /// their original order alongside the ORIGINALS of the ones that changed.
    /// <para>
    /// This runs BEFORE ranking, not inside <see cref="Render"/>, because the ranking reads exactly the
    /// fields the cleaning deletes. An entry whose only match for a changed path is a tag like
    /// <c>[runner](../../../etc/passwd)</c> scores on that tag, takes a retrieval slot, and then has the tag
    /// stripped on the way out — so the delivered set does not contain the relevance that justified selecting
    /// it, while a clean entry that genuinely matched was pushed past the cap. Same crowding-out as ranking
    /// before containment, with a field deletion in place of an entry rejection.
    /// </para>
    /// <para>
    /// Note this CLEANS, it does not filter: every entry passed in comes back, so no knowledge is lost to a
    /// bad tag. Refusing the entry would delete sound knowledge over a link in a field the reviewer does not
    /// need — the distinction the twin route earned, where the link IS the entry and refusal is the only
    /// option left.
    /// </para>
    /// </summary>
    public static KnowledgeSanitizedEntries SanitizeMetadata(
        IReadOnlyList<KnowledgeEntryMeta> entries, string knowledgeBaseRoot)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        var cleaned = new List<KnowledgeEntryMeta>(entries.Count);
        var neutralized = new List<KnowledgeEntryMeta>();
        foreach (var entry in entries)
        {
            var safe = ClearEscapingMetadata(entry, knowledgeBaseRoot);
            if (!ReferenceEquals(safe, entry))
            {
                neutralized.Add(entry);
            }

            cleaned.Add(safe);
        }

        return new KnowledgeSanitizedEntries(cleaned, neutralized);
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
            return new KnowledgeTocBlock(string.Empty, 0, 0, false, [], 0);
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

        // Refused ENTRY LINES, not refused links: the total below counts entry lines, so only a refusal that
        // removes one of those may be subtracted from it. One line can carry more than one escaping
        // destination, and containment now runs on prose and other-shaped lines that were never counted as
        // entries at all - subtract either and the dropped count goes negative. Every bad link is still
        // reported; that is what the operator needs and it is a different question from the arithmetic.
        var refusedLines = 0;

        // Entry lines already listed, by the canonical path their link resolves to - the SAME key the ranked
        // route deduplicates on, because the two routes carry the same store into the same prompt and a
        // guarantee that holds on only one of them is the recurring defect on this path. A _toc.md merged
        // badly repeats its entries verbatim, and here every repeat also spends characters out of a budget
        // the honest entries then fail to fit inside.
        var listedFiles = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;

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
                // A reference-style link is refused BEFORE the scan, because the scan cannot see it. Every
                // check below keys on "](", and "See [a][outside]" has none — nor does the "[outside]:
                // ../../../etc/passwd" that gives it a destination. The scan returns zero links, the escape
                // filter finds nothing to object to, and both lines are printed verbatim to an agent that
                // resolves them properly. That is the "nothing to check reads as clean input" failure once
                // more, and failing closed on undelimitable destinations does not reach it: nothing here is
                // undelimitable, there is simply nothing to delimit.
                //
                // Refused rather than supported. This route is the DEGRADED one — it runs when _index.jsonl
                // is missing or torn — and its whole contract is that a link is printed verbatim for the
                // agent to join itself. Resolving a reference to its definition would mean implementing the
                // half of CommonMark that binds them, on the path we trust least, to render a form our own
                // generator never emits. Both halves go, because either one alone still hands the agent a
                // live reference. Reported raw, since the destination and the label live on different lines
                // and neither half is a link we could name on its own.
                if (CarriesAReferenceStyleLink(line))
                {
                    refused.Add(line.Trim());
                    if (IsTocEntry(line))
                    {
                        refusedLines++;
                    }

                    continue;
                }

                var links = TocLinks(line);
                var escapes = links
                    .Where(link => !link.Delimited || !IsLinkTheAgentCanSafelyJoin(link.Destination, root))
                    .Select(link => link.Destination)
                    .ToList();
                if (escapes.Count > 0)
                {
                    refused.AddRange(escapes);
                    if (IsTocEntry(line))
                    {
                        refusedLines++;
                    }

                    continue;
                }

                // Judged after containment and before the budget, in that order and for the same reasons the
                // ranked route uses: a refused line is not evidence its file was listed, and a duplicate must
                // not be allowed to spend room a first sighting still needs.
                //
                // Decided over EVERY file the line names, not over its first one. Keying on links[0] and
                // acting by dropping the whole line discarded "- [Alpha again](system/alpha.md) see also
                // [Beta](system/beta.md)" entirely once alpha was listed: beta reached the reviewer from
                // nowhere else, and counting the line as a duplicate rather than as dropped meant the footer
                // promised no route to it either - gone from the block and from both sides of the ledger. A
                // line is not a file, which is the reasoning that moved the marking below the budget check
                // one scope out. So a line is redundant only when every file it names is already in the
                // block, and it is kept WHOLE when it names a new one rather than edited down to the new
                // part: rewriting a model-authored line is how FitTocLine came to misattribute a title to
                // another entry's link, and one repeated destination costs a duplicated path where the edit
                // risks a wrong one.
                var entryFiles = new List<string>();
                if (IsTocEntry(line))
                {
                    foreach (var link in links)
                    {
                        if (TryResolveEntryPath(root, link.Destination, out var entryPath))
                        {
                            entryFiles.Add(entryPath);
                        }
                    }

                    if (entryFiles.Count > 0 && entryFiles.All(listedFiles.Contains))
                    {
                        duplicates++;
                        continue;
                    }
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

                    // Recorded only once the line is in the block. Marking it above would let a first
                    // sighting that the budget cut still silence its own copy, and the file would then be
                    // neither listed nor dropped - present in the count of what the reviewer received, absent
                    // from the block, and absent from the footer that promises a route back to it.
                    //
                    // Every file the line named, for the same reason the check above reads them all: marking
                    // only the first left the file behind a second link rendered but unmarked, so the next
                    // line naming it was rendered again - the doubled table spending budget on a path the
                    // reviewer already had.
                    foreach (var entryFile in entryFiles)
                    {
                        _ = listedFiles.Add(entryFile);
                    }
                }
            }
        }
        else
        {
            truncated = true;
        }

        // A refused entry is a different fact from one that did not fit, so it is subtracted from the total
        // rather than counted as dropped: a footer promising "1 more entry" in _toc.md would route the agent
        // straight back to the link just refused. A duplicate is subtracted for the same reason - the file it
        // names is already in the block above, so counting it would promise a route to a line already read.
        var dropped = total - listed - refusedLines - duplicates;
        if (builder.Length == 0)
        {
            return new KnowledgeTocBlock(string.Empty, 0, dropped, true, refused, duplicates);
        }

        var closing = dropped > 0 ? Footer(dropped, knowledgeBaseRoot)
            : truncated ? TruncatedNotice(knowledgeBaseRoot)
            : string.Empty;
        return new KnowledgeTocBlock(
            builder.Append(closing).ToString(), listed, dropped, truncated, refused, duplicates);
    }

    /// <summary>
    /// The block to render when every Knowledge Base listing was REFUSED for size, so the reviewer receives no
    /// prior knowledge from a store that has some.
    /// <para>
    /// Under <see cref="Heading"/>, and that is the whole point of it existing. The review prompt teaches the
    /// absence of that heading as "this repository has no Knowledge Base", so staying silent here would not
    /// merely withhold the knowledge — it would state, in the one channel the agent has been taught to read,
    /// something false about the store. A log line says this to an operator who is not in the loop; only the
    /// input says it to the reviewer, which is the only party that acts on it.
    /// </para>
    /// </summary>
    /// <param name="refusedPaths">Agent-facing paths of the listings that were refused (at least one).</param>
    /// <param name="knowledgeBaseRoot">The Knowledge Base root as the AGENT resolves it.</param>
    /// <param name="maxBytes">The ceiling the listings exceeded.</param>
    public static string RenderRefusedListings(
        IEnumerable<string> refusedPaths, string knowledgeBaseRoot, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(refusedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseRoot);

        var paths = refusedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        return $"{Heading}\n\n"
            + $"This repository HAS a Knowledge Base and none of it could be loaded for this review: "
            + $"{string.Join(" and ", paths)} exceeded the {maxBytes:N0}-byte limit this daemon reads listings "
            + "with, and were refused whole rather than read in part — half a listing ends mid-record and would "
            + "name entries that do not exist.\n\n"
            + "Do NOT read this as \"there are no prior lessons\". Entries exist under "
            + $"{knowledgeBaseRoot.TrimEnd('/')}/ and are unread here; if the rest of your input names one, you "
            + "may open that file yourself. Otherwise review without prior knowledge, and say that you did.\n";
    }

    /// <summary>
    /// Whether text carries either half of a CommonMark reference-style link: <c>[text][label]</c>, or the
    /// <c>[label]: destination</c> definition that resolves one.
    /// <para>
    /// Deliberately coarse, because it is a REFUSAL gate and never a parser. A false positive costs one
    /// refused <c>_toc.md</c> line - in a block that already reports everything it drops - or one cleared
    /// metadata value that falls back to a blank; a false negative hands the agent a live link nothing else
    /// on either path will look at. A title that genuinely contains <c>][</c> is cleared and counted; that is
    /// the cheaper mistake by a wide margin.
    /// </para>
    /// </summary>
    private static bool CarriesAReferenceStyleLink(string text)
    {
        if (text.Contains("][", StringComparison.Ordinal))
        {
            return true;
        }

        // A link reference definition: "[label]:" at the start of a line, the only place CommonMark
        // recognises one. The destination that follows is what a label elsewhere resolves to. EVERY line is
        // examined rather than only the first, because a metadata value is not one line by construction:
        // _index.jsonl is JSON, "\n" is an ordinary character inside a JSON string, and RenderEntry
        // interpolates the value as it stands - so a title can put a definition at the start of a rendered
        // line. On the _toc.md route the input is already split and the loop simply runs once.
        //
        // "Start of a line" means after any block-container markers, not after the indentation alone.
        // TrimStart left a ">" or a bullet in front, so the first character was not "[" and the line was
        // never examined - while CommonMark reads a definition inside a block quote or a list item exactly
        // as it reads one at column zero. The markers are STRIPPED rather than treated as evidence, because
        // "- [Alpha](system/alpha.md)" is every ordinary entry in the file and refusing lines for wearing a
        // bullet would empty the block; what makes a definition a definition is the ":" after its label.
        foreach (var line in text.Split('\n'))
        {
            // A label that does not open and close on this line. CommonMark lets a link label span lines, so
            // "[foo\nbar]: ../../../etc/passwd" is a perfectly ordinary definition and "[foo\nbar]" is the
            // shortcut reference that resolves to it - while every check written per line looked straight
            // past both halves. Line one has no "]" to find, line two does not begin with "[", and neither
            // carries "][". The definition and its use were printed verbatim to an agent that reads
            // CommonMark properly, which is the whole failure this gate exists to prevent, spelled across two
            // lines instead of one.
            //
            // Balance, not reassembly. Joining the lines back up and re-running the rules would mean deciding
            // where a label really ends - continuation rules, blank lines, block boundaries - which is the
            // parser this is documented never to become, written for the input we trust least. A "[" left
            // open when the line ends, or a "]" that closes nothing, is enough to say "a label may continue
            // past this line" without saying where it goes. Our own generator emits balanced lines, so the
            // refusal costs no real retrieval.
            if (ALabelMayContinuePastThisLine(line))
            {
                return true;
            }

            var trimmed = StripBlockContainerMarkers(line);
            if (trimmed.IsEmpty || trimmed[0] != '[')
            {
                continue;
            }

            // The label's closing bracket is the first UNESCAPED one: a label may contain "\]", and reading
            // the escaped bracket as the end of the label made "[foo\]]: ../../../etc/passwd" look like
            // prose, because the character after it is "]" rather than ":". The shortcut reference that
            // resolves to it carries no "][" either, so neither half of this gate fired on either line.
            var close = IndexOfUnescaped(trimmed, ']', 1);
            if (close > 0 && close + 1 < trimmed.Length && trimmed[close + 1] == ':')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a link label on this line may continue onto the next: a <c>[</c> still open when the line
    /// ends, or a <c>]</c> that closes nothing and does not open an inline destination. Escaped brackets are
    /// skipped, matching <see cref="IndexOfUnescaped"/> — a label may legitimately contain <c>\[</c>.
    /// <para>
    /// The <c>](</c> exemption is what keeps the rule from eating ordinary entries. A destination may contain
    /// a <c>]</c> of its own — <c>[title](&lt;system/a](b.md&gt;)</c> is one link, not a broken label — and
    /// FitTocLine already has a pin standing on exactly that line. A closer followed by anything else is
    /// evidence the opener was on an earlier line, which is the one thing a per-line reading cannot
    /// otherwise see.
    /// </para>
    /// </summary>
    private static bool ALabelMayContinuePastThisLine(ReadOnlySpan<char> line)
    {
        var depth = 0;
        for (var scan = 0; scan < line.Length; scan++)
        {
            if (line[scan] == '\\')
            {
                scan++;
            }
            else if (line[scan] == '[')
            {
                depth++;
            }
            else if (line[scan] == ']')
            {
                if (depth > 0)
                {
                    depth--;
                }
                else if (scan + 1 >= line.Length || line[scan + 1] != '(')
                {
                    return true; // Closes a label opened on an earlier line.
                }
            }
        }

        return depth > 0;
    }

    /// <summary>
    /// A line with its leading indentation and any block-container markers removed: block quotes
    /// (<c>&gt;</c>), bullet list items (<c>-</c>, <c>*</c>, <c>+</c>) and ordered list items
    /// (<c>1.</c>, <c>1)</c>), in any nesting.
    /// </summary>
    private static ReadOnlySpan<char> StripBlockContainerMarkers(ReadOnlySpan<char> line)
    {
        var text = line.TrimStart();
        while (true)
        {
            if (!text.IsEmpty && text[0] == '>')
            {
                text = text[1..].TrimStart();
                continue;
            }

            if (text.Length > 1 && text[0] is '-' or '*' or '+' && (text[1] == ' ' || text[1] == '\t'))
            {
                text = text[2..].TrimStart();
                continue;
            }

            var digits = 0;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            {
                digits++;
            }

            if (digits > 0
                && digits + 1 < text.Length
                && text[digits] is '.' or ')'
                && (text[digits + 1] == ' ' || text[digits + 1] == '\t'))
            {
                text = text[(digits + 2)..].TrimStart();
                continue;
            }

            return text;
        }
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
    /// <para>
    /// Judged on EVERY reading of the text, because the spelling is genuinely ambiguous here and we do not
    /// control which reader the agent is. To CommonMark <c>x\)/../../secrets.md</c> is one path containing a
    /// <c>)</c>; to a Windows path resolver <c>..\..\outside.md</c> is traversal. Resolve the escapes and the
    /// second becomes the harmless <c>....\outside.md</c>; leave them and the first is judged as a name
    /// ending in a separator. Neither reading is wrong, so the link has to be safe under both — an ambiguity
    /// that can be read two ways is not licence to pick the reading that lets it through.
    /// </para>
    /// <para>
    /// Two REFUSALS stand in front of those readings, for forms we decline to interpret at all rather than
    /// try to read correctly. An ampersand may be a character reference, which CommonMark resolves inside a
    /// destination — <c>&amp;sol;etc/passwd</c> is <c>/etc/passwd</c> to the agent and an ordinary contained
    /// relative path to every test below. We decode none of them and refuse all of them; the reasoning is at
    /// <see cref="TryResolveEntryPath"/>, which is where the rule lives so that both routes reach it. An
    /// angle bracket INSIDE an extracted destination is refused for the neighbouring reason: an angle-
    /// delimited destination may not contain an unescaped <c>&lt;</c>, so a destination that still carries
    /// one is not a destination to any conformant reader — <c>&lt;system/ok.md&lt;[b](../../../etc/passwd)&gt;</c>
    /// parses as no outer link at all and renders the NESTED link on its own. The brackets that delimit a
    /// destination are unwrapped before this point, so what is left is the inside; rewriting the extent scan
    /// that produced it belongs to the conformant-parser work, and refusing its output does not.
    /// </para>
    /// </summary>
    private static bool IsLinkTheAgentCanSafelyJoin(string link, string knowledgeBaseRoot) =>
        !link.Contains('<', StringComparison.Ordinal)
        && !link.Contains('>', StringComparison.Ordinal)
        && IsResolvedLinkSafeToJoin(link, knowledgeBaseRoot)
        && IsResolvedLinkSafeToJoin(Unescape(link), knowledgeBaseRoot);

    /// <summary>One reading of a link destination, judged by the shared containment rule.</summary>
    private static bool IsResolvedLinkSafeToJoin(string link, string knowledgeBaseRoot) =>
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
    /// One Markdown link found on a <c>_toc.md</c> line: the destination an agent would resolve, the offset
    /// of the <c>](</c> that opened it, so no later caller has to go looking for it a second time, and
    /// whether the destination could be delimited at all.
    /// <para>
    /// <see cref="Delimited"/> is <c>false</c> for a destination that is never closed. It is reported as a
    /// link rather than dropped because dropping it is precisely the failure: a caller that sees no links
    /// concludes there is nothing to check. An undelimited destination fails every containment rule by
    /// construction — we cannot say where it ends, so we cannot say what the agent resolves.
    /// </para>
    /// </summary>
    private readonly record struct TocLink(string Destination, int Marker, bool Delimited);

    /// <summary>
    /// Every link destination on a <c>_toc.md</c> line, normalized, in source order.
    /// <para>
    /// Every one, not the last one. The previous reading took <c>LastIndexOf("](")</c>, so a line carrying
    /// two links had one of them checked and both of them rendered — the escape only had to not be written
    /// last. The check was right both times it was defeated; what reached it was not.
    /// </para>
    /// <para>
    /// Every LINE, too. This was once gated on <see cref="IsTocEntry"/>, which recognises the single shape
    /// our own generator emits, on the premise that anything else is a heading or prose and carries no path.
    /// That premise is false — <c>See [notes](../../secrets.md).</c> is prose and carries one — and beside
    /// the point for an indented entry, a <c>*</c> bullet or an ordered list, which are the same entry in
    /// ordinary Markdown clothing. The renderer prints every line that fits, so every line is parsed.
    /// </para>
    /// <para>
    /// And every line yields a link or a refusal, never an empty list on syntax it could not read. A
    /// destination that is never closed used to parse to NOTHING, and nothing is indistinguishable from a
    /// clean line to every caller — so <c>- [a](../../../etc/passwd</c> was emitted verbatim without the
    /// containment rule ever being consulted. A parse failure that reads as clean input defeats the rule
    /// without ever reaching it.
    /// </para>
    /// <para>
    /// This is the only place the syntax is read. Callers get the offsets back rather than re-deriving them,
    /// because a second parser over the same syntax is a second set of assumptions to be wrong about, and
    /// this one has already been wrong twice.
    /// </para>
    /// </summary>
    private static IReadOnlyList<TocLink> TocLinks(string line)
    {
        var links = new List<TocLink>();
        var at = 0;
        while (true)
        {
            var marker = line.IndexOf("](", at, StringComparison.Ordinal);
            if (marker < 0)
            {
                break;
            }

            // CommonMark permits whitespace between the "](" and the destination, so the angle form is not
            // always sitting at this offset. Reading the form off the first character after the marker made
            // "]( <a)/../../../etc/passwd> )" look bare, which ends it at the first ")" - back to validating
            // "<a" and unwrapping it to the contained name "a".
            var open = marker + "](".Length;
            while (open < line.Length && (line[open] == ' ' || line[open] == '\t'))
            {
                open++;
            }

            if (!TryEndOfDestination(line, open, out var textEnd, out var next))
            {
                // Everything after an unreadable destination is unreadable too: we do not know whether the
                // next "](" is a second link or part of this one, so we stop rather than guess. Reported RAW
                // rather than normalized, because normalizing means reading it as a form we just established
                // it is not in: "<ok.md> [b](../../../etc/passwd" would be reported as the contained
                // "ok.md", which is a refusal log naming the one part of the line that was fine.
                links.Add(new TocLink(line[open..].TrimEnd(), marker, false));
                break;
            }

            // A BARE destination cannot contain whitespace, so what follows a space inside one is either a
            // title or nothing a Markdown reader can parse - and only the second case ever reaches an agent.
            // "[a](system/ok.md [b](../../../etc/passwd))" has no valid title after "system/ok.md": a title
            // is quoted or parenthesised and "[b](...)" is neither, so the OUTER link does not parse and the
            // nested one renders on its own. Splitting at the space validated "system/ok.md" and handed the
            // agent a line whose only real link was the one we never looked at. Refused rather than split,
            // and reported whole, so the log names the part that matters. This check is now defensive rather
            // than load-bearing for a title: <see cref="TryEndOfDestination"/> stops the bare destination at
            // the first unescaped whitespace, so a well-formed title never reaches this far - only a bare
            // destination followed by something that is not a valid title still lands here as whitespace.
            var raw = line[open..textEnd].Trim();
            if (raw.Length > 0 && raw[0] != '<' && raw.AsSpan().IndexOfAny(' ', '\t') >= 0)
            {
                links.Add(new TocLink(raw, marker, false));
                break;
            }

            links.Add(new TocLink(NormalizeLinkDestination(line[open..textEnd]), marker, true));
            at = next;
        }

        return links;
    }

    /// <summary>
    /// Where the destination beginning at <paramref name="open"/> ends (<paramref name="textEnd"/>,
    /// exclusive) and where the link after it resumes (<paramref name="next"/>), or <c>false</c> when the
    /// destination is never closed.
    /// <para>
    /// Which form the destination is in has to be settled BEFORE deciding where it ends, because the two
    /// forms end on different characters. Cutting at the first <c>)</c> and unwrapping whatever that
    /// produced is the containment defect one layer down: <c>&lt;a)/../../../etc/passwd&gt;</c> cuts to
    /// <c>&lt;a</c>, which unwraps to the contained name <c>a</c> and is ACCEPTED, while an agent resolving
    /// CommonMark reads the whole angle-bracketed path and walks out of the store. Inside <c>&lt;…&gt;</c> a
    /// <c>)</c> is an ordinary character; bare, it terminates — but only when it is neither backslash-escaped
    /// nor balancing a <c>(</c> opened inside the destination, both of which CommonMark keeps in the path.
    /// Three separate ways for our end to land before the agent's, each leaving a contained prefix in front
    /// of the rule and the whole path in front of the agent.
    /// </para>
    /// <para>
    /// Neither form closes the LINK the moment its destination ends — CommonMark allows an optional title
    /// between the destination and the closing <c>)</c>, itself optionally surrounded by whitespace. Both
    /// branches hand that off to <see cref="TryCloseLink"/> once the destination boundary is settled, rather
    /// than each re-deriving title syntax. #258.
    /// </para>
    /// </summary>
    private static bool TryEndOfDestination(string line, int open, out int textEnd, out int next)
    {
        textEnd = -1;
        next = -1;
        if (open < line.Length && line[open] == '<')
        {
            var closingAngle = IndexOfUnescaped(line, '>', open + 1);
            if (closingAngle < 0)
            {
                return false;
            }

            textEnd = closingAngle + 1;
            return TryCloseLink(line, textEnd, out next);
        }

        var depth = 0;
        var scan = open;
        for (; scan < line.Length; scan++)
        {
            var character = line[scan];
            if (character == '\\')
            {
                scan++;
                continue;
            }

            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                if (depth == 0)
                {
                    textEnd = scan;
                    return TryCloseLink(line, scan, out next);
                }

                depth--;
                continue;
            }

            if (depth == 0 && (character == ' ' || character == '\t'))
            {
                // A bare destination may not contain unescaped whitespace, so this is where it ends -
                // whatever comes next is either an optional title or nothing a Markdown reader can parse.
                textEnd = scan;
                return TryCloseLink(line, scan, out next);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the link resumes, one way or another, starting at <paramref name="afterDestination"/> - the
    /// first position after the destination has already been delimited - and if so where it resumes
    /// (<paramref name="next"/>, exclusive of the closing <c>)</c>).
    /// <para>
    /// Between a destination and the <c>)</c> that closes its link, CommonMark permits whitespace, an
    /// optional title, and more whitespace, in that order. A title is quoted (<c>"…"</c> or <c>'…'</c>) or
    /// parenthesised (<c>(…)</c>); anything else found here - a second link's <c>[</c>, an unquoted word - is
    /// not a title CommonMark recognises, so the link fails to parse and we report it undelimited rather than
    /// guess at where it might have meant to close.
    /// </para>
    /// </summary>
    private static bool TryCloseLink(string line, int afterDestination, out int next)
    {
        next = -1;
        var scan = afterDestination;
        while (scan < line.Length && (line[scan] == ' ' || line[scan] == '\t'))
        {
            scan++;
        }

        if (scan < line.Length && line[scan] == ')')
        {
            next = scan + 1;
            return true;
        }

        if (scan >= line.Length || (line[scan] != '"' && line[scan] != '\'' && line[scan] != '('))
        {
            return false;
        }

        if (!TryConsumeTitle(line, scan, out var titleEnd))
        {
            return false;
        }

        scan = titleEnd;
        while (scan < line.Length && (line[scan] == ' ' || line[scan] == '\t'))
        {
            scan++;
        }

        if (scan >= line.Length || line[scan] != ')')
        {
            return false;
        }

        next = scan + 1;
        return true;
    }

    /// <summary>
    /// Consumes a CommonMark link title beginning at <paramref name="start"/> - which must be one of
    /// <c>"</c>, <c>'</c>, or <c>(</c> - and reports where it ends (<paramref name="end"/>, exclusive of the
    /// closing delimiter), or <c>false</c> when it is never closed.
    /// <para>
    /// The title is not read into the rendered output; only its EXTENT matters here, so that whatever
    /// follows it - whitespace, then the closing <c>)</c> - is found at the right offset rather than inside
    /// what a reader would treat as title text. A backslash escapes the next character unconditionally,
    /// including the delimiter itself, so a quoted title may contain an escaped quote of its own kind. A
    /// parenthesised title may not contain an unescaped <c>(</c> at all - CommonMark does not allow nesting
    /// there the way a bare destination allows balanced parens - so one ends the attempt rather than being
    /// counted toward a matching close.
    /// </para>
    /// </summary>
    private static bool TryConsumeTitle(string line, int start, out int end)
    {
        end = -1;
        var opener = line[start];
        var closer = opener == '(' ? ')' : opener;

        for (var scan = start + 1; scan < line.Length; scan++)
        {
            var character = line[scan];
            if (character == '\\')
            {
                scan++;
                continue;
            }

            if (opener == '(' && character == '(')
            {
                return false;
            }

            if (character == closer)
            {
                end = scan + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first <paramref name="target"/> at or after <paramref name="from"/> that is not backslash-escaped,
    /// or <c>-1</c>.
    /// </summary>
    private static int IndexOfUnescaped(ReadOnlySpan<char> text, char target, int from)
    {
        for (var scan = from; scan < text.Length; scan++)
        {
            if (text[scan] == '\\')
            {
                scan++;
            }
            else if (text[scan] == target)
            {
                return scan;
            }
        }

        return -1;
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
    /// <para>
    /// Backslash escapes are left in place. They are resolved where containment is decided, in
    /// <see cref="IsLinkTheAgentCanSafelyJoin"/>, which needs BOTH readings — and the refusal report wants
    /// the destination as the file spells it, so an operator can find the line.
    /// </para>
    /// </summary>
    private static string NormalizeLinkDestination(string destination)
    {
        var text = destination.Trim();
        if (text.StartsWith('<'))
        {
            var end = IndexOfUnescaped(text, '>', 1);
            return (end < 0 ? text[1..] : text[1..end]).Trim();
        }

        var space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// A destination with its CommonMark backslash escapes resolved: <c>x\)/../../secrets.md</c> is one path
    /// containing a <c>)</c>, not a name ending in a backslash.
    /// </summary>
    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var scan = 0; scan < text.Length; scan++)
        {
            // Only ASCII punctuation is escapable in CommonMark; a backslash before anything else is a
            // literal backslash - and on this path it is very likely a Windows-style separator, which
            // TryResolveEntryPath still has to see as one.
            if (text[scan] == '\\'
                && scan + 1 < text.Length
                && EscapableAsciiPunctuation.Contains(text[scan + 1], StringComparison.Ordinal))
            {
                scan++;
            }

            _ = builder.Append(text[scan]);
        }

        return builder.ToString();
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

        // Only a single-link line in OUR entry shape can be shortened. The cut writes "- [" back on the front
        // and slices the title from index 3, so on any other line it would invent an entry out of the middle
        // of a sentence. And on a two-link line the anchor swallows everything between the first link and the
        // last as title text, rendering "- [First entry's title (truncated)](second entry's link)": a label
        // naming one entry over a link pointing at another. Misattributed knowledge is worse than absent
        // knowledge, and either line is counted as dropped with a route to the full _toc.md, so it fits whole
        // or not at all.
        if (links.Count != 1 || !IsTocEntry(line))
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
    /// The entry with any model-authored metadata field that carries a link escaping
    /// <paramref name="knowledgeBaseRoot"/> cleared, or the entry itself when there is nothing to clear.
    /// <para>
    /// <see cref="KnowledgeEntryMeta.File"/> is not the only field of an entry that reaches the reviewer.
    /// Title, tags and scope come from the same knowledge-extraction agent, are rendered into the block
    /// verbatim by <see cref="RenderEntry"/>, and a Markdown link inside one of them resolves exactly like a
    /// link anywhere else — so checking only the field the path is built from left the other three carrying
    /// whatever they liked. Same rule, whole entry.
    /// </para>
    /// <para>
    /// The VERDICT is not the <c>_toc.md</c> route's, because the situations are not alike. There the link
    /// IS the entry: strip it and nothing remains, so refusal is the only remedy available. Here the
    /// load-bearing field is <c>File</c>, which the caller has already cleared, and the offending link sits
    /// in decoration. Refusing the entry over it would delete sound knowledge for a title like
    /// <c>Follow the [ADO onboarding guide](../../docs/ado.md) first</c> — well-intentioned, plausible, and
    /// escaping only because a repository's own docs live outside the Knowledge Base. That is the
    /// knowledge-blindness this feature exists to remove, reintroduced by the fix for it. An ugly title
    /// beats a missing entry.
    /// </para>
    /// <para>
    /// Whole VALUES are replaced, never edited within. Cutting inside a value is what produced this file's
    /// two worst defects — half a path, and a title cut over someone else's link — because a fragment still
    /// reads like the real thing. Nothing partial survives a wholesale replacement, and a cleared title or
    /// scope lands on the blank-value fallback <see cref="RenderEntry"/> already had.
    /// </para>
    /// </summary>
    private static KnowledgeEntryMeta ClearEscapingMetadata(KnowledgeEntryMeta entry, string knowledgeBaseRoot)
    {
        var titleEscapes = CarriesAnEscapingLink(entry.Title, knowledgeBaseRoot);
        var scopeEscapes = CarriesAnEscapingLink(entry.Scope, knowledgeBaseRoot);
        var tags = entry.Tags.Where(tag => !CarriesAnEscapingLink(tag, knowledgeBaseRoot)).ToList();
        if (!titleEscapes && !scopeEscapes && tags.Count == entry.Tags.Count)
        {
            // Returned by reference so the caller can tell "nothing to do" from "cleaned" without comparing
            // fields a second time and getting a different answer than this method did.
            return entry;
        }

        return entry with
        {
            Title = titleEscapes ? string.Empty : entry.Title,
            Scope = scopeEscapes ? string.Empty : entry.Scope,
            Tags = tags,
        };
    }

    /// <summary>
    /// Whether a metadata value carries a link the agent must not be handed. The same two questions the
    /// <c>_toc.md</c> gate asks - is it a reference we refuse to resolve, and does its destination stay
    /// inside the Knowledge Base - only the verdict differs, per <see cref="ClearEscapingMetadata"/>.
    /// <para>
    /// A reference-style link counts as escaping whether or not its definition points somewhere harmless,
    /// because deciding that would mean resolving it, and this side of the file resolves nothing. Clearing
    /// a decorative title that happened to use one costs a hint; carrying it costs the guarantee.
    /// </para>
    /// </summary>
    private static bool CarriesAnEscapingLink(string? field, string knowledgeBaseRoot) =>
        !string.IsNullOrEmpty(field)
        && (CarriesAReferenceStyleLink(field)
            || TocLinks(field).Any(
                link => !link.Delimited || !IsLinkTheAgentCanSafelyJoin(link.Destination, knowledgeBaseRoot)));

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
        var title = entry.EffectiveTitle;
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
    /// already contains it, so it lands harmlessly under the root. A path carrying an ampersand is refused
    /// outright, for the reason given at the check itself.
    /// </summary>
    private static bool TryResolveEntryPath(string knowledgeBaseRoot, string? file, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        // Refused, not decoded, and refused on ANY ampersand rather than on the entities we happen to know.
        // A separator written as a character reference is invisible to the split below: "..&#x2F;..&#x2F;etc"
        // contains no literal "/" until its last segment, so it reduces to one ordinary directory name and
        // resolves happily inside the root, while the agent handed that path reads the separators and walks
        // out of the store. The previous reading decoded the value and required containment of the result -
        // which closed the numeric spelling and left "..&sol;..&sol;etc" wide open, because
        // WebUtility.HtmlDecode implements a PRE-HTML5 entity table and a GFM reader implements HTML5.
        //
        // So the rule is not "decode better". Assembling our own table would be the same mistake a second
        // time: we would again be asserting which entity set the reader implements, over a list of thousands
        // of names that does not stay still, on a value written by an LLM. We do not need to SUPPORT
        // character references; we need to not be fooled by one. Our own generator never emits an ampersand
        // in a path, so refusing every one of them costs no real retrieval and is strictly stronger than any
        // decoding, including a correct decoding. Checked here because this is the one containment rule both
        // routes share, and "file" reaches it without passing the link rule.
        if (file.Contains('&', StringComparison.Ordinal))
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

    private static int Score(KnowledgeEntryMeta entry, HashSet<string> pathTokens, HashSet<string> proseTokens)
    {
        if (pathTokens.Count == 0 && proseTokens.Count == 0)
        {
            return 0;
        }

        var tagTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in entry.Tags)
        {
            AddTokens(tag, tagTokens);
        }

        var score = (PathTagWeight * tagTokens.Count(pathTokens.Contains))
            + (ProseTagWeight * tagTokens.Count(proseTokens.Contains));

        var titleTokens = new HashSet<string>(StringComparer.Ordinal);
        AddTokens(entry.EffectiveTitle, titleTokens);
        titleTokens.ExceptWith(tagTokens); // A word already counted as a tag must not be paid for twice.

        return score
            + (PathTitleWeight * titleTokens.Count(pathTokens.Contains))
            + (ProseTitleWeight * titleTokens.Count(proseTokens.Contains));
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
/// that text after the character budget was applied, the entries refused because their path did not
/// resolve inside the Knowledge Base, and the entries KEPT after a metadata field of theirs was cleared for
/// carrying an escaping link. All four are reported together so the caller can log what the reviewer
/// genuinely received AND what was withheld from it - a refusal nobody logs is indistinguishable from a
/// Knowledge Base that never held the entry.
/// <para>
/// <paramref name="Neutralized"/> is separate from <paramref name="Rejected"/> because the entry was NOT
/// rejected: it keeps its slot and its path is intact. What the operator needs to know is that the knowledge
/// agent wrote a link into a title, tag or scope that pointed outside the Knowledge Base - a fact about
/// extraction quality that would otherwise leave no trace at all, since the entry it happened to arrives
/// looking perfectly healthy.
/// </para>
/// <para>
/// It is <b>not</b> a subset of <paramref name="Rendered"/>, and reading it as one is how a delivery claim
/// gets made out of a defect report. An entry is added here before the character budget is applied, so an
/// entry cut by the budget - or every entry, when the header alone does not fit - appears here and in
/// neither <paramref name="Text"/> nor <paramref name="Rendered"/>. That asymmetry is deliberate: what the
/// extraction agent wrote is true whether or not there was room left to print it. A caller that wants the
/// entries the reviewer actually received has to intersect the two lists itself.
/// </para>
/// </summary>
internal sealed record KnowledgeDigestBlock(
    string Text,
    IReadOnlyList<KnowledgeEntryMeta> Rendered,
    IReadOnlyList<KnowledgeEntryMeta> Rejected,
    IReadOnlyList<KnowledgeEntryMeta> Neutralized);

/// <summary>
/// The rendered <c>_toc.md</c> fallback block plus what it actually carried. <see cref="Listed"/> and
/// <see cref="Dropped"/> exist so the caller can log what the reviewer RECEIVED rather than the size of the
/// file that was read - once the block is budgeted, those two numbers stop being the same, and a log that
/// reports the read is the same silent-failure shape the ranked digest's proof-of-use line was added to fix.
/// <see cref="Truncated"/> is tracked separately because a table of contents with no recognisable entry
/// lines can be cut without <see cref="Dropped"/> ever moving off zero. <see cref="Duplicates"/> counts entry
/// lines every one of whose files is already listed above them, which are neither listed nor dropped: they
/// were removed for the same reason as on the ranked route, and counting one as "1 more entry in _toc.md"
/// would route the agent back to the line it just read. Every one, because a line that also names a file
/// nothing else lists is not a repeat, and dropping it lost that file out of the block and out of both
/// counts at once.
/// </summary>
internal sealed record KnowledgeTocBlock(
    string Text, int Listed, int Dropped, bool Truncated, IReadOnlyList<string> Refused, int Duplicates);

/// <summary>
/// Knowledge Base entries with records naming the same file collapsed to one apiece, plus what was
/// collapsed away. <see cref="Collapsed"/> and <see cref="Conflicting"/> are carried rather than counted
/// because the two say different things to an operator: repetition means an index that was merged badly,
/// disagreement means one that is torn, and only the second is a reason to go and look at the file.
/// </summary>
internal sealed record KnowledgeDeduplication(
    IReadOnlyList<KnowledgeEntryMeta> Entries,
    IReadOnlyList<KnowledgeEntryMeta> Collapsed,
    IReadOnlyList<KnowledgeEntryMeta> Conflicting);

/// <summary>
/// Knowledge Base entries split by whether their path resolves inside the Knowledge Base root.
/// <see cref="Refused"/> is carried rather than discarded because an entry that simply vanishes is
/// indistinguishable from one the Knowledge Base never held, and these were written by an LLM with file
/// tools - the refusal is the interesting signal, not the omission.
/// </summary>
internal sealed record KnowledgeContainmentPartition(
    IReadOnlyList<KnowledgeEntryMeta> Usable,
    IReadOnlyList<KnowledgeEntryMeta> Refused);

/// <summary>
/// Knowledge Base entries with their model-authored metadata already cleaned, plus the ORIGINALS of the
/// ones that needed cleaning.
/// <para>
/// The originals are carried rather than the cleaned copies because the diagnostic exists to say what the
/// extraction agent wrote, and the cleaned copy is precisely the evidence with the interesting part removed.
/// </para>
/// </summary>
internal sealed record KnowledgeSanitizedEntries(
    IReadOnlyList<KnowledgeEntryMeta> Entries,
    IReadOnlyList<KnowledgeEntryMeta> Neutralized);
