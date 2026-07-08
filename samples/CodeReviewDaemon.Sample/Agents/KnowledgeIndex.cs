using System.Text;
using System.Text.Json;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Pure, deterministic helpers for the Knowledge Base's queryable index (design §2). Each entry carries
/// YAML frontmatter (<c>title</c>, <c>tags</c>, <c>scope</c>, <c>sourcePrs</c>, <c>updated</c>);
/// <see cref="ParseFrontmatter"/> reads that flat block into a <see cref="KnowledgeEntryMeta"/>, and
/// <see cref="RenderIndex"/> emits <c>_index.jsonl</c> — one compact JSON object per line, stable key
/// order, sorted by file — so the same set of entries always regenerates byte-identically (no spurious
/// diffs). Kept separate from the IO in <see cref="KnowledgeAgent"/> so it is unit-testable in isolation.
/// A minimal hand-rolled reader (no YAML dependency) covers only the flat scalar/list keys above.
/// </summary>
internal static class KnowledgeIndex
{
    /// <summary>Fixed key order emitted per JSONL line, matching the design's <c>_index.jsonl</c> schema.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// Parses the leading <c>---</c>…<c>---</c> YAML frontmatter block of <paramref name="entryMarkdown"/>
    /// into a <see cref="KnowledgeEntryMeta"/> whose <see cref="KnowledgeEntryMeta.File"/> is
    /// <paramref name="relFile"/> (the entry's KB-relative path). Only the flat keys <c>title</c>,
    /// <c>tags</c>, <c>scope</c>, <c>sourcePrs</c>, <c>updated</c> are read; missing keys default to
    /// empty. Returns <c>null</c> when there is no frontmatter block (the document does not open with a
    /// <c>---</c> fence, or the fence is never closed).
    /// </summary>
    public static KnowledgeEntryMeta? ParseFrontmatter(string relFile, string entryMarkdown)
    {
        ArgumentNullException.ThrowIfNull(relFile);
        if (string.IsNullOrEmpty(entryMarkdown))
        {
            return null;
        }

        var lines = entryMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        // The block must open with a `---` fence (skipping only leading blank lines).
        var open = 0;
        while (open < lines.Length && lines[open].Trim().Length == 0)
        {
            open++;
        }

        if (open >= lines.Length || !IsFence(lines[open]))
        {
            return null;
        }

        var close = -1;
        for (var i = open + 1; i < lines.Length; i++)
        {
            if (IsFence(lines[i]))
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return null;
        }

        string title = string.Empty, scope = string.Empty, updated = string.Empty;
        IReadOnlyList<string> tags = [];
        IReadOnlyList<string> sourcePrs = [];

        for (var i = open + 1; i < close; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "title":
                    title = Unquote(value);
                    break;
                case "scope":
                    scope = Unquote(value);
                    break;
                case "updated":
                    updated = Unquote(value);
                    break;
                case "tags":
                    tags = ParseFlowList(value);
                    break;
                case "sourcePrs":
                    sourcePrs = ParseFlowList(value);
                    break;
                default:
                    break;
            }
        }

        return new KnowledgeEntryMeta(relFile, title, tags, scope, sourcePrs, updated);
    }

    /// <summary>
    /// Renders <paramref name="entries"/> as <c>_index.jsonl</c>: one compact JSON object per line with a
    /// fixed key order (<c>file, title, tags, scope, sourcePrs, updated</c>), entries sorted by
    /// <see cref="KnowledgeEntryMeta.File"/> ordinal so regeneration is byte-stable. Each line ends with a
    /// newline.
    /// </summary>
    public static string RenderIndex(IReadOnlyList<KnowledgeEntryMeta> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries.OrderBy(entry => entry.File, StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var entry in ordered)
        {
            _ = builder.Append(RenderLine(entry)).Append('\n');
        }

        return builder.ToString();
    }

    private static string RenderLine(KnowledgeEntryMeta entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("file", entry.File);
            writer.WriteString("title", entry.Title);
            writer.WriteStartArray("tags");
            foreach (var tag in entry.Tags)
            {
                writer.WriteStringValue(tag);
            }

            writer.WriteEndArray();
            writer.WriteString("scope", entry.Scope);
            writer.WriteStartArray("sourcePrs");
            foreach (var pr in entry.SourcePrs)
            {
                writer.WriteStringValue(pr);
            }

            writer.WriteEndArray();
            writer.WriteString("updated", entry.Updated);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A frontmatter fence is a line that is exactly <c>---</c> once trimmed.</summary>
    private static bool IsFence(string line) => line.Trim() == "---";

    /// <summary>Strips a single pair of matching surrounding single or double quotes, if present.</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            if ((first == '"' || first == '\'') && value[^1] == first)
            {
                return value[1..^1];
            }
        }

        return value;
    }

    /// <summary>
    /// Parses a flow-style YAML list (<c>[a, b]</c> or <c>["x", "y"]</c>) into its trimmed, unquoted,
    /// non-empty items. Bare and quoted scalars are both accepted; an empty list (<c>[]</c>) yields none.
    /// </summary>
    private static IReadOnlyList<string> ParseFlowList(string value)
    {
        var inner = value.Trim();
        if (inner.StartsWith('[') && inner.EndsWith(']'))
        {
            inner = inner[1..^1];
        }

        var items = new List<string>();
        foreach (var part in inner.Split(','))
        {
            var item = Unquote(part.Trim());
            if (item.Length > 0)
            {
                items.Add(item);
            }
        }

        return items;
    }
}

/// <summary>
/// The metadata for one Knowledge Base entry as it appears in <c>_index.jsonl</c>: its KB-relative
/// <paramref name="File"/> path, <paramref name="Title"/>, <paramref name="Tags"/>,
/// <paramref name="Scope"/> (<c>system</c> or a repo name), the <paramref name="SourcePrs"/> that
/// contributed it, and the daemon-injected <paramref name="Updated"/> date.
/// </summary>
internal sealed record KnowledgeEntryMeta(
    string File,
    string Title,
    IReadOnlyList<string> Tags,
    string Scope,
    IReadOnlyList<string> SourcePrs,
    string Updated
);
