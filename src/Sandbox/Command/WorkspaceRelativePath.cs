namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// Validates and normalizes a caller-supplied working directory as a <b>workspace-relative</b> POSIX
/// path, independent of the host OS the SDK happens to run on. The result is the working directory the
/// gateway's Bash wrapper will <c>cd</c> into, resolved beneath the sandbox workspace root — so a
/// value that is rooted, drive/UNC/device-qualified, or lexically escapes the workspace must be
/// refused <i>before</i> anything is submitted to the gateway.
/// </summary>
/// <remarks>
/// <para>
/// The rules are purely lexical and identical on Windows and Linux (they must not depend on
/// <see cref="System.IO.Path"/>, whose separator/root semantics differ per host). A path is rejected
/// when it contains a NUL byte, is a POSIX absolute path (<c>/…</c>), carries a Windows drive prefix
/// (<c>C:</c>, <c>C:\…</c>, <c>C:/…</c>), contains a backslash at all (which covers UNC <c>\\host</c>,
/// device <c>\\?\</c>/<c>\\.\</c> roots, and any mixed-separator form such as <c>a\..\b</c>), or
/// contains a <c>..</c> segment (lexical parent-directory escape). A backslash is never a valid
/// separator or filename byte in a workspace-relative POSIX path, so refusing it outright is both
/// safe and the single rule that collapses the Windows-root and mixed-separator cases.
/// </para>
/// <para>
/// This is a <i>necessary</i> guard, not a sufficient one: the gateway remains the authority for
/// filesystem containment (in particular, symlink traversal — a component that is a symlink pointing
/// outside the workspace cannot be detected lexically and is enforced remotely).
/// </para>
/// </remarks>
internal static class WorkspaceRelativePath
{
    private const char Nul = '\0';

    /// <summary>
    /// Normalizes <paramref name="path"/> to a clean, forward-slash, workspace-relative path with no
    /// leading slash, no <c>.</c> segments, and no redundant separators. A <c>null</c>, empty, or
    /// all-<c>.</c>/separator path normalizes to the empty string, meaning "the workspace root".
    /// Throws <see cref="ArgumentException"/> (with <paramref name="paramName"/>) for any of the
    /// rejected forms described on the type.
    /// </summary>
    public static string Normalize(string? path, string paramName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path.IndexOf(Nul, StringComparison.Ordinal) >= 0)
        {
            throw Rejected(paramName, "it contains a NUL byte");
        }

        if (path.IndexOf('\\', StringComparison.Ordinal) >= 0)
        {
            throw Rejected(
                paramName,
                "it contains a backslash (a workspace-relative path is POSIX/forward-slash only; this "
                    + "rejects Windows drive, UNC, and device roots and any mixed-separator traversal)"
            );
        }

        if (path[0] == '/')
        {
            throw Rejected(paramName, "it is a POSIX absolute path (must be relative to the workspace root)");
        }

        if (HasWindowsDrivePrefix(path))
        {
            throw Rejected(
                paramName,
                "it carries a Windows drive-letter prefix (must be relative to the workspace root)"
            );
        }

        var segments = path.Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                throw Rejected(paramName, "it contains a '..' segment that escapes the workspace root");
            }

            kept.Add(segment);
        }

        return string.Join('/', kept);
    }

    /// <summary>
    /// True when <paramref name="path"/> starts with a <c>&lt;letter&gt;:</c> Windows drive prefix
    /// (e.g. <c>C:</c>, <c>C:foo</c>, <c>C:/foo</c>). The backslash form <c>C:\foo</c> is already
    /// rejected by the earlier backslash check; this catches the forward-slash and drive-relative
    /// variants.
    /// </summary>
    private static bool HasWindowsDrivePrefix(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);

    private static ArgumentException Rejected(string paramName, string reason) =>
        new($"Working directory is not a valid workspace-relative path: {reason}.", paramName);
}
