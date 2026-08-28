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

        string title = string.Empty,
            scope = string.Empty,
            updated = string.Empty;
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
    public static IReadOnlyList<KnowledgeEntryMeta> ParseIndex(string? indexJsonl) =>
        ParseIndex(indexJsonl, MaxIndexRecords, out _);

    /// <summary>
    /// Ceiling on <c>_index.jsonl</c> records EXAMINED by <see cref="ParseIndex(string?, int, out bool)"/>,
    /// so the cost of reading the index is bounded by this constant rather than by the size of a file the
    /// model writes.
    /// <para>
    /// The digest already caps what the reviewer is SHOWN — a count of entries and a character budget — and
    /// that cap was mistaken for a bound on the work. It is not: every record in the file was parsed,
    /// materialized, partitioned, sanitized, scored and sorted, and only then were the top few taken. So the
    /// index was trusted for its SIZE by exactly the code that established it must not be trusted for its
    /// CONTENT. One oversized <c>_index.jsonl</c> — a runaway extraction, a hand edit, a merge that
    /// concatenated the file with itself — buys unbounded CPU and memory on every review of that store.
    /// </para>
    /// <para>
    /// Counted over records EXAMINED rather than records KEPT, because a malformed line costs a parse
    /// attempt whether or not it yields an entry: bounding only the kept ones leaves a file of a million
    /// unparseable lines fully scanned, which is the same unbounded work wearing a different hat.
    /// </para>
    /// A real Knowledge Base is hundreds of entries; this is generous by more than an order of magnitude, so
    /// reaching it means something is wrong with the file rather than rich with the store.
    /// </summary>
    public const int MaxIndexRecords = 5_000;

    /// <summary>
    /// Ceiling on the length of a single record. The record count alone does not bound the work, because one
    /// line can be arbitrarily long on its own and <see cref="JsonDocument"/> would parse all of it; a
    /// metadata record is a few hundred characters, so this is refused as malformed rather than truncated.
    /// </summary>
    private const int MaxIndexRecordChars = 64 * 1024;

    /// <summary>
    /// <see cref="ParseIndex(string?)"/> with the ceiling made explicit, reporting through
    /// <paramref name="truncated"/> whether it was reached — a silently shortened index would make a
    /// half-read Knowledge Base indistinguishable from a small one in the daemon's logs, which is the exact
    /// blindness the retrieval logging exists to end.
    /// </summary>
    public static IReadOnlyList<KnowledgeEntryMeta> ParseIndex(string? indexJsonl, int maxRecords, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(indexJsonl) || maxRecords <= 0)
        {
            return [];
        }

        // Walked incrementally rather than split. The previous reading allocated two whole copies of the
        // file (normalizing "\r\n" and then "\r") plus an array holding every line, before a single record
        // had been judged worth keeping - so the peak cost was several times the file size no matter what
        // the caps downstream said. Line endings are handled in the scan instead, where they cost nothing.
        var entries = new List<KnowledgeEntryMeta>();
        var examined = 0;
        var at = 0;
        while (at < indexJsonl.Length)
        {
            var rest = indexJsonl.AsSpan(at);
            var lineBreak = rest.IndexOfAny('\n', '\r');
            var line = lineBreak < 0 ? rest : rest[..lineBreak];
            var start = at;
            at =
                lineBreak < 0
                    ? indexJsonl.Length
                    : at
                        + lineBreak
                        + (
                            rest[lineBreak] == '\r' && lineBreak + 1 < rest.Length && rest[lineBreak + 1] == '\n'
                                ? 2
                                : 1
                        );

            if (line.IsWhiteSpace())
            {
                continue; // A blank line is not a record, so it neither costs a parse nor spends the budget.
            }

            if (examined == maxRecords)
            {
                truncated = true;
                break;
            }

            examined++;
            if (line.Length > MaxIndexRecordChars)
            {
                continue; // Too long to be a metadata record; refused for the same reason a torn line is.
            }

            KnowledgeEntryMeta? entry;
            try
            {
                using var document = JsonDocument.Parse(indexJsonl.AsMemory(start, line.Length));
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
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("file", out var fileElement)
            || fileElement.ValueKind != JsonValueKind.String
        )
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
                ReadString(root, "updated")
            );
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
)
{
    /// <summary>
    /// The title a reader actually sees: <see cref="Title"/> when it says anything, and <see cref="File"/>
    /// when it does not (issue #259) — e.g. blank frontmatter, or a title cleared because it carried an
    /// escaping link. Named once and shared by every site that surfaces an entry's title — the write-side
    /// <c>_toc.md</c> renderer, the read-side digest renderer, and its scorer — so an entry never renders
    /// with an empty label in one place while showing its path in another.
    /// </summary>
    public string EffectiveTitle => string.IsNullOrWhiteSpace(Title) ? File : Title;
}
