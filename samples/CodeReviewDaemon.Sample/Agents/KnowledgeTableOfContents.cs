using System.Text;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Pure, deterministic renderer for the Knowledge Base <c>_toc.md</c> (plan §13). Given the entries
/// present in <c>KnowledgeBase/</c> it produces a stable Markdown table of contents — sorted by file
/// name so the same set of entries always yields byte-identical output (no spurious diffs on regen).
/// Separated from the IO in <see cref="KnowledgeAgent"/> so the formatting is unit-testable in isolation.
/// </summary>
internal static class KnowledgeTableOfContents
{
    public const string Header = "# Knowledge Base";

    /// <summary>Matches the seeded <c>_toc.md</c> stub so an empty KB regenerates identically.</summary>
    private const string EmptyBody = "_Table of contents (generated)._";

    public static string Render(IEnumerable<KnowledgeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries
            .OrderBy(entry => entry.FileName, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        _ = builder.Append(Header).Append("\n\n");

        if (ordered.Count == 0)
        {
            _ = builder.Append(EmptyBody).Append('\n');
            return builder.ToString();
        }

        foreach (var entry in ordered)
        {
            _ = builder.Append("- [").Append(entry.Title).Append("](").Append(entry.FileName).Append(")\n");
        }

        return builder.ToString();
    }
}

/// <summary>A Knowledge Base entry as it appears in the table of contents: its file name and link text.</summary>
internal sealed record KnowledgeEntry(string FileName, string Title);
