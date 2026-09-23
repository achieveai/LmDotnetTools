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
/// absolute paths, <c>file://</c> URIs, <c>sandbox:</c> container URIs, or plain relative paths; only the server
/// knows the session's HostPath, so the conversion lives here rather than in the client. The result is purely
/// lexical — the caller still resolves it component by component against the gateway listing for existence and
/// type.
/// </summary>
public static class WorkspaceLinkResolver
{
    public const string InvalidPath = "invalid_path";

    public const string OutsideWorkspace = "outside_workspace";

    /// <summary>
    /// The workspace mount root INSIDE the sandbox container. A <c>sandbox:</c> link is written by a model that
    /// sees the container filesystem, so its paths are rooted here rather than at the session's HostPath. It is a
    /// fixed convention on this host, not a per-session value: <c>SandboxSessionRegistry.ToWorkspaceRelativePath</c>
    /// strips the same literal, and <c>SANDBOX_WORKSPACE</c> — the only knob that could move it — is a PROTECTED
    /// name in <c>SandboxEnvRules</c>, so no workspace, mode or provision env layer can change it.
    /// </summary>
    private const string ContainerWorkspaceRoot = "/workspace";

    /// <summary>
    /// Resolves <paramref name="target"/> against <paramref name="hostPath"/>. A <c>?query</c> or <c>#fragment</c>
    /// is dropped and percent-encoding decoded; an absolute path must lie under HostPath on a segment boundary
    /// (compared case-insensitively, with either separator, when HostPath is Windows-style); a <c>sandbox:</c> URI
    /// is measured against the container mount root instead; a relative path loses any leading <c>./</c> and must
    /// not contain a backslash. <c>.</c> and <c>..</c> segments are normalised away, and a <c>..</c> that would
    /// climb above the workspace root is refused rather than clamped. NUL and unrecognised URI schemes are invalid.
    /// </summary>
    /// <remarks>
    /// A relative target is always measured from the workspace ROOT. A link written inside a previewed file is
    /// relative to that file's own directory instead, but only the client knows which file a rendered link came
    /// from, so the renderer joins the directory onto the href before it ever reaches here (see the client's
    /// <c>workspaceLinks.resolveAgainstBaseDir</c>). The join is naive concatenation; the dot-segment
    /// normalisation and the containment rule below are what make it safe, and they stay on this side.
    /// </remarks>
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
        // True when the target addressed the CONTAINER mount rather than the host filesystem, which decides which
        // root the absolute path below is measured against.
        var container = false;
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = FileUriPath(raw["file:".Length..]);
            if (filePath is null)
            {
                return WorkspaceLinkResolution.Fail(InvalidPath);
            }

            decoded = filePath;
        }
        else if (raw.StartsWith("sandbox:", StringComparison.OrdinalIgnoreCase))
        {
            // The scheme a sandbox-backed model writes for a file it can see. Collapse a leading run of slashes so
            // `sandbox:/workspace/x`, `sandbox://workspace/x` (workspace spelled where a URI authority goes) and
            // `sandbox:///workspace/x` all name the same container path.
            var payload = Uri.UnescapeDataString(raw["sandbox:".Length..]);
            decoded = payload.StartsWith('/') ? "/" + payload.TrimStart('/') : payload;
            container = true;
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
        if (container)
        {
            // A `sandbox:` URI with a relative payload names no container path, exactly as `file:docs/a.md` names
            // no local one.
            if (!IsAbsolute(decoded))
            {
                return WorkspaceLinkResolution.Fail(InvalidPath);
            }

            // Measured against the container mount, NOT HostPath: the mount root is what the model saw. A bare
            // absolute `/workspace/...` keeps its old meaning in the branch below — only the scheme grants
            // container semantics, so a locally hosted Windows workspace still refuses one as outside itself.
            var mounted = StripRoot(decoded, ContainerWorkspaceRoot);
            if (mounted is null)
            {
                return WorkspaceLinkResolution.Fail(OutsideWorkspace);
            }

            relative = mounted;
        }
        else if (IsAbsolute(decoded))
        {
            var stripped = StripRoot(decoded, hostPath);
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

        return Normalize(relative);
    }

    /// <summary>
    /// Collapses <c>.</c> and <c>..</c> segments into the workspace-relative '/' path the file API accepts. A
    /// <c>..</c> with nothing left to pop is REFUSED rather than clamped at the root: clamping would silently
    /// resolve a different file than the link named. The result therefore never contains a dot segment, which the
    /// controller's component-wise resolution relies on — it rejects one outright.
    /// </summary>
    private static WorkspaceLinkResolution Normalize(string relative)
    {
        var components = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(components.Length);
        foreach (var component in components)
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (normalized.Count == 0)
                {
                    return WorkspaceLinkResolution.Fail(InvalidPath);
                }

                normalized.RemoveAt(normalized.Count - 1);
                continue;
            }

            normalized.Add(component);
        }

        return WorkspaceLinkResolution.Ok(string.Join('/', normalized));
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
    /// The part of <paramref name="path"/> below <paramref name="root"/> — the session's HostPath, or the
    /// container mount root for a <c>sandbox:</c> link — or null when it is not under it. A Windows-style root
    /// compares case-insensitively and treats '\' and '/' alike; a POSIX root compares ordinally. The match must
    /// end on a segment boundary, so <c>B:\ws</c> never claims <c>B:\ws2</c> and <c>/workspace</c> never claims
    /// <c>/workspace2</c>.
    /// </summary>
    private static string? StripRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var windows = IsWindowsStyle(root);
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = (windows ? root.Replace('\\', '/') : root).TrimEnd('/');
        var candidate = windows ? path.Replace('\\', '/') : path;

        if (!candidate.StartsWith(normalizedRoot, comparison))
        {
            return null;
        }

        var rest = candidate[normalizedRoot.Length..];
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
