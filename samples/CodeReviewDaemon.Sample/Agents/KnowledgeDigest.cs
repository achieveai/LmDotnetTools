using System.Text;

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
    /// path the agent can Read directly. Entries are appended while the block fits in
    /// <paramref name="charBudget"/>; whatever does not fit, plus the <paramref name="omitted"/> entries
    /// the ranking already dropped, is reported in a footer that points at <c>_toc.md</c> so nothing
    /// disappears silently. Returns an empty string when there is nothing to say, letting the caller leave
    /// the review input untouched.
    /// </summary>
    public static string Render(
        IReadOnlyList<KnowledgeEntryMeta> entries,
        string knowledgeBaseRoot,
        int charBudget,
        int omitted)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(knowledgeBaseRoot);

        if (entries.Count == 0)
        {
            return omitted > 0 ? Header() + Footer(omitted, knowledgeBaseRoot) : string.Empty;
        }

        var builder = new StringBuilder(Header());
        var listed = 0;
        foreach (var entry in entries)
        {
            var line = RenderEntry(entry, knowledgeBaseRoot);
            if (listed > 0 && builder.Length + line.Length > charBudget)
            {
                break; // Always list at least one entry, even under an implausibly small budget.
            }

            _ = builder.Append(line);
            listed++;
        }

        var missing = omitted + (entries.Count - listed);
        return builder.Append(missing > 0 ? Footer(missing, knowledgeBaseRoot) : string.Empty).ToString();
    }

    private static string Header() =>
        """
        ## Prior knowledge (Knowledge Base)

        Durable lessons from earlier reviews, ranked by relevance to the files this PR changes. Open one
        with the Read tool using the EXACT ABSOLUTE PATH shown below — do NOT Grep or Glob for it, because
        a root-level Grep can come back empty even when the file exists. When you dispatch a sub-agent for
        a dimension, copy the paths that match that dimension into its brief; it has no other way to see
        them and will otherwise review with no prior knowledge at all.


        """;

    private static string Footer(int missing, string knowledgeBaseRoot) =>
        $"\n{missing} more entr{(missing == 1 ? "y is" : "ies are")} not listed here; the full list is in "
        + $"{Join(knowledgeBaseRoot, "_toc.md")}.\n";

    private static string RenderEntry(KnowledgeEntryMeta entry, string knowledgeBaseRoot)
    {
        var title = string.IsNullOrWhiteSpace(entry.Title) ? entry.File : entry.Title;
        var tags = entry.Tags.Count == 0 ? "(none)" : string.Join(", ", entry.Tags);
        var scope = string.IsNullOrWhiteSpace(entry.Scope) ? "(unscoped)" : entry.Scope;

        return $"- {title}\n  tags: {tags} | scope: {scope}\n  {Join(knowledgeBaseRoot, entry.File)}\n";
    }

    /// <summary>Joins a KB-relative entry path onto the root with forward slashes (the checkout the agent
    /// sees is POSIX, whatever the daemon host is).</summary>
    private static string Join(string root, string relative) =>
        $"{root.Replace('\\', '/').TrimEnd('/')}/{relative.Replace('\\', '/').TrimStart('/')}";

    /// <summary>
    /// Splits the <c>a/… b/…</c> remainder of a <c>diff --git</c> header. Paths may themselves contain
    /// " b/", so the candidate whose two sides are equal wins (the overwhelmingly common unchanged-name
    /// case); failing that, the first candidate is taken, which is correct for a rename.
    /// </summary>
    private static (string Left, string Right) SplitHeaderPaths(string remainder)
    {
        var fallback = (Left: string.Empty, Right: string.Empty);
        for (var i = remainder.IndexOf(" b/", StringComparison.Ordinal); i >= 0;
            i = remainder.IndexOf(" b/", i + 1, StringComparison.Ordinal))
        {
            var left = Unprefix(remainder[..i], "a/");
            var right = Unprefix(remainder[(i + 1)..], "b/");
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
        var trimmed = value.Trim().Trim('"');
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) ? trimmed[prefix.Length..] : trimmed;
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
