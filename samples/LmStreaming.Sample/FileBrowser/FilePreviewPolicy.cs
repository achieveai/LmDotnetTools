namespace LmStreaming.Sample.FileBrowser;

/// <summary>
/// The single, centralized, server-side allowlist that decides whether a workspace file is eligible for
/// inline preview (WI #195). The server is authoritative — the client only renders whatever the preview
/// endpoint returns. Eligibility is by file extension (or a small set of well-known extension-less names);
/// a non-listed file is treated as binary and offered as download-only. Two disjoint sets feed it: the TEXT
/// allowlist, whose bytes are decoded as UTF-8, and <see cref="SpreadsheetExtensions"/>, whose bytes go to
/// <see cref="SpreadsheetPreviewReader"/> instead. A file under a
/// dot-directory is excluded outright, ahead of the allowlist — see <see cref="IsUnderDotDirectory"/>.
/// </summary>
public static class FilePreviewPolicy
{
    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".markdown",
        ".rst",
        ".json",
        ".jsonl",
        ".ndjson",
        ".csv",
        ".tsv",
        ".log",
        ".xml",
        ".yaml",
        ".yml",
        ".toml",
        ".ini",
        ".cfg",
        ".conf",
        ".env",
        ".properties",
        ".js",
        ".mjs",
        ".cjs",
        ".ts",
        ".tsx",
        ".jsx",
        ".cs",
        ".fs",
        ".vb",
        ".py",
        ".rb",
        ".php",
        ".go",
        ".rs",
        ".java",
        ".kt",
        ".kts",
        ".scala",
        ".swift",
        ".c",
        ".h",
        ".cpp",
        ".hpp",
        ".cc",
        ".cxx",
        ".m",
        ".mm",
        ".sh",
        ".bash",
        ".zsh",
        ".ps1",
        ".psm1",
        ".bat",
        ".cmd",
        ".html",
        ".htm",
        ".css",
        ".scss",
        ".sass",
        ".less",
        ".vue",
        ".svelte",
        ".sql",
        ".graphql",
        ".gql",
        ".proto",
        ".r",
        ".jl",
        ".lua",
        ".pl",
        ".dockerfile",
        ".gitignore",
        ".dockerignore",
        ".editorconfig",
        ".gitattributes",
    };

    /// <summary>
    /// The OOXML spreadsheet extensions, kept SEPARATE from <see cref="PreviewableExtensions"/> because they
    /// are not text: a file here is handed to <see cref="SpreadsheetPreviewReader"/> and never decoded as
    /// UTF-8. Legacy <c>.xls</c> (binary BIFF) and <c>.xlsb</c> are deliberately absent — the first is a much
    /// larger binary parse surface to expose to an untrusted file, the second is not supported by the reader.
    /// </summary>
    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx",
        ".xlsm",
    };

    private static readonly HashSet<string> PreviewableExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile",
        "makefile",
        "readme",
        "license",
        "notice",
        "authors",
        "changelog",
        "copying",
        ".gitignore",
        ".dockerignore",
        ".editorconfig",
        ".gitattributes",
        ".env",
    };

    /// <summary>
    /// True when <paramref name="name"/> (a file's non-recursive name) is eligible for inline preview —
    /// either on the text allowlist (extension first, then well-known extension-less name) or a spreadsheet.
    /// A caller that needs to know WHICH reader to use asks <see cref="IsSpreadsheet"/>.
    /// </summary>
    public static bool IsPreviewable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && (PreviewableExtensions.Contains(ext) || SpreadsheetExtensions.Contains(ext)))
        {
            return true;
        }

        return PreviewableExactNames.Contains(name);
    }

    /// <summary>
    /// True when <paramref name="name"/> is an OOXML workbook, i.e. the preview must go through
    /// <see cref="SpreadsheetPreviewReader"/> and its own byte cap rather than the UTF-8 text path.
    /// </summary>
    public static bool IsSpreadsheet(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var ext = Path.GetExtension(name);
        return !string.IsNullOrEmpty(ext) && SpreadsheetExtensions.Contains(ext);
    }

    /// <summary>
    /// True when any DIRECTORY component of <paramref name="serverPath"/> is a dot-directory — the file lives
    /// under machine-owned bookkeeping such as <c>.conversations/</c> or <c>.git/</c> rather than under content
    /// a person put there. Such a file is never previewable, whatever its extension: the workspace transcript
    /// mirror (#251) writes every message of a conversation into <c>.conversations/*.jsonl</c>, and
    /// <c>.jsonl</c> is on the allowlist, so without this an agent previewing its own workspace reads its own
    /// transcript back. The FINAL component is deliberately not considered — a dot-FILE such as
    /// <c>.gitignore</c> stays previewable, which is why this is not folded into <see cref="IsPreviewable"/>.
    /// </summary>
    public static bool IsUnderDotDirectory(string serverPath)
    {
        if (string.IsNullOrEmpty(serverPath))
        {
            return false;
        }

        var components = serverPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < components.Length - 1; i++)
        {
            if (components[i].StartsWith('.'))
            {
                return true;
            }
        }

        return false;
    }
}
