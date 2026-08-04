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

        foreach (var raw in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
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
    public static IReadOnlyList<string> ParseChangedPaths(string? nameOnlyListing)
    {
        if (string.IsNullOrEmpty(nameOnlyListing))
        {
            return [];
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var markerHead = SandboxLimits.TruncationMarker.TrimStart('\n');

        foreach (var raw in nameOnlyListing.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            // Only the line terminator is stripped, and the split already did that. git permits a filename
            // to begin or end with a space and does NOT quote for one - quoting triggers on non-ASCII,
            // control, quote and backslash bytes only - so trimming here would rename the file into a path
            // git never reported, and Unquote could not repair it because there was never a quoted form.
            // Emptiness is therefore length, not whitespace: "  " is a legal (absurd) filename and survives
            // as one. The truncation marker is still matched against the trimmed form so that detecting it
            // does not depend on how the producer happened to space it.
            if (raw.Length == 0 || raw.Trim().StartsWith(markerHead, StringComparison.Ordinal))
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

        if (entries.Count == 0)
        {
            return new KnowledgeDigestBlock(
                omitted > 0 ? Header() + Footer(omitted, knowledgeBaseRoot) : string.Empty, [], []);
        }

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

        var builder = new StringBuilder(Header());
        var rendered = new List<KnowledgeEntryMeta>(resolved.Count);
        foreach (var (entry, absolute) in resolved)
        {
            var line = RenderEntry(entry, absolute);
            if (rendered.Count > 0 && builder.Length + line.Length > charBudget)
            {
                break; // Always list at least one entry, even under an implausibly small budget.
            }

            _ = builder.Append(line);
            rendered.Add(entry);
        }

        // Counted off the RESOLVED pool, so a rejected entry is neither rendered nor missing. The footer's
        // promise is that whatever did not fit is reachable through _toc.md, and pointing the agent at an
        // entry we just refused to resolve would be an invitation to go and find the very thing refused.
        var missing = omitted + (resolved.Count - rendered.Count);
        if (rendered.Count == 0)
        {
            // Every entry was refused. A header with no paths beneath it reads exactly like a Knowledge
            // Base that happens to be empty, so say nothing at all and let the caller leave the review
            // input untouched; the refusals travel back through Rejected, which is where they get logged.
            return new KnowledgeDigestBlock(
                missing > 0 ? Header() + Footer(missing, knowledgeBaseRoot) : string.Empty, [], rejected);
        }

        return new KnowledgeDigestBlock(
            builder.Append(missing > 0 ? Footer(missing, knowledgeBaseRoot) : string.Empty).ToString(),
            rendered,
            rejected);
    }

    /// <summary>
    /// Renders a raw <c>_toc.md</c> as the prior-knowledge block, for when <c>_index.jsonl</c> is missing or
    /// torn (a Knowledge Base written before the index existed, or a crash mid-write). Strictly weaker than
    /// <see cref="Render"/> — the ToC carries titles and KB-relative links, so there are no tags, no scope
    /// and no ranking — but it arrives under the same <see cref="Heading"/> and states the absolute root the
    /// links hang off, so the agent can still open an entry and still hand paths to its sub-agents. Returns
    /// an empty string for a blank table of contents, letting the caller leave the review input untouched.
    /// </summary>
    public static string RenderTableOfContents(string? tableOfContents, string knowledgeBaseRoot)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        if (string.IsNullOrWhiteSpace(tableOfContents))
        {
            return string.Empty;
        }

        var root = knowledgeBaseRoot.Replace('\\', '/').TrimEnd('/');
        return $"""
            {Heading}

            Durable lessons from earlier reviews. The ranked index was unavailable, so this is the Knowledge
            Base table of contents verbatim, read from {Join(knowledgeBaseRoot, "_toc.md")}. Its links are
            RELATIVE to {root}/ — join a link onto that directory to get the entry's exact absolute path and
            open it with the Read tool; do NOT Grep or Glob for it, because a root-level Grep can come back
            empty even when the file exists. When you dispatch a sub-agent for a dimension, copy the paths
            that match that dimension into its brief; it has no other way to see them and will otherwise
            review with no prior knowledge at all.


            """ + tableOfContents.TrimEnd('\n') + "\n";
    }

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

    private static string RenderEntry(KnowledgeEntryMeta entry, string absolutePath)
    {
        var title = string.IsNullOrWhiteSpace(entry.Title) ? entry.File : entry.Title;
        var tags = entry.Tags.Count == 0 ? "(none)" : string.Join(", ", entry.Tags);
        var scope = string.IsNullOrWhiteSpace(entry.Scope) ? "(unscoped)" : entry.Scope;

        return $"- {title}\n  tags: {tags} | scope: {scope}\n  {absolutePath}\n";
    }

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
