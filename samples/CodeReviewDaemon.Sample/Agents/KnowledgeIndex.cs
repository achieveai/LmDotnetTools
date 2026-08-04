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

    /// <summary>
    /// Reads <c>_index.jsonl</c> back into its entries — the inverse of <see cref="RenderIndex"/> — so a
    /// consumer can filter the Knowledge Base on <see cref="KnowledgeEntryMeta.Tags"/>/
    /// <see cref="KnowledgeEntryMeta.Scope"/> without opening every entry file.
    /// <para>
    /// Deliberately TOLERANT: a blank line, a line that is not JSON, or a line carrying no <c>file</c> key
    /// is skipped rather than thrown on. The index is a regenerated derivative — one torn line (a crash
    /// mid-write, a hand edit) must never blind a whole review to the entries around it. Missing optional
    /// keys default to empty, matching <see cref="ParseFrontmatter"/>.
    /// </para>
    /// Order is preserved as-read, which for a <see cref="RenderIndex"/> output is already sorted by file.
    /// </summary>
    public static IReadOnlyList<KnowledgeEntryMeta> ParseIndex(string? indexJsonl)
    {
        if (string.IsNullOrWhiteSpace(indexJsonl))
        {
            return [];
        }

        var entries = new List<KnowledgeEntryMeta>();
        var lines = indexJsonl.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            KnowledgeEntryMeta? entry;
            try
            {
                using var document = JsonDocument.Parse(line);
                entry = ReadEntry(document.RootElement);
            }
            catch (JsonException)
            {
                continue; // A torn or hand-mangled line costs only itself.
            }

            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Reads one index object, or <c>null</c> when it carries no usable <c>file</c> path (an entry
    /// with no path cannot be Read by the agent, so surfacing it would only waste prompt budget).</summary>
    private static KnowledgeEntryMeta? ReadEntry(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("file", out var fileElement)
            || fileElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var file = fileElement.GetString();
        return string.IsNullOrWhiteSpace(file)
            ? null
            : new KnowledgeEntryMeta(
                file,
                ReadString(root, "title"),
                ReadStringArray(root, "tags"),
                ReadString(root, "scope"),
                ReadStringArray(root, "sourcePrs"),
                ReadString(root, "updated"));
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                items.Add(text);
            }
        }

        return items;
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
