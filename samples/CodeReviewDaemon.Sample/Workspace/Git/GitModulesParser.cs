namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>One submodule declared in a <c>.gitmodules</c> file.</summary>
/// <param name="Name">The section name (<c>[submodule "&lt;name&gt;"]</c>).</param>
/// <param name="Path">The checkout path relative to the superproject root.</param>
/// <param name="Url">The configured submodule URL (may be absolute or relative; unresolved here).</param>
internal sealed record SubmoduleEntry(string Name, string Path, string Url);

/// <summary>
/// Parses a <c>.gitmodules</c> file into its submodule entries (plan §3.1 — parse before any init).
/// The format is git-config INI: a <c>[submodule "name"]</c> section header followed by indented
/// <c>key = value</c> lines. Only entries that declare BOTH a <c>path</c> and a <c>url</c> are
/// returned — an entry missing either cannot be fetched and is dropped rather than partially trusted.
/// Parsing is deliberately decoupled from validation: every returned entry is still subject to the
/// host/path/transport allow-list before it is ever initialized.
/// </summary>
internal static class GitModulesParser
{
    public static IReadOnlyList<SubmoduleEntry> Parse(string? gitmodulesContent)
    {
        if (string.IsNullOrWhiteSpace(gitmodulesContent))
        {
            return [];
        }

        var entries = new List<SubmoduleEntry>();
        string? name = null;
        string? path = null;
        string? url = null;

        void Flush()
        {
            if (name is not null && !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(url))
            {
                entries.Add(new SubmoduleEntry(name, path, url));
            }

            path = null;
            url = null;
        }

        foreach (var rawLine in gitmodulesContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                // Section header — flush the previous submodule before starting a new one.
                Flush();
                name = ExtractSectionName(line);
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0 || name is null)
            {
                continue;
            }

            var key = line[..eq].Trim().ToLowerInvariant();
            var value = Unquote(line[(eq + 1)..].Trim());
            if (key == "path")
            {
                path = value;
            }
            else if (key == "url")
            {
                url = value;
            }
        }

        Flush();
        return entries;
    }

    /// <summary>Extracts the quoted submodule name from a <c>[submodule "name"]</c> header.</summary>
    private static string? ExtractSectionName(string header)
    {
        var first = header.IndexOf('"', StringComparison.Ordinal);
        if (first < 0)
        {
            return null;
        }

        var last = header.LastIndexOf('"');
        return last > first ? header[(first + 1)..last] : null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
