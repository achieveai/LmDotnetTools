namespace LmStreaming.Sample.FileBrowser;

/// <summary>
/// The single, centralized, server-side allowlist that decides whether a workspace file is eligible for
/// inline text preview (WI #195). The server is authoritative — the client only renders whatever the
/// preview endpoint returns. Eligibility is by file extension (or a small set of well-known extension-less
/// names); a non-listed file is treated as binary and offered as download-only.
/// </summary>
public static class FilePreviewPolicy
{
    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".json", ".jsonl", ".ndjson", ".csv", ".tsv", ".log",
        ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".properties",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".cs", ".fs", ".vb", ".py", ".rb", ".php",
        ".go", ".rs", ".java", ".kt", ".kts", ".scala", ".swift", ".c", ".h", ".cpp", ".hpp",
        ".cc", ".cxx", ".m", ".mm", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".sql", ".graphql",
        ".gql", ".proto", ".r", ".jl", ".lua", ".pl", ".dockerfile", ".gitignore", ".dockerignore",
        ".editorconfig", ".gitattributes",
    };

    private static readonly HashSet<string> PreviewableExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "readme", "license", "notice", "authors", "changelog", "copying",
        ".gitignore", ".dockerignore", ".editorconfig", ".gitattributes", ".env",
    };

    /// <summary>
    /// True when <paramref name="name"/> (a file's non-recursive name) is on the text-preview allowlist.
    /// Matches by extension first, then by well-known extension-less name.
    /// </summary>
    public static bool IsPreviewable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && PreviewableExtensions.Contains(ext))
        {
            return true;
        }

        return PreviewableExactNames.Contains(name);
    }
}
