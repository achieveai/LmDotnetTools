namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>The transport family of a git remote URL. Only HTTP(S) is permitted for daemon fetches;
/// every other kind is a denied (local/exec/ssh) transport or an unrecognized shape that fails closed.</summary>
internal enum GitUrlKind
{
    Https,
    Http,
    Ssh,
    Git,
    File,
    Ext,

    /// <summary>A relative submodule URL (<c>./</c> or <c>../</c>), resolved against the superproject remote.</summary>
    Relative,

    /// <summary>An unrecognized or bare shape — treated as denied (fail closed).</summary>
    Unknown,
}

/// <summary>
/// A parsed git remote URL reduced to the fields the security policy cares about: its
/// <see cref="Kind"/> (transport family), <see cref="Host"/>, and canonical <see cref="RepoPath"/>
/// (leading slash, no trailing <c>.git</c>). Submodule URLs are attacker-controlled, so parsing is
/// conservative: anything that is not plainly an HTTP(S)/ssh/git/file/ext/relative URL becomes
/// <see cref="GitUrlKind.Unknown"/> and is denied downstream. Relative URLs are resolved against the
/// superproject remote via <see cref="Resolve"/>, mirroring git's own semantics.
/// </summary>
internal sealed record GitRemoteUrl(GitUrlKind Kind, string Host, string RepoPath, string Raw)
{
    public bool IsRelative => Kind == GitUrlKind.Relative;

    public static GitRemoteUrl Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var value = raw.Trim();

        if (value.Length == 0)
        {
            return new GitRemoteUrl(GitUrlKind.Unknown, string.Empty, string.Empty, raw);
        }

        // ext::/file:// transports — RCE vectors, captured explicitly so they are denied by kind.
        if (value.StartsWith("ext::", StringComparison.OrdinalIgnoreCase))
        {
            return new GitRemoteUrl(GitUrlKind.Ext, string.Empty, string.Empty, raw);
        }

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return new GitRemoteUrl(GitUrlKind.File, string.Empty, string.Empty, raw);
        }

        // Relative submodule URLs are resolved against the superproject remote.
        if (value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal))
        {
            return new GitRemoteUrl(GitUrlKind.Relative, string.Empty, value, raw);
        }

        // An absolute local path is a (denied) local transport.
        if (value.StartsWith('/'))
        {
            return new GitRemoteUrl(GitUrlKind.File, string.Empty, NormalizeRepoPath(value), raw);
        }

        var schemeSep = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep > 0)
        {
            var scheme = value[..schemeSep].ToLowerInvariant();
            var rest = value[(schemeSep + 3)..];
            var kind = scheme switch
            {
                "https" => GitUrlKind.Https,
                "http" => GitUrlKind.Http,
                "ssh" => GitUrlKind.Ssh,
                "git" => GitUrlKind.Git,
                _ => GitUrlKind.Unknown,
            };

            var (host, path) = SplitHostAndPath(rest);
            return new GitRemoteUrl(kind, host, NormalizeRepoPath(path), raw);
        }

        // scp-like syntax: user@host:path (no scheme, a colon before any slash) → ssh transport.
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (colon > 0 && (slash < 0 || colon < slash))
        {
            var hostPart = value[..colon];
            var at = hostPart.IndexOf('@', StringComparison.Ordinal);
            var host = at >= 0 ? hostPart[(at + 1)..] : hostPart;
            return new GitRemoteUrl(GitUrlKind.Ssh, host, NormalizeRepoPath(value[(colon + 1)..]), raw);
        }

        // A bare token with no scheme, no relative prefix, no host — unrecognized, fail closed.
        return new GitRemoteUrl(GitUrlKind.Unknown, string.Empty, string.Empty, raw);
    }

    /// <summary>
    /// Resolves this relative URL against an absolute <paramref name="parent"/> remote, mirroring git:
    /// <c>../x</c> pops the parent's last path segment then descends, so a submodule of
    /// <c>https://host/acme/widgets</c> with URL <c>../shared-lib</c> resolves to
    /// <c>https://host/acme/shared-lib</c>. Over-popping past the root yields an
    /// <see cref="GitUrlKind.Unknown"/> result (denied). Calling on a non-relative URL returns it
    /// unchanged.
    /// </summary>
    public GitRemoteUrl Resolve(GitRemoteUrl parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!IsRelative)
        {
            return this;
        }

        var segments = new List<string>(
            parent.RepoPath.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in RepoPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    // Escaped above the parent root — cannot map to a real repo; fail closed.
                    return new GitRemoteUrl(GitUrlKind.Unknown, parent.Host, string.Empty, Raw);
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        var resolvedPath = NormalizeRepoPath("/" + string.Join('/', segments));
        return new GitRemoteUrl(parent.Kind, parent.Host, resolvedPath, Raw);
    }

    /// <summary>
    /// The fixed suffix of a legacy Azure DevOps organization host (<c>{org}.visualstudio.com</c>).
    /// </summary>
    private const string AdoLegacyHostSuffix = ".visualstudio.com";

    /// <summary>
    /// Rewrites a legacy Azure DevOps <c>https://{org}.visualstudio.com/{project}/_git/{repo}</c> URL to the
    /// modern <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c> shape — the org moves from the <b>host</b>
    /// label into the leading <b>path</b> segment. This is a well-known, fixed ADO URL-shape equivalence (not an
    /// attacker-controlled mapping), so it is safe to apply unconditionally: it is a pure string transform that
    /// changes only the URL <i>spelling</i>, never <i>which</i> repo is addressed. It does NOT broaden the
    /// allow-list — the downstream host+path allow rule still gates which repos may be fetched.
    /// <para>
    /// Only an HTTPS <c>*.visualstudio.com</c> URL with a single-label org is transformed; every other URL
    /// (different host, non-HTTPS transport, relative, or a multi-label <c>a.b.visualstudio.com</c> that is not a
    /// bare org host) is returned unchanged so the caller's transport/host/path checks stay fail-closed.
    /// </para>
    /// </summary>
    public static GitRemoteUrl CanonicalizeAdoLegacyHost(GitRemoteUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Kind != GitUrlKind.Https
            || !url.Host.EndsWith(AdoLegacyHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var org = url.Host[..^AdoLegacyHostSuffix.Length];
        if (org.Length == 0 || org.Contains('.', StringComparison.Ordinal))
        {
            // Not a bare "{org}.visualstudio.com" (empty or an extra sub-domain) — leave untouched, fail closed.
            return url;
        }

        var rewrittenPath = NormalizeRepoPath($"/{org}{url.RepoPath}");
        return url with { Host = "dev.azure.com", RepoPath = rewrittenPath };
    }

    private static (string Host, string Path) SplitHostAndPath(string authorityAndPath)
    {
        // Strip optional userinfo.
        var at = authorityAndPath.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            authorityAndPath = authorityAndPath[(at + 1)..];
        }

        var slash = authorityAndPath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return (StripPort(authorityAndPath), string.Empty);
        }

        var host = authorityAndPath[..slash];
        var path = authorityAndPath[slash..];
        return (StripPort(host), path);
    }

    private static string StripPort(string host)
    {
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? host : host[..colon];
    }

    /// <summary>Canonicalizes a repo path to a leading slash, no trailing <c>.git</c> or slash.</summary>
    private static string NormalizeRepoPath(string path)
    {
        var p = path.Trim();
        if (p.Length == 0)
        {
            return string.Empty;
        }

        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        if (p.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            p = p[..^4];
        }

        return p.TrimEnd('/');
    }
}
