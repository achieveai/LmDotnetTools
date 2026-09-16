using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>What a tool result is to compaction (eval spec §3 rows 5a–5e, §4).</summary>
public enum ToolKind
{
    /// <summary>Re-readable: the newest copy per identity is the only one the view needs (RC1).</summary>
    Resource,

    /// <summary>Changes state; its path lands in Artifacts, its content may clear.</summary>
    Mutation,

    /// <summary>Not reproducible: trimmed, never fully cleared, once older than the keep window (RC2).</summary>
    Shell,

    /// <summary>A collaboration receipt or spawn; summarised, and read by RC3 for open exchanges.</summary>
    Collab,
}

/// <summary>One tool's entry in the knowledge registry; bindable from <c>Compaction:ToolKnowledge:{name}</c>.</summary>
public sealed record ToolKnowledgeEntry
{
    /// <summary>What compaction may assume about this tool's results.</summary>
    public ToolKind Kind { get; init; } = ToolKind.Shell;

    /// <summary>
    ///     Argument names, in order, whose string values identify a resource. Empty means the tool name alone
    ///     identifies it (a tool that always reads the same thing).
    /// </summary>
    public IReadOnlyList<string> Identity { get; init; } = [];

    /// <summary>For a mutation: the argument naming the artifact path.</summary>
    public string? PathArgument { get; init; }
}

/// <summary>
///     Tool names → what compaction may assume about their results. An unknown tool is
///     <see cref="ToolKind.Shell" /> (conservative: never deduped, never fully cleared). <see cref="Default" />
///     knows the sandbox and board tools; a host merges its own entries over it through
///     <see cref="CompactionOptions.ToolKnowledge" />.
/// </summary>
public sealed class ToolKnowledgeRegistry
{
    private static readonly ToolKnowledgeEntry Unknown = new();

    private readonly Dictionary<string, ToolKnowledgeEntry> _entries;

    private ToolKnowledgeRegistry(Dictionary<string, ToolKnowledgeEntry> entries) => _entries = entries;

    /// <summary>The built-in entries alone.</summary>
    public static ToolKnowledgeRegistry Default { get; } = new(BuiltIn());

    /// <summary><see cref="Default" /> with <paramref name="overrides" /> added or replaced, by case-insensitive name.</summary>
    public static ToolKnowledgeRegistry Merge(IReadOnlyDictionary<string, ToolKnowledgeEntry>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return Default;
        }

        var entries = BuiltIn();
        foreach (var (name, entry) in overrides)
        {
            entries[name] = entry;
        }

        return new ToolKnowledgeRegistry(entries);
    }

    /// <summary>What is known about <paramref name="toolName" />; an unregistered tool resolves to a shell entry.</summary>
    public ToolKnowledgeEntry Resolve(string? toolName) =>
        toolName is { Length: > 0 } name && _entries.TryGetValue(name, out var entry) ? entry : Unknown;

    /// <summary>
    ///     <c>{tool}|{arg}={value}|…</c> for a resource call, from the top-level string arguments its entry names;
    ///     null for any other kind, and when the arguments never parsed. Two calls with the same key read the same thing.
    /// </summary>
    public string? IdentityKey(string? toolName, string? argsJson)
    {
        var entry = Resolve(toolName);
        if (entry.Kind != ToolKind.Resource)
        {
            return null;
        }

        if (entry.Identity.Count == 0)
        {
            return toolName;
        }

        Dictionary<string, string> strings;
        try
        {
            using var document = JsonDocument.Parse(argsJson ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            strings = document
                .RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }

        return toolName + string.Concat(entry.Identity.Select(arg => $"|{arg}={strings.GetValueOrDefault(arg, "")}"));
    }

    private static Dictionary<string, ToolKnowledgeEntry> BuiltIn() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Read"] = new() { Kind = ToolKind.Resource, Identity = ["file_path"] },
            ["Glob"] = new() { Kind = ToolKind.Resource, Identity = ["pattern", "path"] },
            ["Grep"] = new() { Kind = ToolKind.Resource, Identity = ["pattern", "path", "glob"] },
            ["Skill"] = new() { Kind = ToolKind.Resource, Identity = ["skill"] },
            ["WebFetch"] = new() { Kind = ToolKind.Resource, Identity = ["url"] },
            ["WebSearch"] = new() { Kind = ToolKind.Resource, Identity = ["query"] },
            ["list-tasks"] = new() { Kind = ToolKind.Resource },
            ["get-task"] = new() { Kind = ToolKind.Resource, Identity = ["taskId"] },
            ["Bash"] = new() { Kind = ToolKind.Shell },
            ["PowerShell"] = new() { Kind = ToolKind.Shell },
            ["Write"] = new() { Kind = ToolKind.Mutation, PathArgument = "file_path" },
            ["Edit"] = new() { Kind = ToolKind.Mutation, PathArgument = "file_path" },
            ["MultiEdit"] = new() { Kind = ToolKind.Mutation, PathArgument = "file_path" },
            ["NotebookEdit"] = new() { Kind = ToolKind.Mutation, PathArgument = "notebook_path" },
            ["SendMessage"] = new() { Kind = ToolKind.Collab },
            ["Agent"] = new() { Kind = ToolKind.Collab },
        };
}
