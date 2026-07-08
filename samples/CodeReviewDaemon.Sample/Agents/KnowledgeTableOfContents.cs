using System.Text;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Pure, deterministic renderer for the Knowledge Base <c>_toc.md</c> (plan §13). The KB is layered:
/// entries live under a scope directory (<c>system/</c>, <c>&lt;repo&gt;/</c>). Given the entries present,
/// this groups them by scope (the first path segment) under a <c>## &lt;scope&gt;</c> heading — scopes and
/// entries sorted ordinal — so the same set always yields byte-identical output (no spurious diffs on
/// regen). Entries with no scope segment render flat directly under the header, preserving the legacy
/// single-directory layout. Separated from the IO in <see cref="KnowledgeAgent"/> so the formatting is
/// unit-testable in isolation.
/// </summary>
internal static class KnowledgeTableOfContents
{
    public const string Header = "# Knowledge Base";

    /// <summary>Matches the seeded <c>_toc.md</c> stub so an empty KB regenerates identically.</summary>
    private const string EmptyBody = "_Table of contents (generated)._";

    public static string Render(IReadOnlyList<KnowledgeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return $"{Header}\n\n{EmptyBody}\n";
        }

        var sections = new List<string>();

        // Unscoped entries (no scope directory) render flat directly under the header, preserving the
        // legacy single-directory ToC layout for entries not yet placed under a scope.
        var unscoped = entries
            .Where(entry => ScopeOf(entry.RelPath) is null)
            .OrderBy(entry => entry.RelPath, StringComparer.Ordinal);
        var flat = RenderItems(unscoped);
        if (flat.Length > 0)
        {
            sections.Add(flat);
        }

        // Scoped entries group under a `## <scope>` heading (scope = first path segment), scopes sorted.
        var scoped = entries
            .Select(entry => (Scope: ScopeOf(entry.RelPath), Entry: entry))
            .Where(pair => pair.Scope is not null)
            .GroupBy(pair => pair.Scope, pair => pair.Entry, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in scoped)
        {
            var items = group.OrderBy(entry => entry.RelPath, StringComparer.Ordinal);
            sections.Add($"## {group.Key}\n\n{RenderItems(items)}");
        }

        return $"{Header}\n\n{string.Join("\n", sections)}";
    }

    private static string RenderItems(IEnumerable<KnowledgeEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            _ = builder.Append("- [").Append(entry.Title).Append("](").Append(entry.RelPath).Append(")\n");
        }

        return builder.ToString();
    }

    /// <summary>The scope (first path segment) of <paramref name="relPath"/>, or <c>null</c> when it has none.</summary>
    private static string? ScopeOf(string relPath)
    {
        var slash = relPath.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? relPath[..slash] : null;
    }
}

/// <summary>
/// A Knowledge Base entry as it appears in the table of contents: its scope-qualified relative path
/// (e.g. <c>system/x.md</c> or <c>LmDotnetTools/y.md</c>) and its link text.
/// </summary>
internal sealed record KnowledgeEntry(string RelPath, string Title);
