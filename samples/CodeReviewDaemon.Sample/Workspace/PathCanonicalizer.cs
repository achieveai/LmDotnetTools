using System.Globalization;
using System.Text;

namespace CodeReviewDaemon.Sample.Workspace;


/// <summary>
/// Canonicalizes attacker-influenced relative paths (submodule paths from <c>.gitmodules</c>,
/// artifact subjects, repo-created paths) before they are used for policy matching or filesystem
/// access. The daemon reviews <b>untrusted</b> PR code, so a crafted path must never be able to
/// escape its intended root (<c>..</c> traversal), smuggle a Windows/UNC path, or hide an absolute
/// path behind Unicode tricks.
/// </summary>
/// <remarks>
/// The contract is deliberately strict and fail-closed: anything that is not an obviously-safe
/// forward-slash relative path that stays at or below its root is rejected. Symlink/hardlink
/// resolution is enforced at the sandbox boundary (the gateway runs the actual filesystem ops); this
/// type rejects the <i>textual</i> escape vectors before a path is ever handed to git or the shell.
/// </remarks>
internal static class PathCanonicalizer
{
    /// <summary>
    /// Canonicalizes <paramref name="raw"/> as a relative path that must stay within its root.
    /// </summary>
    /// <param name="raw">The untrusted path text.</param>
    /// <param name="canonical">The normalized <c>a/b/c</c> form when the result is <c>true</c>;
    /// otherwise an empty string.</param>
    /// <param name="error">A human-readable rejection reason when the result is <c>false</c>.</param>
    /// <returns><c>true</c> when the path is a safe in-scope relative path; <c>false</c> otherwise.</returns>
    public static bool TryCanonicalizeRelative(
        string? raw,
        out string canonical,
        out string? error
    )
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "path is empty";
            return false;
        }

        // Normalize Unicode so visually-equal/composed forms cannot bypass later comparisons.
        var normalized = raw.Normalize(NormalizationForm.FormC);

        if (normalized.Contains('\0', StringComparison.Ordinal))
        {
            error = "path contains a NUL byte";
            return false;
        }

        if (normalized.Contains('\\', StringComparison.Ordinal))
        {
            error = "path contains a backslash (Windows/UNC paths are rejected)";
            return false;
        }

        // Drive-letter absolute path (C:/...). Backslash UNC is already rejected above.
        if (
            normalized.Length >= 2
            && char.IsLetter(normalized[0])
            && normalized[1] == ':'
        )
        {
            error = "path is a drive-qualified absolute path";
            return false;
        }

        if (normalized.StartsWith('/'))
        {
            error = "path is absolute";
            return false;
        }

        var stack = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            switch (segment)
            {
                case "":
                    error = "path contains an empty segment";
                    return false;
                case ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        error = "path escapes its root via '..'";
                        return false;
                    }
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                default:
                    stack.Add(segment);
                    continue;
            }
        }

        if (stack.Count == 0)
        {
            error = "path resolves to its own root";
            return false;
        }

        canonical = string.Join('/', stack);
        error = null;
        return true;
    }

    /// <summary>
    /// Lowercases a repository path segment for case-insensitive identity comparison (guards the
    /// <c>LmDotnetTools</c> vs <c>LmDotNetTools</c> casing-drift hazard called out in the plan §7).
    /// The display form must be preserved separately by the caller.
    /// </summary>
    public static string NormalizeForComparison(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture);
    }
}
