namespace LmStreaming.Sample.FileBrowser;

/// <summary>
/// The outcome of <see cref="WorkspaceLinkResolver.Resolve"/>: either a workspace-relative '/' <see cref="Path"/>
/// (empty for the workspace root) or a <see cref="FailureCode"/> of <see cref="WorkspaceLinkResolver.InvalidPath"/>
/// or <see cref="WorkspaceLinkResolver.OutsideWorkspace"/>.
/// </summary>
public sealed record WorkspaceLinkResolution(string? Path, string? FailureCode)
{
    public bool Success => FailureCode is null;

    internal static WorkspaceLinkResolution Ok(string path) => new(path, null);

    internal static WorkspaceLinkResolution Fail(string code) => new(null, code);
}

/// <summary>
/// Converts the raw href of a file link written by the model into the workspace-relative path the file browser
/// API accepts. The model is told the workspace's ABSOLUTE host path, so its links arrive as Windows or POSIX
/// absolute paths, <c>file://</c> URIs, or plain relative paths; only the server knows the session's HostPath, so
/// the conversion lives here rather than in the client. The result is purely lexical — the caller still resolves
/// it component by component against the gateway listing for existence and type.
/// </summary>
public static class WorkspaceLinkResolver
{
    public const string InvalidPath = "invalid_path";

    public const string OutsideWorkspace = "outside_workspace";

    /// <summary>
    /// Resolves <paramref name="target"/> against <paramref name="hostPath"/>. A <c>?query</c> or <c>#fragment</c>
    /// is dropped and percent-encoding decoded; an absolute path must lie under HostPath on a segment boundary
    /// (compared case-insensitively, with either separator, when HostPath is Windows-style); a relative path loses
    /// any leading <c>./</c> and must not contain a backslash. <c>.</c>/<c>..</c> segments, NUL, and non-file URI
    /// schemes are invalid.
    /// </summary>
    public static WorkspaceLinkResolution Resolve(string? target, string? hostPath)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return WorkspaceLinkResolution.Fail(InvalidPath);
        }

        // Strip the suffix BEFORE decoding so an encoded '#' or '?' (%23, %3F) stays part of the file name.
        var raw = target.Trim();
        var suffix = raw.IndexOfAny(['?', '#']);
        if (suffix >= 0)
        {
            raw = raw[..suffix];
        }

        string decoded;
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = FileUriPath(raw["file:".Length..]);
            if (filePath is null)
            {
                return WorkspaceLinkResolution.Fail(InvalidPath);
            }

            decoded = filePath;
        }
        else if (HasNonFileScheme(raw))
        {
            return WorkspaceLinkResolution.Fail(InvalidPath);
        }
        else
        {
            decoded = Uri.UnescapeDataString(raw);
        }

        // Validation runs on the DECODED text, so `%2E%2E` and `%00` cannot slip past it.
        if (decoded.Length == 0 || decoded.Contains('\0'))
        {
            return WorkspaceLinkResolution.Fail(InvalidPath);
        }

        string relative;
        if (IsAbsolute(decoded))
        {
            var stripped = StripHostPath(decoded, hostPath);
            if (stripped is null)
            {
                return WorkspaceLinkResolution.Fail(OutsideWorkspace);
            }

            relative = stripped;
        }
        else
        {
            // A backslash is a legal POSIX name character, never a separator (see the controller's
            // ResolveTargetAsync), so it is only rewritten for the Windows host-path-prefixed case above.
            if (decoded.Contains('\\'))
            {
                return WorkspaceLinkResolution.Fail(InvalidPath);
            }

            relative = decoded;
            while (relative.StartsWith("./", StringComparison.Ordinal))
            {
                relative = relative[2..];
            }
        }

        var components = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(c => c is "." or ".."))
        {
            return WorkspaceLinkResolution.Fail(InvalidPath);
        }

        return WorkspaceLinkResolution.Ok(string.Join('/', components));
    }

    /// <summary>
    /// The decoded local path of a <c>file:</c> URI (text after the scheme), or null when the URI names a remote
    /// host or has no absolute path. <c>file:///B:/x</c> becomes <c>B:/x</c>; <c>file:///x</c> stays <c>/x</c>.
    /// </summary>
    private static string? FileUriPath(string afterScheme)
    {
        var rest = afterScheme;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? string.Empty : rest[slash..];
            if (IsDriveRoot(authority))
            {
                // `file://B:/ws/a.md` — a common malformed spelling that puts the drive where the host goes.
                rest = authority + rest;
            }
            else if (authority.Length > 0 && !authority.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        rest = Uri.UnescapeDataString(rest);
        if (rest.Length >= 3 && rest[0] == '/' && IsDriveRoot(rest[1..3]))
        {
            rest = rest[1..];
        }

        return IsAbsolute(rest) ? rest : null;
    }

    /// <summary>
    /// The part of <paramref name="path"/> below <paramref name="hostPath"/>, or null when it is not under it. A
    /// Windows-style HostPath compares case-insensitively and treats '\' and '/' alike; a POSIX HostPath compares
    /// ordinally. The match must end on a segment boundary, so <c>B:\ws</c> never claims <c>B:\ws2</c>.
    /// </summary>
    private static string? StripHostPath(string path, string? hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return null;
        }

        var windows = IsWindowsStyle(hostPath);
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = (windows ? hostPath.Replace('\\', '/') : hostPath).TrimEnd('/');
        var candidate = windows ? path.Replace('\\', '/') : path;

        if (!candidate.StartsWith(root, comparison))
        {
            return null;
        }

        var rest = candidate[root.Length..];
        return rest.Length == 0 || rest[0] == '/' ? rest : null;
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 2 && IsDriveRoot(path[..2]));

    private static bool IsWindowsStyle(string hostPath) =>
        (hostPath.Length >= 2 && IsDriveRoot(hostPath[..2])) || hostPath.Contains('\\');

    private static bool IsDriveRoot(string text) => text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':';

    /// <summary>
    /// True for a URI scheme other than <c>file</c> (<c>http:</c>, <c>mailto:</c>, ...). A single letter before the
    /// colon is a drive, not a scheme.
    /// </summary>
    private static bool HasNonFileScheme(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 2)
        {
            return false;
        }

        return char.IsAsciiLetter(text[0])
            && text[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }
}
